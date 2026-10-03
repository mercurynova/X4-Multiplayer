using X4MP.Core.Mods;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Tests.Mods;

/// <summary>Table-driven: every rule x player state of docs/mod-management.md 3.4, plus classification, versions and links.</summary>
public class ModPolicyEvaluatorTests
{
    // ------------------------------------------------------------------ builders

    private const string Nexus = "https://www.nexusmods.com/x4foundations/mods/1234";

    private static ExtensionInfoT Ext(
        string id, string version = "1.0", bool enabled = true, ExtensionClass hint = ExtensionClass.Unknown, byte[]? hash = null, string? name = null) => new()
    {
        Id = id,
        Name = name ?? id,
        Version = version,
        Enabled = enabled,
        ClassHint = hint,
        ContentHash = hash is null ? [] : [.. hash],
        Source = id.StartsWith("ego_dlc_", StringComparison.Ordinal) ? ExtensionSource.Dlc : ExtensionSource.Workshop,
    };

    private static ModPolicyEntryT Entry(
        string id, ModRule rule = ModRule.Required, bool enabled = true, string version = "", VersionRule vr = VersionRule.Exact,
        ExtensionClass cls = ExtensionClass.Unknown, byte[]? hash = null, string nexus = "", string notes = "", string? name = null) => new()
    {
        Id = id,
        Name = name ?? id,
        Rule = rule,
        Enabled = enabled,
        Version = version,
        VersionRule = vr,
        ModClass = cls,
        ContentHash = hash is null ? [] : [.. hash],
        NexusUrl = nexus,
        Notes = notes,
    };

    private static ModPolicyT Policy(
        ModSourceMode mode = ModSourceMode.AdminList, UnknownModDefault unknown = UnknownModDefault.AllowClientOnly,
        ModEnforcement enforcement = ModEnforcement.Strict, ModPolicyEntryT[]? entries = null) => new()
    {
        Version = 9,
        SourceMode = mode,
        UnknownDefault = unknown,
        Enforcement = enforcement,
        Entries = [.. entries ?? []],
    };

    private sealed record Case(
        string Name, ModPolicyT Policy, ExtensionInfoT[] Player, ModVerdict Verdict, string Install = "", string Enable = "", string Disable = "",
        string Update = "", bool Dlc = false, ExtensionInfoT[]? Authority = null);

    private static readonly byte[] H1 = [1, 2, 3];
    private static readonly byte[] H2 = [9, 9, 9];

    private static readonly Case[] Cases =
    [
        // ---- Required
        new("required absent -> install", Policy(entries: [Entry("ws_1", version: "1.0")]), [], ModVerdict.Reject, Install: "ws_1"),
        new("required disabled -> enable", Policy(entries: [Entry("ws_1", version: "1.0")]), [Ext("ws_1", enabled: false)], ModVerdict.Reject, Enable: "ws_1"),
        new("required exact ok", Policy(entries: [Entry("ws_1", version: "1.0")]), [Ext("ws_1")], ModVerdict.Admit),
        new("required exact wrong version -> update", Policy(entries: [Entry("ws_1", version: "1.4")]), [Ext("ws_1", "1.0")], ModVerdict.Reject, Update: "ws_1"),
        new("required exact no expected version accepts anything", Policy(entries: [Entry("ws_1")]), [Ext("ws_1", "7")], ModVerdict.Admit),
        new("required at-least newer ok", Policy(entries: [Entry("ws_1", version: "1.4", vr: VersionRule.AtLeast)]), [Ext("ws_1", "1.10")], ModVerdict.Admit),
        new("required at-least equal ok", Policy(entries: [Entry("ws_1", version: "1.4", vr: VersionRule.AtLeast)]), [Ext("ws_1", "1.4")], ModVerdict.Admit),
        new("required at-least older -> update", Policy(entries: [Entry("ws_1", version: "1.4", vr: VersionRule.AtLeast)]), [Ext("ws_1", "1.3")], ModVerdict.Reject, Update: "ws_1"),
        new("required any version ok", Policy(entries: [Entry("ws_1", version: "9", vr: VersionRule.Any)]), [Ext("ws_1", "1")], ModVerdict.Admit),
        new("required hash differs (sim) -> update", Policy(entries: [Entry("ws_1", version: "1.0", hash: H1)]), [Ext("ws_1", hash: H2)], ModVerdict.Reject, Update: "ws_1"),
        new("required hash equal ok", Policy(entries: [Entry("ws_1", version: "1.0", hash: H1)]), [Ext("ws_1", hash: H1)], ModVerdict.Admit),
        new("required hash on one side only ok", Policy(entries: [Entry("ws_1", version: "1.0", hash: H1)]), [Ext("ws_1")], ModVerdict.Admit),
        new("required hash differs but client-only -> info only", Policy(entries: [Entry("ws_1", version: "1.0", hash: H1, cls: ExtensionClass.ClientOnly)]), [Ext("ws_1", hash: H2)], ModVerdict.Admit),
        new("required hash differs but version rule any", Policy(entries: [Entry("ws_1", vr: VersionRule.Any, hash: H1)]), [Ext("ws_1", hash: H2)], ModVerdict.Admit),

        // ---- Required but switched off by the admin acts as Blocked
        new("required off + player enabled -> disable", Policy(entries: [Entry("ws_1", enabled: false)]), [Ext("ws_1")], ModVerdict.Reject, Disable: "ws_1"),
        new("required off + player disabled ok", Policy(entries: [Entry("ws_1", enabled: false)]), [Ext("ws_1", enabled: false)], ModVerdict.Admit),
        new("required off + player absent ok", Policy(entries: [Entry("ws_1", enabled: false)]), [], ModVerdict.Admit),

        // ---- Blocked
        new("blocked enabled -> disable", Policy(entries: [Entry("cheat", ModRule.Blocked)]), [Ext("cheat")], ModVerdict.Reject, Disable: "cheat"),
        new("blocked disabled ok", Policy(entries: [Entry("cheat", ModRule.Blocked)]), [Ext("cheat", enabled: false)], ModVerdict.Admit),
        new("blocked absent ok", Policy(entries: [Entry("cheat", ModRule.Blocked)]), [], ModVerdict.Admit),

        // ---- Allowed
        new("allowed absent ok", Policy(entries: [Entry("ws_2", ModRule.Allowed, version: "1.0")]), [], ModVerdict.Admit),
        new("allowed present ok", Policy(entries: [Entry("ws_2", ModRule.Allowed, version: "1.0")]), [Ext("ws_2")], ModVerdict.Admit),
        new("allowed sim wrong version -> update", Policy(entries: [Entry("ws_2", ModRule.Allowed, version: "2.0")]), [Ext("ws_2", "1.0")], ModVerdict.Reject, Update: "ws_2"),
        new("allowed client-only wrong version ok", Policy(entries: [Entry("ws_2", ModRule.Allowed, version: "2.0", cls: ExtensionClass.ClientOnly)]), [Ext("ws_2", "1.0")], ModVerdict.Admit),
        new("allowed disabled wrong version ok", Policy(entries: [Entry("ws_2", ModRule.Allowed, version: "2.0")]), [Ext("ws_2", "1.0", enabled: false)], ModVerdict.Admit),
        new("allowed ignores the enabled switch", Policy(entries: [Entry("ws_2", ModRule.Allowed, enabled: false)]), [Ext("ws_2")], ModVerdict.Admit),

        // ---- unlisted mods: unknown_default
        new("unlisted sim, AllowClientOnly -> disable", Policy(), [Ext("m", hint: ExtensionClass.Sim)], ModVerdict.Reject, Disable: "m"),
        new("unlisted unknown class counts as sim -> disable", Policy(), [Ext("m")], ModVerdict.Reject, Disable: "m"),
        new("unlisted client-only hint, AllowClientOnly ok", Policy(), [Ext("m", hint: ExtensionClass.ClientOnly)], ModVerdict.Admit),
        new("unlisted disabled sim ok", Policy(), [Ext("m", enabled: false, hint: ExtensionClass.Sim)], ModVerdict.Admit),
        new("unlisted client-only, Block -> disable", Policy(unknown: UnknownModDefault.Block), [Ext("m", hint: ExtensionClass.ClientOnly)], ModVerdict.Reject, Disable: "m"),
        new("unlisted sim, AllowAll ok", Policy(unknown: UnknownModDefault.AllowAll), [Ext("m", hint: ExtensionClass.Sim)], ModVerdict.Admit),

        // ---- classification and the allowlist
        new("allowlisted library unlisted, Block ok", Policy(unknown: UnknownModDefault.Block), [Ext("ws_2042901274", "999")], ModVerdict.Admit),
        new("allowlisted library hinted sim ok", Policy(), [Ext("kuerteeUIExtensionsAndHUD", hint: ExtensionClass.Sim)], ModVerdict.Admit),
        new("allowlisted library listed required: version never rejects", Policy(entries: [Entry("ws_3477279743", version: "5")]), [Ext("ws_3477279743", "1")], ModVerdict.Admit),
        new("allowlisted library listed required: absent -> install", Policy(entries: [Entry("ws_3477279743")]), [], ModVerdict.Reject, Install: "ws_3477279743"),
        new("allowlisted library listed blocked: enabled -> disable", Policy(entries: [Entry("ws_3514258146", ModRule.Blocked)]), [Ext("ws_3514258146")], ModVerdict.Reject, Disable: "ws_3514258146"),
        new("allowlist beats the admin class", Policy(unknown: UnknownModDefault.Block, entries: [Entry("ws_2042901274", ModRule.Allowed, cls: ExtensionClass.Sim, version: "1", vr: VersionRule.Exact)]), [Ext("ws_2042901274", "2")], ModVerdict.Admit),
        new("admin class beats the hint: client-only override", Policy(entries: [Entry("m", ModRule.Allowed, cls: ExtensionClass.ClientOnly, version: "2")]), [Ext("m", "1", hint: ExtensionClass.Sim)], ModVerdict.Admit),
        new("hint used without an override: sim", Policy(entries: [Entry("m", ModRule.Allowed, version: "2")]), [Ext("m", "1", hint: ExtensionClass.Sim)], ModVerdict.Reject, Update: "m"),
        new("x4mp and x4native are ignored", Policy(unknown: UnknownModDefault.Block), [Ext("x4mp"), Ext("x4native", "9")], ModVerdict.Admit),
        new("x4mp as a Required entry is ignored", Policy(entries: [Entry("x4mp")]), [], ModVerdict.Admit),

        // ---- Dlc is always strict
        new("dlc version 900 equals 9.00 (hundredths vs dotted)", Policy(enforcement: ModEnforcement.Warn, entries: [Entry("ego_dlc_split", version: "900")]), [Ext("ego_dlc_split", "9.00")], ModVerdict.Admit),
        new("dlc authority 9.00 admits a client reporting 900", Policy(enforcement: ModEnforcement.Warn), [Ext("ego_dlc_split", "900")], ModVerdict.Admit, Authority: [Ext("ego_dlc_split", "9.00")]),
        new("dlc authority 900 admits a client reporting 9.00", Policy(enforcement: ModEnforcement.Warn), [Ext("ego_dlc_split", "9.00")], ModVerdict.Admit, Authority: [Ext("ego_dlc_split", "900")]),
        new("dlc 9.00 vs 8.00 still rejects", Policy(enforcement: ModEnforcement.Warn), [Ext("ego_dlc_split", "800")], ModVerdict.Reject, Update: "ego_dlc_split", Dlc: true, Authority: [Ext("ego_dlc_split", "9.00")]),
        new("dlc missing under Strict", Policy(entries: [Entry("ego_dlc_split", version: "900")]), [], ModVerdict.Reject, Install: "ego_dlc_split", Dlc: true),
        new("dlc missing under Warn still rejects", Policy(enforcement: ModEnforcement.Warn, entries: [Entry("ego_dlc_split", version: "900")]), [], ModVerdict.Reject, Install: "ego_dlc_split", Dlc: true),
        new("dlc version differs under Warn still rejects", Policy(enforcement: ModEnforcement.Warn, entries: [Entry("ego_dlc_split", version: "900")]), [Ext("ego_dlc_split", "800")], ModVerdict.Reject, Update: "ego_dlc_split", Dlc: true),
        new("dlc disabled under Warn still rejects", Policy(enforcement: ModEnforcement.Warn, entries: [Entry("ego_dlc_split", version: "900")]), [Ext("ego_dlc_split", "900", enabled: false)], ModVerdict.Reject, Enable: "ego_dlc_split", Dlc: true),
        new("dlc blocked under Warn still rejects", Policy(enforcement: ModEnforcement.Warn, entries: [Entry("ego_dlc_boron", ModRule.Blocked)]), [Ext("ego_dlc_boron")], ModVerdict.Reject, Disable: "ego_dlc_boron", Dlc: true),
        new("dlc admin class cannot hide a dlc", Policy(enforcement: ModEnforcement.Warn, entries: [Entry("ego_dlc_split", cls: ExtensionClass.ClientOnly, version: "900")]), [], ModVerdict.Reject, Install: "ego_dlc_split", Dlc: true),
        new("dlc extra the authority lacks rejects (AdminList)", Policy(enforcement: ModEnforcement.Warn), [Ext("ego_dlc_split"), Ext("ego_dlc_boron")], ModVerdict.Reject, Disable: "ego_dlc_boron", Dlc: true, Authority: [Ext("ego_dlc_split")]),
        new("dlc extra with no authority yet is not judged", Policy(), [Ext("ego_dlc_boron")], ModVerdict.Admit),

        // ---- Warn: admit and flag
        new("warn admits a missing sim mod", Policy(enforcement: ModEnforcement.Warn, entries: [Entry("ws_1", version: "1.0")]), [], ModVerdict.AdmitWithWarning, Install: "ws_1"),
        new("warn admits an unlisted sim mod", Policy(enforcement: ModEnforcement.Warn), [Ext("m", hint: ExtensionClass.Sim)], ModVerdict.AdmitWithWarning, Disable: "m"),
        new("warn with a mix of sim and dlc violations rejects", Policy(enforcement: ModEnforcement.Warn, entries: [Entry("ws_1"), Entry("ego_dlc_split")]), [], ModVerdict.Reject, Install: "ws_1,ego_dlc_split", Dlc: true),

        // ---- all four lists at once, in policy order
        new("every list at once",
            Policy(entries: [Entry("a_missing"), Entry("b_off"), Entry("c_old", version: "2"), Entry("d_blocked", ModRule.Blocked)]),
            [Ext("b_off", enabled: false), Ext("c_old", "1"), Ext("d_blocked"), Ext("z_extra", hint: ExtensionClass.Sim)],
            ModVerdict.Reject, Install: "a_missing", Enable: "b_off", Disable: "d_blocked,z_extra", Update: "c_old"),

        // ---- AuthorityDefines
        new("authority-defines without an authority admits everything", Policy(ModSourceMode.AuthorityDefines), [Ext("m", hint: ExtensionClass.Sim)], ModVerdict.Admit),
        new("authority-defines: authority sim mod is implicitly required (missing)", Policy(ModSourceMode.AuthorityDefines), [], ModVerdict.Reject, Install: "ws_7", Authority: [Ext("ws_7", "3")]),
        new("authority-defines: version must match exactly", Policy(ModSourceMode.AuthorityDefines), [Ext("ws_7", "2")], ModVerdict.Reject, Update: "ws_7", Authority: [Ext("ws_7", "3")]),
        new("authority-defines: matching set admits", Policy(ModSourceMode.AuthorityDefines), [Ext("ws_7", "3"), Ext("ws_ui", hint: ExtensionClass.ClientOnly)], ModVerdict.Admit, Authority: [Ext("ws_7", "3")]),
        new("authority-defines: authority client-only mod is not required", Policy(ModSourceMode.AuthorityDefines), [], ModVerdict.Admit, Authority: [Ext("ws_ui", hint: ExtensionClass.ClientOnly)]),
        new("authority-defines: authority's disabled mod is not required", Policy(ModSourceMode.AuthorityDefines), [], ModVerdict.Admit, Authority: [Ext("ws_7", enabled: false)]),
        new("authority-defines: authority's allowlisted library is not required", Policy(ModSourceMode.AuthorityDefines), [], ModVerdict.Admit, Authority: [Ext("ws_2042901274")]),
        new("authority-defines: player-only sim mod disabled", Policy(ModSourceMode.AuthorityDefines), [Ext("ws_7", "3"), Ext("extra", hint: ExtensionClass.Sim)], ModVerdict.Reject, Disable: "extra", Authority: [Ext("ws_7", "3")]),
        new("authority-defines: explicit Blocked wins over the implicit entry", Policy(ModSourceMode.AuthorityDefines, entries: [Entry("ws_7", ModRule.Blocked)]), [Ext("ws_7", "3")], ModVerdict.Reject, Disable: "ws_7", Authority: [Ext("ws_7", "3")]),
        new("authority-defines: authority hash is compared", Policy(ModSourceMode.AuthorityDefines), [Ext("ws_7", "3", hash: H2)], ModVerdict.Reject, Update: "ws_7", Authority: [Ext("ws_7", "3", hash: H1)]),
        new("authority-defines: dlc differences reject under Warn", Policy(ModSourceMode.AuthorityDefines, enforcement: ModEnforcement.Warn), [], ModVerdict.Reject, Install: "ego_dlc_split", Dlc: true, Authority: [Ext("ego_dlc_split", "900")]),

        // ---- AdminList also checks the authority's dlc, but not its sim mods
        new("admin-list: authority's sim mods are not implicit", Policy(), [], ModVerdict.Admit, Authority: [Ext("ws_7", "3")]),
        new("admin-list: authority's dlc is implicit", Policy(), [], ModVerdict.Reject, Install: "ego_dlc_split", Dlc: true, Authority: [Ext("ego_dlc_split", "900")]),
        new("admin-list: explicit dlc entry wins over the implicit one", Policy(entries: [Entry("ego_dlc_split", vr: VersionRule.Any)]), [Ext("ego_dlc_split", "800")], ModVerdict.Admit, Authority: [Ext("ego_dlc_split", "900")]),
    ];

    public static TheoryData<string> CaseNames => [.. Cases.Select(c => c.Name)];

    private static string Ids(IEnumerable<ModRefT>? refs) => string.Join(",", (refs ?? []).Select(r => r.Id));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void EveryRuleAgainstEveryPlayerState(string name)
    {
        var c = Cases.Single(x => x.Name == name);
        var result = ModPolicyEvaluator.Evaluate(c.Player, c.Policy, c.Authority);

        Assert.Equal(c.Verdict, result.Verdict);
        Assert.Equal(c.Dlc, result.DlcViolation);
        if (c.Verdict == ModVerdict.Admit)
        {
            Assert.Null(result.Violation);
            return;
        }

        var v = result.Violation!;
        Assert.Equal(9u, v.PolicyVersion);
        Assert.Equal(c.Install, Ids(v.Install));
        Assert.Equal(c.Enable, Ids(v.Enable));
        Assert.Equal(c.Disable, Ids(v.Disable));
        Assert.Equal(c.Update, Ids(v.Update));
    }

    [Fact]
    public void CaseNamesAreUnique() => Assert.Equal(Cases.Length, Cases.Select(c => c.Name).Distinct().Count());

    // ------------------------------------------------------------------ the exact ModRef content

    [Fact]
    public void InstallListCarriesLinksNotesAndVersions()
    {
        var policy = Policy(entries:
        [
            Entry("ws_1234567890", version: "1.4", nexus: Nexus, notes: "needs SirNukes too", name: "Warehouse Fleets"),
            Entry("ws_55", version: "2", name: "Other"),
            Entry("loose_mod", version: "3", name: "Loose"),
        ]);
        var result = ModPolicyEvaluator.Evaluate([], policy, null);

        var install = result.Violation!.Install;
        Assert.Equal(3, install.Count);
        var warehouse = install[0];
        Assert.Equal("ws_1234567890", warehouse.Id);
        Assert.Equal("Warehouse Fleets", warehouse.Name);
        Assert.Equal("1.4", warehouse.Version);
        Assert.Equal(string.Empty, warehouse.HaveVersion);
        Assert.Equal(Nexus, warehouse.NexusUrl);
        Assert.Equal(1234567890ul, warehouse.WorkshopId); // derived from the ws_ id
        Assert.Equal("needs SirNukes too", warehouse.Notes);
        Assert.Equal(55ul, install[1].WorkshopId);
        Assert.Equal(0ul, install[2].WorkshopId);
        Assert.Equal("https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890", ModLinks.WorkshopUrl(warehouse.WorkshopId));
        Assert.Equal("steam://url/CommunityFilePage/1234567890", ModLinks.WorkshopSteamUrl(warehouse.WorkshopId));
    }

    [Fact]
    public void UpdateAndDisableEntriesCarryTheHaveVersion()
    {
        var policy = Policy(entries: [Entry("deadair", version: "2.3", name: "DeadAir Scripts"), Entry("cheat", ModRule.Blocked, name: "Cheat Menu")]);
        var result = ModPolicyEvaluator.Evaluate([Ext("deadair", "2.1"), Ext("cheat", "1.0"), Ext("stray", "5", hint: ExtensionClass.Sim, name: "Stray")], policy, null);

        var update = Assert.Single(result.Violation!.Update);
        Assert.Equal("DeadAir Scripts", update.Name);
        Assert.Equal("2.1", update.HaveVersion);
        Assert.Equal("2.3", update.Version);
        Assert.Equal(["cheat", "stray"], result.Violation.Disable.Select(r => r.Id));
        Assert.Equal("Stray", result.Violation.Disable[1].Name);
        Assert.Equal("5", result.Violation.Disable[1].HaveVersion);
    }

    [Fact]
    public void LargeNonEmptyDuplicatePlayerListsAreHandled()
    {
        var player = Enumerable.Range(0, 1000).Select(i => Ext("mod_" + i, hint: ExtensionClass.ClientOnly)).Append(Ext("dup", "1")).Append(Ext("dup", "2")).ToList();
        var result = ModPolicyEvaluator.Evaluate(player, Policy(entries: [Entry("dup", version: "2")]), null);
        Assert.Equal(ModVerdict.Admit, result.Verdict); // the last duplicate wins
    }

    [Fact]
    public void DescribeListsEachGroup()
    {
        var result = ModPolicyEvaluator.Evaluate([Ext("b")], Policy(entries: [Entry("a", name: "Alpha"), Entry("b", ModRule.Blocked, name: "Beta")]), null);
        Assert.Equal("install: Alpha; disable: Beta", ModPolicyEvaluator.Describe(result.Violation!));
    }

    // ------------------------------------------------------------------ classification, hash, versions, links

    [Fact]
    public void ClassificationOrderIsAllowlistThenDlcThenAdminThenHint()
    {
        Assert.Equal(ExtensionClass.ClientOnly, ExtensionReports.Classify("ws_2042901274", Ext("ws_2042901274", hint: ExtensionClass.Sim), ExtensionClass.Sim));
        Assert.Equal(ExtensionClass.Dlc, ExtensionReports.Classify("ego_dlc_x", null, ExtensionClass.ClientOnly));
        Assert.Equal(ExtensionClass.Dlc, ExtensionReports.Classify("odd_id", new ExtensionInfoT { Source = ExtensionSource.Dlc }));
        Assert.Equal(ExtensionClass.ClientOnly, ExtensionReports.Classify("m", Ext("m", hint: ExtensionClass.Sim), ExtensionClass.ClientOnly));
        Assert.Equal(ExtensionClass.ClientOnly, ExtensionReports.Classify("m", Ext("m", hint: ExtensionClass.ClientOnly)));
        Assert.Equal(ExtensionClass.Sim, ExtensionReports.Classify("m", Ext("m")));
        Assert.Equal(ExtensionClass.Sim, ExtensionReports.Classify("m", null));
    }

    [Fact]
    public void ExtensionsHashCoversEnabledDlcAndSimOnly()
    {
        var baseList = new[] { Ext("ego_dlc_split", "900"), Ext("ws_7", "3") };
        var hash = ExtensionReports.ComputeHash(baseList);
        Assert.Equal(32, hash.Length);
        Assert.Equal(hash, ExtensionReports.ComputeHash(baseList.Reverse())); // sorted
        // allowlist, x4mp/x4native, disabled and client-only extensions do not change it
        Assert.Equal(hash, ExtensionReports.ComputeHash([.. baseList, Ext("ws_2042901274", "5"), Ext("x4mp"), Ext("x4native"), Ext("off", enabled: false), Ext("ui", hint: ExtensionClass.ClientOnly)]));
        // a version change, an extra sim mod do
        Assert.NotEqual(hash, ExtensionReports.ComputeHash([Ext("ego_dlc_split", "900"), Ext("ws_7", "4")]));
        Assert.NotEqual(hash, ExtensionReports.ComputeHash([.. baseList, Ext("more")]));
        // exactly the documented line format
        var expected = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("ego_dlc_split@900\nws_7@3\n"));
        Assert.Equal(expected, hash);
    }

    [Fact]
    public void LegacyStringListsAreLiftedIntoExtensionInfos()
    {
        var list = ExtensionReports.FromHello(new ClientHelloT { Extensions = ["ego_dlc_boron@1.0", "ws_42@3", "other@1", "bare"] });
        Assert.Equal(["ego_dlc_boron", "ws_42", "other", "bare"], list.Select(e => e.Id));
        Assert.Equal(ExtensionSource.Dlc, list[0].Source);
        Assert.Equal(42ul, list[1].WorkshopId);
        Assert.Equal("3", list[1].Version);
        Assert.All(list, e => Assert.True(e.Enabled));
        // extension_list wins over the legacy strings
        var full = ExtensionReports.FromHello(new ClientHelloT { Extensions = ["x@1"], ExtensionList = [Ext("y")] });
        Assert.Equal("y", Assert.Single(full).Id);
    }

    [Theory]
    [InlineData("1.4", "1.4", 0)]
    [InlineData("1.10", "1.4", 1)]
    [InlineData("1.3", "1.4", -1)]
    [InlineData("2", "1.9.9", 1)]
    [InlineData("1.0", "1", 0)]
    [InlineData("900", "611", 1)]
    [InlineData("1.0-b", "1.0-a", 1)]
    public void VersionsCompareNumericallyPerSegment(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(ModPolicyEvaluator.CompareVersions(a, b)));

    [Theory]
    [InlineData("https://www.nexusmods.com/x4foundations/mods/1234", "https://www.nexusmods.com/x4foundations/mods/1234")]
    [InlineData("https://nexusmods.com/x4foundations/mods/77/", "https://www.nexusmods.com/x4foundations/mods/77")]
    [InlineData("  https://www.nexusmods.com/x4foundations/mods/5/files?tab=x ", "https://www.nexusmods.com/x4foundations/mods/5")]
    public void NexusUrlsAreNormalised(string input, string expected)
    {
        Assert.True(ModLinks.TryNormaliseNexusUrl(input, out var normalised));
        Assert.Equal(expected, normalised);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://www.nexusmods.com/x4foundations/mods/1")]
    [InlineData("https://www.nexusmods.com/skyrim/mods/1")]
    [InlineData("https://www.nexusmods.com/x4foundations/mods/abc")]
    [InlineData("https://evil.example/x4foundations/mods/1")]
    [InlineData("https://www.nexusmods.com.evil.example/x4foundations/mods/1")]
    public void BadNexusUrlsAreRejected(string input) => Assert.False(ModLinks.TryNormaliseNexusUrl(input, out _));

    [Theory]
    [InlineData("ws_2436999794", 2436999794ul)]
    [InlineData("ws_", 0ul)]
    [InlineData("ws_abc", 0ul)]
    [InlineData("ego_dlc_split", 0ul)]
    public void WorkshopIdsComeFromTheExtensionId(string id, ulong expected) => Assert.Equal(expected, ModLinks.WorkshopIdOf(id));

    // ------------------------------------------------------------------ in-memory provider

    [Fact]
    public void ProviderBumpsTheVersionOnEntryAndSettingChanges()
    {
        var options = new Core.Settings.ModManagementOptions();
        var provider = new InMemoryModPolicyProvider(() => options);
        var first = provider.Current;
        Assert.Same(first, provider.Current);
        Assert.Equal(ModSourceMode.AuthorityDefines, first.SourceMode);

        var raised = new List<ModPolicyT>();
        provider.Changed += raised.Add;
        var edited = provider.SetEntries([Entry("ws_1")]);
        Assert.True(edited.Version > first.Version);
        Assert.Single(raised);

        options.Enforcement = ModEnforcement.Warn;
        var afterSetting = provider.Current;
        Assert.Equal(ModEnforcement.Warn, afterSetting.Enforcement);
        Assert.True(afterSetting.Version > edited.Version);
        Assert.Single(afterSetting.Entries);

        var copy = afterSetting.Clone();
        copy.Entries[0].Rule = ModRule.Blocked;
        Assert.Equal(ModRule.Required, provider.Current.Entries[0].Rule);
    }
}
