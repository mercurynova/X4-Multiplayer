using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;

namespace X4MP.Core.Interest;

/// <summary>
/// How the interest manager reaches nodes. The default (<see cref="NodeTransport"/>) sends on the attached nodes' connections; tests
/// supply a recording one. Sends never block.
/// </summary>
public interface IInterestTransport
{
    /// <summary>Queues a Control-lane frame for a client. The caller keeps and releases its own reference.</summary>
    SendResult Send(int playerId, OutboundFrame frame);

    /// <summary>True once the client's Control lane passed its soft cap: pause spawn producers (server-design 2.2).</summary>
    bool IsOverSoftCap(int playerId);

    /// <summary>True while an authority is attached and in game, i.e. a <c>CaptureSet</c> can be delivered.</summary>
    bool AuthorityReady { get; }

    SendResult SendToAuthority(OutboundFrame frame);
}

/// <summary>The default transport: the connections of the nodes the manager has seen attach.</summary>
internal sealed class NodeTransport(Dictionary<int, SessionNode> nodes) : IInterestTransport
{
    public SendResult Send(int playerId, OutboundFrame frame) =>
        nodes.TryGetValue(playerId, out var node) && node.Announced && node.Connection is { } connection ? connection.TrySend(frame) : SendResult.Closed;

    public bool IsOverSoftCap(int playerId) =>
        nodes.TryGetValue(playerId, out var node) && node.Connection is { } connection && connection.ControlOverSoftCap;

    public bool AuthorityReady => FindAuthority() is not null;

    public SendResult SendToAuthority(OutboundFrame frame) =>
        FindAuthority()?.Connection is { } connection ? connection.TrySend(frame) : SendResult.Closed;

    private SessionNode? FindAuthority()
    {
        foreach (var node in nodes.Values)
        {
            if (node.IsAuthority && node.IsAttached && node.Announced && node.Phase == NodePhase.InGame) // Announced: a resumed connection gets its Welcome first
            {
                return node;
            }
        }

        return null;
    }
}

/// <summary>Why the manager took a ghost out of a client's held set.</summary>
public enum GhostRemoval
{
    /// <summary>An <c>EntityDespawn</c> was sent (left interest, destroyed, evicted, budget cut).</summary>
    Despawned = 0,

    /// <summary>A resync is about to deliver it again: no despawn was sent and a spawn follows.</summary>
    Resync = 1,
}

/// <summary>
/// Watches the per-client held (ghost) sets of the <see cref="InterestManager"/>, on the actor thread, at the moment a spawn is
/// queued or a despawn is sent. Replication keeps its per-client baselines in step with it (protocol.md 10.1: spawn before
/// state, tombstone on despawn).
/// </summary>
public interface IGhostObserver
{
    /// <summary>An <c>EntitySpawn</c> carrying <paramref name="entity"/> was queued to the client (it now holds the ghost).</summary>
    void OnGhostAdded(int playerId, X4MP.Core.World.MirrorEntity entity);

    /// <summary>The client no longer holds the ghost.</summary>
    void OnGhostRemoved(int playerId, uint netId, GhostRemoval reason);

    /// <summary>The client's held set was dropped wholesale (client restarted, world cleared).</summary>
    void OnGhostsReset(int playerId);
}
