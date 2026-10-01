using X4MP.Proto;

namespace X4MP.Core.Session;

/// <summary>Inputs of the session phase machine (architecture 4.2). The edge table is <see cref="SessionTransitions.Edges"/>.</summary>
public enum SessionTrigger
{
    /// <summary>Admin: select save and start; also implied by the first authority connecting. Idle to WaitingForAuthority.</summary>
    Start,

    /// <summary>The authority was admitted. WaitingForAuthority to AuthorityLoading.</summary>
    AuthorityAdmitted,

    /// <summary><c>GalaxyMetadata</c> and the first checkpoint are stored (M1-12). AuthorityLoading to Running.</summary>
    CheckpointStored,

    /// <summary>Admin pause. Running to Paused.</summary>
    Pause,

    /// <summary>Admin resume. Paused to Running.</summary>
    Resume,

    /// <summary>The authority's socket was lost (or it left). Running or Paused to AuthorityLost.</summary>
    AuthorityLost,

    /// <summary>The authority resumed inside the grace and the session had been Running. AuthorityLost to Running.</summary>
    AuthorityResumed,

    /// <summary>The authority resumed inside the grace and the session had been Paused. AuthorityLost to Paused.</summary>
    AuthorityResumedPaused,

    /// <summary>The designated authority rejoined without a resume token (it reloads). AuthorityLost to AuthorityLoading.</summary>
    AuthorityReadmitted,

    /// <summary>The authority never finished loading and is gone for good. AuthorityLoading to WaitingForAuthority.</summary>
    AuthorityLoadFailed,

    /// <summary><c>AuthorityGraceSeconds</c> ran out. AuthorityLost to Stopping.</summary>
    GraceExpired,

    /// <summary>Admin stop. Any live phase to Stopping.</summary>
    Stop,

    /// <summary>The final save finished, or the stop timeout passed. Stopping to Ended.</summary>
    StopCompleted,

    /// <summary>Admin migrate (later milestone; the edge exists, no producer yet). AuthorityLost to Migrating.</summary>
    Migrate,

    /// <summary>Migration done (later milestone). Migrating to Running.</summary>
    MigrationCompleted,

    /// <summary>Admin: start over after the session ended. Ended to Idle.</summary>
    Reset,
}

/// <summary>The <see cref="SessionPhase"/> edge table (architecture 4.2, server-design 2.4).</summary>
public static class SessionTransitions
{
    private static readonly (SessionPhase From, SessionTrigger Trigger, SessionPhase To)[] s_edges =
    [
        (SessionPhase.Idle, SessionTrigger.Start, SessionPhase.WaitingForAuthority),
        (SessionPhase.WaitingForAuthority, SessionTrigger.AuthorityAdmitted, SessionPhase.AuthorityLoading),
        (SessionPhase.WaitingForAuthority, SessionTrigger.Stop, SessionPhase.Stopping),
        (SessionPhase.AuthorityLoading, SessionTrigger.CheckpointStored, SessionPhase.Running),
        (SessionPhase.AuthorityLoading, SessionTrigger.AuthorityLoadFailed, SessionPhase.WaitingForAuthority),
        (SessionPhase.AuthorityLoading, SessionTrigger.Stop, SessionPhase.Stopping),
        (SessionPhase.Running, SessionTrigger.Pause, SessionPhase.Paused),
        (SessionPhase.Running, SessionTrigger.AuthorityLost, SessionPhase.AuthorityLost),
        (SessionPhase.Running, SessionTrigger.Stop, SessionPhase.Stopping),
        (SessionPhase.Paused, SessionTrigger.Resume, SessionPhase.Running),
        (SessionPhase.Paused, SessionTrigger.AuthorityLost, SessionPhase.AuthorityLost),
        (SessionPhase.Paused, SessionTrigger.Stop, SessionPhase.Stopping),
        (SessionPhase.AuthorityLost, SessionTrigger.AuthorityResumed, SessionPhase.Running),
        (SessionPhase.AuthorityLost, SessionTrigger.AuthorityResumedPaused, SessionPhase.Paused),
        (SessionPhase.AuthorityLost, SessionTrigger.AuthorityReadmitted, SessionPhase.AuthorityLoading),
        (SessionPhase.AuthorityLost, SessionTrigger.GraceExpired, SessionPhase.Stopping),
        (SessionPhase.AuthorityLost, SessionTrigger.Stop, SessionPhase.Stopping),
        (SessionPhase.AuthorityLost, SessionTrigger.Migrate, SessionPhase.Migrating),
        (SessionPhase.Migrating, SessionTrigger.MigrationCompleted, SessionPhase.Running),
        (SessionPhase.Migrating, SessionTrigger.Stop, SessionPhase.Stopping),
        (SessionPhase.Stopping, SessionTrigger.StopCompleted, SessionPhase.Ended),
        (SessionPhase.Ended, SessionTrigger.Reset, SessionPhase.Idle),
    ];

    /// <summary>Every legal edge. Anything not listed is illegal.</summary>
    public static IReadOnlyList<(SessionPhase From, SessionTrigger Trigger, SessionPhase To)> Edges => s_edges;

    /// <summary>Triggers an admin (or the save service) may raise through the actor; the rest are raised by the actor itself.</summary>
    public static bool IsExternal(SessionTrigger trigger) => trigger is
        SessionTrigger.Start or SessionTrigger.CheckpointStored or SessionTrigger.Pause or SessionTrigger.Resume
        or SessionTrigger.Stop or SessionTrigger.StopCompleted or SessionTrigger.Migrate
        or SessionTrigger.MigrationCompleted or SessionTrigger.Reset;

    public static bool TryNext(SessionPhase from, SessionTrigger trigger, out SessionPhase to)
    {
        foreach (var edge in s_edges)
        {
            if (edge.From == from && edge.Trigger == trigger)
            {
                to = edge.To;
                return true;
            }
        }

        to = from;
        return false;
    }
}

/// <summary>Inputs of the per-node phase machine (architecture 4.3).</summary>
public enum NodeTrigger
{
    /// <summary>Server: the node must pick a team first. Admitted to AwaitingTeam.</summary>
    RequireTeam,

    /// <summary>Server: team known (or none needed). Admitted or AwaitingTeam to SyncingSave.</summary>
    TeamAssigned,

    /// <summary>Node reported <c>LoadStatus{SyncingSave}</c>.</summary>
    ReportSyncing,

    /// <summary>Node reported <c>LoadStatus{Verifying}</c>.</summary>
    ReportVerifying,

    /// <summary>Node reported <c>LoadStatus{Loading}</c>.</summary>
    ReportLoading,

    /// <summary>Node reported <c>LoadStatus{Matching}</c>.</summary>
    ReportMatching,

    /// <summary>Node reported <c>LoadStatus{CatchingUp}</c>.</summary>
    ReportCatchingUp,

    /// <summary>Node sent <c>NodeReady</c>. Matching or CatchingUp to InGame.</summary>
    Ready,

    /// <summary>Server: an observer needs no save. Admitted to InGame.</summary>
    ObserverReady,

    /// <summary>The node reported <c>LoadStatus{Failed}</c>, or the server rejected it. Any attached phase to Failed.</summary>
    Fail,

    /// <summary>Socket lost or heartbeat timeout. Attached phase to Detached.</summary>
    SocketLost,

    /// <summary><c>Disconnect{ClientReload}</c>. Same edge as <see cref="SocketLost"/>; the session emits no leave or join.</summary>
    ClientReload,

    /// <summary>Valid resume token inside the grace. Detached to the phase it left.</summary>
    Resume,

    /// <summary><c>ResumeGraceSeconds</c> ran out. Detached to left.</summary>
    GraceExpired,

    /// <summary>Orderly quit, kick or session end. Any phase to left.</summary>
    Quit,
}

public enum NodeStepKind
{
    /// <summary>The trigger is not legal in this phase.</summary>
    Invalid,

    /// <summary>Legal, but the phase stays (progress update, duplicate event).</summary>
    Stay,

    /// <summary>Legal; the phase changes to <see cref="NodeStep.Phase"/>.</summary>
    Move,

    /// <summary>The node leaves the session.</summary>
    Leave,
}

public readonly record struct NodeStep(NodeStepKind Kind, NodePhase Phase)
{
    public static NodeStep Invalid { get; } = new(NodeStepKind.Invalid, default);

    public static NodeStep Stay(NodePhase phase) => new(NodeStepKind.Stay, phase);

    public static NodeStep Move(NodePhase phase) => new(NodeStepKind.Move, phase);

    public static NodeStep Leave { get; } = new(NodeStepKind.Leave, NodePhase.Detached);

    public bool Legal => Kind != NodeStepKind.Invalid;
}

/// <summary>The <see cref="NodePhase"/> machine as a pure function (architecture 4.3).</summary>
public static class NodeTransitions
{
    /// <summary>Forward order of the join pipeline; <c>-1</c> for Detached and Failed.</summary>
    private static int Rank(NodePhase phase) => phase switch
    {
        NodePhase.Admitted => 0,
        NodePhase.AwaitingTeam => 1,
        NodePhase.SyncingSave => 2,
        NodePhase.Verifying => 3,
        NodePhase.Loading => 4,
        NodePhase.Matching => 5,
        NodePhase.CatchingUp => 6,
        NodePhase.InGame => 7,
        _ => -1,
    };

    /// <summary>True for every phase of a node with a live socket (Admitted to InGame).</summary>
    public static bool IsAttachedPhase(NodePhase phase) => Rank(phase) >= 0;

    private static NodePhase ReportTarget(NodeTrigger trigger) => trigger switch
    {
        NodeTrigger.ReportSyncing => NodePhase.SyncingSave,
        NodeTrigger.ReportVerifying => NodePhase.Verifying,
        NodeTrigger.ReportLoading => NodePhase.Loading,
        NodeTrigger.ReportMatching => NodePhase.Matching,
        _ => NodePhase.CatchingUp,
    };

    /// <summary>
    /// Applies <paramref name="trigger"/> in phase <paramref name="from"/>. <paramref name="resumeTo"/> is the
    /// phase the node had before it detached (used only by <see cref="NodeTrigger.Resume"/>).
    /// </summary>
    public static NodeStep Apply(NodePhase from, NodeTrigger trigger, NodePhase resumeTo = NodePhase.Admitted)
    {
        int rank = Rank(from);
        switch (trigger)
        {
            case NodeTrigger.RequireTeam:
                return from == NodePhase.Admitted ? NodeStep.Move(NodePhase.AwaitingTeam) : NodeStep.Invalid;

            case NodeTrigger.TeamAssigned:
                return from is NodePhase.Admitted or NodePhase.AwaitingTeam ? NodeStep.Move(NodePhase.SyncingSave) : NodeStep.Invalid;

            case NodeTrigger.ReportSyncing:
            case NodeTrigger.ReportVerifying:
            case NodeTrigger.ReportLoading:
            case NodeTrigger.ReportMatching:
            case NodeTrigger.ReportCatchingUp:
                {
                    // A node waiting for a team may not skip the team step; InGame never goes backwards by report.
                    if (rank < 0 || from == NodePhase.AwaitingTeam)
                    {
                        return NodeStep.Invalid;
                    }

                    var target = ReportTarget(trigger);
                    int targetRank = Rank(target);
                    return rank < targetRank ? NodeStep.Move(target) : rank == targetRank ? NodeStep.Stay(from) : NodeStep.Invalid;
                }

            case NodeTrigger.Ready:
                return from switch
                {
                    NodePhase.Matching or NodePhase.CatchingUp => NodeStep.Move(NodePhase.InGame),
                    NodePhase.InGame => NodeStep.Stay(from),
                    _ => NodeStep.Invalid,
                };

            case NodeTrigger.ObserverReady:
                return from == NodePhase.Admitted ? NodeStep.Move(NodePhase.InGame) : NodeStep.Invalid;

            case NodeTrigger.Fail:
                return rank >= 0 ? NodeStep.Move(NodePhase.Failed) : from == NodePhase.Failed ? NodeStep.Stay(from) : NodeStep.Invalid;

            case NodeTrigger.SocketLost:
            case NodeTrigger.ClientReload:
                return rank >= 0 ? NodeStep.Move(NodePhase.Detached)
                    : from == NodePhase.Detached ? NodeStep.Stay(from)
                    : NodeStep.Leave; // a Failed node is not kept

            case NodeTrigger.Resume:
                return from == NodePhase.Detached ? NodeStep.Move(Rank(resumeTo) >= 0 ? resumeTo : NodePhase.Admitted) : NodeStep.Invalid;

            case NodeTrigger.GraceExpired:
                return from == NodePhase.Detached ? NodeStep.Leave : NodeStep.Invalid;

            case NodeTrigger.Quit:
                return NodeStep.Leave;

            default:
                return NodeStep.Invalid;
        }
    }
}
