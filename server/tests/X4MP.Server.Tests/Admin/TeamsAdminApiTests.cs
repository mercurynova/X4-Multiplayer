using System.Globalization;
using System.Net;
using System.Text.Json;
using X4MP.Persistence;
using X4MP.Server.Tests.Saves;

namespace X4MP.Server.Tests.Admin;

/// <summary>
/// The teams admin REST API (M1-T5) over the real server: a happy and an error path for every endpoint, the roles, the audit rows, and what
/// a lobby password must never leak into. Every test starts from a clean slate (no teams, default settings).
/// </summary>
public sealed class TeamsAdminApiTests(AdminServerFixture f) : IClassFixture<AdminServerFixture>
{
    private const string Base = "/api/v1/teams";

    private HttpClient Admin => f.Admin;

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private async Task<JsonElement> SendAsync(HttpMethod method, string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await Admin.CallAsync(method, url, body);
        string text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{method} {url}: expected {(int)expected}, got {(int)response.StatusCode}: {text}");
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private Task<JsonElement> GetAsync(string url = Base) => SendAsync(HttpMethod.Get, url);

    private async Task ResetAsync()
    {
        await SendAsync(HttpMethod.Patch, Base + "/policy", new { joinMode = "Auto", autoAssign = "SingleTeam", defaultRelation = "Neutral" });
        var state = await GetAsync();
        foreach (var team in state.GetProperty("teams").EnumerateArray())
        {
            await SendAsync(HttpMethod.Delete, $"{Base}/{N(team.GetProperty("id").GetInt64())}", null, HttpStatusCode.NoContent);
        }
    }

    private async Task<long> CreateAsync(string name, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["name"] = name };
        if (extra is not null)
        {
            foreach (var p in extra.GetType().GetProperties())
            {
                body[p.Name] = p.GetValue(extra);
            }
        }

        var team = await SendAsync(HttpMethod.Post, Base, body, HttpStatusCode.Created);
        return team.GetProperty("id").GetInt64();
    }

    private async Task<JsonElement> MemberAsync(long playerId, long? teamId, string? role = null, HttpStatusCode expected = HttpStatusCode.OK) =>
        await SendAsync(HttpMethod.Put, $"{Base}/members/{N(playerId)}", new { teamId, role }, expected);

    private static JsonElement TeamNamed(JsonElement state, string name) =>
        state.GetProperty("teams").EnumerateArray().First(t => t.GetProperty("name").GetString() == name);

    private static string RelationOf(JsonElement relations, long a, long b)
    {
        long lo = Math.Min(a, b);
        long hi = Math.Max(a, b);
        foreach (var e in relations.GetProperty("entries").EnumerateArray())
        {
            if (e.GetProperty("teamA").GetInt64() == lo && e.GetProperty("teamB").GetInt64() == hi)
            {
                return e.GetProperty("relation").GetString()!;
            }
        }

        return relations.GetProperty("defaultRelation").GetString()!;
    }

    // ------------------------------------------------------------------ roles

    public static TheoryData<string, string, bool> Endpoints => new()
    {
        { "GET", Base, false },
        { "POST", Base, true },
        { "GET", Base + "/unassigned", false },
        { "GET", Base + "/relations", false },
        { "PUT", Base + "/relations", true },
        { "PUT", Base + "/relations/1/2", true },
        { "PUT", Base + "/members", true },
        { "PUT", Base + "/members/1", true },
        { "GET", Base + "/preset/FreeForAll/preview", false },
        { "POST", Base + "/preset", true },
        { "GET", Base + "/policy", false },
        { "PATCH", Base + "/policy", true },
        { "GET", Base + "/1", false },
        { "PATCH", Base + "/1", true },
        { "DELETE", Base + "/1", true },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task EveryTeamsEndpointAnswers401WithoutCredentialsAnd403ToAViewerWhereAdminOnly(string method, string url, bool adminOnly)
    {
        object? body = method is "POST" or "PATCH" or "PUT" ? new { } : null;
        using (var anonymous = await f.Anon.CallAsync(new HttpMethod(method), url, body))
        {
            await anonymous.AssertProblemAsync(HttpStatusCode.Unauthorized, "Unauthorized");
        }

        if (adminOnly)
        {
            using var viewer = await f.Viewer.CallAsync(new HttpMethod(method), url, body);
            await viewer.AssertProblemAsync(HttpStatusCode.Forbidden, "Forbidden");
        }
        else
        {
            using var viewer = await f.Viewer.CallAsync(new HttpMethod(method), url, body);
            Assert.True(viewer.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound, $"viewer got {(int)viewer.StatusCode}");
        }
    }

    // ------------------------------------------------------------------ teams

    [Fact]
    public async Task ATeamIsCreatedListedChangedAndDeletedAndEveryStepIsAudited()
    {
        await ResetAsync();
        var created = await SendAsync(
            HttpMethod.Post, Base, new { name = "Wolves", color = "#aa00ff", maxMembers = 3, locked = true, password = "s3cret-pass" }, HttpStatusCode.Created);
        long id = created.GetProperty("id").GetInt64();
        Assert.Equal("Wolves", created.GetProperty("name").GetString());
        Assert.Equal("#AA00FF", created.GetProperty("color").GetString());
        Assert.True(created.GetProperty("locked").GetBoolean());
        Assert.True(created.GetProperty("hasPassword").GetBoolean());
        Assert.Equal(3, created.GetProperty("maxMembers").GetInt32());
        Assert.Equal(1, created.GetProperty("factionSlot").GetInt32());
        Assert.DoesNotContain("s3cret", created.GetRawText(), StringComparison.Ordinal);

        var detail = await GetAsync($"{Base}/{N(id)}");
        Assert.Equal("Wolves", detail.GetProperty("team").GetProperty("name").GetString());
        Assert.Empty(detail.GetProperty("members").EnumerateArray());
        var state = await GetAsync();
        Assert.Contains(state.GetProperty("teams").EnumerateArray(), t => t.GetProperty("id").GetInt64() == id);
        Assert.Equal("Auto", state.GetProperty("policy").GetProperty("joinMode").GetString());

        var patched = await SendAsync(
            HttpMethod.Patch, $"{Base}/{N(id)}", new { name = "Grey Wolves", color = "#112233", locked = false, maxMembers = 0, password = "" });
        Assert.Equal("Grey Wolves", patched.GetProperty("name").GetString());
        Assert.Equal("#112233", patched.GetProperty("color").GetString());
        Assert.False(patched.GetProperty("locked").GetBoolean());
        Assert.False(patched.GetProperty("hasPassword").GetBoolean());
        Assert.Equal(JsonValueKind.Null, patched.GetProperty("maxMembers").ValueKind);

        await SendAsync(HttpMethod.Delete, $"{Base}/{N(id)}", null, HttpStatusCode.NoContent);
        using var gone = await f.Viewer.GetAsync($"{Base}/{N(id)}");
        await gone.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");

        await Api.WaitForAuditAsync(f.Server, "teams.create", N(id));
        await Api.WaitForAuditAsync(f.Server, "teams.update", N(id));
        await Api.WaitForAuditAsync(f.Server, "teams.delete", N(id));
        var audit = f.Server.Service<SqliteAdminQueries>().AuditEntries(2000).Where(a => a.Action.StartsWith("teams.", StringComparison.Ordinal));
        Assert.DoesNotContain(audit, a => a.DataJson?.Contains("s3cret", StringComparison.Ordinal) == true);
        Assert.Contains(audit, a => a.Action == "teams.create" && a.DataJson!.Contains("\"password\":\"set\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TeamEndpointsRejectBadInputWithProblems()
    {
        await ResetAsync();
        long id = await CreateAsync("Alpha");
        await CreateAsync("Beta");

        using (var r = await Admin.CallAsync(HttpMethod.Post, Base, new { name = "   " }))
        {
            await r.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "name");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Post, Base, new { name = "Gamma", color = "red" }))
        {
            await r.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "color");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Post, Base, new { name = "Gamma", factionSlot = 9 }))
        {
            await r.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "factionSlot");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Post, Base, new { name = "alpha" }))
        {
            await r.AssertProblemAsync(HttpStatusCode.Conflict, "NameTaken");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Post, Base, new { name = "Gamma", factionSlot = 1 }))
        {
            await r.AssertProblemAsync(HttpStatusCode.Conflict, "NoFactionSlot");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Patch, $"{Base}/{N(id)}", new { }))
        {
            await r.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "body");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Patch, $"{Base}/{N(id)}", new { name = "beta" }))
        {
            await r.AssertProblemAsync(HttpStatusCode.Conflict, "NameTaken");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Patch, $"{Base}/9999", new { name = "Nope" }))
        {
            await r.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Patch, $"{Base}/{N(id)}", new { leaderPlayerId = 424242 }))
        {
            await r.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "body");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Delete, $"{Base}/9999"))
        {
            await r.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Delete, $"{Base}/{N(id)}?moveMembersTo={N(id)}"))
        {
            await r.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "moveMembersTo");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Get, $"{Base}/9999"))
        {
            await r.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }
    }

    // ------------------------------------------------------------------ members

    [Fact]
    public async Task APlayerIsAssignedMovedPromotedAndUnassignedAndThePlayerListShowsItsTeam()
    {
        await ResetAsync();
        long red = await CreateAsync("Red");
        long blue = await CreateAsync("Blue");
        var (node, player) = await f.JoinAsync(f.NextName("Mv"));
        await using var _ = node;

        var member = await MemberAsync(player, blue); // it was auto-assigned to the only team when it joined
        Assert.Equal(blue, member.GetProperty("teamId").GetInt64());
        Assert.Equal("Leader", member.GetProperty("role").GetString()); // the first member of a team leads it
        Assert.Equal("admin:test-Admin", member.GetProperty("assignedBy").GetString());

        member = await MemberAsync(player, red, "Leader");
        Assert.Equal(red, member.GetProperty("teamId").GetInt64());
        var detail = await GetAsync($"{Base}/{N(red)}");
        Assert.Contains(detail.GetProperty("members").EnumerateArray(), m => m.GetProperty("playerId").GetInt64() == player);
        Assert.Equal(1, detail.GetProperty("team").GetProperty("memberCount").GetInt32());
        Assert.Equal(player, detail.GetProperty("team").GetProperty("leaderPlayerId").GetInt64());
        Assert.Equal(0, (await GetAsync($"{Base}/{N(blue)}")).GetProperty("team").GetProperty("memberCount").GetInt32());

        // the Players endpoints carry the team
        var players = await GetAsync("/api/v1/players");
        var row = players.EnumerateArray().First(p => p.GetProperty("id").GetInt64() == player);
        Assert.Equal(red, row.GetProperty("teamId").GetInt64());
        Assert.Equal("Red", row.GetProperty("teamName").GetString());
        var one = await GetAsync($"/api/v1/players/{N(player)}");
        Assert.Equal("Red", one.GetProperty("player").GetProperty("teamName").GetString());
        Assert.Equal("Red", one.GetProperty("live").GetProperty("teamName").GetString());

        member = await MemberAsync(player, null);
        Assert.Equal(JsonValueKind.Null, member.GetProperty("teamId").ValueKind);
        Assert.Contains((await GetAsync(Base + "/unassigned")).EnumerateArray(), m => m.GetProperty("playerId").GetInt64() == player);
        one = await GetAsync($"/api/v1/players/{N(player)}");
        Assert.Equal(JsonValueKind.Null, one.GetProperty("player").GetProperty("teamId").ValueKind);

        await Api.WaitForAuditAsync(f.Server, "teams.assign", N(player));
        await Api.WaitForAuditAsync(f.Server, "teams.unassign", N(player));
    }

    [Fact]
    public async Task MemberEndpointsRejectUnknownPlayersTeamsAndRolesAndBulkMovesAreChecked()
    {
        await ResetAsync();
        long red = await CreateAsync("Red");
        var (nodeA, a) = await f.JoinAsync(f.NextName("Bk"));
        await using var _ = nodeA;
        var (nodeB, b) = await f.JoinAsync(f.NextName("Bk"));
        await using var __ = nodeB;

        using (var r = await Admin.CallAsync(HttpMethod.Put, $"{Base}/members/424242", new { teamId = red }))
        {
            await r.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Put, $"{Base}/members/{N(a)}", new { teamId = 9999 }))
        {
            await r.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Put, $"{Base}/members/{N(a)}", new { teamId = red, role = "Boss" }))
        {
            await r.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "role");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Put, $"{Base}/members", new { assignments = Array.Empty<object>() }))
        {
            await r.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "assignments");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Put, $"{Base}/members", new { assignments = new[] { new { playerId = a, teamId = 9999L } } }))
        {
            await r.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "assignments[0].teamId");
        }

        var state = await SendAsync(
            HttpMethod.Put, $"{Base}/members", new { assignments = new[] { new { playerId = a, teamId = (long?)red }, new { playerId = b, teamId = (long?)red } } });
        Assert.Equal(2, TeamNamed(state, "Red").GetProperty("memberCount").GetInt32());
        state = await SendAsync(
            HttpMethod.Put, $"{Base}/members", new { assignments = new[] { new { playerId = a, teamId = (long?)null } } });
        Assert.Equal(1, TeamNamed(state, "Red").GetProperty("memberCount").GetInt32());
        await Api.WaitForAuditAsync(f.Server, "teams.bulk-assign");
    }

    [Fact]
    public async Task DeletingATeamMovesItsMembersWhenAskedAndOtherwiseUnassignsThem()
    {
        await ResetAsync();
        long red = await CreateAsync("Red");
        long blue = await CreateAsync("Blue");
        var (nodeA, a) = await f.JoinAsync(f.NextName("Dl"));
        await using var _ = nodeA;
        var (nodeB, b) = await f.JoinAsync(f.NextName("Dl"));
        await using var __ = nodeB;
        await MemberAsync(a, red);
        await MemberAsync(b, red);

        await SendAsync(HttpMethod.Delete, $"{Base}/{N(red)}?moveMembersTo={N(blue)}", null, HttpStatusCode.NoContent);
        var detail = await GetAsync($"{Base}/{N(blue)}");
        Assert.Equal(2, detail.GetProperty("members").GetArrayLength());

        await SendAsync(HttpMethod.Delete, $"{Base}/{N(blue)}", null, HttpStatusCode.NoContent);
        var unassigned = await GetAsync(Base + "/unassigned");
        Assert.Contains(unassigned.EnumerateArray(), m => m.GetProperty("playerId").GetInt64() == a);
        Assert.Contains(unassigned.EnumerateArray(), m => m.GetProperty("playerId").GetInt64() == b);
    }

    // ------------------------------------------------------------------ relations

    [Fact]
    public async Task RelationsAreSetPerCellAndAsAMatrixAreSymmetricAndAudited()
    {
        await ResetAsync();
        long a = await CreateAsync("RelA");
        long b = await CreateAsync("RelB");
        long c = await CreateAsync("RelC");

        var initial = await GetAsync(Base + "/relations");
        Assert.Equal("Neutral", initial.GetProperty("defaultRelation").GetString());

        var one = await SendAsync(HttpMethod.Put, $"{Base}/relations/{N(b)}/{N(a)}", new { relation = "hostile" });
        Assert.Equal("Hostile", RelationOf(one, a, b));
        Assert.Equal("Hostile", RelationOf(one, b, a));
        Assert.Equal("Neutral", RelationOf(one, a, c));
        Assert.True(one.GetProperty("version").GetInt64() > initial.GetProperty("version").GetInt64());

        var matrix = await SendAsync(HttpMethod.Put, $"{Base}/relations", new
        {
            entries = new[]
            {
                new { teamA = a, teamB = b, relation = "Allied" },
                new { teamA = c, teamB = a, relation = "Hostile" },
            },
        });
        Assert.Equal("Allied", RelationOf(matrix, a, b));
        Assert.Equal("Hostile", RelationOf(matrix, a, c));
        Assert.Equal("Neutral", RelationOf(matrix, b, c));
        Assert.Equal("Allied", RelationOf(await GetAsync(Base + "/relations"), a, b));
        Assert.Equal("Allied", RelationOf((await GetAsync()).GetProperty("relations"), a, b));

        await Api.WaitForAuditAsync(f.Server, "teams.relation", $"{N(b)}-{N(a)}");
        await Api.WaitForAuditAsync(f.Server, "teams.relations");
    }

    [Fact]
    public async Task RelationEndpointsRejectBadCells()
    {
        await ResetAsync();
        long a = await CreateAsync("BadA");
        long b = await CreateAsync("BadB");

        using (var r = await Admin.CallAsync(HttpMethod.Put, $"{Base}/relations/{N(a)}/{N(b)}", new { relation = "Friendly" }))
        {
            await r.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "relation");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Put, $"{Base}/relations/{N(a)}/{N(a)}", new { relation = "Allied" }))
        {
            await r.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "teamB");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Put, $"{Base}/relations/{N(a)}/9999", new { relation = "Allied" }))
        {
            await r.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Put, Base + "/relations", new { entries = Array.Empty<object>() }))
        {
            await r.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "entries");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Put, Base + "/relations", new { entries = new[] { new { teamA = a, teamB = a, relation = "Allied" } } }))
        {
            await r.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "entries[0]");
        }

        // one unknown team fails the whole matrix and nothing is applied
        using (var r = await Admin.CallAsync(HttpMethod.Put, Base + "/relations", new
        {
            entries = new[] { new { teamA = a, teamB = b, relation = "Hostile" }, new { teamA = a, teamB = 9999L, relation = "Hostile" } },
        }))
        {
            await r.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        Assert.Equal("Neutral", RelationOf(await GetAsync(Base + "/relations"), a, b));
    }

    // ------------------------------------------------------------------ presets

    [Fact]
    public async Task EveryPresetPreviewsAndAppliesAndTheAutoAssignSettingFollows()
    {
        await ResetAsync();
        var players = new List<long>();
        var nodes = new List<IAsyncDisposable>();
        for (int i = 0; i < 3; i++)
        {
            var (node, id) = await f.JoinAsync(f.NextName("Pr"));
            nodes.Add(node);
            players.Add(id);
        }

        try
        {
            var preview = await GetAsync($"{Base}/preset/FreeForAll/preview");
            Assert.False(preview.GetProperty("running").GetBoolean());
            Assert.False(preview.GetProperty("requiresConfirm").GetBoolean());
            Assert.True(preview.GetProperty("teams").GetArrayLength() >= 3);
            Assert.Equal("NewTeamPerPlayer", preview.GetProperty("autoAssign").GetString());
            Assert.Equal("Hostile", preview.GetProperty("relation").GetString());
            // previewing changes nothing
            Assert.Equal(JsonValueKind.Array, (await GetAsync()).GetProperty("teams").ValueKind);

            var ffa = await SendAsync(HttpMethod.Post, Base + "/preset", new { preset = "FreeForAll" });
            var mine = ffa.GetProperty("members").EnumerateArray().Where(m => players.Contains(m.GetProperty("playerId").GetInt64())).ToList();
            Assert.Equal(3, mine.Count);
            Assert.Equal(3, mine.Select(m => m.GetProperty("teamId").GetInt64()).Distinct().Count());
            long t1 = mine[0].GetProperty("teamId").GetInt64();
            long t2 = mine[1].GetProperty("teamId").GetInt64();
            Assert.Equal("Hostile", RelationOf(ffa.GetProperty("relations"), t1, t2));
            Assert.Equal("NewTeamPerPlayer", ffa.GetProperty("policy").GetProperty("autoAssign").GetString());

            var allied = await SendAsync(HttpMethod.Post, Base + "/preset", new { preset = "alliedseparate" });
            Assert.Equal("Allied", RelationOf(allied.GetProperty("relations"), t1, t2));

            var two = await SendAsync(HttpMethod.Post, Base + "/preset", new { preset = "TwoTeams" });
            Assert.Equal(2, two.GetProperty("teams").GetArrayLength());
            Assert.Equal("Balance", two.GetProperty("policy").GetProperty("autoAssign").GetString());

            var coop = await SendAsync(HttpMethod.Post, Base + "/preset", new { preset = "co-op" });
            Assert.Equal(1, coop.GetProperty("teams").GetArrayLength());
            Assert.Equal("SingleTeam", coop.GetProperty("policy").GetProperty("autoAssign").GetString());

            await Api.WaitForAuditAsync(f.Server, "teams.preset", "FreeForAll");
            await Api.WaitForAuditAsync(f.Server, "teams.preset", "CoOp");
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
    public async Task APresetRejectsAnUnknownName()
    {
        using (var r = await Admin.CallAsync(HttpMethod.Post, Base + "/preset", new { preset = "Chaos" }))
        {
            await r.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "preset");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Post, Base + "/preset", new { }))
        {
            await r.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "preset");
        }

        using var preview = await Admin.GetAsync($"{Base}/preset/Chaos/preview");
        await preview.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "preset");
    }

    // ------------------------------------------------------------------ policy and the unassigned list

    [Fact]
    public async Task ThePolicyIsReadAndPatchedValidatedAndAudited()
    {
        await ResetAsync();
        var policy = await SendAsync(
            HttpMethod.Patch, Base + "/policy", new { joinMode = "Lobby", allowCreateInLobby = true, assetPolicy = "OwnerOnly", maxTeams = 4, defaultRelation = "Hostile" });
        Assert.Equal("Lobby", policy.GetProperty("joinMode").GetString());
        Assert.True(policy.GetProperty("allowCreateInLobby").GetBoolean());
        Assert.Equal("OwnerOnly", policy.GetProperty("assetPolicy").GetString());
        Assert.Equal(4, policy.GetProperty("maxTeams").GetInt32());
        Assert.Equal(8, policy.GetProperty("maxFactionSlots").GetInt32());
        Assert.Equal("Hostile", (await GetAsync(Base + "/policy")).GetProperty("defaultRelation").GetString());
        Assert.Equal("Hostile", (await GetAsync(Base + "/relations")).GetProperty("defaultRelation").GetString());
        await Api.WaitForAuditAsync(f.Server, "teams.policy");

        using (var r = await Admin.CallAsync(HttpMethod.Patch, Base + "/policy", new { joinMode = "Everyone" }))
        {
            await r.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "joinMode");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Patch, Base + "/policy", new { maxTeams = 99 }))
        {
            await r.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "maxTeams");
        }

        using (var r = await Admin.CallAsync(HttpMethod.Patch, Base + "/policy", new { }))
        {
            await r.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "body");
        }

        await ResetAsync();
    }

    [Fact]
    public async Task InAdminAssignModeAJoiningPlayerWaitsUnassignedUntilAnAdminPlacesIt()
    {
        await ResetAsync();
        long red = await CreateAsync("Red");
        await SendAsync(HttpMethod.Patch, Base + "/policy", new { joinMode = "AdminAssign" });
        string name = f.NextName("Wait");
        var (node, player) = await f.JoinAsync(name);
        await using var _ = node;

        var waiting = (await GetAsync(Base + "/unassigned")).EnumerateArray().First(m => m.GetProperty("playerId").GetInt64() == player);
        Assert.Equal(name, waiting.GetProperty("name").GetString());
        Assert.True(waiting.GetProperty("online").GetBoolean());
        Assert.Contains((await GetAsync()).GetProperty("unassigned").EnumerateArray(), m => m.GetProperty("playerId").GetInt64() == player);

        await MemberAsync(player, red);
        Assert.DoesNotContain((await GetAsync(Base + "/unassigned")).EnumerateArray(), m => m.GetProperty("playerId").GetInt64() == player);
        await ResetAsync();
    }
}
