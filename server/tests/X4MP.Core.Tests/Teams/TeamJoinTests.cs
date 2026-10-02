using X4MP.Core.Teams;
using X4MP.Core.Tests.Session;
using X4MP.Proto;
using TeamRelation = X4MP.Core.Teams.TeamRelation;

namespace X4MP.Core.Tests.Teams;

/// <summary>
/// M1-T2 acceptance on the real <c>SessionActor</c> (fake clock, fake connections): each join mode places six clients as
/// expected, a wrong team password or a locked team is rejected, an AdminAssign client goes no further until assigned, the
/// authority needs a team, and a reconnecting player skips <c>AwaitingTeam</c>.
/// </summary>
public class TeamJoinTests
{
    private static async Task AssertPhasesAsync(TeamRig rig, IEnumerable<JoinedNode> nodes, NodePhase expected)
    {
        foreach (var node in nodes)
        {
            Assert.Equal(expected, await rig.PhaseAsync(node));
        }
    }

    // ------------------------------------------------------------------ Auto

    [Fact]
    public async Task AutoSingleTeamPutsSixClientsInTeamOneAndMovesThemOn()
    {
        await using var rig = new TeamRig();
        var nodes = await rig.JoinManyAsync(6);

        var team = Assert.Single(rig.Teams.Teams);
        Assert.Equal(1, team.TeamId);
        Assert.Equal(1, team.FactionSlot);
        Assert.Equal(nodes.Select(n => n.PlayerId).Order(), rig.Teams.MembersOf(team.TeamId));
        Assert.All(nodes, n => Assert.Equal(1, n.Welcome.TeamId));
        await AssertPhasesAsync(rig, nodes, NodePhase.SyncingSave);
        Assert.DoesNotContain(rig.Recorder.Log, l => l.Contains("AwaitingTeam", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WelcomeCarriesTheTeamFieldsTablesRelationsAndPolicy()
    {
        await using var rig = new TeamRig();
        var first = await rig.JoinAsync("C1");
        var second = await rig.JoinAsync("C2");
        await rig.SettleAsync();

        var welcome = second.Welcome;
        Assert.Equal(1, welcome.TeamId);
        Assert.Equal(1, welcome.FactionSlot);
        Assert.Equal(TeamRole.Member, welcome.TeamRole);
        Assert.Equal(TeamRole.Leader, first.Welcome.TeamRole); // the first member leads
        Assert.True(welcome.Teams.Full);
        var info = Assert.Single(welcome.Teams.Teams);
        Assert.Equal("Everyone", info.Name);
        Assert.Equal(new[] { first.PlayerId, second.PlayerId }.Select(i => (ushort)i), info.Members.Select(m => m.PlayerId));
        Assert.Equal(info.Members.Select(m => m.PlayerId).First(), info.LeaderPlayer);
        Assert.True(welcome.Relations.Full);
        Assert.Equal(X4MP.Proto.TeamRelation.Neutral, welcome.Relations.DefaultRelation);
        Assert.Equal(TeamJoinMode.Auto, welcome.Settings.Team.JoinMode);
        Assert.Equal(AutoAssignStrategy.SingleTeam, welcome.Settings.Team.AutoAssign);
        Assert.Equal(8, welcome.Settings.Team.MaxTeams);
        Assert.Equal(TeamAssetPolicy.SharedCommand, welcome.Settings.Team.AssetPolicy);
    }

    [Fact]
    public async Task TheRosterCarriesTheTeamOfEveryPlayer()
    {
        await using var rig = new TeamRig(new TeamOptions { AutoAssign = AutoAssignStrategy.NewTeamPerPlayer });
        var nodes = await rig.JoinManyAsync(3);

        var roster = nodes[2].Connection.SentOf(MsgType.RosterUpdate)[0].Decode<RosterUpdate>();
        var byPlayer = Enumerable.Range(0, roster.PlayersLength).Select(i => roster.Players(i)!.Value).ToDictionary(p => (int)p.PlayerId, p => (int)p.TeamId);
        Assert.Equal(rig.Teams.TeamOf(nodes[2].PlayerId), byPlayer[nodes[2].PlayerId]);
        Assert.NotEqual(0, byPlayer[nodes[2].PlayerId]);
    }

    [Fact]
    public async Task AutoBalanceSplitsSixClientsThreeAndThreeAcrossTheTwoTeamsPreset()
    {
        await using var rig = new TeamRig(new TeamOptions { AutoAssign = AutoAssignStrategy.Balance });
        var preset = await rig.Teams.ApplyPresetAsync(TeamPreset.TwoTeams);
        Assert.True(preset.Ok);

        var nodes = await rig.JoinManyAsync(6);

        Assert.Equal(2, rig.Teams.Teams.Count);
        var sizes = rig.Teams.Teams.Select(t => rig.Teams.MembersOf(t.TeamId).Count).ToArray();
        Assert.Equal([3, 3], sizes);
        Assert.Equal(TeamRelation.Hostile, rig.Teams.RelationBetween(rig.Teams.Teams[0].TeamId, rig.Teams.Teams[1].TeamId));
        // alternating, lowest id first on ties
        Assert.Equal(
            [1, 2, 1, 2, 1, 2],
            nodes.Select(n => rig.Teams.TeamOf(n.PlayerId)!.Value));
        await AssertPhasesAsync(rig, nodes, NodePhase.SyncingSave);
    }

    [Fact]
    public async Task AutoNewTeamPerPlayerGivesSixClientsSixTeamsInSixFactionSlots()
    {
        await using var rig = new TeamRig(new TeamOptions { AutoAssign = AutoAssignStrategy.NewTeamPerPlayer });
        var nodes = await rig.JoinManyAsync(6);

        Assert.Equal(6, rig.Teams.Teams.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6], rig.Teams.Teams.Select(t => t.FactionSlot));
        Assert.Equal(["C1", "C2", "C3", "C4", "C5", "C6"], rig.Teams.Teams.Select(t => t.Name));
        Assert.All(nodes, n => Assert.Single(rig.Teams.MembersOf(rig.Teams.TeamOf(n.PlayerId)!.Value)));
        Assert.Equal(6, nodes.Select(n => rig.Teams.TeamOf(n.PlayerId)).Distinct().Count());
        await AssertPhasesAsync(rig, nodes, NodePhase.SyncingSave);
    }

    [Fact]
    public async Task AutoNewTeamPerPlayerRefusesTheNinthPlayerWithNoFactionSlot()
    {
        await using var rig = new TeamRig(new TeamOptions { AutoAssign = AutoAssignStrategy.NewTeamPerPlayer });
        var nodes = await rig.JoinManyAsync(8);

        var ninth = await rig.JoinAsync("C9");
        await rig.SettleAsync();

        Assert.False(ninth.Accepted);
        Assert.Equal(DisconnectCode.NoFactionSlot, ninth.Verdict.Code);
        Assert.Equal(8, rig.Teams.Teams.Count);
        Assert.Null(rig.Teams.TeamOf(ninth.PlayerId));
        var snapshot = await rig.Rig.SnapshotAsync();
        Assert.Equal(8, snapshot.Nodes.Count); // the refused player does not stay in the roster
        await AssertPhasesAsync(rig, nodes, NodePhase.SyncingSave);
    }

    [Fact]
    public void TheOptionDefaultsFollowAdr017()
    {
        var o = new TeamOptions();

        Assert.Equal(TeamJoinMode.Auto, o.JoinMode);
        Assert.Equal(AutoAssignStrategy.SingleTeam, o.AutoAssign);
        Assert.False(o.AllowSelfTeamChange);
        Assert.Equal(MoveAssetsScope.ShipOnly, o.MoveAssetsWithPlayer);
        Assert.Equal(TeamAssetPolicy.SharedCommand, o.AssetPolicy);
        Assert.Equal(TeamRelation.Neutral, o.DefaultRelation);
        Assert.Equal(8, o.MaxTeams);
        Assert.Equal(RelationChangePolicy.AdminOnly, o.RelationChangePolicy);
        Assert.Equal(300, o.LobbyTimeoutSeconds);
        Assert.False(o.AllowCreateInLobby);
        Assert.False(o.AllowFriendlyFire);
        Assert.False(o.AllowAssetTransfer);
    }

    [Fact]
    public async Task ObserversNeedNoTeam()
    {
        await using var rig = new TeamRig(new TeamOptions { JoinMode = TeamJoinMode.AdminAssign });
        var observer = await rig.JoinAsync("Watcher", Role.Observer);
        await rig.SettleAsync();

        Assert.Equal(NodePhase.InGame, await rig.PhaseAsync(observer));
        Assert.Equal(0, observer.Welcome.TeamId);
        Assert.Empty(await rig.Teams.GetUnassignedAsync());
    }

    // ------------------------------------------------------------------ Lobby

    private static TeamOptions Lobby(bool allowCreate = false) => new() { JoinMode = TeamJoinMode.Lobby, AllowCreateInLobby = allowCreate };

    [Fact]
    public async Task LobbyClientWaitsInAwaitingTeamWithTheTableInItsWelcome()
    {
        await using var rig = new TeamRig(Lobby());
        await rig.Teams.CreateTeamAsync("Red");
        await rig.Teams.CreateTeamAsync("Blue", password: "secret");

        var node = await rig.JoinAsync("C1");
        await rig.SettleAsync();

        Assert.Equal(0, node.Welcome.TeamId);
        Assert.Equal(NodePhase.AwaitingTeam, await rig.PhaseAsync(node));
        Assert.Equal(["Red", "Blue"], node.Welcome.Teams.Teams.Select(t => t.Name));
        Assert.Equal([false, true], node.Welcome.Teams.Teams.Select(t => t.PasswordProtected));
        Assert.Equal(TeamJoinMode.Lobby, node.Welcome.Settings.Team.JoinMode);
        Assert.Null(rig.Teams.TeamOf(node.PlayerId));
        var waiting = Assert.Single(await rig.Teams.GetUnassignedAsync());
        Assert.Equal("C1", waiting.Name);
    }

    [Fact]
    public async Task LobbyPlacesSixClientsWhereTheyChoseAndAClientMayCreateATeam()
    {
        await using var rig = new TeamRig(Lobby(allowCreate: true));
        var red = (await rig.Teams.CreateTeamAsync("Red")).Value!.Id;
        var blue = (await rig.Teams.CreateTeamAsync("Blue")).Value!.Id;
        var nodes = await rig.JoinManyAsync(6);
        await AssertPhasesAsync(rig, nodes, NodePhase.AwaitingTeam);

        foreach (var n in nodes.Take(3))
        {
            var result = await rig.ChooseAsync(n, red);
            Assert.Equal(TeamRequestStatus.Ok, result.Status);
            Assert.Equal(red, result.TeamId);
        }

        foreach (var n in nodes.Skip(3).Take(2))
        {
            Assert.Equal(TeamRequestStatus.Ok, (await rig.ChooseAsync(n, blue)).Status);
        }

        var created = await rig.CreateAsync(nodes[5], "Green");
        Assert.Equal(TeamRequestStatus.Ok, created.Status);

        Assert.Equal(3, rig.Teams.Teams.Count);
        Assert.Equal(3, rig.Teams.MembersOf(red).Count);
        Assert.Equal(2, rig.Teams.MembersOf(blue).Count);
        Assert.Equal([nodes[5].PlayerId], rig.Teams.MembersOf(created.TeamId));
        Assert.Equal(3, rig.Teams.Teams.Single(t => t.TeamId == created.TeamId).FactionSlot); // lowest free slot
        await AssertPhasesAsync(rig, nodes, NodePhase.SyncingSave);
        Assert.Empty(await rig.Teams.GetUnassignedAsync());
    }

    [Fact]
    public async Task LobbyRejectsAWrongTeamPasswordAndAcceptsTheRightProof()
    {
        await using var rig = new TeamRig(Lobby());
        var secret = (await rig.Teams.CreateTeamAsync("Secret", password: "swordfish")).Value!.Id;
        var node = await rig.JoinAsync("C1");
        await rig.SettleAsync();

        var none = await rig.ChooseAsync(node, secret);
        var wrong = await rig.ChooseAsync(node, secret, password: TeamRig.Proof(node, "wrong"));
        var plain = await rig.ChooseAsync(node, secret, password: System.Text.Encoding.UTF8.GetBytes("swordfish")); // the password itself is no proof

        Assert.Equal(TeamRejectReason.BadPassword, none.Reason);
        Assert.Equal(TeamRejectReason.BadPassword, wrong.Reason);
        Assert.Equal(TeamRejectReason.BadPassword, plain.Reason);
        Assert.Equal(TeamRequestStatus.Rejected, wrong.Status);
        Assert.Equal(NodePhase.AwaitingTeam, await rig.PhaseAsync(node));
        Assert.Null(rig.Teams.TeamOf(node.PlayerId));

        var right = await rig.ChooseAsync(node, secret, password: TeamRig.Proof(node, "swordfish"));

        Assert.Equal(TeamRequestStatus.Ok, right.Status);
        Assert.Equal(secret, rig.Teams.TeamOf(node.PlayerId));
        Assert.Equal(NodePhase.SyncingSave, await rig.PhaseAsync(node));
    }

    [Fact]
    public async Task APasswordProofIsBoundToTheConnectionNonce()
    {
        await using var rig = new TeamRig(Lobby());
        var secret = (await rig.Teams.CreateTeamAsync("Secret", password: "pw")).Value!.Id;
        var first = await rig.JoinAsync("C1");
        var second = await rig.JoinAsync("C2");
        await rig.SettleAsync();

        var replay = await rig.ChooseAsync(second, secret, password: TeamRig.Proof(first, "pw")); // a proof meant for another connection

        Assert.Equal(TeamRejectReason.BadPassword, replay.Reason);
    }

    [Fact]
    public async Task LobbyRejectsALockedAFullAndAnUnknownTeam()
    {
        await using var rig = new TeamRig(Lobby());
        var locked = (await rig.Teams.CreateTeamAsync("Locked", locked: true)).Value!.Id;
        var tiny = (await rig.Teams.CreateTeamAsync("Tiny", maxMembers: 1)).Value!.Id;
        var open = (await rig.Teams.CreateTeamAsync("Open")).Value!.Id;
        var a = await rig.JoinAsync("C1");
        var b = await rig.JoinAsync("C2");
        await rig.SettleAsync();

        Assert.Equal(TeamRejectReason.Locked, (await rig.ChooseAsync(a, locked)).Reason);
        Assert.Equal(TeamRejectReason.UnknownTeam, (await rig.ChooseAsync(a, 99)).Reason);
        Assert.Equal(TeamRequestStatus.Ok, (await rig.ChooseAsync(a, tiny)).Status);
        Assert.Equal(TeamRejectReason.Full, (await rig.ChooseAsync(b, tiny)).Reason);
        Assert.Equal(NodePhase.AwaitingTeam, await rig.PhaseAsync(b));
        Assert.Equal(TeamRequestStatus.Ok, (await rig.ChooseAsync(b, open)).Status);
        Assert.Empty(rig.Teams.MembersOf(locked));
    }

    [Fact]
    public async Task ADuplicateRequestKeyGetsTheOriginalAnswerAndChangesNothing()
    {
        await using var rig = new TeamRig(Lobby());
        var locked = (await rig.Teams.CreateTeamAsync("Locked", locked: true)).Value!.Id;
        var open = (await rig.Teams.CreateTeamAsync("Open")).Value!.Id;
        var node = await rig.JoinAsync("C1");
        await rig.SettleAsync();

        var first = await rig.ChooseAsync(node, locked, requestKey: 77);
        int changes = rig.Versions.Count;
        var again = await rig.ChooseAsync(node, open, requestKey: 77); // a retry of the same request: the original answer, nothing is done

        Assert.Equal(TeamRejectReason.Locked, first.Reason);
        Assert.Equal(TeamRejectReason.Locked, again.Reason);
        Assert.Equal(changes, rig.Versions.Count);
        Assert.Null(rig.Teams.TeamOf(node.PlayerId));

        var fresh = await rig.ChooseAsync(node, open, requestKey: 78);
        Assert.Equal(TeamRequestStatus.Ok, fresh.Status);
        Assert.Equal(open, rig.Teams.TeamOf(node.PlayerId));
    }

    [Fact]
    public async Task LobbyTeamCreationNeedsTheSetting()
    {
        await using var rig = new TeamRig(Lobby(allowCreate: false));
        var node = await rig.JoinAsync("C1");
        await rig.SettleAsync();

        var result = await rig.CreateAsync(node, "Mine");

        Assert.Equal(TeamRejectReason.NotPermitted, result.Reason);
        Assert.Empty(rig.Teams.Teams);
    }

    [Fact]
    public async Task LobbyCreationReportsNameTakenAndNoFactionSlot()
    {
        await using var rig = new TeamRig(Lobby(allowCreate: true) with { MaxTeams = 2 });
        await rig.Teams.CreateTeamAsync("Red");
        var node = await rig.JoinAsync("C1");
        await rig.SettleAsync();

        Assert.Equal(TeamRejectReason.NameTaken, (await rig.CreateAsync(node, "red")).Reason);
        Assert.Equal(TeamRequestStatus.Ok, (await rig.CreateAsync(node, "Blue")).Status);

        var second = await rig.JoinAsync("C2");
        await rig.SettleAsync();
        Assert.Equal(TeamRejectReason.NoFactionSlot, (await rig.CreateAsync(second, "Green")).Reason); // MaxTeams = 2
    }

    [Fact]
    public async Task ALobbyNodeThatKeepsFailingIsRateLimited()
    {
        await using var rig = new TeamRig(Lobby());
        var team = (await rig.Teams.CreateTeamAsync("Locked", locked: true)).Value!.Id;
        var open = (await rig.Teams.CreateTeamAsync("Open")).Value!.Id;
        var node = await rig.JoinAsync("C1");
        await rig.SettleAsync();

        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(TeamRejectReason.Locked, (await rig.ChooseAsync(node, team)).Reason);
        }

        Assert.Equal(TeamRejectReason.RateLimited, (await rig.ChooseAsync(node, open)).Reason);
        Assert.Null(rig.Teams.TeamOf(node.PlayerId));
    }

    [Fact]
    public async Task LobbyFallsBackToAutoAfterTheTimeout()
    {
        await using var rig = new TeamRig(Lobby() with { AutoAssign = AutoAssignStrategy.NewTeamPerPlayer });
        var node = await rig.JoinAsync("C1");
        await rig.SettleAsync();

        await rig.AdvanceSecondsAsync(299);
        Assert.Equal(NodePhase.AwaitingTeam, await rig.PhaseAsync(node));

        await rig.AdvanceSecondsAsync(2);

        Assert.Equal(NodePhase.SyncingSave, await rig.PhaseAsync(node));
        var team = Assert.Single(rig.Teams.Teams);
        Assert.Equal("C1", team.Name);
        Assert.Equal(team.TeamId, rig.Teams.TeamOf(node.PlayerId));
        Assert.Equal("lobby-timeout", rig.Teams.Snapshot().Members.Single().AssignedBy);
    }

    [Fact]
    public async Task TheLobbyTimeoutIsALiveSetting()
    {
        await using var rig = new TeamRig(Lobby());
        rig.Options.LobbyTimeoutSeconds = 10;
        var node = await rig.JoinAsync("C1");
        await rig.SettleAsync();

        await rig.AdvanceSecondsAsync(11);

        Assert.Equal(NodePhase.SyncingSave, await rig.PhaseAsync(node));
    }

    [Fact]
    public async Task ALobbyFallbackThatFindsNoSlotRemovesTheNode()
    {
        await using var rig = new TeamRig(Lobby() with { AutoAssign = AutoAssignStrategy.NewTeamPerPlayer, MaxTeams = 1 });
        await rig.Teams.CreateTeamAsync("Only");
        var node = await rig.JoinAsync("C1");
        await rig.SettleAsync();

        await rig.AdvanceSecondsAsync(301);

        Assert.DoesNotContain((await rig.Rig.SnapshotAsync()).Nodes, n => n.PlayerId == node.PlayerId);
        Assert.Equal(DisconnectCode.NoFactionSlot, node.Connection.CloseCode);
    }

    // ------------------------------------------------------------------ AdminAssign

    [Fact]
    public async Task AdminAssignClientWaitsAndDoesNotAdvanceUntilAssigned()
    {
        await using var rig = new TeamRig(new TeamOptions { JoinMode = TeamJoinMode.AdminAssign });
        var team = (await rig.Teams.CreateTeamAsync("Red")).Value!.Id;
        var nodes = await rig.JoinManyAsync(6);

        await AssertPhasesAsync(rig, nodes, NodePhase.AwaitingTeam);
        Assert.Equal(6, (await rig.Teams.GetUnassignedAsync()).Count);

        // No save step before a team: the node may not skip ahead, and the policy refuses its save traffic in this phase.
        await rig.Rig.LoadStatusAsync(nodes[0], NodePhase.SyncingSave);
        Assert.Equal(NodePhase.AwaitingTeam, await rig.PhaseAsync(nodes[0]));

        // and there is no timeout: an hour later it still waits
        await rig.AdvanceSecondsAsync(3600);
        await AssertPhasesAsync(rig, nodes, NodePhase.AwaitingTeam);

        foreach (var n in nodes.Take(4))
        {
            var result = await rig.Teams.AssignPlayerAsync(n.PlayerId, team);
            Assert.True(result.Ok);
        }

        await rig.SettleAsync();
        await AssertPhasesAsync(rig, nodes.Take(4), NodePhase.SyncingSave);
        await AssertPhasesAsync(rig, nodes.Skip(4), NodePhase.AwaitingTeam);
        Assert.Equal([nodes[4].PlayerId, nodes[5].PlayerId], (await rig.Teams.GetUnassignedAsync()).Select(u => u.PlayerId));
        Assert.Equal(4, rig.Teams.MembersOf(team).Count);
    }

    [Fact]
    public async Task AdminAssignPlacesSixClientsAsTheAdminDecides()
    {
        await using var rig = new TeamRig(new TeamOptions { JoinMode = TeamJoinMode.AdminAssign });
        var red = (await rig.Teams.CreateTeamAsync("Red")).Value!.Id;
        var blue = (await rig.Teams.CreateTeamAsync("Blue")).Value!.Id;
        var nodes = await rig.JoinManyAsync(6);

        for (int i = 0; i < 6; i++)
        {
            Assert.True((await rig.Teams.AssignPlayerAsync(nodes[i].PlayerId, i % 2 == 0 ? red : blue, assignedBy: "admin:root")).Ok);
        }

        await rig.SettleAsync();
        Assert.Equal(3, rig.Teams.MembersOf(red).Count);
        Assert.Equal(3, rig.Teams.MembersOf(blue).Count);
        await AssertPhasesAsync(rig, nodes, NodePhase.SyncingSave);
        Assert.Equal("admin:root", rig.Teams.Snapshot().Members[0].AssignedBy);
    }

    [Fact]
    public async Task AdminAssignToAnUnknownTeamFailsAndTheNodeKeepsWaiting()
    {
        await using var rig = new TeamRig(new TeamOptions { JoinMode = TeamJoinMode.AdminAssign });
        var node = await rig.JoinAsync("C1");
        await rig.SettleAsync();

        var result = await rig.Teams.AssignPlayerAsync(node.PlayerId, 42);

        Assert.False(result.Ok);
        Assert.Equal(TeamRejectReason.UnknownTeam, result.Reason);
        Assert.Equal(NodePhase.AwaitingTeam, await rig.PhaseAsync(node));
    }

    // ------------------------------------------------------------------ the authority gate

    [Fact]
    public async Task AnAuthorityWithoutATeamIsGatedUntilAnAdminAssignsIt()
    {
        await using var rig = new TeamRig(new TeamOptions { JoinMode = TeamJoinMode.AdminAssign });
        var team = (await rig.Teams.CreateTeamAsync("Red")).Value!.Id;
        var authority = await rig.JoinAsync("Boss", Role.Authority | Role.Client);
        await rig.SettleAsync();

        Assert.True(rig.Teams.AuthorityAwaitingTeam);
        Assert.Equal(NodePhase.AwaitingTeam, await rig.PhaseAsync(authority));

        // Save and world traffic from the authority is dropped before anyone else sees it.
        await rig.Rig.SendAsync(authority, MsgType.SaveStarted, TeamFrames.Empty());
        await rig.Rig.SendAsync(authority, MsgType.GalaxyMetadata, TeamFrames.Empty());
        Assert.Empty(rig.Recorder.Messages);

        Assert.True((await rig.Teams.AssignPlayerAsync(authority.PlayerId, team)).Ok);
        await rig.SettleAsync();

        Assert.False(rig.Teams.AuthorityAwaitingTeam);
        Assert.Equal(NodePhase.SyncingSave, await rig.PhaseAsync(authority));
        await rig.Rig.SendAsync(authority, MsgType.SaveStarted, TeamFrames.Empty());
        Assert.Equal([MsgType.SaveStarted], rig.Recorder.Messages.Select(m => m.Type));
    }

    [Fact]
    public async Task AnAuthorityIsPlacedLikeAnyoneUnderAuto()
    {
        await using var rig = new TeamRig();
        var authority = await rig.JoinAsync("Boss", Role.Authority | Role.Client);
        await rig.SettleAsync();

        Assert.Equal(1, authority.Welcome.TeamId);
        Assert.False(rig.Teams.AuthorityAwaitingTeam);
        Assert.Equal(NodePhase.SyncingSave, await rig.PhaseAsync(authority));
    }

    [Fact]
    public async Task AnAuthorityMayPickItsTeamInTheLobby()
    {
        await using var rig = new TeamRig(Lobby());
        var team = (await rig.Teams.CreateTeamAsync("Red")).Value!.Id;
        var authority = await rig.JoinAsync("Boss", Role.Authority | Role.Client);
        await rig.SettleAsync();
        Assert.True(rig.Teams.AuthorityAwaitingTeam);

        var result = await rig.ChooseAsync(authority, team);

        Assert.Equal(TeamRequestStatus.Ok, result.Status);
        Assert.False(rig.Teams.AuthorityAwaitingTeam);
        Assert.Equal(NodePhase.SyncingSave, await rig.PhaseAsync(authority));
    }

    [Fact]
    public async Task AnAuthorityWithoutTheClientRoleCannotChooseSoItIsAutoAssignedInTheLobby()
    {
        await using var rig = new TeamRig(Lobby());
        var authority = await rig.JoinAsync("Boss", Role.Authority);
        await rig.SettleAsync();

        Assert.Equal(1, authority.Welcome.TeamId);
        Assert.Equal(NodePhase.SyncingSave, await rig.PhaseAsync(authority));
    }

    // ------------------------------------------------------------------ sticky membership

    [Fact]
    public async Task AReconnectingPlayerKeepsItsTeamAndSkipsAwaitingTeam()
    {
        await using var rig = new TeamRig(Lobby());
        await rig.Teams.CreateTeamAsync("Red");
        var blue = (await rig.Teams.CreateTeamAsync("Blue")).Value!.Id;
        var first = await rig.JoinAsync("C1");
        await rig.SettleAsync();
        await rig.ChooseAsync(first, blue);
        await rig.Rig.DisconnectAsync(first, DisconnectCode.ClientQuit); // leaves for good
        await rig.SettleAsync();
        Assert.Equal(1, rig.Recorder.Log.Count(l => l.EndsWith("Admitted->AwaitingTeam", StringComparison.Ordinal)));

        var again = await rig.JoinAsync("C1");
        await rig.SettleAsync();

        Assert.Equal(blue, again.Welcome.TeamId);
        Assert.Equal(2, again.Welcome.FactionSlot);
        Assert.Equal(NodePhase.SyncingSave, await rig.PhaseAsync(again));
        Assert.Equal(1, rig.Recorder.Log.Count(l => l.EndsWith("Admitted->AwaitingTeam", StringComparison.Ordinal))); // still only the first join
    }

    [Fact]
    public async Task AResumedNodeKeepsItsTeamAndNeverReEntersAwaitingTeam()
    {
        await using var rig = new TeamRig(Lobby());
        var team = (await rig.Teams.CreateTeamAsync("Red")).Value!.Id;
        var first = await rig.JoinAsync("C1");
        await rig.SettleAsync();
        await rig.ChooseAsync(first, team);
        first.Connection.Drop();
        await rig.WaitForPhaseAsync(first, NodePhase.Detached);

        var resumed = await rig.Rig.ResumeAsync("C1", first);
        await rig.SettleAsync();

        Assert.True(resumed.Welcome.Resumed);
        Assert.Equal(team, resumed.Welcome.TeamId);
        Assert.Equal(1, rig.Recorder.Log.Count(l => l.EndsWith("->AwaitingTeam", StringComparison.Ordinal)));
        Assert.Equal(NodePhase.SyncingSave, await rig.PhaseAsync(resumed));
    }

    [Fact]
    public async Task ANodeAssignedWhileDetachedResumesPastAwaitingTeam()
    {
        await using var rig = new TeamRig(new TeamOptions { JoinMode = TeamJoinMode.AdminAssign });
        var team = (await rig.Teams.CreateTeamAsync("Red")).Value!.Id;
        var first = await rig.JoinAsync("C1");
        await rig.SettleAsync();
        first.Connection.Drop();
        await rig.SettleAsync();
        await rig.WaitForPhaseAsync(first, NodePhase.Detached);

        await rig.Teams.AssignPlayerAsync(first.PlayerId, team);
        var resumed = await rig.Rig.ResumeAsync("C1", first);
        await rig.SettleAsync();

        Assert.Equal(team, resumed.Welcome.TeamId);
        Assert.Equal(NodePhase.SyncingSave, await rig.PhaseAsync(resumed));
    }

    [Fact]
    public async Task MembershipsSurviveARestartThroughTheStore()
    {
        var store = new MemoryTeamStore();
        int redId;
        int[] ids;
        await using (var before = new TeamRig(Lobby(), store))
        {
            redId = (await before.Teams.CreateTeamAsync("Red")).Value!.Id;
            await before.Teams.CreateTeamAsync("Blue", password: "pw");
            await before.Teams.SetRelationAsync(1, 2, TeamRelation.Hostile);
            var a = await before.JoinAsync("C1");
            var b = await before.JoinAsync("C2");
            await before.SettleAsync();
            await before.ChooseAsync(a, redId);
            await before.ChooseAsync(b, redId);
            ids = [a.PlayerId, b.PlayerId];
        }

        Assert.Equal([(1, 2, TeamRelation.Hostile)], store.Latest!.Relations);
        await using var after = new TeamRig(Lobby(), store); // a new process: the module loads from the store
        Assert.Equal([(1, 2, TeamRelation.Hostile)], after.Teams.Snapshot().Relations);
        Assert.Equal([(1, 2, TeamRelation.Hostile)], after.Teams.Matrix.Entries);
        Assert.Equal(["Red", "Blue"], after.Teams.Teams.Select(t => t.Name));
        Assert.Equal(TeamRelation.Hostile, after.Teams.RelationBetween(1, 2));
        Assert.Equal(ids, after.Teams.MembersOf(redId));
        Assert.True(after.Teams.TeamDetails.Single(t => t.Name == "Blue").PasswordProtected);

        var returning = await after.JoinAsync("C1");
        await after.JoinAsync("C2");
        var stranger = await after.JoinAsync("C3");
        await after.SettleAsync();

        Assert.Equal(redId, returning.Welcome.TeamId);
        Assert.Equal(NodePhase.SyncingSave, await after.PhaseAsync(returning));
        Assert.Equal(NodePhase.AwaitingTeam, await after.PhaseAsync(stranger)); // not known: the lobby applies
        Assert.DoesNotContain(after.Recorder.Log, l => l.Contains($"node:{returning.PlayerId}:Admitted->AwaitingTeam", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryChangeIsSavedUnderTheSessionRow()
    {
        await using var rig = new TeamRig();
        await rig.JoinAsync("C1");
        await rig.SettleAsync();

        var (session, snapshot) = rig.Store.Saves[^1];
        Assert.Equal(1, session);
        Assert.Single(snapshot.Teams);
        Assert.Single(snapshot.Members);
    }

    [Fact]
    public async Task ChangesMadeBeforeTheSessionRowExistsAreSavedOnceItDoes()
    {
        await using var rig = new TeamRig();
        await rig.Teams.CreateTeamAsync("Early"); // nobody joined yet: no sessions row to save under
        Assert.Empty(rig.Store.Saves);

        await rig.JoinAsync("C1");
        await rig.SettleAsync();

        Assert.NotEmpty(rig.Store.Saves);
        Assert.Contains(rig.Store.Saves[^1].Snapshot.Teams, t => t.Name == "Early");
    }

    // ------------------------------------------------------------------ directory

    [Fact]
    public async Task TheDirectoryRaisesChangedWithRisingVersionsAndAnswersFromAnyThread()
    {
        await using var rig = new TeamRig(new TeamOptions { AutoAssign = AutoAssignStrategy.NewTeamPerPlayer });
        var a = await rig.JoinAsync("C1");
        await rig.JoinAsync("C2");
        await rig.SettleAsync();
        var teams = rig.Teams.Teams;
        await rig.Teams.SetRelationAsync(teams[0].TeamId, teams[1].TeamId, TeamRelation.Allied);

        Assert.NotEmpty(rig.Versions);
        Assert.Equal(rig.Versions.Order(), rig.Versions);
        Assert.Equal(rig.Versions.Count, rig.Versions.Distinct().Count());
        Assert.Equal(TeamRelation.Allied, rig.Teams.RelationBetween(teams[1].TeamId, teams[0].TeamId));
        Assert.Equal(TeamRelation.Allied, rig.Teams.RelationBetween(7, 7));
        Assert.Equal(teams[0].TeamId, rig.Teams.TeamOf(a.PlayerId));
        Assert.Null(rig.Teams.TeamOf(999));
        Assert.Empty(rig.Teams.MembersOf(999));
        Assert.Equal(teams.Select(t => t.TeamId).Order(), teams.Select(t => t.TeamId)); // ordered by id
    }

    [Fact]
    public async Task TheModuleExposesTheLeaderOfATeam()
    {
        await using var rig = new TeamRig();
        var a = await rig.JoinAsync("C1");
        var b = await rig.JoinAsync("C2");
        await rig.SettleAsync();

        Assert.Equal(a.PlayerId, rig.Teams.LeaderOf(1)); // the first member leads
        Assert.Null(rig.Teams.LeaderOf(9));

        await rig.Teams.UpdateTeamAsync(1, new TeamRegistry.TeamPatch(LeaderPlayerId: b.PlayerId));

        Assert.Equal(b.PlayerId, rig.Teams.LeaderOf(1));
        Assert.Equal(TeamRole.Leader, rig.Teams.Snapshot().Members.Single(m => m.PlayerId == b.PlayerId).Role);
        Assert.Equal(TeamRole.Member, rig.Teams.Snapshot().Members.Single(m => m.PlayerId == a.PlayerId).Role);
    }

    [Fact]
    public async Task ARelationSetBeforeAJoinIsInTheWelcomeAsAWireRelation()
    {
        await using var rig = new TeamRig(new TeamOptions { AutoAssign = AutoAssignStrategy.NewTeamPerPlayer });
        await rig.JoinAsync("C1");
        await rig.JoinAsync("C2");
        await rig.SettleAsync();
        await rig.Teams.SetRelationAsync(1, 2, TeamRelation.Hostile);

        var late = await rig.JoinAsync("C3");

        var entry = Assert.Single(late.Welcome.Relations.Entries);
        Assert.Equal((1, 2, X4MP.Proto.TeamRelation.Hostile), (entry.TeamA, entry.TeamB, entry.Relation));
        Assert.Equal(3, late.Welcome.Teams.Teams.Count);
    }

    [Fact]
    public async Task ApplyingAPresetPlacesTheConnectedPlayersAndReleasesWaitingNodes()
    {
        await using var rig = new TeamRig(new TeamOptions { JoinMode = TeamJoinMode.AdminAssign });
        var nodes = await rig.JoinManyAsync(3);

        var result = await rig.Teams.ApplyPresetAsync(TeamPreset.FreeForAll);
        await rig.SettleAsync();

        Assert.True(result.Ok);
        Assert.Equal(3, rig.Teams.Teams.Count);
        Assert.All(nodes, n => Assert.NotNull(rig.Teams.TeamOf(n.PlayerId)));
        await AssertPhasesAsync(rig, nodes, NodePhase.SyncingSave);
        Assert.Equal(TeamRelation.Hostile, rig.Teams.RelationBetween(1, 3));
    }

    [Fact]
    public async Task APerPlayerPresetPlacesOnlyAttachedNodesSoDetachedOnesCannotExhaustTheFactionSlots()
    {
        await using var rig = new TeamRig(new TeamOptions { JoinMode = TeamJoinMode.AdminAssign });
        var nodes = await rig.JoinManyAsync(10); // 10 known players: more than the 8 faction slots
        foreach (var gone in nodes.Take(3))
        {
            gone.Connection.Drop();
        }

        foreach (var gone in nodes.Take(3))
        {
            await rig.WaitForPhaseAsync(gone, NodePhase.Detached);
        }

        var result = await rig.Teams.ApplyPresetAsync(TeamPreset.FreeForAll);
        await rig.SettleAsync();

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(7, rig.Teams.Teams.Count);
        Assert.All(nodes.Skip(3), n => Assert.NotNull(rig.Teams.TeamOf(n.PlayerId)));
        Assert.All(nodes.Take(3), n => Assert.Null(rig.Teams.TeamOf(n.PlayerId))); // unassigned; the normal join path places them on return
        var preview = await rig.Teams.PreviewPresetAsync(TeamPreset.FreeForAll);
        Assert.Equal(default, preview.Blocked);
    }

    [Fact]
    public async Task TheDefaultRelationSettingAppliesToUnsetPairs()
    {
        await using var rig = new TeamRig(new TeamOptions { AutoAssign = AutoAssignStrategy.NewTeamPerPlayer, DefaultRelation = TeamRelation.Hostile });
        await rig.JoinAsync("C1");
        var second = await rig.JoinAsync("C2");
        await rig.SettleAsync();

        Assert.Equal(TeamRelation.Hostile, rig.Teams.RelationBetween(1, 2));
        Assert.Equal(X4MP.Proto.TeamRelation.Hostile, second.Welcome.Relations.DefaultRelation);
    }
}
