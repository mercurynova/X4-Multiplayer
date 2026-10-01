using X4MP.Core.Teams;
using X4MP.Proto;
using TeamRelation = X4MP.Core.Teams.TeamRelation;

namespace X4MP.Core.Tests.Teams;

/// <summary>M1-T1 acceptance: presets produce the documented tables, the matrix is symmetric and versioned, slot exhaustion gives NoFactionSlot.</summary>
public class TeamRegistryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static TeamPlayer[] Players(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new TeamPlayer(i, "Pilot" + i))];

    // ------------------------------------------------------------------ matrix

    [Fact]
    public void MatrixIsSymmetricAndTheSameTeamIsAllied()
    {
        var matrix = new TeamRelationMatrix().With(1, 2, TeamRelation.Hostile).With(3, 2, TeamRelation.Allied);

        Assert.Equal(TeamRelation.Hostile, matrix.Get(1, 2));
        Assert.Equal(TeamRelation.Hostile, matrix.Get(2, 1));
        Assert.Equal(TeamRelation.Allied, matrix.Get(2, 3));
        Assert.Equal(TeamRelation.Allied, matrix.Get(3, 2));
        Assert.Equal(TeamRelation.Neutral, matrix.Get(1, 3)); // default
        Assert.Equal(TeamRelation.Allied, matrix.Get(4, 4));
        Assert.Throws<ArgumentException>(() => matrix.With(2, 2, TeamRelation.Hostile));
    }

    [Fact]
    public void MatrixIsImmutableAndVersioned()
    {
        var empty = new TeamRelationMatrix(TeamRelation.Neutral);
        var one = empty.With(1, 2, TeamRelation.Hostile);
        var two = one.With(2, 1, TeamRelation.Allied);
        var same = two.With(1, 2, TeamRelation.Allied);

        Assert.Equal(0, empty.Version);
        Assert.Equal(1, one.Version);
        Assert.Equal(2, two.Version);
        Assert.Same(two, same); // no change, no new version
        Assert.Equal(TeamRelation.Neutral, empty.Get(1, 2)); // the older instances did not change
        Assert.Equal(TeamRelation.Hostile, one.Get(1, 2));
        Assert.Equal(3, two.Without(2).Version); // dropping a team's pairs is a change too
        Assert.Empty(two.Without(2).Entries);
    }

    [Fact]
    public void MatrixEntriesAreOrderedPairsAndTheDefaultAppliesToTheRest()
    {
        var matrix = new TeamRelationMatrix(TeamRelation.Hostile).With(3, 1, TeamRelation.Allied);

        Assert.Equal([(1, 3, TeamRelation.Allied)], matrix.Entries);
        Assert.Equal(TeamRelation.Hostile, matrix.Get(1, 2));
        Assert.Equal(matrix.Version + 1, matrix.WithDefault(TeamRelation.Neutral).Version);
        Assert.Equal(TeamRelation.Neutral, matrix.WithDefault(TeamRelation.Neutral).Get(1, 2));
        Assert.Equal(TeamRelation.Allied, matrix.WithDefault(TeamRelation.Neutral).Get(1, 3)); // explicit stays
    }

    // ------------------------------------------------------------------ slots

    [Fact]
    public void SlotsAreHandedOutLowestFirstAndExhaustWithNoFactionSlot()
    {
        var registry = new TeamRegistry();
        for (int i = 1; i <= 8; i++)
        {
            var created = registry.CreateTeam("T" + i, T0);
            Assert.True(created.Ok);
            Assert.Equal(i, created.Value!.FactionSlot);
        }

        var ninth = registry.CreateTeam("T9", T0);
        Assert.False(ninth.Ok);
        Assert.Equal(TeamRejectReason.NoFactionSlot, ninth.Reason);
        Assert.Equal(8, registry.Teams.Count);
    }

    [Fact]
    public void ADeletedTeamFreesItsSlotForTheNextOne()
    {
        var registry = new TeamRegistry();
        for (int i = 1; i <= 8; i++)
        {
            registry.CreateTeam("T" + i, T0);
        }

        Assert.True(registry.DeleteTeam(3).Ok);

        var again = registry.CreateTeam("New", T0);
        Assert.True(again.Ok);
        Assert.Equal(3, again.Value!.FactionSlot);
        Assert.Equal(9, again.Value.Id); // team ids are never reused
    }

    [Fact]
    public void MaxTeamsCapsBelowTheEightSlotsAndANamedSlotCanBeRequested()
    {
        var registry = new TeamRegistry();
        Assert.True(registry.CreateTeam("A", T0, maxTeams: 2, slot: 5).Ok);
        Assert.Equal(5, registry.Teams[0].FactionSlot);
        Assert.Equal(TeamRejectReason.NoFactionSlot, registry.CreateTeam("B", T0, maxTeams: 2, slot: 5).Reason);
        Assert.True(registry.CreateTeam("B", T0, maxTeams: 2).Ok);
        Assert.Equal(TeamRejectReason.NoFactionSlot, registry.CreateTeam("C", T0, maxTeams: 2).Reason);
    }

    [Fact]
    public void TeamNamesAreValidatedAndUniquePerSessionIgnoringCase()
    {
        var registry = new TeamRegistry();
        Assert.True(registry.CreateTeam("Alpha", T0).Ok);

        Assert.Equal(TeamRejectReason.NameTaken, registry.CreateTeam("ALPHA", T0).Reason);
        Assert.Equal(TeamRejectReason.NotPermitted, registry.CreateTeam("", T0).Reason);
        Assert.Equal(TeamRejectReason.NotPermitted, registry.CreateTeam(new string('x', 25), T0).Reason);
        Assert.True(registry.CreateTeam(new string('x', 24), T0).Ok);
    }

    // ------------------------------------------------------------------ presets (server-design 2.13)

    [Fact]
    public void CoOpPresetIsOneTeamInSlotOneWithEveryone()
    {
        var registry = new TeamRegistry();
        var result = registry.ApplyPreset(TeamPreset.CoOp, Players(4), T0);

        Assert.True(result.Ok);
        Assert.Equal(AutoAssignStrategy.SingleTeam, result.Value!.AutoAssign);
        var team = Assert.Single(registry.Teams);
        Assert.Equal(1, team.FactionSlot);
        Assert.Equal([1, 2, 3, 4], registry.MembersOf(team.Id));
        Assert.Empty(registry.Matrix.Entries); // relations: n/a
    }

    [Fact]
    public void AlliedSeparatePresetIsOneTeamPerPlayerAllAllied()
    {
        var registry = new TeamRegistry();
        var result = registry.ApplyPreset(TeamPreset.AlliedSeparate, Players(3), T0);

        Assert.True(result.Ok);
        Assert.Equal(AutoAssignStrategy.NewTeamPerPlayer, result.Value!.AutoAssign);
        AssertOneTeamPerPlayer(registry, 3);
        AssertAllPairs(registry, TeamRelation.Allied);
    }

    [Fact]
    public void FreeForAllPresetIsOneTeamPerPlayerAllHostile()
    {
        var registry = new TeamRegistry();
        var result = registry.ApplyPreset(TeamPreset.FreeForAll, Players(4), T0);

        Assert.True(result.Ok);
        Assert.Equal(AutoAssignStrategy.NewTeamPerPlayer, result.Value!.AutoAssign);
        AssertOneTeamPerPlayer(registry, 4);
        AssertAllPairs(registry, TeamRelation.Hostile);
        Assert.Equal(6, registry.Matrix.Entries.Count); // 4 teams: 6 pairs
    }

    [Fact]
    public void TwoTeamsPresetIsTwoHostileTeamsBalancedByPlayer()
    {
        var registry = new TeamRegistry();
        var result = registry.ApplyPreset(TeamPreset.TwoTeams, Players(5), T0);

        Assert.True(result.Ok);
        Assert.Equal(AutoAssignStrategy.Balance, result.Value!.AutoAssign);
        Assert.Equal(2, registry.Teams.Count);
        AssertAllPairs(registry, TeamRelation.Hostile);
        Assert.Equal([1, 3, 5], registry.MembersOf(registry.Teams[0].Id));
        Assert.Equal([2, 4], registry.MembersOf(registry.Teams[1].Id));
    }

    [Fact]
    public void TwoTeamsPresetWithoutPlayersStillCreatesBothTeams()
    {
        var registry = new TeamRegistry();
        Assert.True(registry.ApplyPreset(TeamPreset.TwoTeams, [], T0).Ok);
        Assert.Equal(2, registry.Teams.Count);
        Assert.Equal(TeamRelation.Hostile, registry.Relation(registry.Teams[0].Id, registry.Teams[1].Id));
    }

    [Fact]
    public void ApplyingAPresetReplacesTheTablesAndBumpsTheVersionOnce()
    {
        var registry = new TeamRegistry();
        registry.ApplyPreset(TeamPreset.FreeForAll, Players(3), T0);
        int before = registry.Version;
        int matrixBefore = registry.Matrix.Version;

        Assert.True(registry.ApplyPreset(TeamPreset.CoOp, Players(3), T0).Ok);

        Assert.Equal(before + 1, registry.Version);
        Assert.True(registry.Matrix.Version > matrixBefore);
        Assert.Single(registry.Teams);
        Assert.Empty(registry.Matrix.Entries);
        Assert.Equal(1, registry.Teams[0].FactionSlot);
    }

    [Fact]
    public void APerPlayerPresetWithTooManyPlayersChangesNothing()
    {
        var registry = new TeamRegistry();
        registry.ApplyPreset(TeamPreset.CoOp, Players(2), T0);
        int version = registry.Version;

        var result = registry.ApplyPreset(TeamPreset.FreeForAll, Players(9), T0);

        Assert.False(result.Ok);
        Assert.Equal(TeamRejectReason.NoFactionSlot, result.Reason);
        Assert.Equal(version, registry.Version);
        Assert.Single(registry.Teams);
        Assert.Equal([1, 2], registry.MembersOf(registry.Teams[0].Id));
    }

    private static void AssertOneTeamPerPlayer(TeamRegistry registry, int players)
    {
        Assert.Equal(players, registry.Teams.Count);
        Assert.Equal(Enumerable.Range(1, players), registry.Teams.Select(t => t.FactionSlot));
        for (int p = 1; p <= players; p++)
        {
            int team = registry.TeamOf(p)!.Value;
            Assert.Equal([p], registry.MembersOf(team));
            Assert.Equal("Pilot" + p, registry.Find(team)!.Name);
        }
    }

    private static void AssertAllPairs(TeamRegistry registry, TeamRelation expected)
    {
        foreach (var a in registry.Teams)
        {
            foreach (var b in registry.Teams.Where(t => t.Id != a.Id))
            {
                Assert.Equal(expected, registry.Relation(a.Id, b.Id));
            }
        }
    }

    // ------------------------------------------------------------------ auto-assign strategies

    [Fact]
    public void SingleTeamPutsEveryoneInTheFirstTeamAndCreatesOneWhenThereIsNone()
    {
        var registry = new TeamRegistry();
        for (int i = 1; i <= 6; i++)
        {
            Assert.True(registry.AutoAssign(new TeamPlayer(i, "P" + i), AutoAssignStrategy.SingleTeam, T0).Ok);
        }

        var team = Assert.Single(registry.Teams);
        Assert.Equal(6, registry.MemberCount(team.Id));
        Assert.Equal(1, team.LeaderPlayerId); // the first member leads
    }

    [Fact]
    public void BalancePicksTheUnlockedTeamWithTheFewestMembersLowestIdOnTies()
    {
        var registry = new TeamRegistry();
        var a = registry.CreateTeam("A", T0).Value!;
        var b = registry.CreateTeam("B", T0).Value!;
        var c = registry.CreateTeam("C", T0, locked: true).Value!;
        var d = registry.CreateTeam("D", T0, maxMembers: 1).Value!;
        registry.Assign(100, d.Id, T0, "test");

        var placed = Enumerable.Range(1, 5)
            .Select(i => registry.AutoAssign(new TeamPlayer(i, "P" + i), AutoAssignStrategy.Balance, T0).Value!.TeamId)
            .ToArray();

        Assert.Equal([a.Id, b.Id, a.Id, b.Id, a.Id], placed);
        Assert.Equal(0, registry.MemberCount(c.Id)); // locked
        Assert.Equal(1, registry.MemberCount(d.Id)); // full
    }

    [Fact]
    public void BalanceCreatesATeamWhenThereIsNoneAndFailsWhenEveryTeamIsClosed()
    {
        var registry = new TeamRegistry();
        Assert.True(registry.AutoAssign(new TeamPlayer(1, "P1"), AutoAssignStrategy.Balance, T0).Ok);
        Assert.Single(registry.Teams);

        registry.UpdateTeam(registry.Teams[0].Id, new TeamRegistry.TeamPatch(Locked: true));
        var result = registry.AutoAssign(new TeamPlayer(2, "P2"), AutoAssignStrategy.Balance, T0, maxTeams: 1);

        Assert.False(result.Ok);
        Assert.Equal(TeamRejectReason.Full, result.Reason);
    }

    [Fact]
    public void NewTeamPerPlayerCreatesATeamNamedAfterThePlayerAndFailsWhenTheSlotsAreGone()
    {
        var registry = new TeamRegistry();
        for (int i = 1; i <= 8; i++)
        {
            var result = registry.AutoAssign(new TeamPlayer(i, "Pilot" + i), AutoAssignStrategy.NewTeamPerPlayer, T0);
            Assert.True(result.Ok);
            Assert.Equal(i, registry.Find(result.Value!.TeamId)!.FactionSlot);
        }

        var ninth = registry.AutoAssign(new TeamPlayer(9, "Pilot9"), AutoAssignStrategy.NewTeamPerPlayer, T0);

        Assert.False(ninth.Ok);
        Assert.Equal(TeamRejectReason.NoFactionSlot, ninth.Reason);
        Assert.Null(registry.TeamOf(9));
        Assert.Equal(8, registry.Teams.Count);
    }

    // ------------------------------------------------------------------ membership

    [Fact]
    public void MembershipIsStickyAcrossAssignAndLeadershipFollowsTheMembers()
    {
        var registry = new TeamRegistry();
        var a = registry.CreateTeam("A", T0).Value!;
        var b = registry.CreateTeam("B", T0).Value!;
        registry.Assign(1, a.Id, T0, "auto");
        registry.Assign(2, a.Id, T0, "auto");
        Assert.Equal(1, registry.Find(a.Id)!.LeaderPlayerId);
        Assert.Equal(TeamRole.Member, registry.MembershipOf(2)!.Role);

        registry.Assign(1, b.Id, T0, "admin:root"); // the leader moves away

        Assert.Equal(b.Id, registry.TeamOf(1));
        Assert.Equal(2, registry.Find(a.Id)!.LeaderPlayerId);
        Assert.Equal(TeamRole.Leader, registry.MembershipOf(2)!.Role);
        Assert.Equal("admin:root", registry.MembershipOf(1)!.AssignedBy);
        Assert.Equal([2], registry.MembersOf(a.Id));
    }

    [Fact]
    public void DeletingATeamMovesItsMembersOrUnassignsThemAndDropsItsRelations()
    {
        var registry = new TeamRegistry();
        var a = registry.CreateTeam("A", T0).Value!;
        var b = registry.CreateTeam("B", T0).Value!;
        var c = registry.CreateTeam("C", T0).Value!;
        registry.Assign(1, a.Id, T0, "x");
        registry.Assign(2, b.Id, T0, "x");
        registry.SetRelation(a.Id, b.Id, TeamRelation.Hostile);
        registry.SetRelation(b.Id, c.Id, TeamRelation.Allied);

        Assert.True(registry.DeleteTeam(a.Id, moveMembersTo: b.Id).Ok);
        Assert.Equal(b.Id, registry.TeamOf(1));
        Assert.Equal(TeamRelation.Allied, registry.Relation(b.Id, c.Id));

        Assert.True(registry.DeleteTeam(b.Id).Ok);
        Assert.Null(registry.TeamOf(1));
        Assert.Null(registry.TeamOf(2));
        Assert.Empty(registry.Matrix.Entries);
        Assert.Equal(TeamRejectReason.UnknownTeam, registry.DeleteTeam(b.Id).Reason);
    }

    [Fact]
    public void CheckJoinRefusesLockedAndFullTeams()
    {
        var registry = new TeamRegistry();
        var open = registry.CreateTeam("Open", T0).Value!;
        var locked = registry.CreateTeam("Locked", T0, locked: true).Value!;
        var tiny = registry.CreateTeam("Tiny", T0, maxMembers: 1).Value!;
        registry.Assign(9, tiny.Id, T0, "x");

        Assert.True(registry.CheckJoin(open.Id, 1).Ok);
        Assert.Equal(TeamRejectReason.Locked, registry.CheckJoin(locked.Id, 1).Reason);
        Assert.Equal(TeamRejectReason.Full, registry.CheckJoin(tiny.Id, 1).Reason);
        Assert.Equal(TeamRejectReason.UnknownTeam, registry.CheckJoin(99, 1).Reason);
        Assert.Equal(TeamRejectReason.SameTeam, registry.CheckJoin(tiny.Id, 9).Reason);
    }

    [Fact]
    public void ChangesBumpTheDirectoryVersionAndNoOpsDoNot()
    {
        var registry = new TeamRegistry();
        var a = registry.CreateTeam("A", T0).Value!;
        var b = registry.CreateTeam("B", T0).Value!;
        int version = registry.Version;

        registry.SetRelation(a.Id, b.Id, TeamRelation.Hostile);
        Assert.Equal(version + 1, registry.Version);

        registry.SetRelation(a.Id, b.Id, TeamRelation.Hostile); // same value
        Assert.Equal(version + 1, registry.Version);

        Assert.Equal(TeamRejectReason.SameTeam, registry.SetRelation(a.Id, a.Id, TeamRelation.Hostile).Reason);
        Assert.Equal(TeamRejectReason.UnknownTeam, registry.SetRelation(a.Id, 77, TeamRelation.Hostile).Reason);
    }

    // ------------------------------------------------------------------ snapshot / restore

    [Fact]
    public void ASnapshotRestoresTheSameTeamsMembersAndRelations()
    {
        var registry = new TeamRegistry();
        registry.ApplyPreset(TeamPreset.FreeForAll, Players(3), T0);
        registry.UpdateTeam(2, new TeamRegistry.TeamPatch(Locked: true, MaxMembers: 4, PasswordHash: TeamRules.HashPassword("pw")));
        var snapshot = registry.Snapshot();

        var restored = new TeamRegistry();
        restored.Restore(snapshot);

        Assert.Equal(registry.Teams.Select(t => (t.Id, t.Name, t.FactionSlot, t.Locked, t.MaxMembers)), restored.Teams.Select(t => (t.Id, t.Name, t.FactionSlot, t.Locked, t.MaxMembers)));
        Assert.Equal(registry.Members.Select(m => (m.PlayerId, m.TeamId, m.Role)).Order(), restored.Members.Select(m => (m.PlayerId, m.TeamId, m.Role)).Order());
        Assert.Equal(registry.Matrix.Entries, restored.Matrix.Entries);
        Assert.True(restored.Find(2)!.PasswordProtected);
        Assert.True(restored.CreateTeam("Next", T0, maxTeams: 8).Ok);
        Assert.Equal(4, restored.Find(4)!.Id); // ids continue after the highest stored one
        Assert.Equal(4, restored.Find(4)!.FactionSlot); // slots 1..3 are taken again
    }
}
