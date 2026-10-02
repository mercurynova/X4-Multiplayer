using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using X4MP.Proto;
using X4MP.Server.Api;
using X4MP.Server.Auth;
using X4MP.Server.Hubs;
using X4MP.Server.Tests.Admin;
using X4MP.Server.Tests.Saves;

namespace X4MP.Server.Tests.Hubs;

/// <summary>The teams topic of the admin hub (M1-T5) and the teams REST paths that need a running session.</summary>
public sealed class TeamsHubTests
{
    private const string Base = "/api/v1/teams";

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static async Task<JsonElement> SendAsync(HttpClient admin, HttpMethod method, string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await admin.CallAsync(method, url, body);
        string text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{method} {url}: expected {(int)expected}, got {(int)response.StatusCode}: {text}");
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<long> CreateAsync(HttpClient admin, string name) =>
        (await SendAsync(admin, HttpMethod.Post, Base, new { name }, HttpStatusCode.Created)).GetProperty("id").GetInt64();

    private static async Task<long> JoinAsync(HubRig rig, string name, List<IAsyncDisposable> nodes)
    {
        var node = await rig.Server.ConnectAsync(name, Role.Client);
        nodes.Add(node);
        await rig.Server.WaitForAsync(s => s.Nodes.Any(n => n.Name == name && n.Connected), 10_000, "join " + name);
        return node.Welcome.PlayerId;
    }

    // ------------------------------------------------------------------ pushes

    [Fact]
    public async Task ASubscriberReceivesEveryKindOfTeamsPush()
    {
        await using var rig = await HubRig.StartAsync();
        using var admin = rig.Server.Http(rig.Server.AdminToken());
        var nodes = new List<IAsyncDisposable>();
        try
        {
            var (viewer, rec) = await rig.ConnectAsync(AdminRoles.Viewer); // a Viewer may subscribe
            var state = await viewer.InvokeCoreAsync<TeamsStateDto>(AdminHubMethods.SubscribeTeams, []);
            Assert.Empty(state.Teams);
            Assert.Equal("Auto", state.Policy.JoinMode);
            Assert.Equal(1, rig.Subscriptions.Count(HubTopic.Teams));

            // TeamUpserted (create, then rename)
            long red = await CreateAsync(admin, "Red");
            long blue = await CreateAsync(admin, "Blue");
            var upsert = await rec.WaitAsync<TeamDto>("TeamUpserted", t => t.Id == red);
            Assert.Equal("Red", upsert.Name);
            await SendAsync(admin, HttpMethod.Patch, $"{Base}/{N(red)}", new { name = "Crimson", color = "#CC0000" });
            var renamed = await rec.WaitAsync<TeamDto>("TeamUpserted", t => t.Id == red && t.Name == "Crimson");
            Assert.Equal("#CC0000", renamed.Color);

            // TeamMemberChanged + the member count of the team (a joining player is auto-assigned, then moved by an admin)
            long player = await JoinAsync(rig, "Mover", nodes);
            await SendAsync(admin, HttpMethod.Put, $"{Base}/members/{N(player)}", new { teamId = blue });
            var moved = await rec.WaitAsync<TeamMemberDto>("TeamMemberChanged", m => m.PlayerId == player && m.TeamId == blue);
            Assert.Equal("Mover", moved.Name);
            Assert.True(moved.Online);
            await rec.WaitAsync<TeamDto>("TeamUpserted", t => t.Id == blue && t.MemberCount == 1);

            // TeamRelationsChanged
            await SendAsync(admin, HttpMethod.Put, $"{Base}/relations/{N(red)}/{N(blue)}", new { relation = "Hostile" });
            var relations = await rec.WaitAsync<TeamRelationsDto>("TeamRelationsChanged", r => r.Entries.Any(e => e.Relation == "Hostile"));
            Assert.Contains(relations.Entries, e => e.TeamA == Math.Min(red, blue) && e.TeamB == Math.Max(red, blue));

            // TeamPolicyChanged
            await SendAsync(admin, HttpMethod.Patch, Base + "/policy", new { allowFriendlyFire = true, assetPolicy = "OwnerOnly" });
            var policy = await rec.WaitAsync<TeamPolicyDto>("TeamPolicyChanged", p => p.AllowFriendlyFire);
            Assert.Equal("OwnerOnly", policy.AssetPolicy);

            // PlayerAwaitingTeam (an AdminAssign joiner)
            await SendAsync(admin, HttpMethod.Patch, Base + "/policy", new { joinMode = "AdminAssign" });
            await rec.WaitAsync<TeamPolicyDto>("TeamPolicyChanged", p => p.JoinMode == "AdminAssign");
            long waiting = await JoinAsync(rig, "Waiter", nodes);
            var awaiting = await rec.WaitAsync<TeamMemberDto>("PlayerAwaitingTeam", m => m.PlayerId == waiting);
            Assert.Null(awaiting.TeamId);
            Assert.Equal("Waiter", awaiting.Name);

            // TeamsReset: a waiting player that leaves vanishes from both lists
            await SendAsync(admin, HttpMethod.Post, $"/api/v1/players/{N(waiting)}/kick", new { reason = "test" }, HttpStatusCode.Accepted);
            var reset = await rec.WaitAsync<TeamsStateDto>("TeamsReset");
            Assert.DoesNotContain(reset.Unassigned, m => m.PlayerId == waiting);
            Assert.Contains(reset.Teams, t => t.Id == red);

            // TeamDeleted
            await SendAsync(admin, HttpMethod.Delete, $"{Base}/{N(red)}", null, HttpStatusCode.NoContent);
            await rec.WaitForValueAsync("TeamDeleted", red);
        }
        finally
        {
            foreach (var node in nodes)
            {
                await node.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task APresetArrivesAsOneConsistentSetOfPushes()
    {
        await using var rig = await HubRig.StartAsync();
        using var admin = rig.Server.Http(rig.Server.AdminToken());
        var nodes = new List<IAsyncDisposable>();
        try
        {
            var ids = new List<long>();
            for (int i = 1; i <= 3; i++)
            {
                ids.Add(await JoinAsync(rig, "Pre" + N(i), nodes));
            }

            var (connection, rec) = await rig.ConnectAsync();
            var before = await connection.InvokeCoreAsync<TeamsStateDto>(AdminHubMethods.SubscribeTeams, []);
            Assert.Single(before.Teams); // Auto + SingleTeam: one team for all

            await SendAsync(admin, HttpMethod.Post, Base + "/preset", new { preset = "FreeForAll" });
            var relations = await rec.WaitAsync<TeamRelationsDto>("TeamRelationsChanged", r => r.Entries.Count == 3);
            Assert.All(relations.Entries, e => Assert.Equal("Hostile", e.Relation));
            foreach (long id in ids.Skip(1))
            {
                long oldTeam = before.Members.First(m => m.PlayerId == id).TeamId!.Value;
                await rec.WaitAsync<TeamMemberDto>("TeamMemberChanged", m => m.PlayerId == id && m.TeamId != oldTeam);
            }

            var policy = await rec.WaitAsync<TeamPolicyDto>("TeamPolicyChanged", p => p.AutoAssign == "NewTeamPerPlayer");
            Assert.Equal("NewTeamPerPlayer", policy.AutoAssign);
        }
        finally
        {
            foreach (var node in nodes)
            {
                await node.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task NothingIsBuiltWithoutSubscribersAndStopsAfterUnsubscribing()
    {
        await using var rig = await HubRig.StartAsync();
        using var admin = rig.Server.Http(rig.Server.AdminToken());
        var nodes = new List<IAsyncDisposable>();
        try
        {
            var broadcaster = rig.Broadcaster;
            long team = await CreateAsync(admin, "Quiet");
            long player = await JoinAsync(rig, "Silent", nodes);
            await SendAsync(admin, HttpMethod.Put, $"{Base}/members/{N(player)}", new { teamId = team });
            await SendAsync(admin, HttpMethod.Patch, Base + "/policy", new { allowFriendlyFire = true });
            await Task.Delay(300);
            Assert.Equal(0, rig.Subscriptions.Count(HubTopic.Teams));
            Assert.False(broadcaster.PayloadsByKind.ContainsKey("teams"), "the teams state was built without a subscriber");

            var (connection, rec) = await rig.ConnectAsync();
            var state = await connection.InvokeCoreAsync<TeamsStateDto>(AdminHubMethods.SubscribeTeams, []);
            Assert.Contains(state.Teams, t => t.Id == team && t.MemberCount == 1);
            await SendAsync(admin, HttpMethod.Patch, $"{Base}/{N(team)}", new { locked = true });
            await rec.WaitAsync<TeamDto>("TeamUpserted", t => t.Id == team && t.Locked);
            Assert.True(broadcaster.PayloadsByKind["teams"] >= 1);

            await connection.InvokeAsync(AdminHubMethods.UnsubscribeTeams);
            Assert.Equal(0, rig.Subscriptions.Count(HubTopic.Teams));
            await Task.Delay(200);
            long built = broadcaster.PayloadsByKind["teams"];
            int received = rec.Count("TeamUpserted");
            await SendAsync(admin, HttpMethod.Patch, $"{Base}/{N(team)}", new { locked = false });
            await Task.Delay(300);
            Assert.Equal(built, broadcaster.PayloadsByKind["teams"]);
            Assert.Equal(received, rec.Count("TeamUpserted"));
        }
        finally
        {
            foreach (var node in nodes)
            {
                await node.DisposeAsync();
            }
        }
    }

    // ------------------------------------------------------------------ running session

    [Fact]
    public async Task TheAuthoritysPlayerCannotMoveWhileRunningAndAPresetNeedsConfirmation()
    {
        await using var rig = await HubRig.StartAsync();
        using var admin = rig.Server.Http(rig.Server.AdminToken());
        var authority = await rig.StartAuthorityAsync();
        long authorityId = authority.Client.Welcome.PlayerId;
        var nodes = new List<IAsyncDisposable>();
        try
        {
            long client = await JoinAsync(rig, "Rider", nodes);
            var state = await SendAsync(admin, HttpMethod.Get, Base);
            Assert.Equal("Running", state.GetProperty("sessionPhase").GetString());
            long home = state.GetProperty("members").EnumerateArray().First(m => m.GetProperty("playerId").GetInt64() == authorityId).GetProperty("teamId").GetInt64();
            long other = await CreateAsync(admin, "Elsewhere");

            // the authority's player stays where it is ...
            using (var r = await admin.CallAsync(HttpMethod.Put, $"{Base}/members/{N(authorityId)}", new { teamId = other }))
            {
                await r.AssertProblemAsync(HttpStatusCode.Conflict, "SessionRunningRestricted");
            }

            using (var r = await admin.CallAsync(HttpMethod.Put, $"{Base}/members/{N(authorityId)}", new { teamId = (long?)null }))
            {
                await r.AssertProblemAsync(HttpStatusCode.Conflict, "SessionRunningRestricted");
            }

            using (var r = await admin.CallAsync(HttpMethod.Delete, $"{Base}/{N(home)}"))
            {
                await r.AssertProblemAsync(HttpStatusCode.Conflict, "SessionRunningRestricted");
            }

            using (var r = await admin.CallAsync(HttpMethod.Put, $"{Base}/members", new { assignments = new[] { new { playerId = authorityId, teamId = (long?)other } } }))
            {
                await r.AssertProblemAsync(HttpStatusCode.Conflict, "SessionRunningRestricted");
            }

            // ... but any other player moves freely
            var moved = await SendAsync(admin, HttpMethod.Put, $"{Base}/members/{N(client)}", new { teamId = other });
            Assert.Equal(other, moved.GetProperty("teamId").GetInt64());
            var current = await SendAsync(admin, HttpMethod.Get, Base);
            Assert.Equal(home, current.GetProperty("members").EnumerateArray().First(m => m.GetProperty("playerId").GetInt64() == authorityId).GetProperty("teamId").GetInt64());

            // a preset while Running shows what it would do and wants a confirmation
            var preview = await SendAsync(admin, HttpMethod.Get, Base + "/preset/TwoTeams/preview");
            Assert.True(preview.GetProperty("running").GetBoolean());
            Assert.True(preview.GetProperty("requiresConfirm").GetBoolean());
            Assert.Equal(2, preview.GetProperty("teams").GetArrayLength());
            using (var r = await admin.CallAsync(HttpMethod.Post, Base + "/preset", new { preset = "CoOp" }))
            {
                var problem = await r.AssertProblemAsync(HttpStatusCode.Conflict, "ConfirmationRequired");
                Assert.Equal("CoOp", problem.GetProperty("preview").GetProperty("preset").GetString());
                Assert.True(problem.GetProperty("preview").GetProperty("requiresConfirm").GetBoolean());
            }

            // confirmed, the preset goes through (in a co-op the authority's player keeps team 1)
            var applied = await SendAsync(admin, HttpMethod.Post, Base + "/preset", new { preset = "CoOp", confirm = true });
            Assert.Equal(1, applied.GetProperty("teams").GetArrayLength());
            Assert.Equal(2, applied.GetProperty("members").EnumerateArray().Count(m => m.GetProperty("playerId").GetInt64() == authorityId || m.GetProperty("playerId").GetInt64() == client));
        }
        finally
        {
            foreach (var node in nodes)
            {
                await node.DisposeAsync();
            }
        }
    }

    // ------------------------------------------------------------------ diff

    [Fact]
    public void TheDiffListsOnlyWhatChangedAndFallsBackToAResetWhenAPlayerVanishes()
    {
        var team = new TeamDto(1, "A", "#112233", 1, 5, false, null, false, 1);
        var member = new TeamMemberDto(5, "Five", 1, "Leader", true, false, "auto", null);
        var relations = new TeamRelationsDto(0, [], "Neutral");
        var policy = new TeamPolicyDto("Auto", "SingleTeam", false, 300, 8, 8, "Neutral", "SharedCommand", false, false, "ShipOnly", false, "AdminOnly");
        var before = new TeamsStateDto([team], [member], [], relations, policy, "Running");

        Assert.Empty(TeamsDiff.Compute(before, before));
        var first = Assert.Single(TeamsDiff.Compute(null, before));
        Assert.Equal("teams-reset", first.Key);

        var after = before with
        {
            Teams = [team with { Name = "B" }, team with { Id = 2, Name = "C", MemberCount = 0 }],
            Members = [member with { TeamId = 2 }],
            Relations = relations with { Version = 1, Entries = [new TeamRelationEntryDto(1, 2, "Hostile")] },
            Policy = policy with { JoinMode = "Lobby" },
        };
        var keys = TeamsDiff.Compute(before, after).Select(p => p.Key).ToList();
        Assert.Equal(["team:1", "team:2", "member:5", "teams-relations", "teams-policy"], keys);

        var gone = after with { Members = [] };
        Assert.Equal(["teams-reset"], TeamsDiff.Compute(after, gone).Select(p => p.Key));

        var removed = after with { Teams = [after.Teams[0]], Members = [member] };
        Assert.Contains("team-deleted:2", TeamsDiff.Compute(after, removed).Select(p => p.Key));
    }
}
