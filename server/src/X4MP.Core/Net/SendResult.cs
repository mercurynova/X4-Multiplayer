namespace X4MP.Core.Net;

/// <summary>Outcome of <see cref="INodeConnection.TrySend"/>. Sending never throws for flow-control reasons.</summary>
public enum SendResult
{
    /// <summary>Accepted and queued behind earlier frames of its lane.</summary>
    Queued,

    /// <summary>Accepted; replaced a still-pending Realtime frame with the same coalesce key (latest wins).</summary>
    Coalesced,

    /// <summary>Not queued: the lane is above its high watermark (Realtime) or cap (Bulk). The connection stays open.</summary>
    DroppedLane,

    /// <summary>Not queued: the Control lane hit its hard cap or its oldest frame is older than the slow-consumer timeout. The connection is being closed with SlowConsumer.</summary>
    ClosedOverflow,

    /// <summary>Not queued: the connection is closed or closing.</summary>
    Closed,
}
