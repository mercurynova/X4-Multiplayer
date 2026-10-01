using System.Net;
using X4MP.Core.Net;
using X4MP.Proto;

namespace X4MP.Core.Session;

/// <summary>Latest <c>NodeStats</c> a node reported (every 2 s, protocol.md 22).</summary>
public sealed record NodeStatsSnapshot(
    float Fps,
    float FrameMsP95,
    double GameTime,
    uint Ghosts,
    uint SuppressedLocal,
    uint PendingMainThreadJobs,
    uint TcpSendQueueBytes,
    float UdpRxLossPct,
    bool UdpActive,
    uint RxBytesPerS,
    uint TxBytesPerS,
    ushort InterpDelayMs,
    long ClockOffsetUs,
    float RttMs,
    uint MemoryMb,
    FeatureState MdHookState,
    FeatureState TeamSetupState,
    DateTimeOffset ReceivedAt);

/// <summary>
/// One player's slot in the session. Created at admission, kept while the node is attached or detached inside
/// the resume grace, removed when it leaves. Mutated only on the actor thread: modules must treat it as owned by
/// the actor and never touch it from elsewhere (read <see cref="SessionSnapshot"/> instead).
/// </summary>
public sealed class SessionNode
{
    internal SessionNode(int playerId, string name, byte[] keyHash, Role roles, DateTimeOffset joinedAt)
    {
        PlayerId = playerId;
        Name = name;
        KeyHash = keyHash;
        Roles = roles;
        JoinedAt = joinedAt;
    }

    public int PlayerId { get; }

    public string Name { get; internal set; }

    /// <summary>SHA-256 of the player key.</summary>
    public byte[] KeyHash { get; }

    public Role Roles { get; internal set; }

    public bool IsAuthority => (Roles & Role.Authority) != 0;

    public NodePhase Phase { get; internal set; } = NodePhase.Admitted;

    /// <summary>The player's team (0 = none yet), kept by the team module so the roster can carry it.</summary>
    public int TeamId { get; internal set; }

    public TeamRole TeamRole { get; internal set; }

    /// <summary>The phase the node left when it detached; a resume goes back to it.</summary>
    public NodePhase PhaseBeforeDetach { get; internal set; } = NodePhase.Admitted;

    /// <summary>The live connection; null while <see cref="NodePhase.Detached"/>.</summary>
    public AdmittedNode? Attached { get; internal set; }

    public INodeConnection? Connection => Attached?.Connection;

    public bool IsAttached => Attached is not null;

    public IPAddress? RemoteAddress { get; internal set; }

    /// <summary>The 128-bit token that lets this player resume (stable for the life of the slot).</summary>
    public Id128T ResumeToken { get; internal set; } = new();

    public DateTimeOffset JoinedAt { get; }

    public DateTimeOffset? DetachedAt { get; internal set; }

    public DetachReason? DetachReason { get; internal set; }

    /// <summary>Starts at 0 and goes up on every resume. A replication module resets its baselines when it sees a new value.</summary>
    public int BaselineEpoch { get; internal set; }

    /// <summary>The last journal sequence the node said it applied (<c>ClientHello.last_journal_seq</c>) on its latest (re)connect.</summary>
    public ulong LastJournalSeq { get; internal set; }

    /// <summary>Universe epoch and loaded save from the last <c>NodeReady</c>.</summary>
    public ulong UniverseEpoch { get; internal set; }

    public NodeStatsSnapshot? Stats { get; internal set; }

    public ClockSync Clock { get; internal set; } = new();

    /// <summary>True after the actor sent the roster and session state following <c>Welcome</c>.</summary>
    internal bool Announced { get; set; }

    internal long DetachDeadline { get; set; } = long.MaxValue;

    internal long LastInboundTicks { get; set; }

    internal long NextPingTicks { get; set; }

    internal uint PingSeq { get; set; }

    internal long PingsSent { get; set; }

    internal long PongsReceived { get; set; }
}

/// <summary>Immutable read model of one slot, for the admin API.</summary>
public sealed record NodeSnapshot(
    int PlayerId,
    string Name,
    Role Roles,
    NodePhase Phase,
    bool Connected,
    long? ConnectionId,
    string? RemoteAddress,
    double RttMs,
    double MinRttMs,
    long ClockOffsetUs,
    int ClockSamples,
    NodeStatsSnapshot? Stats,
    DateTimeOffset JoinedAt,
    DateTimeOffset? DetachedAt,
    DetachReason? DetachReason,
    double? ResumeRemainingSeconds,
    int BaselineEpoch);

public enum AuthorityStatus
{
    /// <summary>No authority yet.</summary>
    None,

    /// <summary>Connected.</summary>
    Live,

    /// <summary>Lost; inside the grace window (<see cref="AuthoritySnapshot.GraceRemainingSeconds"/>).</summary>
    Lost,
}

/// <summary>Authority part of <see cref="SessionSnapshot"/>.</summary>
public sealed record AuthoritySnapshot(
    AuthorityStatus Status,
    int PlayerId,
    string? Name,
    double? GraceRemainingSeconds,
    string? GameBuild,
    string? ModVersion);

/// <summary>
/// Everything the admin side may read about the session, immutable, published by the actor about every
/// <see cref="SessionActorOptions.TickIntervalMs"/> (server-design 2.1 point 3).
/// </summary>
public sealed record SessionSnapshot(
    long Version,
    DateTimeOffset TakenAt,
    Guid SessionId,
    string SessionName,
    SessionPhase Phase,
    DateTimeOffset PhaseSince,
    AuthoritySnapshot Authority,
    IReadOnlyList<NodeSnapshot> Nodes)
{
    public static SessionSnapshot Empty { get; } = new(
        0, DateTimeOffset.MinValue, Guid.Empty, string.Empty, SessionPhase.Idle, DateTimeOffset.MinValue,
        new AuthoritySnapshot(AuthorityStatus.None, 0, null, null, null, null), []);
}

/// <summary>Result of an admin command or a server-driven node trigger.</summary>
public readonly record struct TransitionResult(bool Applied, string From, string To, string? Error)
{
    public static TransitionResult Failed(string from, string error) => new(false, from, from, error);
}
