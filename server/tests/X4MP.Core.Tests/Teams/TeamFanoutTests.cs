using System.Diagnostics;
using X4MP.Core.Session;
using X4MP.Core.Settings;
using X4MP.Core.Teams;
using X4MP.Core.Tests.Session;
using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Protocol;
using TeamRelation = X4MP.Core.Teams.TeamRelation;

namespace X4MP.Core.Tests.Teams;

/// <summary>A fake client on a <see cref="JoinedNode"/>: feeds everything the server sent it into a <see cref="FakeTeamState"/>.</summary>
public sealed class TeamClient
{
    private int _read;

    public TeamClient(JoinedNode node)
    {
        Node = node;
        State.ApplyWelcome(node.Welcome);
    }

    public JoinedNode Node { get; }

    public int PlayerId => Node.PlayerId;

    public FakeTeamState State { get; } = new();

    /// <summary>The types of everything the server sent this node, in order (from the start of the connection).</summary>
    public List<MsgType> Types => [.. Node.Connection.Sent.Select(f => f.Type)];

    /// <summary>Processes the frames that arrived since the last call.</summary>
    public FakeTeamState Pump()
    {
        var sent = Node.Connection.Sent;
        for (; _read < sent.Count; _read++)
        {
            var frame = sent[_read];
            State.Handle(new Frame(frame.Type, FrameOptions.None, Lane.Control, frame.Payload));
        }

        return State;
    }
}

/// <summary>
/// M1-T3 acceptance on the real <c>SessionActor</c> (fake clock, fake connections): team state reaches every in-game node, relation
/// requests follow the policy, a move re-owns assets and the authority's own player stays put while the session runs.
/// </summary>
public class TeamFanoutTests
{
    private sealed class Game : IAsyncDisposable
    {
        public required TeamRig Rig { get; init; }

        public required TeamClient Authority { get; init; }

        public required List<TeamClient> Clients { get; init; }

        public IEnumerable<TeamClient> All => Clients.Prepend(Authority);

        public Task<TeamRequestResultT> RelationAsync(TeamClient from, int otherTeam, X4MP.Proto.TeamRelation relation, ulong key) =>
            SendAsync(from, MsgType.RelationChangeRequest, FakeTeamState.BuildRelationChangeRequest(key, otherTeam, relation), key);

        public Task<TeamRequestResultT> TeamChangeAsync(TeamClient from, int teamId, ulong key) =>
            SendAsync(from, MsgType.TeamChangeRequest, FakeTeamState.BuildTeamChangeRequest(key, teamId), key);

        private async Task<TeamRequestResultT> SendAsync(TeamClient from, MsgType type, OutMessage message, ulong key)
        {
            from.Pump();
            await PushAsync(Rig.Rig, from.Node, type, message.Payload);
            await Rig.SettleAsync();
            from.Pump();
            return from.State.Results.Last(r => r.RequestKey.Lo == key);
        }

        private static async Task PushAsync(ActorRig rig, JoinedNode node, MsgType type, byte[] payload)
        {
            long before = rig.Actor.FramesReceived;
            node.Connection.PushPayload(type, payload);
            long until = Environment.TickCount64 + 5000;
            while (rig.Actor.FramesReceived == before)
            {
                Assert.True(Environment.TickCount64 < until, $"the actor never saw the {type} frame");
                await Task.Delay(1);
            }

            await rig.Actor.FlushAsync();
        }

        public void PumpAll()
        {
            foreach (var client in All)
            {
                client.Pump();
            }
        }

        public ValueTask DisposeAsync() => Rig.DisposeAsync();
    }

    /// <summary>An authority and <paramref name="clients"/> clients in game, the session Running (or not yet), one team per player unless the options say otherwise.</summary>
    private static async Task<Game> StartAsync(TeamOptions options, int clients = 3, bool running = true, Action<TeamRig>? beforeJoin = null)
    {
        var rig = new TeamRig(options);
        beforeJoin?.Invoke(rig);
        var boss = await rig.JoinAsync("Boss", Role.Authority | Role.Client);
        await rig.Rig.BringInGameAsync(boss);
        var authority = new TeamClient(boss);
        var list = new List<TeamClient>();
        for (int i = 1; i <= clients; i++)
        {
            var node = await rig.JoinAsync("C" + i);
            await rig.Rig.BringInGameAsync(node);
            list.Add(new TeamClient(node));
        }

        await rig.SettleAsync();
        if (running)
        {
            Assert.True((await rig.Rig.Actor.ApplyAsync(SessionTrigger.CheckpointStored)).Applied);
        }

        var game = new Game { Rig = rig, Authority = authority, Clients = list };
        game.PumpAll();
        return game;
    }

    private static TeamOptions PerPlayer(RelationChangePolicy policy = RelationChangePolicy.AdminOnly) =>
        new() { AutoAssign = AutoAssignStrategy.NewTeamPerPlayer, RelationChangePolicy = policy };

    // ------------------------------------------------------------------ 1. relation changes reach every node, NPC hostility follows

    [Fact]
    public async Task AdminRelationChangeReachesEveryNodeWithinASecondAndFakeNpcHostilityFollows()
    {
        await using var game = await StartAsync(PerPlayer());
        Assert.All(game.All, c => Assert.False(c.State.IsHostile(2, 3)));
        long before = game.Authority.State.RelationUpdates;

        var watch = Stopwatch.StartNew();
        Assert.True((await game.Rig.Teams.SetRelationAsync(2, 3, TeamRelation.Hostile)).Ok);
        await game.Rig.SettleAsync();
        game.PumpAll();

        Assert.True(watch.ElapsedMilliseconds < 1000);
        Assert.All(game.All, c =>
        {
            Assert.True(c.State.IsHostile(2, 3));
            Assert.True(c.State.IsHostile(3, 2));
            Assert.False(c.State.IsHostile(2, 4)); // other pairs untouched
            Assert.Equal(game.Rig.Teams.Matrix.Version, (int)c.State.RelationsVersion);
        });
        Assert.Equal(before + 1, game.Authority.State.RelationUpdates);

        // A delta, not a full copy: one entry.
        var frame = game.Authority.Node.Connection.SentOf(MsgType.TeamRelations).Last().Decode<TeamRelations>().UnPack();
        Assert.False(frame.Full);
        Assert.Single(frame.Entries);

        Assert.True((await game.Rig.Teams.SetRelationAsync(3, 2, TeamRelation.Allied)).Ok);
        await game.Rig.SettleAsync();
        game.PumpAll();
        Assert.All(game.All, c =>
        {
            Assert.False(c.State.IsHostile(2, 3));
            Assert.Equal(TeamRelation.Allied, FromWire(c.State.Relation(2, 3)));
        });
    }

    private static TeamRelation FromWire(X4MP.Proto.TeamRelation relation) => relation switch
    {
        X4MP.Proto.TeamRelation.Allied => TeamRelation.Allied,
        X4MP.Proto.TeamRelation.Hostile => TeamRelation.Hostile,
        _ => TeamRelation.Neutral,
    };

    [Fact]
    public async Task ChangingTheDefaultRelationSendsAFullRelationsMessage()
    {
        await using var game = await StartAsync(PerPlayer());
        game.Rig.Options.DefaultRelation = TeamRelation.Hostile;
        await game.Rig.Rig.Actor.PushAsync(new SessionSettingsSnapshot(2, new Dictionary<string, System.Text.Json.JsonElement>()), default);
        await game.Rig.SettleAsync();
        game.PumpAll();

        Assert.All(game.All, c => Assert.True(c.State.IsHostile(2, 3)));
        var last = game.Authority.Node.Connection.SentOf(MsgType.TeamRelations).Last().Decode<TeamRelations>().UnPack();
        Assert.True(last.Full);
        Assert.Equal(X4MP.Proto.TeamRelation.Hostile, last.DefaultRelation);
    }

    [Fact]
    public async Task ANodeThatWasLoadingWhileTheStateChangedGetsAFullCopyWhenItIsInGame()
    {
        await using var game = await StartAsync(PerPlayer(), clients: 1);
        var late = await game.Rig.JoinAsync("Late"); // joins, loads
        var lateClient = new TeamClient(late);
        await game.Rig.Rig.LoadStatusAsync(late, NodePhase.SyncingSave);
        Assert.True((await game.Rig.Teams.SetRelationAsync(2, 3, TeamRelation.Hostile)).Ok);
        await game.Rig.SettleAsync();
        lateClient.Pump();
        Assert.False(lateClient.State.IsHostile(2, 3)); // not live: nothing pushed yet

        foreach (var phase in new[] { NodePhase.Verifying, NodePhase.Loading, NodePhase.Matching, NodePhase.CatchingUp })
        {
            await game.Rig.Rig.LoadStatusAsync(late, phase);
        }

        await game.Rig.Rig.ReadyAsync(late);
        lateClient.Pump();
        Assert.True(lateClient.State.IsHostile(2, 3));
    }

    // ------------------------------------------------------------------ 2. LeadersMutualAlly

    [Fact]
    public async Task OneSidedRaiseSendsAProposalAndTheMatchingRequestAppliesIt()
    {
        await using var game = await StartAsync(PerPlayer(RelationChangePolicy.LeadersMutualAlly)); // teams 1..4, one leader each
        var a = game.Clients[0]; // player 2, team 2
        var b = game.Clients[1]; // player 3, team 3

        var first = await game.RelationAsync(a, 3, X4MP.Proto.TeamRelation.Allied, 11);
        Assert.Equal(TeamRequestStatus.Pending, first.Status);
        game.PumpAll();
        var proposal = Assert.Single(b.State.Proposals);
        Assert.Equal((2, 3, X4MP.Proto.TeamRelation.Allied, 120), (proposal.FromTeam, proposal.ToTeam, proposal.Relation, proposal.ExpiresInS));
        Assert.Empty(game.Clients[2].State.Proposals); // only the other leader hears of it
        Assert.All(game.All, c => Assert.Equal(TeamRelation.Neutral, FromWire(c.State.Relation(2, 3))));

        await game.Rig.AdvanceSecondsAsync(60);
        var second = await game.RelationAsync(b, 2, X4MP.Proto.TeamRelation.Allied, 22);
        Assert.Equal(TeamRequestStatus.Ok, second.Status);
        game.PumpAll();
        Assert.All(game.All, c => Assert.Equal(TeamRelation.Allied, FromWire(c.State.Relation(2, 3))));

        // The first leader's pending request is finished with the same key.
        Assert.Contains(a.State.Results, r => r.RequestKey.Lo == 11 && r.Status == TeamRequestStatus.Ok);
        Assert.Equal(0, game.Rig.Teams.OpenProposals);
    }

    [Fact]
    public async Task AProposalExpiresAfter120SecondsOnTheFakeClock()
    {
        await using var game = await StartAsync(PerPlayer(RelationChangePolicy.LeadersMutualAlly));
        var a = game.Clients[0];
        var b = game.Clients[1];

        Assert.Equal(TeamRequestStatus.Pending, (await game.RelationAsync(a, 3, X4MP.Proto.TeamRelation.Allied, 11)).Status);
        await game.Rig.AdvanceSecondsAsync(119);
        Assert.Equal(1, game.Rig.Teams.OpenProposals);
        await game.Rig.AdvanceSecondsAsync(2);
        Assert.Equal(0, game.Rig.Teams.OpenProposals);
        game.PumpAll();
        Assert.Contains(a.State.Results, r => r.RequestKey.Lo == 11 && r.Status == TeamRequestStatus.Rejected);

        // Too late: b's request is now a fresh proposal, nothing is applied.
        var late = await game.RelationAsync(b, 2, X4MP.Proto.TeamRelation.Allied, 22);
        Assert.Equal(TeamRequestStatus.Pending, late.Status);
        game.PumpAll();
        Assert.All(game.All, c => Assert.Equal(TeamRelation.Neutral, FromWire(c.State.Relation(2, 3))));
    }

    [Fact]
    public async Task LoweringARelationIsAppliedAtOnceAndANonLeaderIsRejected()
    {
        await using var game = await StartAsync(PerPlayer(RelationChangePolicy.LeadersMutualAlly));
        Assert.True((await game.Rig.Teams.SetRelationAsync(2, 3, TeamRelation.Allied)).Ok);
        var a = game.Clients[0];

        var lowered = await game.RelationAsync(a, 3, X4MP.Proto.TeamRelation.Hostile, 31);
        Assert.Equal(TeamRequestStatus.Ok, lowered.Status);
        game.PumpAll();
        Assert.All(game.All, c => Assert.True(c.State.IsHostile(2, 3)));
        Assert.Equal(0, game.Rig.Teams.OpenProposals);

        // A member who is not the leader: put C3 into team 2 as a plain member.
        Assert.True((await game.Rig.Teams.AssignPlayerAsync(game.Clients[2].PlayerId, 2)).Ok);
        var rejected = await game.RelationAsync(game.Clients[2], 3, X4MP.Proto.TeamRelation.Neutral, 32);
        Assert.Equal(TeamRequestStatus.Rejected, rejected.Status);
        Assert.Equal(TeamRejectReason.NotLeader, rejected.Reason);
        Assert.True(game.Authority.State.IsHostile(2, 3)); // unchanged
    }

    [Fact]
    public async Task AdminOnlyRejectsAndUnilateralApplies()
    {
        await using var admin = await StartAsync(PerPlayer(RelationChangePolicy.AdminOnly));
        var denied = await admin.RelationAsync(admin.Clients[0], 3, X4MP.Proto.TeamRelation.Hostile, 41);
        Assert.Equal(TeamRejectReason.NotPermitted, denied.Reason);
        Assert.Equal(TeamRelation.Neutral, admin.Rig.Teams.RelationBetween(2, 3));

        await using var open = await StartAsync(PerPlayer(RelationChangePolicy.LeadersUnilateral));
        var raised = await open.RelationAsync(open.Clients[0], 3, X4MP.Proto.TeamRelation.Allied, 42);
        Assert.Equal(TeamRequestStatus.Ok, raised.Status);
        Assert.Equal(TeamRelation.Allied, open.Rig.Teams.RelationBetween(2, 3));
        open.PumpAll();
        Assert.All(open.All, c => Assert.Equal(TeamRelation.Allied, FromWire(c.State.Relation(2, 3))));
    }

    [Fact]
    public async Task ARepeatedRequestKeyGetsTheSameAnswerAndOwnTeamAndUnknownTeamAreRejected()
    {
        await using var game = await StartAsync(PerPlayer(RelationChangePolicy.LeadersUnilateral));
        var a = game.Clients[0];
        var same = await game.RelationAsync(a, 2, X4MP.Proto.TeamRelation.Hostile, 51);
        Assert.Equal(TeamRejectReason.SameTeam, same.Reason);
        var unknown = await game.RelationAsync(a, 99, X4MP.Proto.TeamRelation.Hostile, 52);
        Assert.Equal(TeamRejectReason.UnknownTeam, unknown.Reason);

        var once = await game.RelationAsync(a, 3, X4MP.Proto.TeamRelation.Hostile, 53);
        int version = game.Rig.Teams.Matrix.Version;
        var again = await game.RelationAsync(a, 3, X4MP.Proto.TeamRelation.Hostile, 53);
        Assert.Equal(once.Status, again.Status);
        Assert.Equal(version, game.Rig.Teams.Matrix.Version);
    }

    // ------------------------------------------------------------------ 3. moving a player

    [Fact]
    public async Task MovingAPlayerFansOutMemberChangeThenTableAndReassignsAssetsAndResyncs()
    {
        var resynced = new List<int>();
        await using var game = await StartAsync(PerPlayer(), beforeJoin: rig => rig.Teams.ResyncPlayer = id =>
        {
            resynced.Add(id);
            return true;
        });
        var mover = game.Clients[0]; // player 2, team 2
        Assert.Equal(2, game.Rig.Teams.TeamOf(mover.PlayerId));

        Assert.True((await game.Rig.Teams.AssignPlayerAsync(mover.PlayerId, 3)).Ok);
        await game.Rig.SettleAsync();
        game.PumpAll();

        foreach (var node in game.All)
        {
            var change = Assert.Single(node.State.MemberChanges, m => m.FromTeam != 0); // the joins before it are announced too
            Assert.Equal((mover.PlayerId, 2, 3, true), (change.PlayerId, (int)change.FromTeam, (int)change.ToTeam, change.ByAdmin));
            Assert.Equal((uint)game.Rig.Versions[^1], change.TableVersion);

            // TeamMemberChanged first, the TeamTable delta after it.
            var types = node.Types;
            int changedAt = types.LastIndexOf(MsgType.TeamMemberChanged);
            int tableAt = types.LastIndexOf(MsgType.TeamTable);
            Assert.True(changedAt >= 0 && tableAt > changedAt, $"order: {string.Join(',', types)}");

            // The table now lists the mover in team 3 and no longer in team 2.
            Assert.Contains(mover.PlayerId, node.State.Team(3)!.Members.Select(m => (int)m.PlayerId));
            Assert.DoesNotContain(mover.PlayerId, node.State.Team(2)!.Members.Select(m => (int)m.PlayerId));
        }

        Assert.Equal(3, mover.State.OwnTeam);

        var reassign = Assert.Single(game.Authority.State.Reassigns);
        Assert.Equal((mover.PlayerId, 2, 3, MoveAssetsScope.ShipOnly), (reassign.PlayerId, (int)reassign.FromTeam, (int)reassign.ToTeam, reassign.Scope));
        Assert.Empty(game.Clients[1].State.Reassigns); // only the authority is told
        Assert.Equal([mover.PlayerId], resynced);

        // The roster carries the new team.
        var roster = game.Clients[1].Node.Connection.SentOf(MsgType.RosterUpdate).Select(f => f.Decode<RosterUpdate>()).Last(r => !r.Full);
        var entry = Assert.IsType<PlayerInfo>(roster.Players(0)!.Value);
        Assert.Equal((ushort)mover.PlayerId, entry.PlayerId);
        Assert.Equal((ushort)3, entry.TeamId);
    }

    [Fact]
    public async Task NothingIsReassignedWhenAssetsStayWithTheTeam()
    {
        var options = PerPlayer();
        options.MoveAssetsWithPlayer = MoveAssetsScope.None;
        await using var game = await StartAsync(options);
        Assert.True((await game.Rig.Teams.AssignPlayerAsync(game.Clients[0].PlayerId, 3)).Ok);
        await game.Rig.SettleAsync();
        game.PumpAll();
        Assert.Empty(game.Authority.State.Reassigns);
        Assert.Contains(game.Authority.State.MemberChanges, m => m.FromTeam == 2 && m.ToTeam == 3); // the membership change is still announced
    }

    [Fact]
    public async Task APlayersFirstTeamIsNotAMoveAndAJoinIsAnnouncedToTheOthers()
    {
        await using var game = await StartAsync(PerPlayer(), clients: 1);
        game.Authority.Pump();
        int announced = game.Authority.State.MemberChanges.Count;
        var late = await game.Rig.JoinAsync("Late");
        await game.Rig.Rig.BringInGameAsync(late);
        game.PumpAll();

        Assert.Equal(announced + 1, game.Authority.State.MemberChanges.Count);
        var join = game.Authority.State.MemberChanges[^1];
        Assert.Equal((late.PlayerId, 0), (join.PlayerId, (int)join.FromTeam));
        Assert.Empty(game.Authority.State.Reassigns);
        Assert.NotNull(game.Authority.State.Team(join.ToTeam));
    }

    [Fact]
    public async Task APlayerMovesItselfWhenAllowedAndAnswersFollowThePolicy()
    {
        var options = PerPlayer();
        options.AllowSelfTeamChange = true;
        await using var game = await StartAsync(options);
        var mover = game.Clients[0];

        var ok = await game.TeamChangeAsync(mover, 3, 61);
        Assert.Equal(TeamRequestStatus.Ok, ok.Status);
        Assert.Equal(3, game.Rig.Teams.TeamOf(mover.PlayerId));
        game.PumpAll();
        Assert.All(game.All, c => Assert.Contains(c.State.MemberChanges, m => m.PlayerId == mover.PlayerId && m.ToTeam == 3 && !m.ByAdmin));
        Assert.Single(game.Authority.State.Reassigns);

        Assert.Equal(TeamRejectReason.SameTeam, (await game.TeamChangeAsync(mover, 3, 62)).Reason);
        Assert.Equal(TeamRejectReason.UnknownTeam, (await game.TeamChangeAsync(mover, 77, 63)).Reason);
        Assert.True((await game.Rig.Teams.UpdateTeamAsync(4, new TeamRegistry.TeamPatch(Locked: true))).Ok);
        Assert.Equal(TeamRejectReason.Locked, (await game.TeamChangeAsync(mover, 4, 64)).Reason);

        options.AllowSelfTeamChange = false;
        Assert.Equal(TeamRejectReason.NotPermitted, (await game.TeamChangeAsync(mover, 2, 65)).Reason);
        Assert.Equal(3, game.Rig.Teams.TeamOf(mover.PlayerId));
    }

    // ------------------------------------------------------------------ 4. the authority's own player

    [Fact]
    public async Task MovingTheAuthoritysPlayerIsRefusedWhileRunningAndAllowedOtherwise()
    {
        await using var running = await StartAsync(PerPlayer());
        int boss = running.Authority.PlayerId;
        int team = running.Rig.Teams.TeamOf(boss)!.Value;

        var refused = await running.Rig.Teams.AssignPlayerAsync(boss, 3);
        Assert.False(refused.Ok);
        Assert.Equal(TeamRejectReason.SessionRunningRestricted, refused.Reason);
        Assert.Equal(team, running.Rig.Teams.TeamOf(boss));
        Assert.False(await running.Rig.Teams.UnassignPlayerAsync(boss));
        Assert.Equal(TeamRejectReason.SessionRunningRestricted, (await running.Rig.Teams.DeleteTeamAsync(team, moveMembersTo: 3)).Reason);

        // Other players, and the authority's role inside its own team, are not moves.
        Assert.True((await running.Rig.Teams.AssignPlayerAsync(running.Clients[0].PlayerId, 3)).Ok);
        Assert.True((await running.Rig.Teams.AssignPlayerAsync(boss, team, TeamRole.Leader)).Ok);

        await using var loading = await StartAsync(PerPlayer(), running: false);
        Assert.NotEqual(SessionPhase.Running, (await loading.Rig.Rig.SnapshotAsync()).Phase);
        var allowed = await loading.Rig.Teams.AssignPlayerAsync(loading.Authority.PlayerId, 3);
        Assert.True(allowed.Ok);
        Assert.Equal(3, loading.Rig.Teams.TeamOf(loading.Authority.PlayerId));
    }

    [Fact]
    public async Task TheAuthoritysSelfTeamChangeIsRefusedWhileRunning()
    {
        var options = PerPlayer();
        options.AllowSelfTeamChange = true;
        await using var game = await StartAsync(options);
        var result = await game.TeamChangeAsync(game.Authority, 3, 71);
        Assert.Equal(TeamRejectReason.SessionRunningRestricted, result.Reason);
        Assert.Equal(1, game.Rig.Teams.TeamOf(game.Authority.PlayerId));
    }

    // ------------------------------------------------------------------ 5. SessionSettings

    [Fact]
    public async Task ATeamPolicyChangePushesSessionSettingsToEveryNodeAndAnUnchangedPolicyDoesNot()
    {
        await using var game = await StartAsync(PerPlayer());
        Assert.All(game.All, c => Assert.Equal(1u, c.State.SettingsVersion));
        Assert.All(game.All, c => Assert.Equal(RelationChangePolicy.AdminOnly, c.State.Policy!.RelationChangePolicy));

        var snapshot = new SessionSettingsSnapshot(2, new Dictionary<string, System.Text.Json.JsonElement>());
        await game.Rig.Rig.Actor.PushAsync(snapshot, default); // nothing changed in the team policy
        await game.Rig.SettleAsync();
        game.PumpAll();
        Assert.All(game.All, c => Assert.Equal(1L, c.State.SettingsUpdates)); // the Welcome's copy only

        game.Rig.Options.RelationChangePolicy = RelationChangePolicy.LeadersMutualAlly;
        game.Rig.Options.AllowFriendlyFire = true;
        await game.Rig.Rig.Actor.PushAsync(snapshot with { Version = 3 }, default);
        await game.Rig.SettleAsync();
        game.PumpAll();

        Assert.All(game.All, c =>
        {
            Assert.Equal(2u, c.State.SettingsVersion);
            Assert.Equal(2L, c.State.SettingsUpdates);
            Assert.Equal(RelationChangePolicy.LeadersMutualAlly, c.State.Policy!.RelationChangePolicy);
            Assert.True(c.State.Policy.AllowFriendlyFire);
        });

        // A later join sees the new version in its Welcome.
        var late = await game.Rig.JoinAsync("Late");
        Assert.Equal(2u, late.Welcome.Settings.Version);
        Assert.Equal(RelationChangePolicy.LeadersMutualAlly, late.Welcome.Settings.Team.RelationChangePolicy);
    }

    [Fact]
    public async Task EconomySettingsSuppliedByTheEconomyGoIntoThePush()
    {
        await using var game = await StartAsync(PerPlayer());
        game.Rig.Teams.EconomySettings = new EconomySettingsT { CreditMode = CreditMode.PerPlayer, MaxTransferAmount = 500 };
        game.Rig.Teams.PushSettings();
        await game.Rig.SettleAsync();
        game.PumpAll();

        Assert.All(game.All, c =>
        {
            Assert.Equal(2u, c.State.SettingsVersion);
            Assert.Equal(500L, c.State.Economy!.MaxTransferAmount);
        });
    }
}
