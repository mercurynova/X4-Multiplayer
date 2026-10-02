using X4MP.Core.Mods;
using X4MP.Core.Net;
using X4MP.Core.Settings;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;

namespace X4MP.Server.Tests.Net;

/// <summary>Every ClientHello's extension report is stored with the verdict (task M1-X3): admitted, warned and rejected.</summary>
[Collection("net")]
public class ModReportStorageTests
{
    public static TheoryData<string> Kinds => ["inproc", "tcp"];

    private static ExtensionInfoT Ext(string id, string version = "1.0", bool enabled = true, ExtensionClass hint = ExtensionClass.Sim) => new()
    {
        Id = id, Name = "Name of " + id, Version = version, Enabled = enabled, ClassHint = hint, Source = ExtensionSource.Workshop,
        WorkshopId = ModLinks.WorkshopIdOf(id), ContentHash = [1, 2, 3], HashKind = HashKind.CatIndex, Dependencies = [],
    };

    private static ModPolicyEntryT Entry(string id, ModRule rule = ModRule.Required, string version = "1.0") => new()
    {
        Id = id, Name = id, Rule = rule, Enabled = true, Version = version, VersionRule = VersionRule.Exact, ContentHash = [],
    };

    private static async Task<NetHarness> StartAsync(string kind, InMemoryModStore store, ModEnforcement enforcement, params ModPolicyEntryT[] entries)
    {
        var provider = new InMemoryModPolicyProvider(() => new ModManagementOptions { SourceMode = ModSourceMode.AdminList, Enforcement = enforcement });
        provider.SetEntries(entries);
        return await NetHarness.CreateAsync(
            kind, new NetOptions { AuthFailureDelayMs = 0, HandshakeTimeoutSeconds = 2, HandshakesPerSecond = 1000 }, withGateway: true, modPolicy: provider, modStore: store);
    }

    private static Task<TcpNodeClient> JoinAsync(NetHarness net, string name, params ExtensionInfoT[] extensions) => JoinWithKeyAsync(net, name, "", extensions);

    /// <summary>A different <paramref name="keySalt"/> is a different player key for the same name.</summary>
    private static async Task<TcpNodeClient> JoinWithKeyAsync(NetHarness net, string name, string keySalt, ExtensionInfoT[] extensions)
    {
        var handle = await net.ConnectAsync();
        try
        {
            var key = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name + keySalt)); // the same name and salt always have the same key
            return await TcpNodeClient.ConnectAsync(
                handle.Stream, new NodeClientOptions { PlayerName = name, PlayerKey = key, RequestedRoles = Role.Client, ExtensionList = extensions });
        }
        catch
        {
            await handle.DisposeAsync();
            throw;
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AnAdmittedJoinStoresTheFullReport(string kind)
    {
        var store = new InMemoryModStore();
        await using var net = await StartAsync(kind, store, ModEnforcement.Strict, Entry("ws_1"));

        await using var client = await JoinAsync(net, "Alice", Ext("ws_1"), Ext("ws_2", enabled: false, hint: ExtensionClass.ClientOnly));

        var report = store.LatestReport(client.Welcome.PlayerId)!;
        Assert.Equal(ModReportOutcome.Admitted, report.Outcome);
        Assert.Null(report.Violation);
        Assert.Equal(["ws_1", "ws_2"], report.Items.Select(i => i.Id));
        Assert.False(report.Items[1].Enabled);
        Assert.Equal(net.Gateway!.AdmittedNodes.Single().Hello.ExtensionsHash?.ToArray() ?? [], report.ExtensionsHash);
        Assert.Equal(2u, report.PolicyVersion); // 1 + the SetEntries of the test setup
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AWarnedJoinStoresTheViolation(string kind)
    {
        var store = new InMemoryModStore();
        await using var net = await StartAsync(kind, store, ModEnforcement.Warn, Entry("ws_1"));

        await using var client = await JoinAsync(net, "Alice");

        var report = store.LatestReport(client.Welcome.PlayerId)!;
        Assert.Equal(ModReportOutcome.Warned, report.Outcome);
        Assert.Equal(["ws_1"], report.Violation!.Install.Select(r => r.Id));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ARejectedJoinOfAnUnknownKeyIsFiledByKey(string kind)
    {
        var store = new InMemoryModStore();
        await using var net = await StartAsync(kind, store, ModEnforcement.Strict, Entry("ws_1"), Entry("ws_9", ModRule.Blocked));

        var ex = await Assert.ThrowsAsync<HandshakeRejectedException>(async () => await (await JoinAsync(net, "Bob", Ext("ws_9"))).DisposeAsync());

        Assert.Equal(DisconnectCode.ExtensionsMismatch, ex.Code);
        Assert.Empty(store.LatestReports());
        var report = Assert.Single(store.UnboundReports());
        Assert.Equal(0, report.PlayerId); // the key is bound to no player: a refusal never claims the name
        Assert.Equal("Bob", report.AttemptedName);
        Assert.Equal(32, report.KeyHash!.Length);
        Assert.Equal(ModReportOutcome.Rejected, report.Outcome);
        Assert.Equal(["ws_1"], report.Violation!.Install.Select(r => r.Id));
        Assert.Equal(["ws_9"], report.Violation!.Disable.Select(r => r.Id));
        Assert.Equal(["ws_9"], report.Items.Select(i => i.Id));
        Assert.Empty(net.Gateway!.AdmittedNodes);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task EachJoinAddsAReportAndTheStoreKeepsTwenty(string kind)
    {
        var store = new InMemoryModStore();
        await using var net = await StartAsync(kind, store, ModEnforcement.Strict);

        int player = 0;
        for (int i = 0; i < IModStore.ReportsPerPlayer + 3; i++)
        {
            await using var client = await JoinAsync(net, "Alice", Ext("ws_" + i, hint: ExtensionClass.ClientOnly));
            player = client.Welcome.PlayerId;
        }

        var reports = store.Reports(player, 100);
        Assert.Equal(IModStore.ReportsPerPlayer, reports.Count);
        Assert.Equal("ws_" + (IModStore.ReportsPerPlayer + 2), reports[0].Items.Single().Id); // newest first
        // the catalog learned the names and Workshop ids
        Assert.Equal("Name of ws_5", store.CatalogEntry("ws_5")!.Name);
        Assert.Equal(5ul, store.CatalogEntry("ws_5")!.WorkshopId);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ARejectedJoinDoesNotReserveTheName(string kind)
    {
        var store = new InMemoryModStore();
        await using var net = await StartAsync(kind, store, ModEnforcement.Strict, Entry("ws_1"));

        await Assert.ThrowsAsync<HandshakeRejectedException>(async () => await (await JoinWithKeyAsync(net, "Dana", "squatter", [])).DisposeAsync());
        Assert.Single(store.UnboundReports());
        Assert.Null(await net.Store!.FindByKeyAsync(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("Dana" + "squatter")), default));

        // another key can take the free name, and is judged on its own
        await using var real = await JoinWithKeyAsync(net, "Dana", "the-real-dana", [Ext("ws_1")]);
        Assert.Equal(ModReportOutcome.Admitted, store.LatestReport(real.Welcome.PlayerId)!.Outcome);
        Assert.Single(store.UnboundReports()); // the squatter's attempt stays filed by its own key
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AKnownPlayersRejectedAttemptAppearsInTheirHistoryAndEarlierAttemptsAttachWhenTheKeyIsAdmitted(string kind)
    {
        var store = new InMemoryModStore();
        var provider = new InMemoryModPolicyProvider(() => new ModManagementOptions { SourceMode = ModSourceMode.AdminList, Enforcement = ModEnforcement.Strict });
        provider.SetEntries([Entry("ws_1")]);
        await using var net = await NetHarness.CreateAsync(
            kind, new NetOptions { AuthFailureDelayMs = 0, HandshakeTimeoutSeconds = 2, HandshakesPerSecond = 1000 }, withGateway: true, modPolicy: provider, modStore: store);

        // a key that is refused first (unknown, filed by key) ...
        await Assert.ThrowsAsync<HandshakeRejectedException>(async () => await (await JoinAsync(net, "Eve")).DisposeAsync());
        Assert.Single(store.UnboundReports());

        // ... and is admitted later: the earlier attempt joins the player's history
        int player;
        await using (var fixedMods = await JoinAsync(net, "Eve", Ext("ws_1")))
        {
            player = fixedMods.Welcome.PlayerId;
        }

        Assert.Empty(store.UnboundReports());
        Assert.Equal([ModReportOutcome.Admitted, ModReportOutcome.Rejected], store.Reports(player, 10).Select(r => r.Outcome));

        // the policy tightens: the now-known player is refused and the attempt is stored under them
        provider.SetEntries([Entry("ws_1"), Entry("ws_2")]);
        await Assert.ThrowsAsync<HandshakeRejectedException>(async () => await (await JoinAsync(net, "Eve", Ext("ws_1"))).DisposeAsync());

        var history = store.Reports(player, 10);
        Assert.Equal([ModReportOutcome.Rejected, ModReportOutcome.Admitted, ModReportOutcome.Rejected], history.Select(r => r.Outcome));
        Assert.Equal(["ws_2"], history[0].Violation!.Install.Select(r => r.Id));
        Assert.Empty(store.UnboundReports());
    }
}
