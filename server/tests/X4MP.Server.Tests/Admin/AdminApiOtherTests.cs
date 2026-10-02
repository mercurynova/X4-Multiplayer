using System.Net;
using System.Security.Cryptography;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Server.Tests.Saves;

namespace X4MP.Server.Tests.Admin;

/// <summary>Server info, dashboard, chat, logs, diagnostics, tokens, audit, saves errors and the role matrix.</summary>
public sealed class AdminApiOtherTests(AdminServerFixture f) : IClassFixture<AdminServerFixture>
{
    // ------------------------------------------------------------------ roles: 401 and 403 on every endpoint

    public static TheoryData<string, string, bool> Endpoints => new()
    {
        { "GET", "/api/v1/server", false },
        { "GET", "/api/v1/dashboard", false },
        { "GET", "/api/v1/sessions", false },
        { "GET", "/api/v1/sessions/current", false },
        { "POST", "/api/v1/sessions", true },
        { "POST", "/api/v1/sessions/1/start", true },
        { "POST", "/api/v1/sessions/1/stop", true },
        { "POST", "/api/v1/sessions/1/request-save", true },
        { "POST", "/api/v1/sessions/1/promote", true },
        { "GET", "/api/v1/sessions/1/events", false },
        { "GET", "/api/v1/players", false },
        { "GET", "/api/v1/players/1", false },
        { "POST", "/api/v1/players/1/kick", true },
        { "POST", "/api/v1/players/1/mute", true },
        { "DELETE", "/api/v1/players/1/mute", true },
        { "POST", "/api/v1/players/1/teleport-view", true },
        { "PATCH", "/api/v1/players/1", true },
        { "GET", "/api/v1/bans", false },
        { "POST", "/api/v1/bans", true },
        { "DELETE", "/api/v1/bans/1", true },
        { "GET", "/api/v1/chat", false },
        { "POST", "/api/v1/chat", true },
        { "GET", "/api/v1/galaxy", false },
        { "GET", "/api/v1/galaxy/sectors/1", false },
        { "GET", "/api/v1/logs", false },
        { "GET", "/api/v1/logs/download", true },
        { "GET", "/api/v1/settings", false },
        { "PATCH", "/api/v1/settings", true },
        { "GET", "/api/v1/diagnostics/connections", false },
        { "POST", "/api/v1/diagnostics/connections/1/trace", true },
        { "GET", "/api/v1/diagnostics/metrics", false },
        { "GET", "/api/v1/tokens", true },
        { "POST", "/api/v1/tokens", true },
        { "DELETE", "/api/v1/tokens/1", true },
        { "GET", "/api/v1/audit", true },
        { "GET", "/api/v1/saves", false },
        { "PATCH", "/api/v1/saves/" + "ab", true },
        { "DELETE", "/api/v1/saves/" + "ab", true },
        { "POST", "/api/v1/saves/uploads", true },
        { "GET", "/api/v1/saves/uploads/x", true },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task EveryEndpointAnswers401WithoutCredentialsAnd403ToAViewerWhereAdminOnly(string method, string url, bool adminOnly)
    {
        using (var anonymous = await f.Anon.CallAsync(new HttpMethod(method), url, method is "POST" or "PATCH" ? new { } : null))
        {
            await anonymous.AssertProblemAsync(HttpStatusCode.Unauthorized, "Unauthorized");
        }

        using var viewer = await f.Viewer.CallAsync(new HttpMethod(method), url, method is "POST" or "PATCH" ? new { } : null);
        if (adminOnly)
        {
            await viewer.AssertProblemAsync(HttpStatusCode.Forbidden, "Forbidden");
        }
        else
        {
            Assert.NotEqual(HttpStatusCode.Forbidden, viewer.StatusCode);
            Assert.NotEqual(HttpStatusCode.Unauthorized, viewer.StatusCode);
        }
    }

    [Fact]
    public async Task UnknownRoutesWrongMethodsAndTheOldAuthPathsAreProblems()
    {
        using (var response = await f.Admin.GetAsync("/api/v1/nothing-here"))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await f.Admin.CallAsync(HttpMethod.Post, "/api/auth/login", new { username = "admin", password = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound"); // no alias for the old route
        }

        using (var response = await f.Admin.CallAsync(HttpMethod.Put, "/api/v1/server", new { }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound"); // the SPA fallback owns every unmatched method
        }
    }

    // ------------------------------------------------------------------ server, dashboard

    [Fact]
    public async Task ServerInfoAndDashboardDescribeTheRunningServer()
    {
        using (var response = await f.Viewer.GetAsync("/api/v1/server"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var info = await response.JsonAsync();
            Assert.False(string.IsNullOrEmpty(info.GetProperty("version").GetString()));
            var range = info.GetProperty("protocolRange");
            Assert.Equal(X4MP.Server.Hosting.ServerInfo.ProtocolMin, range.GetProperty("min").GetInt32());
            // wire version as major * 1000 + minor: this build speaks 0.1 => 1, never the old 0..0
            Assert.Equal((ProtocolConstants.ProtocolMajor * 1000) + ProtocolConstants.ProtocolMinor, range.GetProperty("max").GetInt32());
            Assert.True(range.GetProperty("max").GetInt32() > 0);
            // the node TCP listener is not a GUI URL
            Assert.DoesNotContain(info.GetProperty("adminUrls").EnumerateArray().Select(e => e.GetString()), u => u!.EndsWith($":{f.Server.TcpPort}", StringComparison.Ordinal));
            Assert.Contains($"tcp://127.0.0.1:{f.Server.TcpPort}", info.GetProperty("nodeEndpoints").EnumerateArray().Select(e => e.GetString()));
            Assert.False(info.GetProperty("https").GetBoolean());
        }

        var (node, id) = await f.JoinAsync(f.NextName("Dash"));
        await using var joined = node;
        using (var response = await f.Viewer.GetAsync("/api/v1/dashboard"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var dashboard = await response.JsonAsync();
            Assert.True(dashboard.GetProperty("playersOnline").GetInt32() >= 1);
            Assert.Contains(dashboard.GetProperty("players").EnumerateArray(), p => p.GetProperty("playerId").GetInt64() == id);
            Assert.Equal(32, dashboard.GetProperty("maxPlayers").GetInt32());
        }
    }

    // ------------------------------------------------------------------ chat

    [Fact]
    public async Task AdminChatReachesNodesIsStoredAndShowsInTheHistory()
    {
        string name = f.NextName("Chatty");
        var (node, id) = await f.JoinAsync(name);
        await using var joined = node;

        using (var response = await f.Admin.CallAsync(HttpMethod.Post, "/api/v1/chat", new { text = "server restarts in 5 minutes", channel = "all", asBroadcast = true }))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.True((await response.JsonAsync()).GetProperty("delivered").GetInt32() >= 1);
        }

        using (var response = await f.Admin.CallAsync(HttpMethod.Post, "/api/v1/chat", new { text = "psst", channel = "player", toPlayerId = id }))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal(1, (await response.JsonAsync()).GetProperty("delivered").GetInt32());
        }

        await SaveServer.WaitUntilAsync(() => f.Server.Service<X4MP.Persistence.SqliteAdminQueries>().ChatHistory(null, null, 50).Any(c => c.Text == "psst"), 10_000, "chat row");
        using (var history = await f.Viewer.GetAsync("/api/v1/chat?limit=50"))
        {
            var rows = (await history.JsonAsync()).EnumerateArray().ToList();
            var line = rows.Single(r => r.GetProperty("text").GetString() == "psst");
            Assert.True(line.GetProperty("fromAdmin").GetBoolean());
            Assert.StartsWith("test-", line.GetProperty("from").GetString(), StringComparison.Ordinal);
            Assert.Equal("Admin", line.GetProperty("channel").GetString());
            Assert.Equal(rows.Select(r => r.GetProperty("id").GetInt64()).Order(), rows.Select(r => r.GetProperty("id").GetInt64()));
        }

        var audit = await Api.WaitForAuditAsync(f.Server, "chat.send", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("player", audit.DataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatErrorsAreProblems()
    {
        using (var response = await f.Admin.CallAsync(HttpMethod.Post, "/api/v1/chat", new { text = "  " }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "text");
        }

        using (var response = await f.Admin.CallAsync(HttpMethod.Post, "/api/v1/chat", new { text = new string('x', 300) }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "text");
        }

        using (var response = await f.Admin.CallAsync(HttpMethod.Post, "/api/v1/chat", new { text = "hi", channel = "team" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "channel");
        }

        using (var response = await f.Admin.CallAsync(HttpMethod.Post, "/api/v1/chat", new { text = "hi", channel = "player" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "toPlayerId");
        }

        using (var response = await f.Admin.CallAsync(HttpMethod.Post, "/api/v1/chat", new { text = "hi", channel = "player", toPlayerId = 987654 }))
        {
            await response.AssertProblemAsync(HttpStatusCode.Conflict, "PlayerNotOnline");
        }

        using (var response = await f.Viewer.GetAsync("/api/v1/chat?limit=9999"))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "limit");
        }
    }

    // ------------------------------------------------------------------ logs

    [Fact]
    public async Task LogsFilterByLevelSourceAndTextAndPageBySequence()
    {
        using var response = await f.Viewer.GetAsync("/api/v1/logs?limit=1000");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var all = (await response.JsonAsync()).EnumerateArray().ToList();
        Assert.NotEmpty(all);
        long[] seqs = [.. all.Select(e => e.GetProperty("seq").GetInt64())];
        Assert.Equal(seqs.Order(), seqs);

        var sample = all[^1];
        string message = sample.GetProperty("message").GetString()!;
        using (var byText = await f.Viewer.GetAsync("/api/v1/logs?q=" + Uri.EscapeDataString(message[..Math.Min(20, message.Length)])))
        {
            Assert.Contains((await byText.JsonAsync()).EnumerateArray(), e => e.GetProperty("seq").GetInt64() == sample.GetProperty("seq").GetInt64());
        }

        using (var errors = await f.Viewer.GetAsync("/api/v1/logs?level=Error"))
        {
            Assert.All((await errors.JsonAsync()).EnumerateArray(), e => Assert.True(e.GetProperty("level").GetString() is "Error" or "Fatal"));
        }

        string source = sample.GetProperty("source").GetString()!;
        using (var bySource = await f.Viewer.GetAsync("/api/v1/logs?source=" + Uri.EscapeDataString(source)))
        {
            Assert.All((await bySource.JsonAsync()).EnumerateArray(), e => Assert.Contains(source, e.GetProperty("source").GetString(), StringComparison.OrdinalIgnoreCase));
        }

        using (var paged = await f.Viewer.GetAsync($"/api/v1/logs?before={seqs[^1]}&limit=1000"))
        {
            Assert.DoesNotContain((await paged.JsonAsync()).EnumerateArray(), e => e.GetProperty("seq").GetInt64() >= seqs[^1]);
        }
    }

    [Fact]
    public async Task LogErrorsAreProblemsAndTheDownloadStreamsTheRollingFile()
    {
        using (var response = await f.Viewer.GetAsync("/api/v1/logs?level=Loud"))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "level");
        }

        using (var response = await f.Viewer.GetAsync("/api/v1/logs?limit=0"))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "limit");
        }

        using (var response = await f.Admin.GetAsync("/api/v1/logs/download?date=tomorrow-ish"))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "date");
        }

        using (var response = await f.Admin.GetAsync("/api/v1/logs/download?date=1999-01-01"))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await f.Admin.GetAsync("/api/v1/logs/download"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("x4mp-server", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        await Api.WaitForAuditAsync(f.Server, "logs.download");
    }

    // ------------------------------------------------------------------ diagnostics

    [Fact]
    public async Task ConnectionStatsListTheConnectedNodesAndTraceValidatesItsInput()
    {
        var (node, _) = await f.JoinAsync(f.NextName("Diag"));
        await using var joined = node;
        long connectionId;
        using (var response = await f.Viewer.GetAsync("/api/v1/diagnostics/connections"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var rows = (await response.JsonAsync()).EnumerateArray().ToList();
            Assert.NotEmpty(rows);
            var row = rows[^1];
            connectionId = row.GetProperty("connectionId").GetInt64();
            Assert.True(row.GetProperty("bytesOut").GetInt64() > 0);
            Assert.Equal("tcp", row.GetProperty("transport").GetString());
            Assert.True(row.TryGetProperty("control", out _));
        }

        using (var response = await f.Admin.CallAsync(HttpMethod.Post, $"/api/v1/diagnostics/connections/{connectionId}/trace", new { enabled = true, sampleEvery = 0 }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "sampleEvery");
        }

        using (var response = await f.Admin.CallAsync(HttpMethod.Post, "/api/v1/diagnostics/connections/987654/trace", new { enabled = true, sampleEvery = 10 }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await f.Admin.CallAsync(HttpMethod.Post, $"/api/v1/diagnostics/connections/{connectionId}/trace", new { enabled = true, sampleEvery = 10 }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotImplemented, "NotImplemented");
        }
    }

    [Fact]
    public async Task MetricsStillAnswerUnderTheProblemRules()
    {
        using var response = await f.Viewer.GetAsync("/api/v1/diagnostics/metrics?window=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, (await response.JsonAsync()).ValueKind);
    }

    // ------------------------------------------------------------------ galaxy before an authority

    [Fact]
    public async Task GalaxyReadsAre404UntilTheAuthorityHasSentTheMetadata()
    {
        using (var response = await f.Viewer.GetAsync("/api/v1/galaxy"))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "GalaxyUnavailable");
        }

        using (var response = await f.Viewer.GetAsync("/api/v1/galaxy/sectors/1"))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }
    }

    // ------------------------------------------------------------------ tokens and audit

    [Fact]
    public async Task TokensAreCreatedListedUsedAndRevokedWithoutEverBeingStoredOrAudited()
    {
        string tokenName = f.NextName("ci-");
        string plaintext;
        long id;
        using (var created = await f.Admin.CallAsync(HttpMethod.Post, "/api/v1/tokens", new { name = tokenName, role = "Viewer" }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var body = await created.JsonAsync();
            id = body.GetProperty("id").GetInt64();
            plaintext = body.GetProperty("token").GetString()!;
            Assert.StartsWith("x4mp_", plaintext, StringComparison.Ordinal);
        }

        using (var list = await f.Admin.GetAsync("/api/v1/tokens"))
        {
            string text = await list.Content.ReadAsStringAsync();
            Assert.DoesNotContain(plaintext, text, StringComparison.Ordinal); // the list never shows a token
            Assert.Contains(tokenName, text, StringComparison.Ordinal);
        }

        using (var bearer = f.Server.Http(plaintext))
        using (var read = await bearer.GetAsync("/api/v1/server"))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using var write = await bearer.CallAsync(HttpMethod.Post, "/api/v1/chat", new { text = "hi" });
            await write.AssertProblemAsync(HttpStatusCode.Forbidden, "Forbidden"); // the Viewer role it was created with
        }

        using (var revoke = await f.Admin.CallAsync(HttpMethod.Delete, $"/api/v1/tokens/{id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        using (var bearer = f.Server.Http(plaintext))
        using (var read = await bearer.GetAsync("/api/v1/server"))
        {
            await read.AssertProblemAsync(HttpStatusCode.Unauthorized, "Unauthorized");
        }

        using (var again = await f.Admin.CallAsync(HttpMethod.Delete, $"/api/v1/tokens/{id}"))
        {
            await again.AssertProblemAsync(HttpStatusCode.Conflict, "AlreadyRevoked");
        }

        // the audit trail names the token (id, name, role) but never its value
        await Api.WaitForAuditAsync(f.Server, "token.create", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Api.WaitForAuditAsync(f.Server, "token.revoke", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var audit = await f.Admin.GetAsync("/api/v1/audit?limit=1000");
        string auditText = await audit.Content.ReadAsStringAsync();
        Assert.DoesNotContain(plaintext, auditText, StringComparison.Ordinal);
        var created2 = (await audit.JsonAsync()).EnumerateArray().First(a => a.GetProperty("action").GetString() == "token.create" && a.GetProperty("target").GetString() == id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(tokenName, created2.GetProperty("data").GetProperty("name").GetString());
        Assert.StartsWith("test-", created2.GetProperty("actor").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TokenAndAuditErrorsAreProblems()
    {
        using (var response = await f.Admin.CallAsync(HttpMethod.Post, "/api/v1/tokens", new { name = "", role = "Root" }))
        {
            var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "name");
            Assert.True(problem.GetProperty("errors").TryGetProperty("role", out _));
        }

        using (var response = await f.Admin.CallAsync(HttpMethod.Delete, "/api/v1/tokens/987654"))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await f.Admin.GetAsync("/api/v1/audit?limit=0"))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "limit");
        }
    }

    // ------------------------------------------------------------------ saves: problems

    [Fact]
    public async Task SaveEndpointsAnswerProblemDetails()
    {
        string unknown = new('d', 64);
        using (var response = await f.Admin.CallAsync(HttpMethod.Patch, $"/api/v1/saves/{unknown}", new { displayName = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await f.Admin.CallAsync(HttpMethod.Patch, $"/api/v1/saves/{unknown}", new { }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "body");
        }

        using (var response = await f.Admin.CallAsync(HttpMethod.Delete, "/api/v1/saves/not-a-sha"))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "sha");
        }

        using (var response = await f.Admin.CallAsync(HttpMethod.Delete, $"/api/v1/saves/{unknown}"))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await f.Admin.CallAsync(HttpMethod.Post, "/api/v1/saves/uploads", new { fileName = "a.xml.gz", size = 0, sha256 = "short" }))
        {
            var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "sha256");
            Assert.True(problem.GetProperty("errors").TryGetProperty("size", out _));
        }

        using (var response = await f.Admin.GetAsync("/api/v1/saves/uploads/nope"))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await f.Viewer.GetAsync($"/api/v1/saves/{unknown}/download"))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task SaveListShowsAnUploadedSaveAndRenameWorks()
    {
        // a stored save, added as the admin upload would
        byte[] bytes = RandomNumberGenerator.GetBytes(64);
        string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        f.Server.Saves.Catalog.AddSave(new X4MP.Core.Saves.SaveRecord(
            sha, bytes.Length, "listed", "admin-upload", "tester", DateTimeOffset.UtcNow, X4MP.Core.Saves.SaveMeta.Empty, true, false, "listed.xml.gz"));
        await SaveServer.WaitUntilAsync(() => f.Server.Saves.Catalog.Find(sha) is not null, 10_000, "save row");

        using (var list = await f.Viewer.GetAsync("/api/v1/saves"))
        {
            Assert.Contains((await list.JsonAsync()).EnumerateArray(), s => s.GetProperty("sha256").GetString() == sha);
        }

        using (var rename = await f.Admin.CallAsync(HttpMethod.Patch, $"/api/v1/saves/{sha}", new { displayName = "renamed", pinned = true }))
        {
            Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
            var body = await rename.JsonAsync();
            Assert.Equal("renamed", body.GetProperty("displayName").GetString());
            Assert.True(body.GetProperty("pinned").GetBoolean());
        }

        using (var response = await f.Admin.CallAsync(HttpMethod.Delete, $"/api/v1/saves/{sha}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
    }
}

/// <summary>An authority-backed server: galaxy, sector detail, request-save and the live session detail.</summary>
public sealed class AdminApiAuthorityTests
{
    [Fact]
    public async Task AuthorityDrivesGalaxyRequestSaveAndTheLiveSessionDetail()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        using var admin = server.Http(server.AdminToken());
        using var viewer = server.Http(server.AdminToken("Viewer"));
        await using var authority = await AuthorityRig.StartAsync(server, new X4MP.FakeNode.FakeAuthoritySaveOptions
        {
            SaveBytes = 1024 * 1024,
            Directory = Path.Combine(server.Dir, "fake-authority"),
        });
        await server.WaitForAsync(s => s.Phase == SessionPhase.Running, 60_000, "first checkpoint");

        long sectorId;
        using (var galaxy = await viewer.GetAsync("/api/v1/galaxy"))
        {
            Assert.Equal(HttpStatusCode.OK, galaxy.StatusCode);
            var body = await galaxy.JsonAsync();
            var sectors = body.GetProperty("sectors").EnumerateArray().ToList();
            Assert.NotEmpty(sectors);
            Assert.NotEmpty(body.GetProperty("links").EnumerateArray());
            Assert.NotEmpty(body.GetProperty("clusters").EnumerateArray());
            sectorId = sectors[0].GetProperty("id").GetInt64();
        }

        using (var sector = await viewer.GetAsync($"/api/v1/galaxy/sectors/{sectorId}"))
        {
            Assert.Equal(HttpStatusCode.OK, sector.StatusCode);
            var body = await sector.JsonAsync();
            Assert.Equal(sectorId, body.GetProperty("sector").GetProperty("id").GetInt64());
            Assert.True(body.TryGetProperty("neighbors", out _));
        }

        using (var missing = await viewer.GetAsync("/api/v1/galaxy/sectors/60000"))
        {
            await missing.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        long id = await server.Actor.GetStoreSessionIdAsync();
        using (var current = await viewer.GetAsync("/api/v1/sessions/current"))
        {
            var body = await current.JsonAsync();
            Assert.Equal("Running", body.GetProperty("state").GetString());
            Assert.Equal("Live", body.GetProperty("authority").GetProperty("status").GetString());
            Assert.Contains(body.GetProperty("nodes").EnumerateArray(), n => n.GetProperty("roles").GetString()!.Contains("Authority", StringComparison.Ordinal));
        }

        using (var dashboard = await viewer.GetAsync("/api/v1/dashboard"))
        {
            var body = await dashboard.JsonAsync();
            Assert.Equal("Running", body.GetProperty("session").GetProperty("state").GetString());
            Assert.True(body.GetProperty("entitiesInMirror").GetInt32() >= 0);
        }

        // the first checkpoint may still be finishing: 409 SaveNotPossible until the authority is free for the next request
        HttpStatusCode requested = HttpStatusCode.Conflict;
        for (int attempt = 0; attempt < 100 && requested != HttpStatusCode.Accepted; attempt++)
        {
            using var request = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/request-save");
            requested = request.StatusCode;
            if (requested == HttpStatusCode.Conflict)
            {
                await request.AssertProblemAsync(HttpStatusCode.Conflict, "SaveNotPossible");
                await Task.Delay(100);
            }
        }

        Assert.Equal(HttpStatusCode.Accepted, requested);

        await Api.WaitForAuditAsync(server, "session.request-save", id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using (var stop = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/stop", new { requestFinalSave = false }))
        {
            Assert.Equal(HttpStatusCode.Accepted, stop.StatusCode);
            Assert.Equal("Ended", (await stop.JsonAsync()).GetProperty("state").GetString());
        }
    }
}
