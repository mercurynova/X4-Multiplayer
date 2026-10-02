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
    /// <summary>Returns true if the frame was accepted by the datagram path (false: send it on TCP instead, e.g. it does not fit a datagram).</summary>
    bool TrySend(OutboundFrame frame);

    /// <summary>
    /// The connection's delivery callback (<see cref="INodeConnection.SetFlushObserver"/>), handed over so the path can call it per
    /// frame (<see cref="OutboundFrame.DeliveryToken"/>) once the datagram that carried it was acknowledged. May be called with null.
    /// </summary>
    void SetDeliveryObserver(Action<OutboundFrame>? observer)
    {
    }
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

    /// <summary>Removes <paramref name="path"/> (if it is the attached one): Realtime frames go over the TCP lane again.</summary>
    void DetachDatagramPath(IDatagramPath path)
    {
    }

    /// <summary>
    /// True while Realtime frames go out as UDP datagrams (a path is attached and bound). Replication then confirms deliveries one datagram at
    /// a time (acks) instead of in TCP order, and may keep several frames in flight (protocol.md 10.3).
    /// </summary>
    bool RealtimeOverDatagram => false;

    /// <summary>
    /// Registers (or clears with null) a callback invoked for every frame after the flush that carried it completed
    /// (<see cref="OutboundFrame.DeliveryToken"/> says which). It runs on the connection's writer thread, so it must be
    /// thread-safe and quick; replication only publishes a counter from it. A UDP path invokes the same callback when the datagram
    /// was acknowledged. The default does nothing (test doubles that never deliver).
    /// </summary>
    void SetFlushObserver(Action<OutboundFrame>? observer)
    {
    }

    /// <summary>
    /// Orderly close: queues a Disconnect (Control lane, bypassing caps), flushes pending Control frames
    /// (bounded by a short timeout) and closes. Idempotent. Never throws.
    /// <paramref name="expected"/>, <paramref name="retryAfterMs"/> and <paramref name="modViolation"/> fill the matching Disconnect fields.
    /// </summary>
    void Close(DisconnectCode reason, string? detail = null, string? expected = null, uint retryAfterMs = 0, ModPolicyViolationT? modViolation = null);
}

/// <summary>Accepts node connections (server-design 2.2).</summary>
public interface INodeListener : IAsyncDisposable
{
    /// <summary>"tcp", "udp" or "inproc".</summary>
    string Name { get; }

    /// <summary>Yields connections until cancelled or the listener is disposed.</summary>
    IAsyncEnumerable<INodeConnection> AcceptAsync(CancellationToken ct);
}
