using System.Net;
using X4MP.Core.Net;
using X4MP.Proto;

namespace X4MP.Core.Session;

/// <summary>
/// A node that passed the handshake. Created by <see cref="NodeGateway"/>, then owned by the
/// <see cref="IAdmissionHandler"/> (the SessionActor in M1-05), which drives <see cref="Phase"/>.
/// </summary>
public sealed class AdmittedNode
{
    private int _phase = (int)NodePhase.Admitted;

    public required INodeConnection Connection { get; init; }

    /// <summary>Reads policy-checked frames; counts violations and closes the connection past the threshold.</summary>
    public required NodeFrameReader Reader { get; init; }

    /// <summary>Persistent player id (<c>players.id</c>), as sent in <c>Welcome.player_id</c>.</summary>
    public required int PlayerId { get; init; }

    public required string Name { get; init; }

    /// <summary>SHA-256 of the 32-byte player key. The key itself is never kept.</summary>
    public required byte[] KeyHash { get; init; }

    /// <summary>Roles granted by the gateway (Admin only with a valid <c>admin_proof</c>).</summary>
    public required Role Roles { get; init; }

    public required ClientHelloT Hello { get; init; }

    /// <summary>Lower of both peers' protocol minor versions.</summary>
    public required ushort NegotiatedMinor { get; init; }

    public required ulong NegotiatedCaps { get; init; }

    public required IPAddress RemoteAddress { get; init; }

    /// <summary>The Welcome that will be sent. <see cref="IAdmissionHandler.BeforeWelcomeAsync"/> may adjust it.</summary>
    public required WelcomeT Welcome { get; init; }

    /// <summary>The per-connection nonce from <c>ServerHello</c> (the team password proof uses it, protocol.md 4.3). Empty when unknown.</summary>
    public byte[] Nonce { get; init; } = [];

    public bool IsAdmin => (Roles & Role.Admin) != 0;

    public bool IsAuthority => (Roles & Role.Authority) != 0;

    /// <summary>Current node phase. Setting it also updates what <see cref="Reader"/> lets through.</summary>
    public NodePhase Phase
    {
        get => (NodePhase)Volatile.Read(ref _phase);
        set
        {
            Volatile.Write(ref _phase, (int)value);
            Reader.Phase = value;
        }
    }
}

/// <summary>Decision of <see cref="IAdmissionHandler.BeforeWelcomeAsync"/>.</summary>
public readonly record struct AdmissionVerdict(DisconnectCode Code, string? Message = null, string? Expected = null)
{
    public static AdmissionVerdict Accept { get; } = new(DisconnectCode.None);

    public bool Accepted => Code == DisconnectCode.None;
}

/// <summary>The hand-off from the gateway to the session layer.</summary>
public interface IAdmissionHandler
{
    /// <summary>
    /// Called after every handshake check passed and before <c>Welcome</c> is sent. May adjust
    /// <see cref="AdmittedNode.Welcome"/> (resume, team, settings) or refuse (for example session not
    /// joinable). Nothing may be queued on the connection yet.
    /// </summary>
    ValueTask<AdmissionVerdict> BeforeWelcomeAsync(AdmittedNode node, CancellationToken ct);

    /// <summary>
    /// Called after <c>Welcome</c> was queued. The handler now owns the reading side of
    /// <see cref="AdmittedNode.Reader"/>. The gateway does not touch the node again; it may return as soon
    /// as the node is registered (the connection lives on).
    /// </summary>
    Task OnAdmittedAsync(AdmittedNode node, CancellationToken ct);
}
