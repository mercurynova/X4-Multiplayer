using System.Globalization;
using System.Net;
using System.Text.Json;
using X4MP.Core.Mods;
using X4MP.Core.Session;
using X4MP.Persistence;
using X4MP.Proto;
using X4MP.Server.Auth;
using X4MP.Server.Tests.Saves;

namespace X4MP.Server.Tests.Admin;

/// <summary>
/// The mods admin REST API (M1-X4) over the real server: a happy and an error path for every endpoint, the roles (Viewer, ModEditor, Admin), the
/// <c>ModListVisibility</c> rules, the audit rows and the policy version. Every test works on its own mod ids and leaves the policy knobs as it found them.
/// </summary>
public sealed class ModsAdminApiTests(AdminServerFixture f) : IClassFixture<AdminServerFixture>
{
    private const string Base = "/api/v1/mods";
    private const string Nexus = "https://www.nexusmods.com/x4foundations/mods/1234";

    private HttpClient Admin => f.Admin;

    private HttpClient? _editor;

    private HttpClient Editor => _editor ??= f.Server.Http(f.Server.AdminToken(AdminRoles.ModEditor));

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static async Task<JsonElement> SendAsync(HttpClient client, HttpMethod method, string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await client.CallAsync(method, url, body);
        string text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{method} {url}: expected {(int)expected}, got {(int)response.StatusCode}: {text}");
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private Task<JsonElement> StateAsync(HttpClient? client = null) => SendAsync(client ?? Admin, HttpMethod.Get, Base);

    private static JsonElement? Entry(JsonElement state, string id) =>
        state.GetProperty("policy").GetProperty("entries").EnumerateArray().Where(e => e.GetProperty("id").GetString() == id).Select(e => (JsonElement?)e).FirstOrDefault();

    private async Task SetVisibilityAsync(string value)
    {
        await SendAsync(Admin, HttpMethod.Patch, "/api/v1/settings", $$"""{"Mods.ModListVisibility":"{{value}}"}""");
        await SaveServer.WaitUntilAsync(
            () => StateAsync().GetAwaiter().GetResult().GetProperty("policy").GetProperty("modListVisibility").GetString() == value, 10_000, "visibility " + value);
    }

    private async Task<(TcpNodeClientHolder Node, long PlayerId)> JoinAsync(string name, params ExtensionInfoT[] extensions)
    {
        var node = await f.Server.ConnectAsync(name, Role.Client, extensions: extensions);
        return (new TcpNodeClientHolder(node), node.Welcome.PlayerId);
    }

    private sealed class TcpNodeClientHolder(X4MP.Protocol.Client.TcpNodeClient node) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => node.DisposeAsync();
    }

    private static ExtensionInfoT Ext(string id, string version = "1.0", bool enabled = true, ExtensionClass hint = ExtensionClass.Sim) => new()
    {
        Id = id, Name = "Name of " + id, Version = version, Enabled = enabled, ClassHint = hint, Source = ExtensionSource.Workshop,
        WorkshopId = X4MP.Protocol.ModLinks.WorkshopIdOf(id), ContentHash = [], Dependencies = [],
    };

    // ------------------------------------------------------------------ roles

    [Fact]
    public async Task EveryEndpointChecksItsRole()
    {
        string id = "ws_70001";
        var writes = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Patch, Base + "/policy", new { enforcement = "Strict" }),
            (HttpMethod.Put, $"{Base}/entries/{id}", new { rule = "Allowed" }),
            (HttpMethod.Delete, $"{Base}/entries/{id}", null),
            (HttpMethod.Post, Base + "/import-from-authority", new { merge = true }),
            (HttpMethod.Put, $"{Base}/catalog/{id}", new { notes = "x" }),
        };
        foreach (var (method, url, body) in writes)
        {
            using var anon = await f.Anon.CallAsync(method, url, body, csrf: true);
            await anon.AssertProblemAsync(HttpStatusCode.Unauthorized, "Unauthorized");
            using var viewer = await f.Viewer.CallAsync(method, url, body);
            await viewer.AssertProblemAsync(HttpStatusCode.Forbidden, "Forbidden");
        }

        var reads = new[] { Base, Base + "/save-requirements", Base + "/catalog", "/api/v1/players/1/extensions" };
        foreach (string url in reads)
        {
            using var anon = await f.Anon.CallAsync(HttpMethod.Get, url, csrf: true);
            await anon.AssertProblemAsync(HttpStatusCode.Unauthorized, "Unauthorized");
        }

        // a ModEditor and an Admin can edit; a Viewer reads
        using var created = await Editor.CallAsync(HttpMethod.Put, $"{Base}/entries/{id}", new { rule = "Allowed" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var adminEdit = await Admin.CallAsync(HttpMethod.Put, $"{Base}/entries/{id}", new { notes = "admin" });
        Assert.Equal(HttpStatusCode.OK, adminEdit.StatusCode);
        using var viewerRead = await f.Viewer.CallAsync(HttpMethod.Get, Base);
        Assert.Equal(HttpStatusCode.OK, viewerRead.StatusCode);
        await SendAsync(Admin, HttpMethod.Delete, $"{Base}/entries/{id}", null, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task TheStateSaysWhoCanEdit()
    {
        Assert.True((await StateAsync(Admin)).GetProperty("canEdit").GetBoolean());
        Assert.True((await StateAsync(Editor)).GetProperty("canEdit").GetBoolean());
        Assert.False((await StateAsync(f.Viewer)).GetProperty("canEdit").GetBoolean());
        var state = await StateAsync();
        Assert.False(state.GetProperty("saveRequirementsAvailable").GetBoolean());
        Assert.Equal("AuthorityDefines", state.GetProperty("policy").GetProperty("sourceMode").GetString());
        Assert.True(state.GetProperty("policy").GetProperty("version").GetInt64() >= 1);
    }

    // ------------------------------------------------------------------ policy

    [Fact]
    public async Task PatchPolicyChangesTheKnobsBumpsTheVersionAndIsAudited()
    {
        var before = (await StateAsync()).GetProperty("policy");
        long version = before.GetProperty("version").GetInt64();

        var after = await SendAsync(Admin, HttpMethod.Patch, Base + "/policy", new { sourceMode = "adminlist", unknownDefault = "Block", enforcement = "Warn" });
        Assert.Equal(("AdminList", "Block", "Warn"), (after.GetProperty("sourceMode").GetString(), after.GetProperty("unknownDefault").GetString(), after.GetProperty("enforcement").GetString()));
        Assert.Equal(version + 1, after.GetProperty("version").GetInt64());
        Assert.Equal("admin:test-Admin", after.GetProperty("updatedBy").GetString());

        // the same values again: nothing changes, no new version
        var again = await SendAsync(Admin, HttpMethod.Patch, Base + "/policy", new { enforcement = "Warn" });
        Assert.Equal(version + 1, again.GetProperty("version").GetInt64());

        var audit = await Api.WaitForAuditAsync(f.Server, "mods.policy");
        Assert.Contains("AdminList", audit.DataJson);

        // put it back for the other tests
        await SendAsync(Admin, HttpMethod.Patch, Base + "/policy", new { sourceMode = "AuthorityDefines", unknownDefault = "AllowClientOnly", enforcement = "Strict" });
    }

    [Fact]
    public async Task PatchPolicyRejectsBadValues()
    {
        using var bad = await Admin.CallAsync(HttpMethod.Patch, Base + "/policy", new { sourceMode = "Nope", enforcement = "Maybe" });
        var problem = await bad.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "sourceMode");
        Assert.True(problem.GetProperty("errors").TryGetProperty("enforcement", out _));
        using var empty = await Admin.CallAsync(HttpMethod.Patch, Base + "/policy", new { });
        await empty.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "body");
    }

    // ------------------------------------------------------------------ entries

    [Fact]
    public async Task PutEntryCreatesThenUpdatesAndDerivesTheLinks()
    {
        string id = "ws_1234567890";
        long version = (await StateAsync()).GetProperty("policy").GetProperty("version").GetInt64();

        var created = await SendAsync(Admin, HttpMethod.Put, $"{Base}/entries/{id}",
            new { rule = "Required", name = "Warehouse Fleets", version = "1.4", nexusUrl = "https://nexusmods.com/x4foundations/mods/1234/?tab=files", notes = "needs SirNukes" },
            HttpStatusCode.Created);
        Assert.Equal(("Required", true, "Exact", "1.4", Nexus, 1234567890L, "Warehouse Fleets", "needs SirNukes"),
            (created.GetProperty("rule").GetString(), created.GetProperty("enabled").GetBoolean(), created.GetProperty("versionRule").GetString(), created.GetProperty("version").GetString(),
             created.GetProperty("nexusUrl").GetString(), created.GetProperty("workshopId").GetInt64(), created.GetProperty("name").GetString(), created.GetProperty("notes").GetString()));
        Assert.Equal("https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890", created.GetProperty("workshopUrl").GetString());
        Assert.Equal("steam://url/CommunityFilePage/1234567890", created.GetProperty("workshopSteamUrl").GetString());
        Assert.Equal("Sim", created.GetProperty("class").GetString()); // Unknown counts as Sim
        Assert.False(created.GetProperty("isLibrary").GetBoolean());

        // a partial update keeps what is not sent
        var updated = await SendAsync(Admin, HttpMethod.Put, $"{Base}/entries/{id}", new { enabled = false, versionRule = "AtLeast", classOverride = "ClientOnly" });
        Assert.Equal((false, "AtLeast", "ClientOnly", "ClientOnly", "1.4", Nexus), (updated.GetProperty("enabled").GetBoolean(), updated.GetProperty("versionRule").GetString(),
            updated.GetProperty("classOverride").GetString(), updated.GetProperty("class").GetString(), updated.GetProperty("version").GetString(), updated.GetProperty("nexusUrl").GetString()));
        // an empty string clears a link
        var cleared = await SendAsync(Admin, HttpMethod.Put, $"{Base}/entries/{id}", new { nexusUrl = "", contentHash = "AB12" });
        Assert.Null(cleared.GetProperty("nexusUrl").GetString());
        Assert.Equal("ab12", cleared.GetProperty("contentHash").GetString());

        var state = await StateAsync();
        Assert.Equal(version + 3, state.GetProperty("policy").GetProperty("version").GetInt64());
        Assert.NotNull(Entry(state, id));
        Assert.Equal("test-Admin", (await Api.WaitForAuditAsync(f.Server, "mods.entry.add", id)).Actor);
        Assert.Contains("fields", (await Api.WaitForAuditAsync(f.Server, "mods.entry.update", id)).DataJson);

        // what the admin typed is remembered for every later session
        var catalog = await SendAsync(Admin, HttpMethod.Get, Base + "/catalog");
        var known = catalog.EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);
        Assert.Equal(("Warehouse Fleets", true), (known.GetProperty("name").GetString(), known.GetProperty("inPolicy").GetBoolean()));

        await SendAsync(Admin, HttpMethod.Delete, $"{Base}/entries/{id}", null, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task PutEntryRejectsInvalidInputWithFieldErrors()
    {
        string url = $"{Base}/entries/ws_70002";
        foreach (var (body, key) in new (object, string)[]
        {
            (new { rule = "Required", nexusUrl = "https://www.nexusmods.com/skyrimspecialedition/mods/1234" }, "nexusUrl"),
            (new { rule = "Required", nexusUrl = "https://evil.example/x4foundations/mods/1" }, "nexusUrl"),
            (new { rule = "Maybe" }, "rule"),
            (new { name = "no rule on a new entry" }, "rule"),
            (new { rule = "Allowed", classOverride = "Dlc" }, "classOverride"),
            (new { rule = "Allowed", versionRule = "Newer" }, "versionRule"),
            (new { rule = "Allowed", contentHash = "xyz" }, "contentHash"),
            (new { rule = "Allowed", workshopId = -5 }, "workshopId"),
            (new { rule = "Allowed", notes = new string('n', 501) }, "notes"),
        })
        {
            using var response = await Admin.CallAsync(HttpMethod.Put, url, body);
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", key);
        }

        using var badId = await Admin.CallAsync(HttpMethod.Put, Base + "/entries/bad%20id", new { rule = "Allowed" });
        await badId.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "extId");
        using var noBody = await Admin.CallAsync(HttpMethod.Put, url, "null");
        await noBody.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "body");
        Assert.Null(Entry(await StateAsync(), "ws_70002")); // nothing was created
    }

    [Fact]
    public async Task DeleteEntryRemovesItOr404s()
    {
        await SendAsync(Admin, HttpMethod.Put, $"{Base}/entries/del_me", new { rule = "Blocked" }, HttpStatusCode.Created);
        await SendAsync(Admin, HttpMethod.Delete, $"{Base}/entries/del_me", null, HttpStatusCode.NoContent);
        Assert.Null(Entry(await StateAsync(), "del_me"));
        Assert.Equal("del_me", (await Api.WaitForAuditAsync(f.Server, "mods.entry.delete", "del_me")).Target);

        using var again = await Admin.CallAsync(HttpMethod.Delete, $"{Base}/entries/del_me");
        await again.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task SaveRequirementsAreNotImplementedYet()
    {
        using var response = await Admin.CallAsync(HttpMethod.Get, Base + "/save-requirements");
        await response.AssertProblemAsync(HttpStatusCode.NotImplemented, "NotImplemented");
    }

    // ------------------------------------------------------------------ visibility, reports, catalog

    [Fact]
    public async Task PlayerExtensionsAndTheCatalogFollowModListVisibility()
    {
        string name = f.NextName("Modder");
        var (node, playerId) = await JoinAsync(name, Ext("ws_80001", "2.0"), Ext("ws_80002", enabled: false, hint: ExtensionClass.ClientOnly));
        await using var _ = node;
        await SaveServer.WaitUntilAsync(() => f.Server.Service<IModStore>().LatestReport((int)playerId) is not null, 10_000, "report stored");
        string url = $"/api/v1/players/{N(playerId)}/extensions";
        try
        {
            // AdminsOnly (the default): a Viewer is refused, an Admin and a ModEditor see everything
            await SetVisibilityAsync("AdminsOnly");
            using (var hidden = await f.Viewer.CallAsync(HttpMethod.Get, url))
            {
                await hidden.AssertProblemAsync(HttpStatusCode.Forbidden, "ModListHidden");
            }

            using (var hiddenCatalog = await f.Viewer.CallAsync(HttpMethod.Get, Base + "/catalog"))
            {
                await hiddenCatalog.AssertProblemAsync(HttpStatusCode.Forbidden, "ModListHidden");
            }

            var hiddenState = await StateAsync(f.Viewer);
            Assert.True(hiddenState.GetProperty("playersHidden").GetBoolean());
            Assert.Equal(0, hiddenState.GetProperty("players").GetArrayLength());

            var seen = await SendAsync(Admin, HttpMethod.Get, url);
            Assert.Equal(name, seen.GetProperty("name").GetString());
            var latest = seen.GetProperty("latest");
            Assert.Equal("Admitted", latest.GetProperty("outcome").GetString());
            var items = latest.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(["ws_80001", "ws_80002"], items.Select(i => i.GetProperty("id").GetString()));
            Assert.Equal(("Workshop", "Sim", false), (items[0].GetProperty("source").GetString(), items[0].GetProperty("effectiveClass").GetString(), items[1].GetProperty("enabled").GetBoolean()));
            Assert.Equal("https://steamcommunity.com/sharedfiles/filedetails/?id=80001", items[0].GetProperty("workshopUrl").GetString());
            Assert.Equal(1, seen.GetProperty("history").GetArrayLength());
            await SendAsync(Editor, HttpMethod.Get, url);
            Assert.False((await StateAsync(Editor)).GetProperty("playersHidden").GetBoolean());
            JsonElement status = default;
            await SaveServer.WaitUntilAsync( // the roster snapshot follows the join within a tick
                () => (status = StateAsync(Editor).GetAwaiter().GetResult().GetProperty("players").EnumerateArray().Single(p => p.GetProperty("playerId").GetInt64() == playerId))
                    .GetProperty("online").GetBoolean(), 10_000, "online");
            Assert.Equal("Matches", status.GetProperty("status").GetString());

            // AdminsAndViewers and AllPlayers let a Viewer read; neither lets it edit
            foreach (string visibility in new[] { "AdminsAndViewers", "AllPlayers" })
            {
                await SetVisibilityAsync(visibility);
                var viewerSees = await SendAsync(f.Viewer, HttpMethod.Get, url);
                Assert.Equal(2, viewerSees.GetProperty("latest").GetProperty("items").GetArrayLength());
                var catalog = await SendAsync(f.Viewer, HttpMethod.Get, Base + "/catalog");
                Assert.Contains(catalog.EnumerateArray(), c => c.GetProperty("id").GetString() == "ws_80001");
                var state = await StateAsync(f.Viewer);
                Assert.False(state.GetProperty("playersHidden").GetBoolean());
                Assert.False(state.GetProperty("canEdit").GetBoolean());
                using var edit = await f.Viewer.CallAsync(HttpMethod.Put, $"{Base}/entries/ws_80001", new { rule = "Allowed" });
                await edit.AssertProblemAsync(HttpStatusCode.Forbidden, "Forbidden");
            }

            // history: the limit is validated, unknown players are 404
            using var badLimit = await Admin.CallAsync(HttpMethod.Get, url + "?limit=21");
            await badLimit.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "limit");
            using var unknown = await Admin.CallAsync(HttpMethod.Get, "/api/v1/players/99999/extensions");
            await unknown.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }
        finally
        {
            await SetVisibilityAsync("AdminsOnly");
        }
    }

    [Fact]
    public async Task PutCatalogStoresLinksAndNotesAndValidatesThem()
    {
        string id = "ws_90001";
        var put = await SendAsync(Admin, HttpMethod.Put, $"{Base}/catalog/{id}", new { name = "Cat Mod", nexusUrl = Nexus, notes = "from the catalog", classOverride = "ClientOnly" });
        Assert.Equal(("Cat Mod", Nexus, 90001L, "ClientOnly", "from the catalog", false),
            (put.GetProperty("name").GetString(), put.GetProperty("nexusUrl").GetString(), put.GetProperty("workshopId").GetInt64(), put.GetProperty("classOverride").GetString(),
             put.GetProperty("notes").GetString(), put.GetProperty("inPolicy").GetBoolean()));
        Assert.Equal(id, (await Api.WaitForAuditAsync(f.Server, "mods.catalog", id)).Target);

        // a new entry for that mod starts from the catalog
        var entry = await SendAsync(Editor, HttpMethod.Put, $"{Base}/entries/{id}", new { rule = "Required" }, HttpStatusCode.Created);
        Assert.Equal(("Cat Mod", Nexus, "from the catalog", "ClientOnly"), (entry.GetProperty("name").GetString(), entry.GetProperty("nexusUrl").GetString(), entry.GetProperty("notes").GetString(), entry.GetProperty("classOverride").GetString()));
        await SendAsync(Admin, HttpMethod.Delete, $"{Base}/entries/{id}", null, HttpStatusCode.NoContent);

        using var bad = await Admin.CallAsync(HttpMethod.Put, $"{Base}/catalog/{id}", new { nexusUrl = "https://example.com/mods/1" });
        await bad.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "nexusUrl");
        using var noBody = await Admin.CallAsync(HttpMethod.Put, $"{Base}/catalog/{id}", "null");
        await noBody.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "body");
    }

    // ------------------------------------------------------------------ import from the authority (own server: the authority is the first one that joins)

    [Fact]
    public async Task ImportFromTheAuthorityBuildsTheListThenMergesAndReplaces()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        using var admin = server.Http(server.AdminToken());

        using (var none = await admin.CallAsync(HttpMethod.Post, Base + "/import-from-authority", new { merge = true }))
        {
            await none.AssertProblemAsync(HttpStatusCode.Conflict, "NoAuthorityReport");
        }

        var authorityList = new[]
        {
            Ext("ego_dlc_split", "900"), Ext("ws_100", "1.4"), Ext("ws_2042901274", "195", hint: ExtensionClass.ClientOnly), Ext("ui_pack", "2", hint: ExtensionClass.ClientOnly),
            Ext("off_mod", enabled: false),
        };
        await using var authority = await server.ConnectAsync("Host", Role.Authority | Role.Client, extensions: authorityList);
        await server.WaitForAsync(s => s.Authority.Status == AuthorityStatus.Live, 10_000, "authority live");
        await SaveServer.WaitUntilAsync(() => server.Service<IModStore>().LatestReport(authority.Welcome.PlayerId) is not null, 10_000, "authority report stored");

        var imported = await SendAsync(admin, HttpMethod.Post, Base + "/import-from-authority", new { merge = true });
        var entries = imported.GetProperty("entries").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
        Assert.Equal(["ego_dlc_split", "ui_pack", "ws_100", "ws_2042901274"], entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("Required", "Exact", "1.4", 100L), (entries["ws_100"].GetProperty("rule").GetString(), entries["ws_100"].GetProperty("versionRule").GetString(),
            entries["ws_100"].GetProperty("version").GetString(), entries["ws_100"].GetProperty("workshopId").GetInt64()));
        Assert.Equal("https://steamcommunity.com/sharedfiles/filedetails/?id=100", entries["ws_100"].GetProperty("workshopUrl").GetString());
        Assert.Equal(("Allowed", "Any", true), (entries["ws_2042901274"].GetProperty("rule").GetString(), entries["ws_2042901274"].GetProperty("versionRule").GetString(), entries["ws_2042901274"].GetProperty("isLibrary").GetBoolean()));
        Assert.Equal("Required", entries["ego_dlc_split"].GetProperty("rule").GetString());
        Assert.Equal(authority.Welcome.PlayerId, imported.GetProperty("authorityPlayerId").GetInt64());
        var audit = await Api.WaitForAuditAsync(server, "mods.import");
        Assert.Contains("\"entriesAfter\":\"4\"", audit.DataJson);

        // the admin edits; a merge keeps the edit and the entry the authority does not have; a replace drops them
        await SendAsync(admin, HttpMethod.Put, $"{Base}/entries/ws_100", new { notes = "mine", rule = "Allowed" });
        await SendAsync(admin, HttpMethod.Put, $"{Base}/entries/manual", new { rule = "Blocked" }, HttpStatusCode.Created);
        var merged = (await SendAsync(admin, HttpMethod.Post, Base + "/import-from-authority", new { merge = true })).GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(("mine", "Allowed"), (merged.Single(e => e.GetProperty("id").GetString() == "ws_100").GetProperty("notes").GetString(), merged.Single(e => e.GetProperty("id").GetString() == "ws_100").GetProperty("rule").GetString()));
        Assert.Contains(merged, e => e.GetProperty("id").GetString() == "manual");

        var replaced = (await SendAsync(admin, HttpMethod.Post, Base + "/import-from-authority", new { merge = false })).GetProperty("entries").EnumerateArray().ToList();
        Assert.DoesNotContain(replaced, e => e.GetProperty("id").GetString() == "manual");
        Assert.Equal(("mine", "Required"), (replaced.Single(e => e.GetProperty("id").GetString() == "ws_100").GetProperty("notes").GetString(), replaced.Single(e => e.GetProperty("id").GetString() == "ws_100").GetProperty("rule").GetString()));
    }
}
