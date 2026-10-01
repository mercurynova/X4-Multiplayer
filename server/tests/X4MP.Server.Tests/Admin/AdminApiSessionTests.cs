using System.Net;
using X4MP.Core.Saves;
using X4MP.Persistence;
using X4MP.Server.Tests.Saves;

namespace X4MP.Server.Tests.Admin;

/// <summary>Session create, start, stop, request-save and events, and the save selection, each on its own server.</summary>
public sealed class AdminApiSessionTests
{
    private const string NoSession = "/api/v1/sessions/current";

    private static SaveRecord FakeSave(string sha, string name = "Imported") => new(
        sha, 1234, name, "admin-upload", "tester", DateTimeOffset.UtcNow, SaveMeta.Empty, GhostsCleaned: true, Pinned: false, OriginalFileName: name + ".xml.gz");

    [Fact]
    public async Task SessionLifecycleWalksCreateStartStopAndCreateAgain()
    {
        await using var server = await SaveServer.StartAsync();
        using var admin = server.Http(server.AdminToken());
        using var viewer = server.Http(server.AdminToken("Viewer"));

        using (var none = await viewer.GetAsync(NoSession))
        {
            Assert.Equal(HttpStatusCode.NoContent, none.StatusCode);
        }

        using (var response = await admin.CallAsync(HttpMethod.Post, "/api/v1/sessions", new { name = " " }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "name");
        }

        using (var response = await admin.CallAsync(HttpMethod.Post, "/api/v1/sessions", new { name = "Run", saveId = "xyz" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "saveId");
        }

        using (var response = await admin.CallAsync(HttpMethod.Post, "/api/v1/sessions", new { name = "Run", saveId = new string('c', 64) }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        long id;
        using (var created = await admin.CallAsync(HttpMethod.Post, "/api/v1/sessions", new { name = "Friday run" }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var detail = await created.JsonAsync();
            id = detail.GetProperty("id").GetInt64();
            Assert.True(id > 0);
            Assert.Equal("Friday run", detail.GetProperty("name").GetString());
            Assert.Equal("Idle", detail.GetProperty("state").GetString());
            Assert.True(detail.GetProperty("live").GetBoolean());
        }

        using (var current = await viewer.GetAsync(NoSession))
        {
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
            Assert.Equal(id, (await current.JsonAsync()).GetProperty("id").GetInt64());
        }

        // starting a session that is not the live one
        using (var response = await admin.CallAsync(HttpMethod.Post, "/api/v1/sessions/987654/start", new { }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/start", new { authorityPlayerId = 987654 }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/request-save"))
        {
            await response.AssertProblemAsync(HttpStatusCode.Conflict, "SessionNotRunning");
        }

        using (var started = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/start", new { }))
        {
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            Assert.Equal("WaitingForAuthority", (await started.JsonAsync()).GetProperty("state").GetString());
        }

        using (var response = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/start", new { }))
        {
            await response.AssertProblemAsync(HttpStatusCode.Conflict, "SessionNotIdle");
        }

        using (var response = await admin.CallAsync(HttpMethod.Post, "/api/v1/sessions", new { name = "Second" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.Conflict, "SessionActive");
        }

        using (var response = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/promote", new { playerId = 1 }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotImplemented, "NotImplemented");
        }

        using (var response = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/stop", new { requestFinalSave = false, message = new string('m', 300) }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "message");
        }

        using (var stopped = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/stop", new { requestFinalSave = false, message = "maintenance" }))
        {
            Assert.Equal(HttpStatusCode.Accepted, stopped.StatusCode);
            Assert.Equal("Ended", (await stopped.JsonAsync()).GetProperty("state").GetString());
        }

        using (var response = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/stop", new { }))
        {
            await response.AssertProblemAsync(HttpStatusCode.Conflict, "SessionNotRunning");
        }

        // the ended session is reset and a new one is created
        long second;
        using (var created = await admin.CallAsync(HttpMethod.Post, "/api/v1/sessions", new { name = "Second" }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            second = (await created.JsonAsync()).GetProperty("id").GetInt64();
            Assert.NotEqual(id, second);
        }

        using (var list = await viewer.GetAsync("/api/v1/sessions"))
        {
            var rows = (await list.JsonAsync()).EnumerateArray().ToList();
            Assert.Equal(second, rows[0].GetProperty("id").GetInt64()); // newest first
            Assert.Contains(rows, r => r.GetProperty("id").GetInt64() == id);
            Assert.Equal("Idle", rows[0].GetProperty("state").GetString());
        }

        using (var filtered = await viewer.GetAsync("/api/v1/sessions?state=idle&limit=5"))
        {
            Assert.All((await filtered.JsonAsync()).EnumerateArray(), r => Assert.Equal("Idle", r.GetProperty("state").GetString()));
        }

        // audit: actor, action, target, reason
        var stop = await Api.WaitForAuditAsync(server, "session.stop", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("maintenance", stop.DataJson, StringComparison.Ordinal);
        await Api.WaitForAuditAsync(server, "session.create", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Api.WaitForAuditAsync(server, "session.start", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ListAndEventsRejectBadInputWithProblems()
    {
        await using var server = await SaveServer.StartAsync();
        using var admin = server.Http(server.AdminToken());
        using var viewer = server.Http(server.AdminToken("Viewer"));
        using (var response = await admin.CallAsync(HttpMethod.Post, "/api/v1/sessions", new { name = "Events" }))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using (var response = await viewer.GetAsync("/api/v1/sessions?state=bogus"))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "state");
        }

        using (var response = await viewer.GetAsync("/api/v1/sessions?limit=501"))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "limit");
        }

        long id = await server.Actor.GetStoreSessionIdAsync();
        using (var response = await viewer.GetAsync($"/api/v1/sessions/{id}/events?since=yesterday"))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "since");
        }

        using (var response = await viewer.GetAsync("/api/v1/sessions/987654/events"))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await viewer.GetAsync($"/api/v1/sessions/{id}/events?type=SessionStateChanged&limit=10"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(System.Text.Json.JsonValueKind.Array, (await response.JsonAsync()).ValueKind);
        }
    }

    [Fact]
    public async Task EventsListThePhaseChangesOfTheSession()
    {
        await using var server = await SaveServer.StartAsync();
        using var admin = server.Http(server.AdminToken());
        using var viewer = server.Http(server.AdminToken("Viewer"));
        using var created = await admin.CallAsync(HttpMethod.Post, "/api/v1/sessions", new { name = "Events" });
        long id = (await created.JsonAsync()).GetProperty("id").GetInt64();
        using (var started = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/start", new { }))
        {
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        }

        await SaveServer.WaitUntilAsync(
            () => server.Service<SqliteAdminQueries>().SessionEvents(id, "SessionStateChanged", null, 10).Count > 0, 10_000, "session event row");
        using var events = await viewer.GetAsync($"/api/v1/sessions/{id}/events?type=SessionStateChanged");
        var first = (await events.JsonAsync()).EnumerateArray().First();
        Assert.Equal("SessionStateChanged", first.GetProperty("type").GetString());
    }

    // ------------------------------------------------------------------ selecting a save

    [Fact]
    public async Task ASelectedSaveIsShownAndCannotBeDeletedWhileTheSessionUsesIt()
    {
        await using var server = await SaveServer.StartAsync();
        using var admin = server.Http(server.AdminToken());
        string sha = new('a', 64);
        server.Saves.Catalog.AddSave(FakeSave(sha));
        await SaveServer.WaitUntilAsync(() => server.Saves.Catalog.Find(sha) is not null, 10_000, "save row written");

        using (var created = await admin.CallAsync(HttpMethod.Post, "/api/v1/sessions", new { name = "With save", saveId = sha.ToUpperInvariant() }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var detail = await created.JsonAsync();
            Assert.Equal(sha, detail.GetProperty("saveSha256").GetString());
            Assert.Equal("Imported", detail.GetProperty("saveName").GetString());
        }

        using (var delete = await admin.CallAsync(HttpMethod.Delete, $"/api/v1/saves/{sha}"))
        {
            await delete.AssertProblemAsync(HttpStatusCode.Conflict, "InUse");
        }

        long id = await server.Actor.GetStoreSessionIdAsync();
        using (var stopStart = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/start", new { }))
        {
            Assert.Equal(HttpStatusCode.Accepted, stopStart.StatusCode);
        }

        using (var stopped = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/stop", new { requestFinalSave = false }))
        {
            Assert.Equal(HttpStatusCode.Accepted, stopped.StatusCode);
        }

        // an ended session no longer holds the save (the delete may also have to wait for the write-behind row update)
        await SaveServer.WaitUntilAsync(
            () => !server.Saves.Catalog.ReferencedSha256().Contains(sha), 10_000, "session row ended");
        using (var delete = await admin.CallAsync(HttpMethod.Delete, $"/api/v1/saves/{sha}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }
    }

    [Fact]
    public async Task SessionSettingsAreValidatedLikeTheSettingsPatch()
    {
        await using var server = await SaveServer.StartAsync();
        using var admin = server.Http(server.AdminToken());
        using (var response = await admin.CallAsync(HttpMethod.Post, "/api/v1/sessions", new { name = "S", settings = new Dictionary<string, object> { ["No.Such"] = 1 } }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "settings.No.Such");
        }

        using (var response = await admin.CallAsync(HttpMethod.Post, "/api/v1/sessions", new { name = "S", settings = new Dictionary<string, object> { ["Alerts.AuthorityFpsSeconds"] = 45 } }))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using var settings = await admin.GetAsync("/api/v1/settings");
        Assert.Equal(45, (await settings.JsonAsync()).GetProperty("sections").GetProperty("Alerts").GetProperty("AuthorityFpsSeconds").GetInt32());
    }
}
