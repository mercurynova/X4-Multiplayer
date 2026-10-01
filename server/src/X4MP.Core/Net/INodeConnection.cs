using System.Net;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Net;

/// <summary>
/// Second path for the Realtime lane (UDP, later milestone). When attached, Realtime frames prefer it.
/// Same contract as <see cref="INodeConnection.TrySend"/>: never blocks.
/// </summary>
public interface IDatagramPath
{
    /// <summary>Returns true if the frame was accepted by the datagram path.</summary>
    bool TrySend(OutboundFrame frame);
}

/// <summary>One node connection (server-design 2.2). Transport-agnostic: TCP, InProc, and later UDP-assisted.</summary>
public interface INodeConnection : IAsyncDisposable
{
    /// <summary>Monotonically increasing id.</summary>
    ConnectionId Id { get; }

    EndPoint RemoteEndPoint { get; }

    /// <summary>Interlocked counters (server-design 2.11).</summary>
    ConnectionStats Stats { get; }

    /// <summary>Cancelled as soon as the connection starts closing.</summary>
    CancellationToken Closed { get; }

    /// <summary>Completes when the connection is fully closed and its resources are released.</summary>
    Task Completion { get; }

    /// <summary>Largest accepted inbound payload. The gateway lowers it during the handshake. Validated before allocating.</summary>
    int MaxInboundFrameBytes { get; set; }

    /// <summary>
    /// Reads one frame; returns null on orderly close or when the connection is closed locally. Only the
    /// reader loop calls this. Throws <see cref="ProtocolViolation"/> for a structurally bad
    /// frame (the stream is then unusable) and <see cref="OperationCanceledException"/> when
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    ValueTask<InboundFrame?> ReadAsync(CancellationToken ct);

    /// <summary>
    /// NEVER blocks and NEVER throws for flow-control reasons. The connection takes its own reference to
    /// <paramref name="frame"/> when it queues it, so the caller keeps (and releases) its own reference:
    /// encode once, TrySend to N connections, release once.
    /// </summary>
    SendResult TrySend(OutboundFrame frame);

    /// <summary>Pull-model hint for realtime producers: true while the Realtime lane is below its low watermark.</summary>
    bool CanAcceptRealtime { get; }

    /// <summary>True once the Control lane passed its soft cap: pause spawn and catch-up producers.</summary>
    bool ControlOverSoftCap { get; }

    /// <summary>Bytes currently queued on a lane.</summary>
    long QueuedBytes(Lane lane);

    /// <summary>Optional second path (UDP) bound after the handshake; the Realtime lane prefers it when present.</summary>
    void AttachDatagramPath(IDatagramPath path);

    /// <summary>
    /// Orderly close: queues a Disconnect (Control lane, bypassing caps), flushes pending Control frames
    /// (bounded by a short timeout) and closes. Idempotent. Never throws.
    /// <paramref name="expected"/> and <paramref name="retryAfterMs"/> fill the matching Disconnect fields.
    /// </summary>
    void Close(DisconnectCode reason, string? detail = null, string? expected = null, uint retryAfterMs = 0);
}

/// <summary>Accepts node connections (server-design 2.2).</summary>
public interface INodeListener : IAsyncDisposable
{
    /// <summary>"tcp", "udp" or "inproc".</summary>
    string Name { get; }

    /// <summary>Yields connections until cancelled or the listener is disposed.</summary>
    IAsyncEnumerable<INodeConnection> AcceptAsync(CancellationToken ct);
}
