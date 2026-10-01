using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using X4MP.Core.Net;
using X4MP.Core.Settings;
using X4MP.Persistence;
using X4MP.Server.Auth;
using X4MP.Server.Settings;

namespace X4MP.Server.Tests;

public class SettingsApiTests
{
    private static HttpRequestMessage Patch(string json, bool csrf = true, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/settings")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        if (csrf)
        {
            request.Headers.Add(AuthFactory.Csrf, "1");
        }

        if (token is not null)
        {
            request.Headers.Authorization = new("Bearer", token);
        }

        return request;
    }

    private static HttpRequestMessage Get(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new("Bearer", token);
        return request;
    }

    [Fact]
    public async Task SchemaDescribesLiveAndBootSettings()
    {
        await using var factory = new AuthFactory();
        _ = factory.NewClient();
        var token = factory.Services.GetRequiredService<AdminStore>().CreateToken("v", AdminRoles.Viewer);

        using var response = await factory.NewClient().SendAsync(Get("/api/v1/settings/schema", token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var all = doc.RootElement.GetProperty("settings").EnumerateArray().ToDictionary(e => e.GetProperty("key").GetString()!);

        var tick = all["Replication.TickRateHz"];
        Assert.False(tick.GetProperty("requiresRestart").GetBoolean());
        Assert.Equal("int", tick.GetProperty("type").GetString());
        Assert.Equal(1, tick.GetProperty("min").GetDouble());
        Assert.Equal(60, tick.GetProperty("max").GetDouble());
        Assert.Equal(20, tick.GetProperty("default").GetInt32());

        Assert.True(all["Net.MaxFrameBytes"].GetProperty("requiresRestart").GetBoolean());
        Assert.True(all["Net.JoinPassword"].GetProperty("secret").GetBoolean());

        var visibility = all["Mods.ModListVisibility"];
        Assert.Equal("enum", visibility.GetProperty("type").GetString());
        Assert.Equal(["AdminsOnly", "AdminsAndViewers", "AllPlayers"], visibility.GetProperty("values").EnumerateArray().Select(v => v.GetString()!).ToArray());
        Assert.True(visibility.GetProperty("pushToNodes").GetBoolean());
    }

    [Fact]
    public async Task ReadsNeedViewerWritesNeedAdminAndCsrf()
    {
        await using var factory = new AuthFactory();
        _ = factory.NewClient();
        var store = factory.Services.GetRequiredService<AdminStore>();
        var viewer = store.CreateToken("v", AdminRoles.Viewer);
        var admin = store.CreateToken("a", AdminRoles.Admin);
        var client = factory.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/settings")).StatusCode);
        using var read = await client.SendAsync(Get("/api/v1/settings", viewer));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var viewerPatch = await client.SendAsync(Patch("""{"Replication.TickRateHz":30}""", token: viewer));
        Assert.Equal(HttpStatusCode.Forbidden, viewerPatch.StatusCode);

        var (cookieAdmin, _) = await factory.AdminReadyAsync();
        using var noCsrf = await cookieAdmin.SendAsync(Patch("""{"Replication.TickRateHz":30}""", csrf: false));
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        Assert.Equal(20, factory.Services.GetRequiredService<IOptionsMonitor<ReplicationOptions>>().CurrentValue.TickRateHz);

        using var ok = await client.SendAsync(Patch("""{"Replication.TickRateHz":30}""", token: admin));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task PatchIsEffectiveWithinOneSecondAndPersisted()
    {
        await using var factory = new AuthFactory();
        var (admin, _) = await factory.AdminReadyAsync();
        var monitor = factory.Services.GetRequiredService<IOptionsMonitor<ReplicationOptions>>();
        var seen = new List<int>();
        using var subscription = monitor.OnChange(o => seen.Add(o.TickRateHz));
        Assert.Equal(20, monitor.CurrentValue.TickRateHz);

        var clock = Stopwatch.StartNew();
        using var response = await admin.SendAsync(Patch("""{"Replication.TickRateHz":45}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(45, monitor.CurrentValue.TickRateHz);
        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
        Assert.Contains(45, seen);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(45, doc.RootElement.GetProperty("sections").GetProperty("Replication").GetProperty("TickRateHz").GetInt32());
        Assert.Contains("Replication.TickRateHz", doc.RootElement.GetProperty("overrides").EnumerateArray().Select(e => e.GetString()));

        using var again = JsonDocument.Parse(await admin.GetStringAsync("/api/v1/settings"));
        Assert.Equal(45, again.RootElement.GetProperty("sections").GetProperty("Replication").GetProperty("TickRateHz").GetInt32());

        using var db = new SqliteConnectionFactory(new PersistenceOptions { DataDir = factory.DataDir }).Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value_json, updated_by FROM config_overrides WHERE key = 'Replication.TickRateHz'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("45", reader.GetString(0));
        Assert.Equal("admin", reader.GetString(1));

        // null resets to the default
        using var reset = await admin.SendAsync(Patch("""{"Replication.TickRateHz":null}"""));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(20, monitor.CurrentValue.TickRateHz);
    }

    [Fact]
    public async Task InvalidValuesGiveOneErrorPerKeyAndApplyNothing()
    {
        await using var factory = new AuthFactory();
        var (admin, _) = await factory.AdminReadyAsync();
        var monitor = factory.Services.GetRequiredService<IOptionsMonitor<AlertOptions>>();

        using var response = await admin.SendAsync(Patch(
            """{"Replication.TickRateHz":0,"Alerts.AuthorityFpsThreshold":"fast","Mods.ModListVisibility":"Everyone","No.Such":1,"Alerts.AuthorityFpsSeconds":45}"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ValidationFailed", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(400, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            doc.RootElement.GetProperty("errorCodes").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal),
            doc.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)); // a message list per key, a code per key
        var errors = doc.RootElement.GetProperty("errorCodes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        Assert.Equal(
            new Dictionary<string, string?>
            {
                ["Replication.TickRateHz"] = "OutOfRange",
                ["Alerts.AuthorityFpsThreshold"] = "InvalidType",
                ["Mods.ModListVisibility"] = "UnknownValue",
                ["No.Such"] = "UnknownKey",
            },
            errors);

        // the valid key in the same request was not applied
        Assert.Equal(30, monitor.CurrentValue.AuthorityFpsSeconds);
        Assert.DoesNotContain(factory.AuditRows(), r => r.Action == "settings.update");
    }

    [Fact]
    public async Task OutOfRangeAndWrongTypeAreRejectedForEveryBound()
    {
        await using var factory = new AuthFactory();
        var (admin, _) = await factory.AdminReadyAsync();
        foreach (var body in new[]
        {
            """{"Replication.TickRateHz":61}""",
            """{"Replication.TickRateHz":1.5}""",
            """{"Replication.TickRateHz":"20"}""",
            """{"Replication.TickRateHz":true}""",
        })
        {
            using var response = await admin.SendAsync(Patch(body));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task BootOnlyKeyAnswersRestartRequiredAndIsNotChanged()
    {
        await using var factory = new AuthFactory();
        var (admin, _) = await factory.AdminReadyAsync();
        var before = factory.Services.GetRequiredService<NetOptions>().MaxFrameBytes;

        using var response = await admin.SendAsync(Patch("""{"Net.MaxFrameBytes":2097152}"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("errors").EnumerateObject().Single();
        Assert.Equal("Net.MaxFrameBytes", error.Name);
        Assert.Equal("RestartRequired", doc.RootElement.GetProperty("errorCodes").GetProperty("Net.MaxFrameBytes").GetString());
        Assert.Contains("restart required", error.Value[0].GetString(), StringComparison.Ordinal);
        Assert.Equal(before, factory.Services.GetRequiredService<NetOptions>().MaxFrameBytes);
        Assert.Equal(before, factory.Services.GetRequiredService<IOptionsMonitor<NetOptions>>().CurrentValue.MaxFrameBytes);
    }

    [Fact]
    public async Task ChangesAreAuditedWithOldAndNewValues()
    {
        await using var factory = new AuthFactory();
        var (admin, _) = await factory.AdminReadyAsync();
        using var response = await admin.SendAsync(Patch("""{"Replication.TickRateHz":33,"Mods.ModListVisibility":"allplayers"}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = factory.AuditRows().Where(r => r.Action == "settings.update").ToList();
        Assert.Equal(2, rows.Count);
        var tick = rows.Single(r => r.Target == "Replication.TickRateHz");
        Assert.Equal("admin", tick.Actor);
        using var data = JsonDocument.Parse(tick.Data!);
        Assert.Equal("20", data.RootElement.GetProperty("old").GetString());
        Assert.Equal("33", data.RootElement.GetProperty("new").GetString());
        Assert.Equal(
            ModListVisibility.AllPlayers,
            factory.Services.GetRequiredService<IOptionsMonitor<ModManagementOptions>>().CurrentValue.ModListVisibility);
    }

    [Fact]
    public async Task SecretsAreMaskedInResponses()
    {
        await using var factory = new AuthFactory(new Dictionary<string, string> { ["X4MP:Net:JoinPassword"] = "hunter2-secret" });
        var (admin, _) = await factory.AdminReadyAsync();
        var body = await admin.GetStringAsync("/api/v1/settings");
        Assert.DoesNotContain("hunter2-secret", body, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("***", doc.RootElement.GetProperty("sections").GetProperty("Net").GetProperty("JoinPassword").GetString());
        var schema = await admin.GetStringAsync("/api/v1/settings/schema");
        Assert.DoesNotContain("hunter2-secret", schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingAPushedSettingCallsTheSessionSettingsHook()
    {
        var pusher = new RecordingPusher();
        await using var baseFactory = new AuthFactory();
        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ISessionSettingsPusher>(pusher)));
        var token = factory.Services.GetRequiredService<AdminStore>().CreateToken("a", AdminRoles.Admin);
        var client = factory.CreateClient();

        using var alert = await client.SendAsync(Patch("""{"Alerts.AuthorityFpsSeconds":40}""", token: token));
        Assert.Equal(HttpStatusCode.OK, alert.StatusCode);
        Assert.Empty(pusher.Snapshots); // not a pushed setting

        using var response = await client.SendAsync(Patch("""{"Replication.TickRateHz":25,"Mods.ModListVisibility":"AllPlayers"}""", token: token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = Assert.Single(pusher.Snapshots);
        Assert.Equal(25, snapshot.Values["Replication.TickRateHz"].GetInt32());
        Assert.Equal("AllPlayers", snapshot.Values["Mods.ModListVisibility"].GetString());
        Assert.DoesNotContain("Alerts.AuthorityFpsSeconds", snapshot.Values.Keys);
        Assert.Equal(snapshot.Values["Replication.TickRateHz"].GetInt32(), factory.Services.GetRequiredService<SettingsService>().GetSessionSettings().Values["Replication.TickRateHz"].GetInt32());
    }

    [Fact]
    public async Task OnlyNodeRelevantLiveChangesReachTheSessionActor()
    {
        await using var factory = new AuthFactory();
        var token = factory.Services.GetRequiredService<AdminStore>().CreateToken("a", AdminRoles.Admin);
        var client = factory.CreateClient();
        var actor = factory.Services.GetRequiredService<X4MP.Core.Session.SessionActor>();

        var start = Stopwatch.StartNew();
        while (actor.Settings is null)
        {
            Assert.True(start.Elapsed < TimeSpan.FromSeconds(5), "the actor never got its initial node-relevant settings");
            await Task.Delay(10);
        }

        var initial = actor.Settings;
        Assert.Contains("Mods.ModListVisibility", initial.Values.Keys);
        Assert.Contains("Interest.MaxGhosts", initial.Values.Keys);
        Assert.DoesNotContain("Alerts.AuthorityFpsSeconds", initial.Values.Keys);

        using var alert = await client.SendAsync(Patch("""{"Alerts.AuthorityFpsSeconds":40}""", token: token));
        Assert.Equal(HttpStatusCode.OK, alert.StatusCode);
        await actor.FlushAsync();
        Assert.Same(initial, actor.Settings); // not node-relevant: nothing pushed

        using var relevant = await client.SendAsync(Patch("""{"Mods.ModListVisibility":"AllPlayers"}""", token: token));
        Assert.Equal(HttpStatusCode.OK, relevant.StatusCode);
        var until = Stopwatch.StartNew();
        while (ReferenceEquals(actor.Settings, initial))
        {
            Assert.True(until.Elapsed < TimeSpan.FromSeconds(1), "the push took longer than a second");
            await Task.Delay(5);
        }

        Assert.Equal("AllPlayers", actor.Settings!.Values["Mods.ModListVisibility"].GetString());
        Assert.True(actor.Settings.Version > initial.Version);
    }

    private sealed class RecordingPusher : ISessionSettingsPusher
    {
        public List<SessionSettingsSnapshot> Snapshots { get; } = [];

        public ValueTask PushAsync(SessionSettingsSnapshot snapshot, CancellationToken cancellationToken)
        {
            Snapshots.Add(snapshot);
            return ValueTask.CompletedTask;
        }
    }
}
