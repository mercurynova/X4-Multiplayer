using X4MP.Core.Session;
using X4MP.Core.Teams;
using X4MP.Proto;
using TeamRelation = X4MP.Core.Teams.TeamRelation;

namespace X4MP.Persistence.Tests;

/// <summary>The team module over the real SQLite store: memberships survive a restart (M1-T1 acceptance).</summary>
public sealed class TeamModuleStoreTests : TeamDbFixture
{
    /// <summary>Runs module work inline, as if on the actor thread.</summary>
    private sealed class InlineDriver(long sessionId) : ISessionNodeDriver
    {
        public long? StoreSessionId { get; } = sessionId;

        public Task<T> CallAsync<T>(Func<T> work) => Task.FromResult(work());

        public Task<TransitionResult> ApplyNodeTriggerAsync(int playerId, NodeTrigger trigger) =>
            Task.FromResult(new TransitionResult(true, string.Empty, string.Empty, null));

        public Task<bool> RemoveNodeAsync(int playerId, DisconnectCode code, string reason) => Task.FromResult(false);
    }

    private TeamModule NewModule(long sessionId)
    {
        var module = new TeamModule(new TeamOptions(), new SqliteTeamStore(Factory, Writer));
        module.Bind(new InlineDriver(sessionId));
        return module;
    }

    [Fact]
    public async Task ATeamModuleRestoresMembershipsAndRelationsAfterARestart()
    {
        long session = await NewSessionAsync();
        TeamModule before = NewModule(session);
        var red = (await before.CreateTeamAsync("Red")).Value!;
        var blue = (await before.CreateTeamAsync("Blue", locked: true, maxMembers: 2, password: "pw")).Value!;
        await before.AssignPlayerAsync(Players[0], red.Id, assignedBy: "admin:root");
        await before.AssignPlayerAsync(Players[1], red.Id);
        await before.AssignPlayerAsync(Players[2], blue.Id);
        await before.SetRelationAsync(red.Id, blue.Id, TeamRelation.Hostile);
        await Writer.FlushAsync();

        // a new process: a new session row and a new module that loads what the last one stored
        long nextRun = await NewSessionAsync("after restart");
        TeamModule after = NewModule(nextRun);

        Assert.Equal(["Red", "Blue"], after.Teams.Select(t => t.Name));
        Assert.Equal([Players[0], Players[1]], after.MembersOf(red.Id));
        Assert.Equal(red.Id, after.TeamOf(Players[1]));
        Assert.Equal(blue.Id, after.TeamOf(Players[2]));
        Assert.Equal(TeamRelation.Hostile, after.RelationBetween(red.Id, blue.Id));
        var blueAfter = after.TeamDetails.Single(t => t.Id == blue.Id);
        Assert.True(blueAfter.Locked);
        Assert.Equal(2, blueAfter.MaxMembers);
        Assert.True(blueAfter.PasswordProtected);
        Assert.Equal(TeamRole.Leader, after.Snapshot().Members.Single(m => m.PlayerId == Players[0]).Role);
        Assert.Equal("admin:root", after.Snapshot().Members.Single(m => m.PlayerId == Players[0]).AssignedBy);

        // the restarted module keeps saving, now under the new session row
        await after.AssignPlayerAsync(Players[1], blue.Id);
        await Writer.FlushAsync();
        Assert.Equal(nextRun, Scalar<long>("SELECT MAX(session_id) FROM team_members"));
        Assert.Equal(blue.Id, Scalar<long>("SELECT team_id FROM team_members WHERE session_id = @nextRun AND player_id = @p", new { nextRun, p = Players[1] }));
    }
}
