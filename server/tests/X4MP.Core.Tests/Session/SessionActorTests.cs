using System.Diagnostics;
using X4MP.Core.Events;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;

namespace X4MP.Core.Tests.Session;

public class SessionActorTests
{
    private static async Task WaitUntilAsync(ActorRig rig, Func<SessionSnapshot, bool> condition)
    {
        var until = Environment.TickCount64 + 5000;
        while (true)
        {
            var snapshot = await rig.SnapshotAsync();
            if (condition(snapshot))
            {
                return;
            }

            Assert.True(Environment.TickCount64 < until, "condition not met in time");
            await Task.Delay(5);
        }
    }

    /// <summary>The socket vanishes without a Disconnect; waits until the actor noticed.</summary>
    private static async Task DropAsync(ActorRig rig, JoinedNode node)
    {
        node.Connection.Drop();
        await WaitUntilAsync(rig, s => s.Nodes.Any(n => n.PlayerId == node.PlayerId && !n.Connected));
    }

    private static async Task<SessionPhase> PhaseAsync(ActorRig rig) => (await rig.SnapshotAsync()).Phase;

    private static async Task<JoinedNode> RunningSessionAsync(ActorRig rig, string authority = "Boss")
    {
        var boss = await rig.JoinAuthorityAsync(authority);
        Assert.True(boss.Accepted);
        Assert.True((await rig.Actor.ApplyAsync(SessionTrigger.CheckpointStored)).Applied);
        Assert.Equal(SessionPhase.Running, await PhaseAsync(rig));
        return boss;
    }

    // ------------------------------------------------------------------ joining

    [Fact]
    public async Task ClientJoinsIdleSessionAndIsAnnounced()
    {
        await using var rig = new ActorRig();
        var alice = await rig.JoinAsync("Alice");

        Assert.True(alice.Accepted);
        Assert.False(alice.Welcome.Resumed);
        Assert.Equal(1000, alice.Welcome.HeartbeatIntervalMs);
        Assert.Equal(60, alice.Welcome.ResumeGraceS);

        // After Welcome: SessionState, the full roster, then the first Ping.
        var types = alice.Connection.Sent.Select(f => f.Type).ToArray();
        Assert.Equal([MsgType.SessionState, MsgType.RosterUpdate, MsgType.Ping], types);
        var state = alice.Connection.SentOf(MsgType.SessionState).Single().Decode<SessionState>();
        Assert.Equal(SessionPhase.Idle, state.Phase);
        var roster = alice.Connection.SentOf(MsgType.RosterUpdate).Single().Decode<RosterUpdate>();
        Assert.True(roster.Full);
        Assert.Equal(1, roster.PlayersLength);
        Assert.Equal("Alice", roster.Players(0)!.Value.Name);

        var snap = await rig.SnapshotAsync();
        Assert.Equal(SessionPhase.Idle, snap.Phase);
        var node = Assert.Single(snap.Nodes);
        Assert.Equal(NodePhase.Admitted, node.Phase);
        Assert.True(node.Connected);
        Assert.Equal(AuthorityStatus.None, snap.Authority.Status);

        Assert.Single(rig.Store.Sessions);
        Assert.Single(rig.Store.Joins);
        Assert.Single(rig.Events.OfType<PlayerJoined>());
        Assert.Single(rig.Events.OfType<NodeConnected>());
    }

    [Fact]
    public async Task ANodeAdmittedUnderWarnGetsAServerNoticeNamingTheMods()
    {
        await using var rig = new ActorRig();
        var warning = new ModPolicyViolationT
        {
            PolicyVersion = 3,
            Install = [new ModRefT { Id = "ws_1", Name = "Warehouse Fleets" }],
            Disable = [new ModRefT { Id = "cheat", Name = "Cheat Menu" }],
        };
        var alice = await rig.JoinAsync("Alice", modWarning: warning);
        var bob = await rig.JoinAsync("Bob");

        var notice = alice.Connection.SentOf(MsgType.ServerNotice).Single().Decode<ServerNotice>();
        Assert.Equal(NoticeSeverity.Warning, notice.Severity);
        Assert.Contains("install: Warehouse Fleets; disable: Cheat Menu", notice.Text);
        Assert.Empty(bob.Connection.SentOf(MsgType.ServerNotice));
    }

    [Fact]
    public async Task ObserverSkipsTheLoadPipeline()
    {
        await using var rig = new ActorRig();
        var tool = await rig.JoinAsync("Inspector", Role.Observer);
        Assert.Equal(NodePhase.InGame, (await rig.NodeAsync(tool.PlayerId)).Phase);
        Assert.Equal(NodePhase.InGame, tool.Node.Phase);
    }

    [Fact]
    public async Task AuthorityAdmissionStartsTheSessionAndRecordsItsIdentity()
    {
        await using var rig = new ActorRig();
        var boss = await rig.JoinAuthorityAsync();

        var snap = await rig.SnapshotAsync();
        Assert.Equal(SessionPhase.AuthorityLoading, snap.Phase);
        Assert.Equal(AuthorityStatus.Live, snap.Authority.Status);
        Assert.Equal(boss.PlayerId, snap.Authority.PlayerId);
        Assert.NotNull(rig.Gateway.Authority);
        Assert.True(rig.Gateway.AuthorityLive);
        Assert.Equal(boss.PlayerId, rig.Gateway.DesignatedAuthorityPlayerId);
        Assert.Equal(SessionPhase.AuthorityLoading, rig.Gateway.Phase);

        // Idle -> WaitingForAuthority -> AuthorityLoading, both persisted and published.
        Assert.Equal([SessionPhase.WaitingForAuthority, SessionPhase.AuthorityLoading], rig.Store.Phases.Select(p => p.Phase).ToArray());
        Assert.Equal(
            [("Idle", "WaitingForAuthority"), ("WaitingForAuthority", "AuthorityLoading")],
            rig.Events.OfType<SessionStateChanged>().Select(e => (e.From, e.To)).ToArray());

        // The authority learned the phase it joined into.
        Assert.Equal(SessionPhase.AuthorityLoading, boss.Connection.SentOf(MsgType.SessionState).First().Decode<SessionState>().Phase);
    }

    [Fact]
    public async Task SecondAuthorityIsRefusedAndNotJoinableWhileStopping()
    {
        await using var rig = new ActorRig();
        await RunningSessionAsync(rig);

        var usurper = await rig.JoinAsync("Usurper", Role.Authority | Role.Client);
        Assert.Equal(DisconnectCode.RoleUnavailable, usurper.Verdict.Code);

        await rig.Actor.ApplyAsync(SessionTrigger.Stop);
        var late = await rig.JoinAsync("Late");
        Assert.Equal(DisconnectCode.NotJoinable, late.Verdict.Code);
    }

    // ------------------------------------------------------------------ ping, rtt, clock

    [Fact]
    public async Task PingsGoOutEverySecond()
    {
        await using var rig = new ActorRig();
        var alice = await rig.JoinAsync("Alice");
        int Pings() => alice.Connection.SentOf(MsgType.Ping).Count;

        Assert.Equal(1, Pings());
        await rig.AdvanceSecondsAsync(0.5);
        Assert.Equal(1, Pings());
        await rig.AdvanceSecondsAsync(0.5);
        Assert.Equal(2, Pings());
        await rig.AdvanceSecondsAsync(2);
        Assert.Equal(3, Pings()); // one tick at +2 s: one ping, not two (no catch-up burst)
        Assert.Equal([1u, 2u, 3u], alice.Connection.SentOf(MsgType.Ping).Select(f => f.Decode<Ping>().Seq).ToArray());
    }

    private static async Task PongAsync(ActorRig rig, JoinedNode node, long oneWayUs, long nodeClockOffsetUs, long nodeHoldUs = 0)
    {
        var ping = node.Connection.SentOf(MsgType.Ping).Last().Decode<Ping>();
        rig.Time.Advance(TimeSpan.FromMicroseconds(oneWayUs));
        ulong recv = (ulong)((long)ping.SendTimeUs + oneWayUs + nodeClockOffsetUs);
        ulong reply = recv + (ulong)nodeHoldUs;
        rig.Time.Advance(TimeSpan.FromMicroseconds(oneWayUs + nodeHoldUs));
        await rig.SendAsync(node, MsgType.Pong, Frames.Pong(ping.Seq, ping.SendTimeUs, recv, reply));
    }

    [Fact]
    public async Task PongYieldsRttAndClockOffsetInTheSnapshot()
    {
        await using var rig = new ActorRig();
        var alice = await rig.JoinAsync("Alice");

        await PongAsync(rig, alice, oneWayUs: 10_000, nodeClockOffsetUs: 5_000_000);

        var node = await rig.NodeAsync(alice.PlayerId);
        Assert.Equal(20.0, node.RttMs, 3);
        Assert.Equal(20.0, node.MinRttMs, 3);
        Assert.Equal(5_000_000, node.ClockOffsetUs);
        Assert.Equal(1, node.ClockSamples);
    }

    [Fact]
    public async Task ClockOffsetTracksTheLowestRttSampleNotTheLatest()
    {
        await using var rig = new ActorRig();
        var alice = await rig.JoinAsync("Alice");

        await PongAsync(rig, alice, oneWayUs: 4_000, nodeClockOffsetUs: 777);          // clean: RTT 8 ms
        await rig.AdvanceSecondsAsync(1);
        await PongAsync(rig, alice, oneWayUs: 150_000, nodeClockOffsetUs: 777 + 9_000); // congested, skewed

        var node = await rig.NodeAsync(alice.PlayerId);
        Assert.Equal(2, node.ClockSamples);
        Assert.Equal(8.0, node.MinRttMs, 3);
        Assert.Equal(777, node.ClockOffsetUs);
        Assert.True(node.RttMs > 8.0 && node.RttMs < 300.0);
    }

    [Fact]
    public async Task NodeHoldTimeIsNotCountedAsRtt()
    {
        await using var rig = new ActorRig();
        var alice = await rig.JoinAsync("Alice");
        await PongAsync(rig, alice, oneWayUs: 6_000, nodeClockOffsetUs: 0, nodeHoldUs: 40_000);
        Assert.Equal(12.0, (await rig.NodeAsync(alice.PlayerId)).RttMs, 3);
    }

    [Fact]
    public async Task ServerAnswersANodePingWithItsOwnClock()
    {
        await using var rig = new ActorRig();
        var alice = await rig.JoinAsync("Alice");
        await rig.AdvanceSecondsAsync(3);

        await rig.SendAsync(alice, MsgType.Ping, Frames.Ping(9, 123_456));
        var pong = alice.Connection.SentOf(MsgType.Pong).Single().Decode<Pong>();
        Assert.Equal(9u, pong.Seq);
        Assert.Equal(123_456ul, pong.EchoSendTimeUs);
        Assert.Equal(3_000_000ul, pong.RecvTimeUs);
        Assert.True(pong.ReplyTimeUs >= pong.RecvTimeUs);
    }

    [Fact]
    public async Task SilentNodeIsClosedAfterTheHeartbeatTimeoutAndDetached()
    {
        await using var rig = new ActorRig(new SessionActorOptions { HeartbeatTimeoutMs = 10_000 });
        var alice = await rig.JoinAsync("Alice");
        var bob = await rig.JoinAsync("Bob");

        await rig.AdvanceSecondsAsync(8);
        await rig.SendAsync(alice, MsgType.Ping, Frames.Ping(1, 0)); // any frame is proof of life
        await rig.AdvanceSecondsAsync(8);                           // t=16: Bob silent for 16 s, Alice for 8 s

        Assert.Equal(DisconnectCode.HeartbeatTimeout, bob.Connection.CloseCode);
        Assert.False(alice.Connection.IsClosed);
        var snap = await rig.SnapshotAsync();
        var bobNode = snap.Nodes.Single(n => n.PlayerId == bob.PlayerId);
        Assert.Equal(NodePhase.Detached, bobNode.Phase);
        Assert.Equal(DetachReason.HeartbeatTimeout, bobNode.DetachReason);
        Assert.True(snap.Nodes.Single(n => n.PlayerId == alice.PlayerId).Connected);
    }

    // ------------------------------------------------------------------ NodeStats, load pipeline

    [Fact]
    public async Task NodeStatsAreIngestedForSnapshotModulesAndEvents()
    {
        var module = new RecordingModule();
        await using var rig = new ActorRig(modules: module);
        var alice = await rig.JoinAsync("Alice");

        await rig.SendAsync(alice, MsgType.NodeStats, Frames.NodeStats(59.5f, 12f, -300, 812));

        var stats = (await rig.NodeAsync(alice.PlayerId)).Stats;
        Assert.NotNull(stats);
        Assert.Equal(59.5f, stats.Fps);
        Assert.Equal(812u, stats.Ghosts);
        Assert.Equal(-300, stats.ClockOffsetUs);
        Assert.Equal(2048u, stats.MemoryMb);
        var reported = Assert.Single(rig.Events.OfType<NodeStatsReported>());
        Assert.Equal(59.5, reported.Fps, 1);
        Assert.False(reported.IsAuthority);
    }

    [Fact]
    public async Task LoadStatusReportsWalkTheNodeToInGame()
    {
        var module = new RecordingModule();
        await using var rig = new ActorRig(modules: module);
        var alice = await rig.JoinAsync("Alice");
        var bob = await rig.JoinAsync("Bob");

        await rig.LoadStatusAsync(alice, NodePhase.SyncingSave);
        Assert.Equal(NodePhase.SyncingSave, (await rig.NodeAsync(alice.PlayerId)).Phase);
        Assert.Equal(NodePhase.SyncingSave, alice.Node.Phase); // the frame reader follows

        await rig.LoadStatusAsync(alice, NodePhase.Verifying);
        await rig.LoadStatusAsync(alice, NodePhase.Loading);
        await rig.LoadStatusAsync(alice, NodePhase.Loading); // progress update: no change
        await rig.LoadStatusAsync(alice, NodePhase.Matching);
        await rig.LoadStatusAsync(alice, NodePhase.CatchingUp);
        await rig.ReadyAsync(alice);

        Assert.Equal(NodePhase.InGame, (await rig.NodeAsync(alice.PlayerId)).Phase);
        Assert.Equal(
            ["SyncingSave", "Verifying", "Loading", "Matching", "CatchingUp", "InGame"],
            rig.Events.OfType<NodePhaseChanged>().Where(e => e.PlayerId == alice.PlayerId).Select(e => e.To).ToArray());
        Assert.Contains($"node:{alice.PlayerId}:CatchingUp->InGame", module.Log);

        // Bob saw Alice's phase changes in his roster.
        var upserts = bob.Connection.SentOf(MsgType.RosterUpdate).Select(f => f.Decode<RosterUpdate>()).Where(r => !r.Full).ToList();
        Assert.Equal(6, upserts.Count);
        Assert.Equal(NodePhase.InGame, upserts[^1].Players(0)!.Value.Phase);
    }

    [Fact]
    public async Task IllegalPhaseReportsAreIgnoredAndCountedAsViolations()
    {
        await using var rig = new ActorRig();
        var alice = await rig.JoinAsync("Alice");

        await rig.LoadStatusAsync(alice, NodePhase.InGame);            // InGame is reached by NodeReady only
        await rig.LoadStatusAsync(alice, NodePhase.Detached);          // server-side only

        Assert.Equal(NodePhase.Admitted, (await rig.NodeAsync(alice.PlayerId)).Phase);
        Assert.Equal(2, alice.Connection.Stats.Violations);

        await rig.LoadStatusAsync(alice, NodePhase.Loading);
        await rig.LoadStatusAsync(alice, NodePhase.SyncingSave); // backwards
        Assert.Equal(NodePhase.Loading, (await rig.NodeAsync(alice.PlayerId)).Phase);
        Assert.Equal(3, alice.Connection.Stats.Violations);

        await rig.LoadStatusAsync(alice, NodePhase.Failed);
        Assert.Equal(NodePhase.Failed, (await rig.NodeAsync(alice.PlayerId)).Phase);
    }

    [Fact]
    public async Task ServerDrivenTriggersAndKick()
    {
        await using var rig = new ActorRig();
        var alice = await rig.JoinAsync("Alice");

        var team = await rig.Actor.ApplyNodeTriggerAsync(alice.PlayerId, NodeTrigger.RequireTeam);
        Assert.True(team.Applied);
        await rig.LoadStatusAsync(alice, NodePhase.SyncingSave); // may not skip the team step
        Assert.Equal(NodePhase.AwaitingTeam, (await rig.NodeAsync(alice.PlayerId)).Phase);

        Assert.True((await rig.Actor.ApplyNodeTriggerAsync(alice.PlayerId, NodeTrigger.TeamAssigned)).Applied);
        Assert.Equal(NodePhase.SyncingSave, (await rig.NodeAsync(alice.PlayerId)).Phase);
        Assert.False((await rig.Actor.ApplyNodeTriggerAsync(alice.PlayerId, NodeTrigger.RequireTeam)).Applied);
        Assert.False((await rig.Actor.ApplyNodeTriggerAsync(alice.PlayerId, NodeTrigger.Quit)).Applied);
        Assert.False((await rig.Actor.ApplyNodeTriggerAsync(99, NodeTrigger.Fail)).Applied);

        Assert.True(await rig.Actor.RemoveNodeAsync(alice.PlayerId, DisconnectCode.Kicked, "bad manners"));
        Assert.Equal(DisconnectCode.Kicked, alice.Connection.CloseCode);
        Assert.Empty((await rig.SnapshotAsync()).Nodes);
        Assert.Equal("bad manners", rig.Events.OfType<PlayerLeft>().Single().Reason);
    }

    [Fact]
    public async Task UnhandledFramesGoToModulesFirstConsumerWins()
    {
        var first = new RecordingModule { ConsumeMessages = true };
        var second = new RecordingModule();
        await using var rig = new ActorRig(modules: [first, second]);
        var alice = await rig.JoinAsync("Alice");

        var chat = new Google.FlatBuffers.FlatBufferBuilder(64);
        chat.Finish(ChatSend.CreateChatSend(chat, ChatChannel.All, 0, chat.CreateString("hi")).Value);
        await rig.SendAsync(alice, MsgType.ChatSend, chat);

        Assert.Equal(MsgType.ChatSend, Assert.Single(first.Messages).Type);
        Assert.Empty(second.Messages);
    }

    // ------------------------------------------------------------------ leaving, ClientReload

    [Fact]
    public async Task ClientQuitFreesTheSlotAtOnceAndClosesTheConnection()
    {
        await using var rig = new ActorRig();
        var alice = await rig.JoinAsync("Alice");
        var bob = await rig.JoinAsync("Bob");

        await rig.DisconnectAsync(alice, DisconnectCode.ClientQuit);

        Assert.Equal(DisconnectCode.ClientQuit, alice.Connection.CloseCode);
        Assert.Equal([bob.PlayerId], (await rig.SnapshotAsync()).Nodes.Select(n => n.PlayerId).ToArray());
        Assert.Single(rig.Events.OfType<PlayerLeft>());
        Assert.Single(rig.Store.Leaves);
        var removal = bob.Connection.SentOf(MsgType.RosterUpdate).Select(f => f.Decode<RosterUpdate>()).Last();
        Assert.Equal(1, removal.RemovedLength);
        Assert.Equal((ushort)alice.PlayerId, removal.Removed(0));
    }

    [Fact]
    public async Task ClientReloadKeepsTheSlotAndEmitsNoLeaveOrJoin()
    {
        var module = new RecordingModule();
        await using var rig = new ActorRig(modules: module);
        var alice = await rig.JoinAsync("Alice");
        var bob = await rig.JoinAsync("Bob");
        await rig.BringInGameAsync(alice);
        int bobRosterFrames = bob.Connection.SentOf(MsgType.RosterUpdate).Count;
        int joinsBefore = rig.Events.OfType<PlayerJoined>().Count;

        await rig.DisconnectAsync(alice, DisconnectCode.ClientReload);

        var node = await rig.NodeAsync(alice.PlayerId);
        Assert.Equal(NodePhase.Detached, node.Phase);
        Assert.Equal(DetachReason.ClientReload, node.DetachReason);
        Assert.False(node.Connected);
        Assert.Equal(DisconnectCode.ClientReload, alice.Connection.CloseCode);

        Assert.Empty(rig.Events.OfType<PlayerLeft>());
        Assert.Empty(rig.Store.Leaves);
        Assert.Equal(joinsBefore, rig.Events.OfType<PlayerJoined>().Count);
        Assert.Single(rig.Events.OfType<PlayerDetached>(), e => e.Reason == "ClientReload");
        Assert.Equal(bobRosterFrames, bob.Connection.SentOf(MsgType.RosterUpdate).Count); // the others see nothing

        // ... and the reloaded node comes back as the same player, no join.
        var back = await rig.ResumeAsync("Alice", alice);
        Assert.True(back.Accepted);
        Assert.True(back.Welcome.Resumed);
        Assert.Equal(joinsBefore, rig.Events.OfType<PlayerJoined>().Count);
        Assert.Empty(rig.Events.OfType<PlayerLeft>());
        Assert.Equal(bobRosterFrames, bob.Connection.SentOf(MsgType.RosterUpdate).Count);
        Assert.Contains($"detached:{alice.PlayerId}:ClientReload", module.Log);
    }

    // ------------------------------------------------------------------ resume

    [Fact]
    public async Task ResumeInsideTheGraceKeepsTheRowAndResetsBaselines()
    {
        var module = new RecordingModule();
        await using var rig = new ActorRig(modules: module);
        var alice = await rig.JoinAsync("Alice");
        await rig.BringInGameAsync(alice);

        await DropAsync(rig, alice);
        var detached = await rig.NodeAsync(alice.PlayerId);
        Assert.Equal(NodePhase.Detached, detached.Phase);
        Assert.Equal(DetachReason.SocketLost, detached.DetachReason);
        Assert.Equal(60.0, detached.ResumeRemainingSeconds!.Value, 1);
        Assert.Empty(rig.Events.OfType<PlayerLeft>());

        await rig.AdvanceSecondsAsync(59);
        Assert.Equal(1.0, (await rig.NodeAsync(alice.PlayerId)).ResumeRemainingSeconds!.Value, 1);

        var back = await rig.ResumeAsync("Alice", alice);
        Assert.True(back.Accepted);
        Assert.True(back.Welcome.Resumed);
        Assert.Equal(alice.PlayerId, back.PlayerId);
        Assert.Equal(alice.Welcome.ResumeToken.Lo, back.Welcome.ResumeToken.Lo);
        Assert.Equal(alice.Welcome.ResumeToken.Hi, back.Welcome.ResumeToken.Hi);

        var node = await rig.NodeAsync(alice.PlayerId);
        Assert.Equal(NodePhase.InGame, node.Phase);   // the phase it left, not a new join
        Assert.Equal(NodePhase.InGame, back.Node.Phase);
        Assert.True(node.Connected);
        Assert.Equal(1, node.BaselineEpoch);
        Assert.Equal(back.Connection.Id.Value, node.ConnectionId);

        // One row, no leave, no second join; the replication hook was told to reset baselines.
        Assert.Single(rig.Store.Joins);
        Assert.Empty(rig.Store.Leaves);
        Assert.Single(rig.Events.OfType<PlayerJoined>());
        var resumed = Assert.Single(rig.Events.OfType<PlayerResumed>());
        Assert.Equal(1, resumed.BaselineEpoch);
        Assert.Equal([(alice.PlayerId, false, 0), (alice.PlayerId, true, 1)], module.Attached);

        // The resumed connection gets the session announcement again.
        Assert.Contains(back.Connection.Sent, f => f.Type == MsgType.SessionState);
        Assert.Contains(back.Connection.Sent, f => f.Type == MsgType.RosterUpdate);
    }

    [Fact]
    public async Task ResumeAfterTheGraceIsRefusedAndTheSlotIsGone()
    {
        await using var rig = new ActorRig();
        var alice = await rig.JoinAsync("Alice");
        await DropAsync(rig, alice);

        await rig.AdvanceSecondsAsync(60);

        Assert.Empty((await rig.SnapshotAsync()).Nodes);
        var left = Assert.Single(rig.Events.OfType<PlayerLeft>());
        Assert.Equal("ResumeGraceExpired", left.Reason);
        Assert.Single(rig.Store.Leaves);

        var late = await rig.ResumeAsync("Alice", alice);
        Assert.Equal(DisconnectCode.ResumeExpired, late.Verdict.Code);

        // Joining afresh works and is a new join with a new row.
        var fresh = await rig.JoinAsync("Alice");
        Assert.True(fresh.Accepted);
        Assert.False(fresh.Welcome.Resumed);
        Assert.Equal(2, rig.Store.Joins.Count);
    }

    [Theory]
    [InlineData(59.9, true)]
    [InlineData(60.0, false)]
    public async Task ResumeWindowEdge(double secondsAfterDrop, bool accepted)
    {
        await using var rig = new ActorRig();
        var alice = await rig.JoinAsync("Alice");
        await DropAsync(rig, alice);
        await rig.AdvanceAsync(TimeSpan.FromSeconds(secondsAfterDrop));

        var back = await rig.ResumeAsync("Alice", alice);
        Assert.Equal(accepted, back.Accepted);
        Assert.Equal(accepted, back.Welcome.Resumed);
    }

    [Fact]
    public async Task WrongTokenOrRoleOrUnknownPlayerCannotResume()
    {
        await using var rig = new ActorRig();
        var alice = await rig.JoinAsync("Alice");
        await DropAsync(rig, alice);

        var wrongToken = await rig.JoinAsync("Alice", Role.Client, new Id128T { Lo = 1, Hi = 2 });
        Assert.Equal(DisconnectCode.ResumeExpired, wrongToken.Verdict.Code);

        var asObserver = await rig.ResumeAsync("Alice", alice, Role.Observer);
        Assert.Equal(DisconnectCode.ResumeExpired, asObserver.Verdict.Code);

        var stranger = await rig.JoinAsync("Mallory", Role.Client, alice.Welcome.ResumeToken);
        Assert.Equal(DisconnectCode.ResumeExpired, stranger.Verdict.Code);

        Assert.True((await rig.ResumeAsync("Alice", alice)).Accepted); // the real one still can
    }

    [Fact]
    public async Task ResumeTakesOverASlotWhoseOldSocketIsNotYetDeclaredDead()
    {
        await using var rig = new ActorRig();
        var alice = await rig.JoinAsync("Alice");
        await rig.BringInGameAsync(alice);

        var back = await rig.ResumeAsync("Alice", alice); // the old connection is still attached
        Assert.True(back.Welcome.Resumed);
        Assert.Equal(NodePhase.Detached, alice.Node.Phase); // superseded
        Assert.Equal(NodePhase.InGame, (await rig.NodeAsync(alice.PlayerId)).Phase);

        // The old socket finally dying must not detach the new connection.
        alice.Connection.Drop();
        await rig.Actor.FlushAsync();
        await Task.Delay(20);
        await rig.Actor.FlushAsync();
        var node = await rig.NodeAsync(alice.PlayerId);
        Assert.True(node.Connected);
        Assert.Equal(back.Connection.Id.Value, node.ConnectionId);
    }

    [Fact]
    public async Task FreshJoinByAnAttachedPlayerReplacesTheOldSlot()
    {
        await using var rig = new ActorRig();
        var first = await rig.JoinAsync("Alice");
        var second = await rig.JoinAsync("Alice"); // no token: a new join, not a resume

        Assert.True(second.Accepted);
        Assert.False(second.Welcome.Resumed);
        Assert.Equal(2, rig.Store.Joins.Count);
        Assert.Single(rig.Store.Leaves);
        Assert.Equal("Rejoined", rig.Events.OfType<PlayerLeft>().Single().Reason);
        Assert.Equal(NodePhase.Admitted, (await rig.NodeAsync(first.PlayerId)).Phase);
    }

    // ------------------------------------------------------------------ authority grace

    [Fact]
    public async Task AuthorityDropGoesAuthorityLostAndBackToRunningOnResume()
    {
        var module = new RecordingModule();
        await using var rig = new ActorRig(modules: module);
        var boss = await RunningSessionAsync(rig);
        var bob = await rig.JoinAsync("Bob");

        await DropAsync(rig, boss);

        var lost = await rig.SnapshotAsync();
        Assert.Equal(SessionPhase.AuthorityLost, lost.Phase);
        Assert.Equal(AuthorityStatus.Lost, lost.Authority.Status);
        Assert.Equal(120.0, lost.Authority.GraceRemainingSeconds!.Value, 1);
        Assert.False(rig.Gateway.AuthorityLive);
        Assert.NotNull(rig.Gateway.Authority); // identity kept: the resumer must match it
        Assert.Equal(SessionPhase.AuthorityLost, bob.Connection.SentOf(MsgType.SessionState).Last().Decode<SessionState>().Phase);

        await rig.AdvanceSecondsAsync(119);
        Assert.Equal(SessionPhase.AuthorityLost, await PhaseAsync(rig));

        var back = await rig.ResumeAsync("Boss", boss);
        Assert.True(back.Welcome.Resumed);
        Assert.Equal(120, back.Welcome.ResumeGraceS);

        var running = await rig.SnapshotAsync();
        Assert.Equal(SessionPhase.Running, running.Phase);
        Assert.Equal(AuthorityStatus.Live, running.Authority.Status);
        Assert.True(rig.Gateway.AuthorityLive);
        Assert.Equal(SessionPhase.Running, bob.Connection.SentOf(MsgType.SessionState).Last().Decode<SessionState>().Phase);
        Assert.Equal(
            ["AuthorityLoading>Running", "Running>AuthorityLost", "AuthorityLost>Running"],
            rig.Events.OfType<SessionStateChanged>().Skip(2).Select(e => $"{e.From}>{e.To}").ToArray());
        Assert.Contains(rig.Events.OfType<AuthorityChanged>(), e => e.ToPlayerId is null && e.FromPlayerId == boss.PlayerId);
        Assert.Contains(rig.Events.OfType<AuthorityChanged>(), e => e.FromPlayerId is null && e.ToPlayerId == boss.PlayerId && e.Reason == "authority resumed");
        Assert.Empty(rig.Events.OfType<PlayerLeft>());
    }

    [Fact]
    public async Task AuthorityGraceExpiryStopsTheSessionAndThenEndsIt()
    {
        await using var rig = new ActorRig();
        var boss = await RunningSessionAsync(rig);
        var bob = await rig.JoinAsync("Bob");
        await DropAsync(rig, boss);

        await rig.AdvanceSecondsAsync(119.9);
        Assert.Equal(SessionPhase.AuthorityLost, await PhaseAsync(rig));
        await rig.AdvanceSecondsAsync(0.2);
        Assert.Equal(SessionPhase.Stopping, await PhaseAsync(rig));
        Assert.Equal(SessionPhase.Stopping, bob.Connection.SentOf(MsgType.SessionState).Last().Decode<SessionState>().Phase);

        var late = await rig.ResumeAsync("Boss", boss);
        Assert.Equal(DisconnectCode.NotJoinable, late.Verdict.Code);

        await rig.AdvanceSecondsAsync(29);
        Assert.Equal(SessionPhase.Stopping, await PhaseAsync(rig));
        await rig.AdvanceSecondsAsync(1.1);
        var ended = await rig.SnapshotAsync();
        Assert.Equal(SessionPhase.Ended, ended.Phase);
        Assert.Empty(ended.Nodes);
        Assert.Equal(DisconnectCode.ServerShutdown, bob.Connection.CloseCode);
        Assert.Equal(SessionPhase.Ended, rig.Store.Phases.Last().Phase);
        Assert.Equal(SessionPhase.Ended, rig.Gateway.Phase);
    }

    [Fact]
    public async Task PausedSessionComesBackPausedAfterAnAuthorityBlip()
    {
        await using var rig = new ActorRig();
        var boss = await RunningSessionAsync(rig);
        Assert.True((await rig.Actor.ApplyAsync(SessionTrigger.Pause)).Applied);
        await DropAsync(rig, boss);
        Assert.Equal(SessionPhase.AuthorityLost, await PhaseAsync(rig));

        Assert.True((await rig.ResumeAsync("Boss", boss)).Welcome.Resumed);
        Assert.Equal(SessionPhase.Paused, await PhaseAsync(rig));
    }

    [Fact]
    public async Task OnlyTheLostAuthorityMayReclaimTheRoleAndAFreshRejoinReloads()
    {
        await using var rig = new ActorRig();
        var boss = await RunningSessionAsync(rig);
        await DropAsync(rig, boss);

        var other = await rig.JoinAsync("Other", Role.Authority | Role.Client);
        Assert.Equal(DisconnectCode.RoleUnavailable, other.Verdict.Code);

        // The authority comes back without its token (the game restarted): it must load again.
        var again = await rig.JoinAuthorityAsync("Boss");
        Assert.True(again.Accepted);
        Assert.False(again.Welcome.Resumed);
        Assert.Equal(SessionPhase.AuthorityLoading, await PhaseAsync(rig));
        Assert.True(rig.Gateway.AuthorityLive);
    }

    [Fact]
    public async Task AuthorityLoadingDropResumesInPlaceAndFailsBackAfterTheGrace()
    {
        await using var rig = new ActorRig();
        var boss = await rig.JoinAuthorityAsync();
        await DropAsync(rig, boss);
        Assert.Equal(SessionPhase.AuthorityLoading, await PhaseAsync(rig)); // loading survives a reload

        Assert.True((await rig.ResumeAsync("Boss", boss)).Welcome.Resumed);
        Assert.Equal(SessionPhase.AuthorityLoading, await PhaseAsync(rig));

        await DropAsync(rig, (await rig.ResumeAsync("Boss", boss)) is { Accepted: true } r ? r : boss);
        await rig.AdvanceSecondsAsync(120);
        var snap = await rig.SnapshotAsync();
        Assert.Equal(SessionPhase.WaitingForAuthority, snap.Phase);
        Assert.Null(rig.Gateway.Authority);
        Assert.Equal(AuthorityStatus.None, snap.Authority.Status);

        // A different build may be the authority now.
        var next = await rig.JoinAsync("Next", Role.Authority | Role.Client, modBuild: "other-build");
        Assert.True(next.Accepted);
        Assert.Equal(SessionPhase.AuthorityLoading, await PhaseAsync(rig));
    }

    // ------------------------------------------------------------------ admin commands

    [Fact]
    public async Task AdminCommandsDriveThePhasesAndRefuseTheRest()
    {
        await using var rig = new ActorRig();
        Assert.False((await rig.Actor.ApplyAsync(SessionTrigger.Pause)).Applied);
        Assert.False((await rig.Actor.ApplyAsync(SessionTrigger.AuthorityLost)).Applied); // raised by the actor only

        var start = await rig.Actor.StartAsync("Friday run");
        Assert.True(start.Applied);
        Assert.Equal("Friday run", rig.Store.Sessions.Single().Name);
        Assert.False((await rig.Actor.StartAsync()).Applied);

        var boss = await rig.JoinAuthorityAsync();
        Assert.Equal(SessionPhase.AuthorityLoading, await PhaseAsync(rig));
        Assert.True((await rig.Actor.ApplyAsync(SessionTrigger.CheckpointStored)).Applied);
        Assert.True((await rig.Actor.ApplyAsync(SessionTrigger.Pause)).Applied);
        Assert.True((await rig.Actor.ApplyAsync(SessionTrigger.Resume)).Applied);
        Assert.True((await rig.Actor.ApplyAsync(SessionTrigger.Stop)).Applied);
        Assert.Equal(SessionPhase.Stopping, rig.Gateway.Phase);
        Assert.True((await rig.Actor.ApplyAsync(SessionTrigger.StopCompleted)).Applied);
        Assert.Equal(DisconnectCode.ServerShutdown, boss.Connection.CloseCode);

        var oldSession = rig.Gateway.SessionId;
        Assert.True((await rig.Actor.ApplyAsync(SessionTrigger.Reset)).Applied);
        Assert.Equal(SessionPhase.Idle, rig.Gateway.Phase);
        Assert.NotEqual(oldSession, rig.Gateway.SessionId);
        Assert.Null(rig.Gateway.Authority);

        var again = await rig.JoinAuthorityAsync("Boss");
        Assert.True(again.Accepted);
        Assert.Equal(2, rig.Store.Sessions.Count); // a new sessions row
        Assert.Equal(SessionPhase.AuthorityLoading, await PhaseAsync(rig));
    }

    [Fact]
    public async Task SessionRowsFollowThePlayersAndPhases()
    {
        await using var rig = new ActorRig();
        var boss = await RunningSessionAsync(rig);
        var alice = await rig.JoinAsync("Alice");
        await rig.DisconnectAsync(alice, DisconnectCode.ClientQuit);

        Assert.Equal(2, rig.Store.Joins.Count);
        Assert.Equal(Role.Authority | Role.Client, rig.Store.Joins[0].Roles);
        Assert.Equal((1L, alice.PlayerId, "ClientQuit"), rig.Store.Leaves.Single());
        Assert.Equal(boss.PlayerId, rig.Store.Phases.Last().Authority);
        Assert.Equal(SessionPhase.Running, rig.Store.Phases.Last().Phase);
    }

    [Fact]
    public async Task SettingsPushedByTheSettingsServiceReachModules()
    {
        var module = new RecordingModule();
        await using var rig = new ActorRig(modules: module);
        Assert.Null(rig.Actor.Settings);

        var snapshot = new X4MP.Core.Settings.SessionSettingsSnapshot(3, new Dictionary<string, System.Text.Json.JsonElement>());
        await rig.Actor.PushAsync(snapshot, CancellationToken.None);
        await rig.Actor.FlushAsync();
        Assert.Same(snapshot, rig.Actor.Settings);
    }

    private static X4MP.Core.Settings.SessionSettingsSnapshot Snapshot(long version, string visibility, int tick) => new(
        version,
        new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["Mods.ModListVisibility"] = System.Text.Json.JsonSerializer.SerializeToElement(visibility),
            ["Replication.TickRateHz"] = System.Text.Json.JsonSerializer.SerializeToElement(tick),
        });

    private static List<(ulong Version, Dictionary<string, string> Values)> SettingsUpdates(JoinedNode node) =>
        [.. node.Connection.SentOf(MsgType.ServerSettingsUpdate).Select(f =>
        {
            var update = f.Decode<ServerSettingsUpdate>().UnPack();
            return (update.Version, update.Entries.ToDictionary(e => e.Key, e => e.Value));
        })];

    [Fact]
    public async Task APushedSettingChangeReachesInGameNodesAsAServerSettingsUpdate()
    {
        await using var rig = new ActorRig();
        var boss = await RunningSessionAsync(rig);
        var alice = await rig.JoinAsync("Alice");
        await rig.BringInGameAsync(alice);
        Assert.Empty(SettingsUpdates(alice)); // nothing pushed yet: no snapshot to send

        var watch = Stopwatch.StartNew();
        await rig.Actor.PushAsync(Snapshot(1, "AllPlayers", 30), CancellationToken.None);
        await rig.Actor.FlushAsync();
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        foreach (var node in new[] { alice, boss })
        {
            var update = Assert.Single(SettingsUpdates(node));
            Assert.Equal(1ul, update.Version);
            Assert.Equal("AllPlayers", update.Values["Mods.ModListVisibility"]); // enum names travel unquoted
            Assert.Equal("30", update.Values["Replication.TickRateHz"]);
        }
    }

    [Fact]
    public async Task ANodeThatAttachesLaterGetsTheCurrentSettingsRightAfterItsWelcome()
    {
        await using var rig = new ActorRig();
        await rig.Actor.PushAsync(Snapshot(4, "AdminsOnly", 20), CancellationToken.None);
        await rig.Actor.FlushAsync();

        var alice = await rig.JoinAsync("Alice");
        await rig.Actor.FlushAsync();

        var update = Assert.Single(SettingsUpdates(alice));
        Assert.Equal(4ul, update.Version);
        Assert.Equal("AdminsOnly", update.Values["Mods.ModListVisibility"]);
    }

    // ------------------------------------------------------------------ threading

    [Fact]
    public async Task ProducersNeverBlockEvenWhileTheActorIsStuck()
    {
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var module = new RecordingModule { TickGate = gate, TickEntered = entered };
        await using var rig = new ActorRig(modules: module);
        var alice = await rig.JoinAsync("Alice");

        try
        {
            rig.Time.Advance(TimeSpan.FromSeconds(1)); // the next tick parks the actor inside the module
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "the actor never reached the module");

            int ran = 0;
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 100_000; i++)
            {
                Assert.True(rig.Actor.Post(() => ran++));
            }

            alice.Connection.Push(MsgType.Ping, Frames.Ping(1, 1));
            var pending = rig.Actor.CallAsync(() => 1);
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"posting took {watch.Elapsed}");
            Assert.True(rig.Actor.PendingInputs >= 100_000);
            Assert.False(pending.IsCompleted);

            gate.Set();
            Assert.Equal(1, await pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(100_000, ran);
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task ThrowingModulesAndStoresDoNotStopTheActor()
    {
        var module = new ThrowingModule();
        await using var rig = new ActorRig(modules: module);
        var alice = await rig.JoinAsync("Alice");
        await rig.LoadStatusAsync(alice, NodePhase.SyncingSave);
        await rig.AdvanceSecondsAsync(1);
        Assert.Equal(NodePhase.SyncingSave, (await rig.NodeAsync(alice.PlayerId)).Phase);
        Assert.True(module.Calls > 2);
    }

    private sealed class ThrowingModule : ISessionModule
    {
        public int Calls { get; private set; }

        public void OnNodeAttached(SessionNode node, bool resumed)
        {
            Calls++;
            throw new InvalidOperationException("boom");
        }

        public void OnNodePhaseChanged(SessionNode node, NodePhase previous, NodePhase current)
        {
            Calls++;
            throw new InvalidOperationException("boom");
        }

        public void OnTick(long timestamp)
        {
            Calls++;
            throw new InvalidOperationException("boom");
        }
    }
}
