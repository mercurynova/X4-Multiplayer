using X4MP.Core.Mods;
using X4MP.Core.Net;
using X4MP.Core.Settings;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;

namespace X4MP.Server.Tests.Net;

/// <summary>
/// The mod policy at the gateway (M1-X2): a real <see cref="TcpNodeClient"/> (what FakeNode uses) with an <c>extension_list</c>
/// against the real gateway over both transports. Rejections carry the exact install/enable/disable/update lists.
/// </summary>
[Collection("net")]
public class ModPolicyGatewayTests
{
    public static TheoryData<string> Kinds => ["inproc", "tcp"];

    private const string Nexus = "https://www.nexusmods.com/x4foundations/mods/1234";

    private static ExtensionInfoT Ext(string id, string version = "1.0", bool enabled = true, ExtensionClass hint = ExtensionClass.Unknown, string? name = null) => new()
    {
        Id = id,
        Name = name ?? id,
        Version = version,
        Enabled = enabled,
        ClassHint = hint,
        Source = id.StartsWith("ego_dlc_", StringComparison.Ordinal) ? ExtensionSource.Dlc : ExtensionSource.Workshop,
        WorkshopId = ModLinks.WorkshopIdOf(id),
        ContentHash = [],
        Dependencies = [],
    };

    private static ModPolicyEntryT Entry(string id, ModRule rule = ModRule.Required, string version = "", string nexus = "", string notes = "", string? name = null) => new()
    {
        Id = id, Name = name ?? id, Rule = rule, Enabled = true, Version = version, VersionRule = VersionRule.Exact, NexusUrl = nexus, Notes = notes, ContentHash = [],
    };

    private static NetOptions Fast() => new() { AuthFailureDelayMs = 0, HandshakeTimeoutSeconds = 2 };

    private static async Task<NetHarness> StartAsync(string kind, InMemoryModPolicyProvider provider) =>
        await NetHarness.CreateAsync(kind, Fast(), withGateway: true, handler: s => new AuthorityRecordingHandler(s), modPolicy: provider);

    private static InMemoryModPolicyProvider Provider(
        ModSourceMode mode = ModSourceMode.AdminList, UnknownModDefault unknown = UnknownModDefault.AllowClientOnly,
        ModEnforcement enforcement = ModEnforcement.Strict, params ModPolicyEntryT[] entries)
    {
        var provider = new InMemoryModPolicyProvider(() => new ModManagementOptions { SourceMode = mode, UnknownDefault = unknown, Enforcement = enforcement });
        provider.SetEntries(entries);
        return provider;
    }

    private static async Task<TcpNodeClient> JoinAsync(
        NetHarness net, string name, ExtensionInfoT[]? extensions, Role roles = Role.Client)
    {
        var handle = await net.ConnectAsync();
        try
        {
            // A fixed key per name: a refused player is identified (the rejection is stored under it), so a retry must be the same player.
            var key = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name));
            return await TcpNodeClient.ConnectAsync(handle.Stream, new NodeClientOptions { PlayerName = name, PlayerKey = key, RequestedRoles = roles, ExtensionList = extensions });
        }
        catch
        {
            await handle.DisposeAsync();
            throw;
        }
    }

    private static async Task<HandshakeRejectedException> RejectedAsync(NetHarness net, string name, ExtensionInfoT[]? extensions, Role roles = Role.Client) =>
        await Assert.ThrowsAsync<HandshakeRejectedException>(async () => await (await JoinAsync(net, name, extensions, roles)).DisposeAsync());

    private static async Task WaitForAuthorityAsync(NetHarness net)
    {
        var until = Environment.TickCount64 + 5000;
        while (net.State!.Authority is null)
        {
            Assert.True(Environment.TickCount64 < until, "authority not recorded");
            await Task.Delay(10);
        }
    }

    // ------------------------------------------------------------------ AdminList

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task MissingRequiredModIsRefusedWithTheExactInstallListAndLinks(string kind)
    {
        var provider = Provider(entries:
        [
            Entry("ws_1234567890", version: "1.4", nexus: Nexus, notes: "needs SirNukes too", name: "Warehouse Fleets"),
            Entry("ws_2042901274", ModRule.Allowed, name: "SirNukes Mod Support APIs"),
        ]);
        await using var net = await StartAsync(kind, provider);

        var ex = await RejectedAsync(net, "Alice", [Ext("ego_dlc_split", "900"), Ext("ws_2042901274", "195", hint: ExtensionClass.ClientOnly)]);

        Assert.Equal(DisconnectCode.ExtensionsMismatch, ex.Code);
        var v = ex.ModViolation!;
        Assert.Equal(provider.Current.Version, v.PolicyVersion);
        var install = Assert.Single(v.Install);
        Assert.Equal("ws_1234567890", install.Id);
        Assert.Equal("Warehouse Fleets", install.Name);
        Assert.Equal("1.4", install.Version);
        Assert.Equal(string.Empty, install.HaveVersion);
        Assert.Equal(Nexus, install.NexusUrl);
        Assert.Equal(1234567890ul, install.WorkshopId);
        Assert.Equal("needs SirNukes too", install.Notes);
        Assert.Equal("https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890", ModLinks.WorkshopUrl(install.WorkshopId));
        Assert.Empty(v.Enable);
        Assert.Empty(v.Disable);
        Assert.Empty(v.Update);
        Assert.Contains("install: Warehouse Fleets", ex.ServerMessage);
        Assert.Empty(net.Gateway!.AdmittedNodes);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AllFourListsComeBackTogether(string kind)
    {
        var provider = Provider(entries:
        [
            Entry("a_missing", version: "1"),
            Entry("b_off", version: "1"),
            Entry("c_old", version: "2"),
            Entry("d_blocked", ModRule.Blocked),
        ]);
        await using var net = await StartAsync(kind, provider);

        var ex = await RejectedAsync(net, "Alice", [Ext("b_off", "1", enabled: false), Ext("c_old", "1"), Ext("d_blocked"), Ext("zz_extra", hint: ExtensionClass.Sim)]);

        var v = ex.ModViolation!;
        Assert.Equal(["a_missing"], v.Install.Select(r => r.Id));
        Assert.Equal(["b_off"], v.Enable.Select(r => r.Id));
        Assert.Equal(["d_blocked", "zz_extra"], v.Disable.Select(r => r.Id));
        var update = Assert.Single(v.Update);
        Assert.Equal(("c_old", "2", "1"), (update.Id, update.Version, update.HaveVersion));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task MatchingModsJoinAndWelcomeCarriesThePolicy(string kind)
    {
        var provider = Provider(entries: [Entry("ws_1234567890", version: "1.4", nexus: Nexus)]);
        await using var net = await StartAsync(kind, provider);

        await using var client = await JoinAsync(net, "Alice", [Ext("ws_1234567890", "1.4"), Ext("ui_only", hint: ExtensionClass.ClientOnly)]);

        Assert.Equal(provider.Current.Version, client.ServerHello.ModPolicyVersion);
        var policy = client.Welcome.Settings.ModPolicy;
        Assert.Equal(provider.Current.Version, policy.Version);
        Assert.Equal(ModSourceMode.AdminList, policy.SourceMode);
        var entry = Assert.Single(policy.Entries);
        Assert.Equal(("ws_1234567890", Nexus), (entry.Id, entry.NexusUrl));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AdminListChecksTheAuthorityToo(string kind)
    {
        var provider = Provider(entries: [Entry("ws_1", version: "1.0")]);
        await using var net = await StartAsync(kind, provider);

        var ex = await RejectedAsync(net, "Host", [Ext("ego_dlc_split", "900")], Role.Authority | Role.Client);
        Assert.Equal(DisconnectCode.ExtensionsMismatch, ex.Code);
        Assert.Equal(["ws_1"], ex.ModViolation!.Install.Select(r => r.Id));
        Assert.Null(net.State!.Authority);

        await using var host = await JoinAsync(net, "Host", [Ext("ws_1")], Role.Authority | Role.Client);
        await WaitForAuthorityAsync(net);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task DlcMismatchRejectsEvenUnderWarn(string kind)
    {
        var provider = Provider(enforcement: ModEnforcement.Warn, entries: [Entry("ego_dlc_split", version: "900")]);
        await using var net = await StartAsync(kind, provider);

        var ex = await RejectedAsync(net, "Alice", [Ext("ego_dlc_split", "800")]);
        Assert.Equal(DisconnectCode.ExtensionsMismatch, ex.Code);
        Assert.Equal(["ego_dlc_split"], ex.ModViolation!.Update.Select(r => r.Id));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task WarnAdmitsASimMismatchAndFlagsTheNode(string kind)
    {
        var provider = Provider(enforcement: ModEnforcement.Warn, entries: [Entry("ws_1", version: "1.0", name: "One")]);
        await using var net = await StartAsync(kind, provider);

        await using var client = await JoinAsync(net, "Alice", []);

        var node = Assert.Single(net.Gateway!.AdmittedNodes);
        Assert.Equal(["ws_1"], node.ModWarning!.Install.Select(r => r.Id));
        Assert.Equal("install: One", ModPolicyEvaluator.Describe(node.ModWarning));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AllowlistedLibraryDifferencesNeverReject(string kind)
    {
        var provider = Provider(unknown: UnknownModDefault.Block);
        await using var net = await StartAsync(kind, provider);

        await using var host = await JoinAsync(net, "Host", [Ext("ego_dlc_split", "900"), Ext("ws_2042901274", "195")], Role.Authority | Role.Client);
        await WaitForAuthorityAsync(net);

        // other library versions, an extra allowlisted library, nothing at all of the host's libraries
        await using var a = await JoinAsync(net, "Alice", [Ext("ego_dlc_split", "900"), Ext("ws_2042901274", "999"), Ext("kuerteeUIExtensionsAndHUD", "1")]);
        await using var b = await JoinAsync(net, "Bob", [Ext("ego_dlc_split", "900")]);
        Assert.Equal(3, net.Gateway!.AdmittedNodes.Count);
    }

    // ------------------------------------------------------------------ AuthorityDefines (the default)

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AuthorityDefinesRequiresTheAuthoritysSimMods(string kind)
    {
        var provider = Provider(ModSourceMode.AuthorityDefines);
        await using var net = await StartAsync(kind, provider);
        ExtensionInfoT[] hostMods = [Ext("ego_dlc_split", "900"), Ext("ws_7", "3", name: "Seven"), Ext("ui", hint: ExtensionClass.ClientOnly)];
        await using var host = await JoinAsync(net, "Host", hostMods, Role.Authority | Role.Client);
        await WaitForAuthorityAsync(net);

        // identical sim set (fast path: equal hash), a different client-only mod is fine
        await using var ok = await JoinAsync(net, "Alice", [Ext("ego_dlc_split", "900"), Ext("ws_7", "3"), Ext("other_ui", hint: ExtensionClass.ClientOnly)]);

        var missing = await RejectedAsync(net, "Bob", [Ext("ego_dlc_split", "900")]);
        Assert.Equal(["ws_7"], missing.ModViolation!.Install.Select(r => r.Id));
        Assert.Equal("Seven", missing.ModViolation.Install[0].Name);
        Assert.Equal(7ul, missing.ModViolation.Install[0].WorkshopId);
        Assert.Equal("3", missing.ModViolation.Install[0].Version);

        var old = await RejectedAsync(net, "Carol", [Ext("ego_dlc_split", "900"), Ext("ws_7", "2")]);
        Assert.Equal(["ws_7"], old.ModViolation!.Update.Select(r => r.Id));

        var extra = await RejectedAsync(net, "Dave", [Ext("ego_dlc_split", "900"), Ext("ws_7", "3"), Ext("sneaky", hint: ExtensionClass.Sim)]);
        Assert.Equal(["sneaky"], extra.ModViolation!.Disable.Select(r => r.Id));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task LegacyNodesWithOnlyTheStringListStillWork(string kind)
    {
        var provider = Provider(ModSourceMode.AuthorityDefines);
        await using var net = await StartAsync(kind, provider);
        await using var hostClient = await net.ConnectAsync();
        var host = new TestNode("Host") { Roles = Role.Authority | Role.Client, Extensions = ["ego_dlc_boron@1.0", "ws_9@2"] };
        TestNode.AsWelcome((await host.JoinAsync(hostClient)).Reply);
        await WaitForAuthorityAsync(net);

        await using var c = await net.ConnectAsync();
        var (_, reply) = await new TestNode("Alice") { Extensions = ["ego_dlc_boron@1.0"] }.JoinAsync(c);
        var d = TestNode.AsDisconnect(reply);
        Assert.Equal(DisconnectCode.ExtensionsMismatch, d.Code);
        Assert.Equal("ws_9", d.ModViolation!.Value.Install(0)!.Value.Id);
        Assert.Contains("missing: [ws_9@2]", d.Expected);
    }
}
