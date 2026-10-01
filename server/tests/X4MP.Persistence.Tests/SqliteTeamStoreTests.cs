using Dapper;
using Microsoft.Data.Sqlite;
using X4MP.Core.Teams;
using TeamRelation = X4MP.Core.Teams.TeamRelation;
using TeamRole = X4MP.Proto.TeamRole;

namespace X4MP.Persistence.Tests;

/// <summary>Migration 0003_teams and <see cref="SqliteTeamStore"/>: teams, memberships and relations are stored and loaded back (M1-T1).</summary>
public sealed class SqliteTeamStoreTests : TeamDbFixture
{
    [Fact]
    public void TheTeamMigrationCreatesTheTeamTables()
    {
        Assert.Equal(4, Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('teams','team_members','team_relations','team_assets')"));
        Assert.True(new MigrationRunner(Factory).LatestVersion >= 3);
    }

    [Fact]
    public async Task ASnapshotRoundTripsThroughTheDatabase()
    {
        long session = await NewSessionAsync();
        var store = new SqliteTeamStore(Factory, Writer);
        var teams = new[]
        {
            new Team(1, "Red", "#FF5A5A", 1, Players[0], false, null, null, T0),
            new Team(2, "Blue", "#3FA7FF", 5, null, true, 3, TeamRules.HashPassword("pw"), T0),
        };
        var members = new[]
        {
            new TeamMembership(Players[0], 1, TeamRole.Leader, T0, "auto"),
            new TeamMembership(Players[1], 1, TeamRole.Member, T0.AddMinutes(1), "admin:root"),
        };
        var snapshot = new TeamStateSnapshot(TeamRelation.Neutral, teams, members, [(1, 2, TeamRelation.Hostile)]);

        Assert.True(store.Save(session, snapshot));
        await Writer.FlushAsync();
        var loaded = store.LoadLatest();

        Assert.NotNull(loaded);
        Assert.Equal(["Red", "Blue"], loaded.Teams.Select(t => t.Name));
        Assert.Equal([1, 5], loaded.Teams.Select(t => t.FactionSlot));
        Assert.Equal([false, true], loaded.Teams.Select(t => t.Locked));
        Assert.Equal([null, 3], loaded.Teams.Select(t => t.MaxMembers));
        Assert.Equal([Players[0], (int?)null], loaded.Teams.Select(t => t.LeaderPlayerId));
        Assert.Equal(TeamRules.HashPassword("pw"), loaded.Teams[1].JoinPasswordHash);
        Assert.Null(loaded.Teams[0].JoinPasswordHash);
        Assert.Equal(members.Select(m => (m.PlayerId, m.TeamId, m.Role, m.AssignedBy, m.Since)), loaded.Members.Select(m => (m.PlayerId, m.TeamId, m.Role, m.AssignedBy, m.Since)));
        Assert.Equal([(1, 2, TeamRelation.Hostile)], loaded.Relations);
    }

    [Fact]
    public async Task ASaveReplacesThePreviousSnapshotOfTheSameSession()
    {
        long session = await NewSessionAsync();
        var store = new SqliteTeamStore(Factory, Writer);
        store.Save(session, new TeamStateSnapshot(
            TeamRelation.Neutral,
            [new Team(1, "Old", "#FFFFFF", 1, null, false, null, null, T0), new Team(2, "Gone", "#000000", 2, null, false, null, null, T0)],
            [new TeamMembership(Players[0], 2, TeamRole.Leader, T0, "auto")],
            [(1, 2, TeamRelation.Allied)]));
        store.Save(session, new TeamStateSnapshot(
            TeamRelation.Neutral,
            [new Team(1, "New", "#FFFFFF", 3, null, false, null, null, T0)],
            [new TeamMembership(Players[0], 1, TeamRole.Leader, T0, "auto")],
            []));
        await Writer.FlushAsync();

        var loaded = store.LoadLatest()!;

        Assert.Equal("New", Assert.Single(loaded.Teams).Name);
        Assert.Equal(1, Assert.Single(loaded.Members).TeamId);
        Assert.Empty(loaded.Relations);
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM teams"));
    }

    [Fact]
    public async Task LoadLatestTakesTheMostRecentSessionThatHasTeams()
    {
        long first = await NewSessionAsync("first");
        long second = await NewSessionAsync("second");
        long third = await NewSessionAsync("third (no teams)");
        var store = new SqliteTeamStore(Factory, Writer);
        store.Save(first, new TeamStateSnapshot(TeamRelation.Neutral, [new Team(1, "FromFirst", "#111111", 1, null, false, null, null, T0)], [], []));
        store.Save(second, new TeamStateSnapshot(TeamRelation.Neutral, [new Team(1, "FromSecond", "#222222", 1, null, false, null, null, T0)], [], []));
        await Writer.FlushAsync();

        Assert.NotEqual(second, third);
        Assert.Equal("FromSecond", Assert.Single(store.LoadLatest()!.Teams).Name);
        Assert.Equal(2, Scalar<long>("SELECT COUNT(*) FROM teams")); // both sessions keep their own rows (team ids are per session)
    }

    [Fact]
    public void WithoutAnyTeamsTheStoreLoadsNothing() =>
        Assert.Null(new SqliteTeamStore(Factory, Writer).LoadLatest());

    [Fact]
    public async Task TheTeamsTablesFollowTheirSessionWhenItIsDeleted()
    {
        long session = await NewSessionAsync();
        new SqliteTeamStore(Factory, Writer).Save(session, new TeamStateSnapshot(
            TeamRelation.Neutral,
            [new Team(1, "A", "#111111", 1, null, false, null, null, T0), new Team(2, "B", "#222222", 2, null, false, null, null, T0)],
            [new TeamMembership(Players[0], 1, TeamRole.Leader, T0, "auto")],
            [(1, 2, TeamRelation.Hostile)]));
        await Writer.FlushAsync();

        using (var c = Factory.Open())
        {
            c.Execute("DELETE FROM sessions WHERE id = @session", new { session });
        }

        Assert.Equal(0, Scalar<long>("SELECT COUNT(*) FROM teams"));
        Assert.Equal(0, Scalar<long>("SELECT COUNT(*) FROM team_members"));
        Assert.Equal(0, Scalar<long>("SELECT COUNT(*) FROM team_relations"));
    }

    [Fact]
    public async Task TheSchemaEnforcesSlotRangeUniqueSlotsAndOrderedRelationPairs()
    {
        long session = await NewSessionAsync();
        using var c = Factory.Open();
        const string insert = "INSERT INTO teams (session_id, id, name, color, faction_slot, created_at) VALUES (@session, @id, @name, '#000000', @slot, 'x')";

        Assert.Throws<SqliteException>(() => c.Execute(insert, new { session, id = 1, name = "A", slot = 0 }));
        Assert.Throws<SqliteException>(() => c.Execute(insert, new { session, id = 1, name = "A", slot = 9 }));
        c.Execute(insert, new { session, id = 1, name = "A", slot = 1 });
        Assert.Throws<SqliteException>(() => c.Execute(insert, new { session, id = 2, name = "B", slot = 1 })); // slot taken
        Assert.Throws<SqliteException>(() => c.Execute(insert, new { session, id = 2, name = "a", slot = 2 })); // name taken (NOCASE)
        c.Execute(insert, new { session, id = 2, name = "B", slot = 2 });
        Assert.Throws<SqliteException>(() => c.Execute(
            "INSERT INTO team_relations (session_id, team_a, team_b, relation, updated_at) VALUES (@session, 2, 1, 1, 'x')", new { session }));
        Assert.Throws<SqliteException>(() => c.Execute(
            "INSERT INTO team_relations (session_id, team_a, team_b, relation, updated_at) VALUES (@session, 1, 2, 5, 'x')", new { session }));
        Assert.Throws<SqliteException>(() => c.Execute(
            "INSERT INTO team_members (session_id, player_id, team_id, since, assigned_by) VALUES (@session, @p, 99, 'x', 'auto')", new { session, p = Players[0] })); // no such team
    }
}
