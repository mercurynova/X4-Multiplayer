using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Admin;

/// <summary>
/// The in-game admin bridge: <c>AdminCommand</c> arrives from the mod on the Control lane (the requesting node has the Admin
/// role) and the module answers with <c>AdminResult</c> carrying the same <c>request_id</c>. Kick and ban are implemented -
/// they are the two operations whose target the game can name (an online player id); the remaining <see cref="AdminBody"/> arms
/// are answered with a clear refusal instead of being silently ignored, and the admin API covers them.
/// <para>
/// Every callback runs on the actor thread. <see cref="ISessionNodeDriver.RemoveNodeAsync"/> and the ban store are asynchronous,
/// so the work is handed to a task and the reply is queued back onto the actor thread with <see cref="ISessionNodeDriver.Post"/>.
/// </para>
/// </summary>
public sealed partial class AdminModule : ISessionModule, ISessionActorBound
{
    /// <summary>Same ceiling the admin API uses for a reason string.</summary>
    private const int MaxReasonLength = 200;

    private readonly IBanStore _bans;
    private readonly TimeProvider _time;
    private readonly ILogger<AdminModule> _log;

    /// <summary>The actor, set once by <see cref="Bind"/>.</summary>
    private ISessionNodeDriver? _driver;

    /// <summary>
    /// The last command handled per node. The mod numbers its requests from 1 in every process, so the key has to be the whole
    /// command (id, arm and target) and has to be dropped when the node attaches again - a reloaded mod is the same player id with
    /// a fresh counter, and keying on the id alone would swallow its first kick or ban.
    /// </summary>
    private readonly Dictionary<int, (uint RequestId, AdminBody Arm, int Target)> _lastRequest = [];

    public AdminModule(IBanStore bans, TimeProvider? time = null, ILogger<AdminModule>? logger = null)
    {
        _bans = bans ?? throw new ArgumentNullException(nameof(bans));
        _time = time ?? TimeProvider.System;
        _log = logger ?? NullLogger<AdminModule>.Instance;
    }

    /// <inheritdoc />
    public void Bind(ISessionNodeDriver driver) => _driver = driver;

    /// <inheritdoc />
    public bool OnMessage(SessionNode node, InboundFrame frame)
    {
        if (frame.Type != MsgType.AdminCommand)
        {
            return false;
        }

        AdminCommandT request;
        try
        {
            request = MessageRegistry.Default.Decode<AdminCommand>(frame.Frame).UnPack();
        }
        catch (ProtocolViolation)
        {
            return true;  // the actor logs the violation; the frame is not ours to answer
        }

        (uint RequestId, AdminBody Arm, int Target) key = (request.RequestId, request.Body.Type, TargetOf(request.Body));
        if (_lastRequest.TryGetValue(node.PlayerId, out var last) && last == key)
        {
            return true;  // the same command again (a retry): the first reply is the answer
        }

        _lastRequest[node.PlayerId] = key;

        switch (request.Body.Type)
        {
            case AdminBody.KickCmd when request.Body.Value is KickCmdT kick:
                Kick(node, request.RequestId, kick);
                break;

            case AdminBody.BanCmd when request.Body.Value is BanCmdT ban:
                Ban(node, request.RequestId, ban);
                break;

            default:
                Reply(node, request.RequestId, false, "the in-game bridge supports kick and ban only; the admin API covers the rest");
                break;
        }

        return true;
    }

    /// <inheritdoc />
    public void OnNodeLeft(SessionNode node, string reason) => _lastRequest.Remove(node.PlayerId);

    /// <inheritdoc />
    public void OnNodeAttached(SessionNode node, bool resumed) => _lastRequest.Remove(node.PlayerId);

    private static int TargetOf(AdminBodyUnion body)
    {
        return body.Value switch
        {
            KickCmdT kick => kick.PlayerId,
            BanCmdT ban => ban.PlayerId,
            _ => 0,
        };
    }

    private void Kick(SessionNode requester, uint requestId, KickCmdT kick)
    {
        int playerId = kick.PlayerId;
        string reason = NormalizeReason(kick.Reason, "Kicked by an admin");
        SessionNode? target = _driver?.FindNode(playerId);
        if (target is null)
        {
            Reply(requester, requestId, false, $"player {playerId} is not in the session");
            return;
        }

        _ = KickAsync(requester, requestId, playerId, DisconnectCode.Kicked, reason, $"{target.Name} was kicked");
    }

    private void Ban(SessionNode requester, uint requestId, BanCmdT ban)
    {
        int playerId = ban.PlayerId;
        string reason = NormalizeReason(ban.Reason, "Banned by an admin");
        SessionNode? target = _driver?.FindNode(playerId);
        if (target is null)
        {
            Reply(requester, requestId, false, $"player {playerId} is not in the session");
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        // 0 = permanent (admin.fbs). A non-zero duration is seconds, the same unit the wire uses.
        DateTimeOffset? expires = ban.DurationS == 0 ? null : now.AddSeconds(ban.DurationS);
        _ = BanAsync(requester, requestId, target, reason, now, expires);
    }

    private async Task KickAsync(SessionNode requester, uint requestId, int playerId, DisconnectCode code, string reason, string okMessage)
    {
        bool removed = false;
        try
        {
            removed = _driver is not null && await _driver.RemoveNodeAsync(playerId, code, reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogKickFailed(playerId, ex);
        }

        string message = removed ? okMessage : $"player {playerId} is not in the session";
        Post(() => Reply(requester, requestId, removed, message));
    }

    private async Task BanAsync(SessionNode requester, uint requestId, SessionNode target, string reason, DateTimeOffset now, DateTimeOffset? expires)
    {
        bool recorded = false;
        try
        {
            recorded = await _bans.AddKeyHashBanAsync(target.KeyHash, reason, "in-game admin", now, expires, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogBanNotStored(target.PlayerId, ex);
        }

        if (!recorded)
        {
            // Without the stored ban the player could simply rejoin, so a half-done ban is reported as a failure.
            Post(() => Reply(requester, requestId, false, $"{target.Name} could not be banned: no stored player record for that key"));
            return;
        }

        bool removed = false;
        try
        {
            removed = _driver is not null &&
                await _driver.RemoveNodeAsync(target.PlayerId, DisconnectCode.Banned, reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogBanCloseFailed(target.PlayerId, ex);
        }

        string message = removed
            ? $"{target.Name} was banned"
            : $"{target.Name} was banned but was no longer in the session";
        Post(() => Reply(requester, requestId, true, message));
    }

    private void Post(Action work)
    {
        if (_driver is null || !_driver.Post(work))
        {
            LogReplyNotQueued();
        }
    }

    private void Reply(SessionNode requester, uint requestId, bool ok, string message)
    {
        INodeConnection? connection = requester.Connection;
        if (connection is null)
        {
            return;
        }

        var reply = new AdminResultT { RequestId = requestId, Ok = ok, Message = message };
        OutboundFrame frame = ControlFrames.Encode(MsgType.AdminResult, fbb => AdminResult.Pack(fbb, reply).Value, 160);
        try
        {
            connection.TrySend(frame);
        }
        finally
        {
            frame.Release();
        }

        LogReplySent(requester.PlayerId, requestId, ok, message);
    }

    private static string NormalizeReason(string? reason, string fallback)
    {
        string text = (reason ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return fallback;
        }

        return text.Length <= MaxReasonLength ? text : text[..MaxReasonLength];
    }

    // ------------------------------------------------------------------ logging

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "admin kick of player {PlayerId} failed")]
    private partial void LogKickFailed(int playerId, Exception ex);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "admin ban of player {PlayerId} was not stored")]
    private partial void LogBanNotStored(int playerId, Exception ex);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "admin ban of player {PlayerId} could not close the session")]
    private partial void LogBanCloseFailed(int playerId, Exception ex);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "admin result could not be queued: the session actor is gone")]
    private partial void LogReplyNotQueued();

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "admin reply to player {PlayerId}: request {RequestId} ok={Ok} {Message}")]
    private partial void LogReplySent(int playerId, uint requestId, bool ok, string message);
}