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
        nodes.TryGetValue(playerId, out var node) && node.Connection is { } connection ? connection.TrySend(frame) : SendResult.Closed;

    public bool IsOverSoftCap(int playerId) =>
        nodes.TryGetValue(playerId, out var node) && node.Connection is { } connection && connection.ControlOverSoftCap;

    public bool AuthorityReady => FindAuthority() is not null;

    public SendResult SendToAuthority(OutboundFrame frame) =>
        FindAuthority()?.Connection is { } connection ? connection.TrySend(frame) : SendResult.Closed;

    private SessionNode? FindAuthority()
    {
        foreach (var node in nodes.Values)
        {
            if (node.IsAuthority && node.IsAttached && node.Phase == NodePhase.InGame)
            {
                return node;
            }
        }

        return null;
    }
}
