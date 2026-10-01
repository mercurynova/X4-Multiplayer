using X4MP.Core.Net;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Session;

/// <summary>
/// The guarded read side of one node connection. Whoever owns the connection reads through this class so
/// that framing errors close the connection with <c>MalformedMessage</c> instead of escaping as exceptions.
/// </summary>
public sealed class NodeFrameReader(INodeConnection connection)
{
    private int _phase;
    private int _roles;

    public INodeConnection Connection { get; } = connection;

    /// <summary>Roles granted to the node (none before the handshake completes).</summary>
    public Role Roles
    {
        get => (Role)Volatile.Read(ref _roles);
        set => Volatile.Write(ref _roles, (int)value);
    }

    /// <summary>Node phase this reader currently uses for filtering.</summary>
    public NodePhase Phase
    {
        get => (NodePhase)Volatile.Read(ref _phase);
        set => Volatile.Write(ref _phase, (int)value);
    }

    /// <summary>
    /// Reads the next acceptable frame; null when the connection ended or was closed because of a bad
    /// frame. Throws <see cref="OperationCanceledException"/> if <paramref name="ct"/> fires.
    /// </summary>
    public async ValueTask<InboundFrame?> ReadAsync(CancellationToken ct)
    {
        try
        {
            return await Connection.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ProtocolViolation violation)
        {
            Connection.Stats.AddViolation();
            Connection.Close(DisconnectCode.MalformedMessage, violation.Code.ToString());
            return null;
        }
    }
}
