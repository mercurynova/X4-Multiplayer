using X4MP.Core.Mods;
using X4MP.Core.Settings;
using X4MP.Proto;

namespace X4MP.Core.Tests.Mods;

/// <summary>"Import from authority" and the persistent policy provider (tasks M1-X3/X4).</summary>
public class ModPolicyEditingTests
{
    private static ExtensionInfoT Ext(
        string id, string version = "1.0", bool enabled = true, ExtensionClass hint = ExtensionClass.Sim, string? name = null, ulong workshop = 0, byte[]? hash = null) => new()
    {
        Id = id, Name = name ?? "Name " + id, Version = version, Enabled = enabled, ClassHint = hint, WorkshopId = workshop,
        Source = id.StartsWith("ego_dlc_", StringComparison.Ordinal) ? ExtensionSource.Dlc : ExtensionSource.Workshop,
        ContentHash = hash is null ? [] : [.. hash], HashKind = hash is null ? HashKind.None : HashKind.CatIndex, Dependencies = [],
    };

    [Fact]
    public void ImportBuildsEntriesFromTheAuthorityList()
    {
        var authority = new[]
        {
            Ext("ego_dlc_split", "900"),
            Ext("ws_1234567890", "1.4", hash: [9, 9]),
            Ext("ws_2042901274", "195", hint: ExtensionClass.ClientOnly),              // allowlisted library
            Ext("ui_pack", "2", hint: ExtensionClass.ClientOnly),
            Ext("off_mod", enabled: false),
            Ext("x4native", "9.0"),
            Ext("custom", "3"),                                                         // Unknown hint counts as Sim
        };

        var entries = ModPolicyImport.FromAuthority(authority, [], merge: false);

        Assert.Equal(["custom", "ego_dlc_split", "ui_pack", "ws_1234567890", "ws_2042901274"], entries.Select(e => e.Id).Order(StringComparer.Ordinal));
        var byId = entries.ToDictionary(e => e.Id);
        Assert.False(byId.ContainsKey("off_mod"));
        Assert.False(byId.ContainsKey("x4native"));

        var warehouse = byId["ws_1234567890"];
        Assert.Equal((ModRule.Required, true, VersionRule.Exact, "1.4", 1234567890ul, ExtensionClass.Unknown), (warehouse.Rule, warehouse.Enabled, warehouse.VersionRule, warehouse.Version, warehouse.WorkshopId, warehouse.ModClass));
        Assert.Equal<byte>([9, 9], warehouse.ContentHash!);
        Assert.Equal((ModRule.Required, VersionRule.Exact, "900"), (byId["ego_dlc_split"].Rule, byId["ego_dlc_split"].VersionRule, byId["ego_dlc_split"].Version));
        Assert.Equal(ModRule.Required, byId["custom"].Rule);
        foreach (var id in new[] { "ws_2042901274", "ui_pack" })
        {
            Assert.Equal((ModRule.Allowed, VersionRule.Any), (byId[id].Rule, byId[id].VersionRule));
        }
    }

    [Fact]
    public void ImportAppliesTheCatalogsLinksAndNotesAndAReplaceDropsOldEntries()
    {
        var known = new ModCatalogRecord("ws_1", "Cat", "https://www.nexusmods.com/x4foundations/mods/77", 1, ExtensionClass.ClientOnly, "from the catalog", DateTimeOffset.UnixEpoch);
        var existing = new[] { new ModPolicyEntryT { Id = "gone", Name = "Gone", Rule = ModRule.Blocked, Enabled = true, ContentHash = [] } };

        var entries = ModPolicyImport.FromAuthority([Ext("ws_1", "2", name: "")], existing, merge: false, id => id == "ws_1" ? known : null);

        var entry = Assert.Single(entries);
        Assert.Equal(("ws_1", "Cat", "https://www.nexusmods.com/x4foundations/mods/77", "from the catalog", ExtensionClass.ClientOnly), (entry.Id, entry.Name, entry.NexusUrl, entry.Notes, entry.ModClass));
    }

    [Fact]
    public void MergeKeepsTheAdminsChoicesRefreshesVersionAndHashAndKeepsUnlistedEntries()
    {
        var existing = new[]
        {
            new ModPolicyEntryT
            {
                Id = "ws_1", Name = "Mine", Rule = ModRule.Allowed, Enabled = false, ModClass = ExtensionClass.ClientOnly, VersionRule = VersionRule.AtLeast, Version = "1.0",
                ContentHash = [1], NexusUrl = "https://www.nexusmods.com/x4foundations/mods/5", WorkshopId = 0, Notes = "mine",
            },
            new ModPolicyEntryT { Id = "manual", Name = "Manual", Rule = ModRule.Blocked, Enabled = true, ContentHash = [] },
        };

        var entries = ModPolicyImport.FromAuthority([Ext("ws_1", "3.0", hash: [7, 7]), Ext("ws_2", "1")], existing, merge: true);

        Assert.Equal(["ws_1", "manual", "ws_2"], entries.Select(e => e.Id));
        var merged = entries[0];
        Assert.Equal(("Mine", ModRule.Allowed, false, ExtensionClass.ClientOnly, VersionRule.AtLeast, "mine", "https://www.nexusmods.com/x4foundations/mods/5"),
            (merged.Name, merged.Rule, merged.Enabled, merged.ModClass, merged.VersionRule, merged.Notes, merged.NexusUrl));
        Assert.Equal("3.0", merged.Version);
        Assert.Equal<byte>([7, 7], merged.ContentHash!);
        Assert.Equal(1ul, merged.WorkshopId); // derived from ws_1 where it was missing
        Assert.Equal(ModRule.Blocked, entries[1].Rule);
        Assert.Equal(ModRule.Required, entries[2].Rule);
        Assert.Equal("Allowed", existing[0].Rule.ToString()); // the input list is not touched
        Assert.Equal("1.0", existing[0].Version);
    }

    [Fact]
    public void ImportedEntriesMakeTheAuthoritysOwnListAdmissible()
    {
        var authority = new[] { Ext("ego_dlc_split", "900"), Ext("ws_1", "1.4"), Ext("ui_pack", "2", hint: ExtensionClass.ClientOnly) };
        var policy = new ModPolicyT { SourceMode = ModSourceMode.AdminList, Entries = ModPolicyImport.FromAuthority(authority, [], merge: false) };

        Assert.Equal(ModVerdict.Admit, ModPolicyEvaluator.Evaluate(authority, policy, authority).Verdict);
        var evaluation = ModPolicyEvaluator.Evaluate([Ext("ego_dlc_split", "900")], policy, authority);
        Assert.Equal(ModVerdict.Reject, evaluation.Verdict);
        Assert.Equal(["ws_1"], evaluation.Violation!.Install.Select(r => r.Id));
    }

    [Fact]
    public void SettingsKnobsAreTheDefaultsThenLaterSettingChangesAreAdoptedAsEdits()
    {
        var settings = new ModManagementOptions { Enforcement = ModEnforcement.Strict };
        var store = new InMemoryModStore();
        var provider = new PersistentModPolicyProvider(store, () => settings);
        var changes = new List<ModPolicyT>();
        provider.Changed += changes.Add;

        Assert.Equal((1u, ModEnforcement.Strict), (provider.Current.Version, provider.Current.Enforcement));
        provider.Update("admin:a", p => p.UnknownDefault = UnknownModDefault.Block);
        Assert.Equal((2u, UnknownModDefault.Block), (provider.Current.Version, provider.Current.UnknownDefault));

        settings = new ModManagementOptions { Enforcement = ModEnforcement.Warn };      // the admin switches the setting
        Assert.Equal((3u, ModEnforcement.Warn), (provider.Current.Version, provider.Current.Enforcement));
        Assert.Equal(UnknownModDefault.AllowClientOnly, provider.Current.UnknownDefault); // the setting's other knobs follow too
        Assert.Equal(3u, provider.Current.Version);                                       // adopted once
        Assert.Equal([2u, 3u], changes.Select(c => c.Version));
        Assert.Equal("settings", store.LoadPolicy()!.UpdatedBy);

        // the stored policy outlives the provider
        var again = new PersistentModPolicyProvider(store, () => settings);
        Assert.Equal((3u, ModEnforcement.Warn), (again.Current.Version, again.Current.Enforcement));
    }

    [Fact]
    public void EquivalentIgnoresTheVersionButNotAnyEntryField()
    {
        var a = new ModPolicyT { Version = 1, Entries = [new ModPolicyEntryT { Id = "x", Notes = "n", ContentHash = [1] }] };
        var b = a.Clone();
        b.Version = 9;
        Assert.True(ModPolicyCopy.Equivalent(a, b));
        b.Entries![0].Notes = "other";
        Assert.False(ModPolicyCopy.Equivalent(a, b));
        b = a.Clone();
        b.Entries![0].ContentHash = [2];
        Assert.False(ModPolicyCopy.Equivalent(a, b));
        b = a.Clone();
        b.Enforcement = ModEnforcement.Warn;
        Assert.False(ModPolicyCopy.Equivalent(a, b));
    }
}
