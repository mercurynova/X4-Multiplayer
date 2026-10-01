using X4MP.Core.Net;
using X4MP.Core.Session;

namespace X4MP.Core.Replication;

/// <summary>
/// How replication reaches nodes. The default (<see cref="NodeReplicationTransport"/>) uses the attached nodes' connections; tests supply a
/// recording one that can drop frames and confirm deliveries by hand. This is also the seam for the UDP realtime lane (M1-09): a
/// transport that sends <c>Replication</c> as datagrams reports acks through the same delivery observer.
/// </summary>
public interface IReplicationTransport
{
    /// <summary>The pull-model hint: true while the client's Realtime lane is below its low watermark.</summary>
    bool CanAcceptRealtime(int playerId);

    /// <summary>Queues a <c>Replication</c> frame (Realtime lane). Never blocks; the caller keeps and releases its own reference.</summary>
    SendResult SendRealtime(int playerId, OutboundFrame frame);

    /// <summary>Queues a Control-lane frame (<c>InterestChecksum</c>). The caller keeps and releases its own reference.</summary>
    SendResult SendControl(int playerId, OutboundFrame frame);

    /// <summary>
    /// Registers (or clears with null) the callback that is told which frames were delivered: <see cref="OutboundFrame.DeliveryToken"/> of each
    /// frame sent with <see cref="SendRealtime"/>. For TCP the delivery point is the writer's flush (protocol.md 10.3); a UDP path reports
    /// when the ack arrives. The callback is thread-safe and may run on any thread. <paramref name="connection"/> is the connection the
    /// observer belongs to, so a stale registration is never cleared on a newer connection.
    /// </summary>
    void SetDeliveryObserver(int playerId, INodeConnection? connection, Action<OutboundFrame>? observer);
}

/// <summary>The default transport: the connections of the nodes the module has seen attach.</summary>
internal sealed class NodeReplicationTransport(Dictionary<int, SessionNode> nodes) : IReplicationTransport
{
    public bool CanAcceptRealtime(int playerId) =>
        nodes.TryGetValue(playerId, out var node) && node.Connection is { } connection && connection.CanAcceptRealtime;

    public SendResult SendRealtime(int playerId, OutboundFrame frame) =>
        nodes.TryGetValue(playerId, out var node) && node.Connection is { } connection ? connection.TrySend(frame) : SendResult.Closed;

    public SendResult SendControl(int playerId, OutboundFrame frame) =>
        nodes.TryGetValue(playerId, out var node) && node.Connection is { } connection ? connection.TrySend(frame) : SendResult.Closed;

    public void SetDeliveryObserver(int playerId, INodeConnection? connection, Action<OutboundFrame>? observer)
    {
        if (connection is null)
        {
            return;
        }

        connection.SetFlushObserver(observer);
    }
}
