using X4MP.Core.Session;
using X4MP.Proto;

namespace X4MP.Core.Tests.Session;

/// <summary>
/// The phase machines against an independent copy of architecture 4.2 / 4.3: every (phase, trigger) pair is
/// either one of the listed edges or illegal.
/// </summary>
public class TransitionTableTests
{
    private static readonly Dictionary<(SessionPhase, SessionTrigger), SessionPhase> ExpectedSession = new()
    {
        [(SessionPhase.Idle, SessionTrigger.Start)] = SessionPhase.WaitingForAuthority,
        [(SessionPhase.WaitingForAuthority, SessionTrigger.AuthorityAdmitted)] = SessionPhase.AuthorityLoading,
        [(SessionPhase.AuthorityLoading, SessionTrigger.CheckpointStored)] = SessionPhase.Running,
        [(SessionPhase.AuthorityLoading, SessionTrigger.AuthorityLoadFailed)] = SessionPhase.WaitingForAuthority,
        [(SessionPhase.Running, SessionTrigger.Pause)] = SessionPhase.Paused,
        [(SessionPhase.Paused, SessionTrigger.Resume)] = SessionPhase.Running,
        [(SessionPhase.Running, SessionTrigger.AuthorityLost)] = SessionPhase.AuthorityLost,
        [(SessionPhase.Paused, SessionTrigger.AuthorityLost)] = SessionPhase.AuthorityLost,
        [(SessionPhase.AuthorityLost, SessionTrigger.AuthorityResumed)] = SessionPhase.Running,
        [(SessionPhase.AuthorityLost, SessionTrigger.AuthorityResumedPaused)] = SessionPhase.Paused,
        [(SessionPhase.AuthorityLost, SessionTrigger.AuthorityReadmitted)] = SessionPhase.AuthorityLoading,
        [(SessionPhase.AuthorityLost, SessionTrigger.GraceExpired)] = SessionPhase.Stopping,
        [(SessionPhase.AuthorityLost, SessionTrigger.Migrate)] = SessionPhase.Migrating,
        [(SessionPhase.Migrating, SessionTrigger.MigrationCompleted)] = SessionPhase.Running,
        [(SessionPhase.WaitingForAuthority, SessionTrigger.Stop)] = SessionPhase.Stopping,
        [(SessionPhase.AuthorityLoading, SessionTrigger.Stop)] = SessionPhase.Stopping,
        [(SessionPhase.Running, SessionTrigger.Stop)] = SessionPhase.Stopping,
        [(SessionPhase.Paused, SessionTrigger.Stop)] = SessionPhase.Stopping,
        [(SessionPhase.AuthorityLost, SessionTrigger.Stop)] = SessionPhase.Stopping,
        [(SessionPhase.Migrating, SessionTrigger.Stop)] = SessionPhase.Stopping,
        [(SessionPhase.Stopping, SessionTrigger.StopCompleted)] = SessionPhase.Ended,
        [(SessionPhase.Ended, SessionTrigger.Reset)] = SessionPhase.Idle,
    };

    public static TheoryData<SessionPhase, SessionTrigger> SessionPairs()
    {
        var data = new TheoryData<SessionPhase, SessionTrigger>();
        foreach (var phase in Enum.GetValues<SessionPhase>())
        {
            foreach (var trigger in Enum.GetValues<SessionTrigger>())
            {
                data.Add(phase, trigger);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SessionPairs))]
    public void EverySessionPhaseTriggerPairIsAListedEdgeOrIllegal(SessionPhase from, SessionTrigger trigger)
    {
        bool legal = SessionTransitions.TryNext(from, trigger, out var to);
        if (ExpectedSession.TryGetValue((from, trigger), out var expected))
        {
            Assert.True(legal, $"{from} --{trigger}--> should be legal");
            Assert.Equal(expected, to);
        }
        else
        {
            Assert.False(legal, $"{from} --{trigger}--> should be illegal but went to {to}");
            Assert.Equal(from, to);
        }
    }

    [Fact]
    public void EdgeListMatchesTheExpectedTable() =>
        Assert.Equal(ExpectedSession.Count, SessionTransitions.Edges.Count);

    [Fact]
    public void EndedIsOnlyLeftByReset()
    {
        foreach (var trigger in Enum.GetValues<SessionTrigger>())
        {
            Assert.Equal(trigger == SessionTrigger.Reset, SessionTransitions.TryNext(SessionPhase.Ended, trigger, out _));
        }
    }

    // ---- node phases ----

    private static readonly NodePhase[] Pipeline =
    [
        NodePhase.Admitted, NodePhase.AwaitingTeam, NodePhase.SyncingSave, NodePhase.Verifying, NodePhase.Loading,
        NodePhase.Matching, NodePhase.CatchingUp, NodePhase.InGame,
    ];

    private static NodeStep Expected(NodePhase from, NodeTrigger trigger, NodePhase resumeTo)
    {
        int rank = Array.IndexOf(Pipeline, from);
        bool attached = rank >= 0;
        NodePhase? report = trigger switch
        {
            NodeTrigger.ReportSyncing => NodePhase.SyncingSave,
            NodeTrigger.ReportVerifying => NodePhase.Verifying,
            NodeTrigger.ReportLoading => NodePhase.Loading,
            NodeTrigger.ReportMatching => NodePhase.Matching,
            NodeTrigger.ReportCatchingUp => NodePhase.CatchingUp,
            _ => null,
        };

        if (report is { } target)
        {
            if (!attached || from == NodePhase.AwaitingTeam)
            {
                return NodeStep.Invalid;
            }

            int targetRank = Array.IndexOf(Pipeline, target);
            return rank < targetRank ? NodeStep.Move(target) : rank == targetRank ? NodeStep.Stay(from) : NodeStep.Invalid;
        }

        return trigger switch
        {
            NodeTrigger.RequireTeam => from == NodePhase.Admitted ? NodeStep.Move(NodePhase.AwaitingTeam) : NodeStep.Invalid,
            NodeTrigger.TeamAssigned => from is NodePhase.Admitted or NodePhase.AwaitingTeam ? NodeStep.Move(NodePhase.SyncingSave) : NodeStep.Invalid,
            NodeTrigger.Ready => from is NodePhase.Matching or NodePhase.CatchingUp ? NodeStep.Move(NodePhase.InGame)
                : from == NodePhase.InGame ? NodeStep.Stay(from) : NodeStep.Invalid,
            NodeTrigger.ObserverReady => from == NodePhase.Admitted ? NodeStep.Move(NodePhase.InGame) : NodeStep.Invalid,
            NodeTrigger.Fail => attached ? NodeStep.Move(NodePhase.Failed) : from == NodePhase.Failed ? NodeStep.Stay(from) : NodeStep.Invalid,
            NodeTrigger.SocketLost or NodeTrigger.ClientReload => attached ? NodeStep.Move(NodePhase.Detached)
                : from == NodePhase.Detached ? NodeStep.Stay(from) : NodeStep.Leave,
            NodeTrigger.Resume => from == NodePhase.Detached ? NodeStep.Move(resumeTo) : NodeStep.Invalid,
            NodeTrigger.GraceExpired => from == NodePhase.Detached ? NodeStep.Leave : NodeStep.Invalid,
            NodeTrigger.Quit => NodeStep.Leave,
            _ => throw new InvalidOperationException(trigger.ToString()),
        };
    }

    public static TheoryData<NodePhase, NodeTrigger> NodePairs()
    {
        var data = new TheoryData<NodePhase, NodeTrigger>();
        foreach (var phase in Enum.GetValues<NodePhase>())
        {
            foreach (var trigger in Enum.GetValues<NodeTrigger>())
            {
                data.Add(phase, trigger);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(NodePairs))]
    public void EveryNodePhaseTriggerPairMatchesTheTable(NodePhase from, NodeTrigger trigger)
    {
        // resumeTo is only read by Resume; use a distinctive phase to prove it is honoured.
        var actual = NodeTransitions.Apply(from, trigger, NodePhase.Loading);
        Assert.Equal(Expected(from, trigger, NodePhase.Loading), actual);
    }

    [Theory]
    [InlineData(NodePhase.Detached)]
    [InlineData(NodePhase.Failed)]
    public void ResumeToAnUnattachedPhaseFallsBackToAdmitted(NodePhase bogus)
    {
        var step = NodeTransitions.Apply(NodePhase.Detached, NodeTrigger.Resume, bogus);
        Assert.Equal(NodeStep.Move(NodePhase.Admitted), step);
    }

    [Fact]
    public void ClientReloadAndSocketLossTakeTheSameEdge()
    {
        foreach (var phase in Enum.GetValues<NodePhase>())
        {
            Assert.Equal(
                NodeTransitions.Apply(phase, NodeTrigger.SocketLost),
                NodeTransitions.Apply(phase, NodeTrigger.ClientReload));
        }
    }

    [Fact]
    public void ThePipelineGoesForwardOnlyByReports()
    {
        // The happy path of architecture 4.3, one report at a time.
        var phase = NodePhase.Admitted;
        foreach (var (trigger, expected) in new[]
        {
            (NodeTrigger.TeamAssigned, NodePhase.SyncingSave),
            (NodeTrigger.ReportVerifying, NodePhase.Verifying),
            (NodeTrigger.ReportLoading, NodePhase.Loading),
            (NodeTrigger.ReportMatching, NodePhase.Matching),
            (NodeTrigger.ReportCatchingUp, NodePhase.CatchingUp),
            (NodeTrigger.Ready, NodePhase.InGame),
        })
        {
            var step = NodeTransitions.Apply(phase, trigger);
            Assert.Equal(NodeStepKind.Move, step.Kind);
            Assert.Equal(expected, step.Phase);
            phase = step.Phase;
        }
    }
}
