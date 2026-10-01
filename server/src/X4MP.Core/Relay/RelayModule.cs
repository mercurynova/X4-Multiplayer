using Google.FlatBuffers;
using Microsoft.Extensions.Logging;
using X4MP.Core.Events;
using X4MP.Core.Net;
using X4MP.Core.Permissions;
using X4MP.Core.Session;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Relay;

/// <summary>
/// The relay (server-design 2.6 and 2.9, protocol.md 13 and 16, M1-10): everything that passes between nodes without the server
/// owning the answer.
/// <list type="bullet">
/// <item><b>Player state.</b> A client's <c>PlayerState</c> gets a velocity (3-sample slope, <see cref="MotionEstimator"/>) that is
/// written into the <see cref="WorldMirror"/> for replication to the other players, and is fed to the authority, capped at
/// <see cref="RelayOptions.PlayerStateRelayHz"/> (20 Hz) with the avatar's <c>net_id</c> stamped, latest sample wins. Inbound floods are
/// coalesced before they get here (<see cref="NodeInbox"/>).</item>
/// <item><b>Avatar flow.</b> <c>PlayerShip</c> goes to the authority (held while there is none: protocol.md 13 "registration waits");
/// the authority's <c>EntitySpawn{controller_player}</c>, applied by the mirror and replicated by the interest manager to everyone
/// including the sender, tells the relay which ship is the player's: the roster carries it (<c>PlayerInfo.ship_net_id</c>).</item>
/// <item><b>Intents.</b> Forwarded to the authority with the sender stamped, answered exactly once: by the authority's
/// <c>IntentResult</c>, by <c>Rejected{Timeout}</c> after <see cref="RelayOptions.IntentTimeoutMs"/>, or immediately by
/// <c>Rejected{...}</c> (no authority, rate limit, a failed gate). A <c>KillClaim</c> goes only to the authority.</item>
/// <item><b>Game events.</b> The authority's <c>GameEvent</c>s get a server sequence and clock, are fanned out by interest (player-related
/// events to everybody) and published as <see cref="GameEventOccurred"/> (-&gt; <c>session_events</c>).</item>
/// <item><b>Chat.</b> All, Team, Whisper and Admin, with length and control-character cleanup, a rate limit, per-player mute
/// (<see cref="IChatControl"/>) and persistence into <c>chat_messages</c> and as <see cref="ChatPosted"/>.</item>
/// </list>
/// It is an <see cref="ISessionModule"/>; register it after the world mirror (<c>AddRelay()</c>). Everything runs on the actor thread.
/// <para>
/// Seam for the on-foot state (ADR-046): a later <c>OnFootState</c> message is one more case in <see cref="OnMessage"/> feeding
/// <see cref="PlayerFlow"/> (its own estimator and feed) without touching the other paths.
/// </para>
/// </summary>
public sealed partial class RelayModule : ISessionModule, ISessionActorBound, IWorldObserver, IChatControl
{
    private readonly Func<RelayOptions> _options;
    private readonly TimeProvider _time;
    private readonly IEventPublisher _events;
    private readonly IChatStore _chat;
    private readonly WorldMirror? _mirror;
    private readonly IRelayInterest? _interest;
    private readonly ILogger _logger;
    private readonly Dictionary<int, SessionNode> _nodes = [];
    private readonly Dictionary<int, PlayerFlow> _flows = [];
    private readonly List<IIntentValidator> _validators = [];
    private ISessionNodeDriver? _driver;
    private ulong _eventSeq;

    public RelayModule(
        Func<RelayOptions> options,
        TimeProvider? time = null,
        IEventPublisher? events = null,
        IChatStore? chat = null,
        WorldMirror? mirror = null,
        IRelayInterest? interest = null,
        ILogger<RelayModule>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _time = time ?? TimeProvider.System;
        _events = events ?? NullEvents.Instance;
        _chat = chat ?? NullChatStore.Instance;
        _mirror = mirror;
        _interest = interest;
        _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _mirror?.AddObserver(this);
    }

    public RelayModule(
        RelayOptions options,
        TimeProvider? time = null,
        IEventPublisher? events = null,
        IChatStore? chat = null,
        WorldMirror? mirror = null,
        IRelayInterest? interest = null)
        : this(() => options, time, events, chat, mirror, interest)
    {
    }

    private sealed class NullEvents : IEventPublisher
    {
        public static NullEvents Instance { get; } = new();

        public void Publish(DomainEvent domainEvent)
        {
        }
    }

    public RelayStats Stats { get; } = new();

    /// <summary>Extra intent gates (the permission checks of protocol.md 16.2). Add before the session starts; they run on the actor thread.</summary>
    public IList<IIntentValidator> IntentValidators => _validators;

    /// <summary>
    /// The asset permission gate (server-design 2.13). Null = no team enforcement (hosts without a Teams module, tests). Set before the
    /// session starts; <c>AddRelay()</c> sets it when an <c>ITeamDirectory</c> is registered.
    /// </summary>
    public AssetPermissionGate? AssetPermissions { get; set; }

    private RelayOptions Opt => _options();

    private long Now => _time.GetTimestamp();

    private long? SessionId => _driver?.StoreSessionId;

    /// <summary>The attached, in-game authority node (the destination of everything the relay forwards upstream).</summary>
    private SessionNode? Authority
    {
        get
        {
            foreach (var node in _nodes.Values)
            {
                if (node.IsAuthority && node.IsAttached && node.Phase == NodePhase.InGame)
                {
                    return node;
                }
            }

            return null;
        }
    }

    // ------------------------------------------------------------------ module callbacks

    public void Bind(ISessionNodeDriver driver) => _driver = driver;

    public void OnSessionBegun(long sessionId) => EnsureMutesLoaded();

    public void OnNodeAttached(SessionNode node, bool resumed)
    {
        EnsureMutesLoaded();
        _nodes[node.PlayerId] = node;
        if (resumed && _flows.TryGetValue(node.PlayerId, out var flow))
        {
            flow.Estimator.Reset(); // the sample clock restarts with the new connection
        }
    }

    public void OnNodePhaseChanged(SessionNode node, NodePhase previous, NodePhase current)
    {
        if (!node.IsAuthority)
        {
            return;
        }

        if (current == NodePhase.InGame)
        {
            ForwardHeldAvatarRequests();
        }
        else if (previous == NodePhase.InGame)
        {
            FailAllIntents(RejectReason.AuthorityUnavailable, "the authority is not in game");
        }
    }

    public void OnNodeDetached(SessionNode node, DetachReason reason)
    {
        DropPlayerWork(node.PlayerId);
        if (node.IsAuthority)
        {
            FailAllIntents(RejectReason.AuthorityUnavailable, "the authority disconnected");
        }
    }

    public void OnNodeLeft(SessionNode node, string reason)
    {
        _nodes.Remove(node.PlayerId);
        _flows.Remove(node.PlayerId);
        _chatBuckets.Remove(node.PlayerId);
        _rateHits.Remove(node.PlayerId);
        _deniedWindows.Remove(node.PlayerId);
        DropPlayerWork(node.PlayerId);
        _avatarRequests.Remove(node.PlayerId);
        if (node.IsAuthority)
        {
            FailAllIntents(RejectReason.AuthorityUnavailable, "the authority left");
        }
    }

    public void OnTick(long timestamp)
    {
        ExpireIntents(timestamp);
        FlushDueStates(timestamp);
    }

    public bool OnMessage(SessionNode node, InboundFrame frame)
    {
        try
        {
            switch (frame.Type)
            {
                case MsgType.PlayerState:
                    OnPlayerState(node, frame);
                    return true;
                case MsgType.PlayerShip:
                    OnPlayerShip(node, frame);
                    return true;
                case MsgType.Intent:
                    OnIntent(node, frame);
                    return true;
                case MsgType.IntentResult:
                    OnIntentResult(node, frame);
                    return true;
                case MsgType.GameEvent:
                    OnGameEvent(node, frame);
                    return true;
                case MsgType.ChatSend:
                    OnChatSend(node, frame);
                    return true;
                default:
                    return false;
            }
        }
        catch (ProtocolViolation violation)
        {
            node.Connection?.Stats.AddViolation();
            node.Connection?.Close(DisconnectCode.MalformedMessage, violation.Code.ToString());
            return true;
        }
    }

    // ------------------------------------------------------------------ small helpers

    private static OutboundFrame Encode<T>(MsgType type, Func<FlatBufferBuilder, Offset<T>> pack, int size = 128, ulong coalesceKey = 0)
        where T : struct =>
        ControlFrames.Encode(type, fbb => pack(fbb).Value, size, coalesceKey);

    /// <summary>Queues a frame on a node's connection (the caller keeps and releases its own reference).</summary>
    private static bool TrySend(SessionNode node, OutboundFrame frame) =>
        node.Connection?.TrySend(frame) is SendResult.Queued or SendResult.Coalesced;

    private void Publish(DomainEvent domainEvent)
    {
        try
        {
            _events.Publish(domainEvent);
        }
        catch (Exception ex)
        {
            LogPublishFailed(domainEvent.GetType().Name, ex);
        }
    }

    private static bool IsLive(SessionNode node) => node.IsAttached && node.Phase is NodePhase.CatchingUp or NodePhase.InGame;

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "relay: loading the stored mutes failed")]
    private partial void LogMutesLoadFailed(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "relay: publishing {Event} failed")]
    private partial void LogPublishFailed(string @event, Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "relay: dropped {What}: {Why}")]
    private partial void LogDropped(string what, string why);
}
