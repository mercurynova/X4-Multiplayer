using Microsoft.Extensions.Logging;
using X4MP.Core.Net;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Session;

/// <summary>
/// The guarded read side of one node connection. Whoever owns the connection reads through this class:
/// every frame header is checked against <see cref="MessagePolicy"/> (role x phase x lane) before the caller
/// sees it. Violating frames are dropped and counted; past the limit (20 per minute by default) the
/// connection is closed with <c>TooManyViolations</c> and the address gets a temporary ban. Structurally
/// broken streams close with <c>MalformedMessage</c>. Before the handshake completes, any violation closes
/// immediately.
/// </summary>
public sealed partial class NodeFrameReader
{
    private readonly NetOptions _options;
    private readonly TimeProvider _time;
    private readonly IBanStore? _bans;
    private readonly ILogger _logger;
    private readonly ViolationTracker _violations;
    private int _policyPhase = (int)PolicyPhase.Handshaking;
    private int _nodePhase = (int)NodePhase.Admitted;
    private int _roles;
    private int _peerMinor = ProtocolConstants.ProtocolMinor;

    public NodeFrameReader(
        INodeConnection connection,
        NetOptions? options = null,
        TimeProvider? time = null,
        IBanStore? bans = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Connection = connection;
        _options = options ?? new NetOptions();
        _time = time ?? TimeProvider.System;
        _bans = bans;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _violations = new ViolationTracker(_options.ViolationLimitPerMinute, _time);
    }

    public INodeConnection Connection { get; }

    /// <summary>Roles granted to the node (none before the handshake completes).</summary>
    public Role Roles
    {
        get => (Role)Volatile.Read(ref _roles);
        set => Volatile.Write(ref _roles, (int)value);
    }

    /// <summary>
    /// Node phase used for filtering. Setting it ends the handshake state. Starts as
    /// <see cref="PolicyPhase.Handshaking"/>.
    /// </summary>
    public NodePhase Phase
    {
        get => (NodePhase)Volatile.Read(ref _nodePhase);
        set
        {
            Volatile.Write(ref _nodePhase, (int)value);
            Volatile.Write(ref _policyPhase, (int)MessagePolicy.ToPolicyPhase(value));
        }
    }

    /// <summary>The raw policy phase (includes <see cref="PolicyPhase.Handshaking"/>).</summary>
    public PolicyPhase PolicyPhase => (PolicyPhase)Volatile.Read(ref _policyPhase);

    /// <summary>The minor version the peer announced; a higher one than ours makes unknown types skippable.</summary>
    public ushort PeerMinor
    {
        get => (ushort)Volatile.Read(ref _peerMinor);
        set => Volatile.Write(ref _peerMinor, value);
    }

    /// <summary>Violations counted in the last minute.</summary>
    public int ViolationCount => _violations.Count;

    /// <summary>
    /// Reads the next policy-allowed frame; null when the connection ended or was closed because of
    /// violations. Throws <see cref="OperationCanceledException"/> if <paramref name="ct"/> fires.
    /// </summary>
    public async ValueTask<InboundFrame?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            InboundFrame? read;
            try
            {
                read = await Connection.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (ProtocolViolation violation)
            {
                // The byte stream can no longer be framed: nothing to resynchronise on.
                Count();
                LogMalformed(Connection.Id.Value, violation.Code);
                Connection.Close(DisconnectCode.MalformedMessage, violation.Code.ToString());
                return null;
            }

            if (read is not { } inbound)
            {
                return null;
            }

            var verdict = MessagePolicy.Evaluate(inbound.Type, inbound.Frame.Lane, Roles, PolicyPhase, PeerMinor);
            switch (verdict)
            {
                case PolicyVerdict.Allowed:
                    return inbound;

                case PolicyVerdict.SkipUnknown:
                    LogSkipped(Connection.Id.Value, (ushort)inbound.Type);
                    continue;
            }

            int count = Count();
            LogViolation(Connection.Id.Value, inbound.Type, verdict, count);

            if (PolicyPhase == PolicyPhase.Handshaking)
            {
                Connection.Close(MessagePolicy.CodeFor(verdict), $"{inbound.Type}: {verdict}");
                return null;
            }

            if (count > _options.ViolationLimitPerMinute)
            {
                await BanAsync(ct).ConfigureAwait(false);
                Connection.Close(DisconnectCode.TooManyViolations, "too many protocol violations", retryAfterMs: 0);
                return null;
            }
        }
    }

    private int Count()
    {
        Connection.Stats.AddViolation();
        return _violations.Record();
    }

    private async Task BanAsync(CancellationToken ct)
    {
        var address = NetAddress.Normalize(Connection.RemoteEndPoint);
        if (_bans is null || address is null)
        {
            return;
        }

        try
        {
            var now = _time.GetUtcNow();
            await _bans.AddIpBanAsync(address, "TooManyViolations", "system", now, now.AddMinutes(_options.TempBanMinutes), ct).ConfigureAwait(false);
            LogBanned(address.ToString(), _options.TempBanMinutes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogBanFailed(address.ToString(), ex);
        }
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "conn {ConnectionId}: unframeable stream ({Code})")]
    private partial void LogMalformed(long connectionId, ViolationCode code);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "conn {ConnectionId}: skipped unknown message type 0x{Type:X4} from a newer peer")]
    private partial void LogSkipped(long connectionId, ushort type);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "conn {ConnectionId}: policy violation {Type} ({Verdict}); {Count} in the last minute")]
    private partial void LogViolation(long connectionId, MsgType type, PolicyVerdict verdict, int count);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "{Address} banned for {Minutes} min after too many violations")]
    private partial void LogBanned(string address, int minutes);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "could not record the temporary ban for {Address}")]
    private partial void LogBanFailed(string address, Exception ex);
}
