using X4MP.Core.Mods;
using X4MP.Proto;

namespace X4MP.Persistence.Tests;

/// <summary>Migration 0008_mods and <see cref="SqliteModStore"/> (task M1-X3): policy, reports, retention and catalog round trips.</summary>
public sealed class SqliteModStoreTests : TeamDbFixture
{
    private SqliteModStore NewStore() => new(Factory, Writer);

    private static ExtensionInfoT Ext(string id, string version = "1.0", bool enabled = true) => new()
    {
        Id = id, Name = "Name " + id, Version = version, Enabled = enabled, Source = ExtensionSource.Workshop, WorkshopId = 42, ContentHash = [0xAB, 0xCD],
        HashKind = HashKind.CatIndex, HasNativeDll = true, ClassHint = ExtensionClass.Sim, Warning = "w",
        Dependencies = [new ExtensionDependencyT { Id = "dep", Optional = true }],
    };

    private static ExtensionReportRecord Report(int player, string id, ModReportOutcome outcome = ModReportOutcome.Admitted, int minute = 0, ModPolicyViolationT? violation = null) =>
        new(player, null, T0.AddMinutes(minute), [1, 2, 3], [Ext(id)], outcome, violation, 7);

    [Fact]
    public void MigrationCreatesTheTablesAndKeepsTheVersionContiguous()
    {
        Assert.Equal(4, Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('player_extension_reports','session_mod_policy','session_mod_entries','mod_catalog')"));
        Assert.True(new MigrationRunner(Factory).LatestVersion >= 8);
        Assert.Equal(new MigrationRunner(Factory).LatestVersion, Scalar<long>("SELECT MAX(version) FROM schema_version"));
    }

    [Fact]
    public void ThePolicyRoundTripsAndNoPolicyLoadsAsNull()
    {
        var store = NewStore();
        Assert.Null(store.LoadPolicy());

        var entries = new[]
        {
            new ModPolicyEntryT
            {
                Id = "ws_1", Name = "One", Rule = ModRule.Required, Enabled = true, ModClass = ExtensionClass.Sim, VersionRule = VersionRule.Exact, Version = "1.4",
                ContentHash = [1, 2, 3], NexusUrl = "https://www.nexusmods.com/x4foundations/mods/1", WorkshopId = 1, Notes = "needs two",
            },
            new ModPolicyEntryT { Id = "a_lib", Name = "Lib", Rule = ModRule.Blocked, Enabled = false, ModClass = ExtensionClass.ClientOnly, VersionRule = VersionRule.Any, Version = "", NexusUrl = "", Notes = "" },
        };
        store.SavePolicy(new StoredModPolicy(5, ModSourceMode.AdminList, UnknownModDefault.Block, ModEnforcement.Warn, T0, "admin:root", entries));

        var loaded = store.LoadPolicy()!;
        Assert.Equal((5u, ModSourceMode.AdminList, UnknownModDefault.Block, ModEnforcement.Warn, T0, "admin:root"),
            (loaded.Version, loaded.SourceMode, loaded.UnknownDefault, loaded.Enforcement, loaded.UpdatedAt, loaded.UpdatedBy));
        Assert.Equal(["ws_1", "a_lib"], loaded.Entries.Select(e => e.Id)); // the order the admin chose, not alphabetical
        var first = loaded.Entries[0];
        Assert.Equal(("One", ModRule.Required, true, ExtensionClass.Sim, VersionRule.Exact, "1.4", 1ul, "needs two"),
            (first.Name, first.Rule, first.Enabled, first.ModClass, first.VersionRule, first.Version, first.WorkshopId, first.Notes));
        Assert.Equal<byte>([1, 2, 3], first.ContentHash!);
        Assert.Equal("https://www.nexusmods.com/x4foundations/mods/1", first.NexusUrl);
        Assert.Equal((ModRule.Blocked, false), (loaded.Entries[1].Rule, loaded.Entries[1].Enabled));

        // saving again replaces entries (no leftovers)
        store.SavePolicy(new StoredModPolicy(6, ModSourceMode.AuthorityDefines, UnknownModDefault.AllowAll, ModEnforcement.Strict, T0.AddHours(1), "admin:two", [entries[1]]));
        var again = store.LoadPolicy()!;
        Assert.Equal(6u, again.Version);
        Assert.Equal(["a_lib"], again.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task ReportsRoundTripWithTheFullListAndSurviveARestart()
    {
        var violation = new ModPolicyViolationT
        {
            PolicyVersion = 7,
            Install = [new ModRefT { Id = "ws_9", Name = "Nine", Version = "2", HaveVersion = "", NexusUrl = "u", WorkshopId = 9, Notes = "n" }],
            Enable = [], Disable = [], Update = [],
        };
        var store = NewStore();
        long recorded = 0;
        store.ReportRecorded += _ => recorded++;
        store.RecordReport(Report(Players[0], "ws_1", ModReportOutcome.Admitted, 0));
        store.RecordReport(Report(Players[0], "ws_2", ModReportOutcome.Rejected, 1, violation));
        Assert.Equal(2, recorded);
        Assert.Equal(ModReportOutcome.Rejected, store.LatestReport(Players[0])!.Outcome); // visible before the write-behind flush
        await Writer.FlushAsync();

        var fresh = NewStore(); // a restart: nothing in memory
        var reports = fresh.Reports(Players[0], 10);
        Assert.Equal(["ws_2", "ws_1"], reports.Select(r => r.Items.Single().Id));
        var latest = reports[0];
        Assert.Equal((ModReportOutcome.Rejected, 7u, T0.AddMinutes(1)), (latest.Outcome, latest.PolicyVersion, latest.At));
        Assert.Equal<byte>([1, 2, 3], latest.ExtensionsHash);
        var item = latest.Items.Single();
        Assert.Equal(("Name ws_2", "1.0", true, ExtensionSource.Workshop, 42ul, HashKind.CatIndex, true, ExtensionClass.Sim, "w"),
            (item.Name, item.Version, item.Enabled, item.Source, item.WorkshopId, item.HashKind, item.HasNativeDll, item.ClassHint, item.Warning));
        Assert.Equal<byte>([0xAB, 0xCD], item.ContentHash);
        Assert.Equal(("dep", true), (item.Dependencies.Single().Id, item.Dependencies.Single().Optional));
        var v = latest.Violation!;
        Assert.Equal(7u, v.PolicyVersion);
        Assert.Equal(("ws_9", "Nine", "2", "", "u", 9ul, "n"), (v.Install[0].Id, v.Install[0].Name, v.Install[0].Version, v.Install[0].HaveVersion, v.Install[0].NexusUrl, v.Install[0].WorkshopId, v.Install[0].Notes));
        Assert.Null(reports[1].Violation);
        Assert.Equal([Players[0]], fresh.LatestReports().Select(r => r.PlayerId));
    }

    [Fact]
    public async Task RefusedReportsOfAnUnboundKeyAreFiledByKeyAndMoveToThePlayerWhenItIsAdmitted()
    {
        byte[] key = [9, 9, 9];
        var store = NewStore();
        store.RecordReport(new ExtensionReportRecord(0, null, T0, [], [Ext("ws_1")], ModReportOutcome.Rejected, null, 3, key, "Zed"));
        store.RecordReport(new ExtensionReportRecord(0, null, T0.AddMinutes(1), [], [Ext("ws_2")], ModReportOutcome.Rejected, null, 3, key, "Zed"));
        store.RecordReport(new ExtensionReportRecord(0, null, T0, [], [Ext("ws_3")], ModReportOutcome.Rejected, null, 3, [7], "Other"));
        await Writer.FlushAsync();
        Assert.Equal(0, Scalar<long>("SELECT COUNT(*) FROM mod_catalog")); // an unauthenticated refusal teaches the catalog nothing
        Assert.Empty(store.LatestReports());

        var fresh = NewStore(); // restart
        var unbound = fresh.UnboundReports();
        Assert.Equal(["Zed", "Other"], unbound.Select(r => r.AttemptedName).Order().Reverse()); // newest per key
        Assert.Equal("ws_2", unbound.Single(r => r.AttemptedName == "Zed").Items.Single().Id);

        fresh.AttachKey(key, Players[1]);
        await Writer.FlushAsync();
        Assert.Equal(["Other"], fresh.UnboundReports().Select(r => r.AttemptedName));
        Assert.Equal(["ws_2", "ws_1"], fresh.Reports(Players[1], 10).Select(r => r.Items.Single().Id));
        var again = NewStore();
        Assert.Equal(["ws_2", "ws_1"], again.Reports(Players[1], 10).Select(r => r.Items.Single().Id));
        Assert.Single(again.UnboundReports());
    }

    [Fact]
    public async Task TheJanitorKeepsTwentyReportsPerPlayer()
    {
        var store = NewStore();
        for (int i = 0; i < 25; i++)
        {
            store.RecordReport(Report(Players[0], "ws_" + i, minute: i));
        }

        store.RecordReport(Report(Players[1], "other"));
        await Writer.FlushAsync();

        Assert.Equal(20, Scalar<long>("SELECT COUNT(*) FROM player_extension_reports WHERE player_id = @p", new { p = Players[0] }));
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM player_extension_reports WHERE player_id = @p", new { p = Players[1] }));
        var fresh = NewStore().Reports(Players[0], 100);
        Assert.Equal(20, fresh.Count);
        Assert.Equal("ws_24", fresh[0].Items.Single().Id);
        Assert.Equal("ws_5", fresh[^1].Items.Single().Id); // 0..4 are gone
        Assert.Equal(20, store.Reports(Players[0], 100).Count);
    }

    [Fact]
    public async Task ReportsFeedTheCatalogWithoutOverwritingAnAdminsEdit()
    {
        var store = NewStore();
        store.UpsertCatalog(new ModCatalogRecord("ws_1", "Admin Name", "https://www.nexusmods.com/x4foundations/mods/5", 1, ExtensionClass.ClientOnly, "note", T0));
        store.RecordReport(Report(Players[0], "ws_1"));
        store.RecordReport(Report(Players[0], "ws_2"));
        store.RecordReport(new ExtensionReportRecord(Players[0], null, T0, [], [new ExtensionInfoT { Id = "ego_dlc_split", Name = "Split", Source = ExtensionSource.Dlc }], ModReportOutcome.Admitted, null, 1));
        await Writer.FlushAsync();

        var catalog = store.Catalog();
        Assert.Equal(["ws_1", "ws_2"], catalog.Select(c => c.ExtId)); // no DLC rows
        var one = store.CatalogEntry("ws_1")!;
        Assert.Equal(("Admin Name", "https://www.nexusmods.com/x4foundations/mods/5", 1ul, ExtensionClass.ClientOnly, "note"), (one.Name, one.NexusUrl, one.WorkshopId, one.ClassOverride, one.Notes));
        var two = store.CatalogEntry("ws_2")!;
        Assert.Equal(("Name ws_2", 42ul, null, ExtensionClass.Unknown), (two.Name, two.WorkshopId, two.NexusUrl, two.ClassOverride));
        Assert.Null(store.CatalogEntry("nope"));
    }

    [Fact]
    public void ThePersistentProviderBumpsTheVersionStoresEditsAndReloads()
    {
        var store = NewStore();
        var provider = new PersistentModPolicyProvider(store, () => new X4MP.Core.Settings.ModManagementOptions());
        var seen = new List<uint>();
        provider.Changed += p => seen.Add(p.Version);
        Assert.Equal(1u, provider.Current.Version);
        Assert.Null(store.LoadPolicy()); // nothing is stored before the first edit

        var edited = provider.Update("admin:root", p => p.Entries!.Add(new ModPolicyEntryT { Id = "ws_1", Name = "One", Rule = ModRule.Required, Enabled = true, ContentHash = [] }));
        Assert.Equal(2u, edited.Version);
        Assert.Equal(edited.Version, provider.Update("admin:root", _ => { }).Version); // a no-op edit changes nothing
        Assert.Equal([2u], seen);

        var restarted = new PersistentModPolicyProvider(NewStore(), () => new X4MP.Core.Settings.ModManagementOptions());
        Assert.Equal(2u, restarted.Current.Version);
        Assert.Equal(["ws_1"], restarted.Current.Entries!.Select(e => e.Id));
        Assert.Equal("admin:root", NewStore().LoadPolicy()!.UpdatedBy);
    }
}
