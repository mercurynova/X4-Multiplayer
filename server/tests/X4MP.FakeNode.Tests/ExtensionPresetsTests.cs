using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode.Tests;

public sealed class ExtensionPresetsTests
{
    private static readonly ModExpectationSettings Strict = new();
    private static readonly ModExpectationSettings Warn = new(ModEnforcement.Warn);

    private static string Ids(IEnumerable<ExpectedMod> m) => string.Join(',', m.Select(x => x.Id));

    [Fact]
    public void PresetsAreDeterministic()
    {
        foreach (var preset in new[] { ExtensionPreset.Vanilla, ExtensionPreset.Modded, ExtensionPreset.Mismatch })
        {
            var a = ExtensionPresets.For(preset, 3)!;
            var b = ExtensionPresets.For(preset, 3)!;
            Assert.Equal(ExtensionReports.ComputeHash(a), ExtensionReports.ComputeHash(b));
            Assert.Equal(a.Select(e => (e.Id, e.Version, e.Enabled)), b.Select(e => (e.Id, e.Version, e.Enabled)));
        }

        Assert.Null(ExtensionPresets.For(ExtensionPreset.None, 0));
    }

    [Fact]
    public void VanillaIsDlcOnlyAndModdedAddsAllowlistedLibrariesAndSimMods()
    {
        var vanilla = ExtensionPresets.Vanilla();
        Assert.All(vanilla, e => Assert.Equal(ExtensionClass.Dlc, ExtensionReports.Classify(e.Id, e)));

        var modded = ExtensionPresets.Modded();
        Assert.Contains(modded, e => ExtensionReports.IsAllowlisted(e.Id) && e.Id == ExtensionPresets.Kuertee);
        Assert.Contains(modded, e => ExtensionReports.IsAllowlisted(e.Id) && e.Id == ExtensionPresets.SirNukesApi);
        Assert.Contains(modded, e => e.Source == ExtensionSource.Workshop && e.WorkshopId == 2458720435ul && e.Id == ExtensionPresets.WorkshopSim);
        Assert.Contains(modded, e => e.Source == ExtensionSource.Install && e.Id == ExtensionPresets.NexusSim);
        Assert.All(vanilla, v => Assert.Contains(modded, m => m.Id == v.Id));
    }

    [Fact]
    public void MismatchCyclesThroughEveryVariant()
    {
        int n = ExtensionPresets.Variants.Count;
        Assert.Equal(6, n);
        for (int i = 0; i < n * 2; i++)
            Assert.Equal(ExtensionPresets.Variants[i % n], ExtensionPresets.VariantOf(i));
        Assert.Equal(n, Enumerable.Range(0, n).Select(i => string.Join('|', ExtensionPresets.For(ExtensionPreset.Mismatch, i)!.Select(e => $"{e.Id}@{e.Version}:{e.Enabled}"))).Distinct().Count());
    }

    [Theory]
    [InlineData(MismatchVariant.MissingRequiredMod, "ws_2458720435", "", "", "", false)]
    [InlineData(MismatchVariant.ExtraBlockedMod, "", "", "ws_9000000001", "", false)]
    [InlineData(MismatchVariant.DisabledRequiredMod, "", "sn_better_traders", "", "", false)]
    [InlineData(MismatchVariant.OutdatedVersion, "", "", "", "ws_2458720435", false)]
    [InlineData(MismatchVariant.MissingDlc, "ego_dlc_terran", "", "", "", true)]
    [InlineData(MismatchVariant.ExtraDlc, "", "", "ego_dlc_boron", "", true)]
    public void EachVariantBreaksExactlyOneRuleAgainstAModdedAuthority(MismatchVariant variant, string install, string enable, string disable, string update, bool dlc)
    {
        var expectation = ModExpectation.Compute(ExtensionPresets.Mismatched(variant), ExtensionPresets.Modded(), Strict);
        Assert.Equal(ExpectedModOutcome.Reject, expectation.Outcome);
        Assert.Equal((install, enable, disable, update, dlc), (Ids(expectation.Install), Ids(expectation.Enable), Ids(expectation.Disable), Ids(expectation.Update), expectation.DlcViolation));
    }

    [Fact]
    public void MatchingPresetsAreAdmitted()
    {
        Assert.Equal(ExpectedModOutcome.Admit, ModExpectation.Compute(ExtensionPresets.Modded(), ExtensionPresets.Modded(), Strict).Outcome);
        Assert.Equal(ExpectedModOutcome.Admit, ModExpectation.Compute(ExtensionPresets.Vanilla(), ExtensionPresets.Vanilla(), Strict).Outcome);
    }

    [Fact]
    public void VanillaAgainstAModdedAuthorityMissesTheSimModsButNotTheLibraries()
    {
        var e = ModExpectation.Compute(ExtensionPresets.Vanilla(), ExtensionPresets.Modded(), Strict);
        Assert.Equal(ExpectedModOutcome.Reject, e.Outcome);
        Assert.Equal("sn_better_traders,ws_2458720435", Ids(e.Install));
        Assert.False(e.DlcViolation);
    }

    [Fact]
    public void ModdedAgainstAVanillaAuthorityHasToDisableTheSimModsOnly()
    {
        var e = ModExpectation.Compute(ExtensionPresets.Modded(), ExtensionPresets.Vanilla(), Strict);
        Assert.Equal("sn_better_traders,ws_2458720435", Ids(e.Disable)); // the allowlisted libraries are fine
    }

    [Fact]
    public void WarnAdmitsEverythingButDlcDifferences()
    {
        var outcomes = ExtensionPresets.Variants.ToDictionary(v => v, v => ModExpectation.Compute(ExtensionPresets.Mismatched(v), ExtensionPresets.Modded(), Warn).Outcome);
        Assert.Equal(ExpectedModOutcome.AdmitWithWarning, outcomes[MismatchVariant.MissingRequiredMod]);
        Assert.Equal(ExpectedModOutcome.AdmitWithWarning, outcomes[MismatchVariant.ExtraBlockedMod]);
        Assert.Equal(ExpectedModOutcome.AdmitWithWarning, outcomes[MismatchVariant.DisabledRequiredMod]);
        Assert.Equal(ExpectedModOutcome.AdmitWithWarning, outcomes[MismatchVariant.OutdatedVersion]);
        Assert.Equal(ExpectedModOutcome.Reject, outcomes[MismatchVariant.MissingDlc]);
        Assert.Equal(ExpectedModOutcome.Reject, outcomes[MismatchVariant.ExtraDlc]);
    }

    [Theory]
    [InlineData(UnknownModDefault.AllowAll, ExpectedModOutcome.Admit)]
    [InlineData(UnknownModDefault.AllowClientOnly, ExpectedModOutcome.Reject)]
    [InlineData(UnknownModDefault.Block, ExpectedModOutcome.Reject)]
    public void UnknownDefaultDecidesTheExtraSimMod(UnknownModDefault unknown, ExpectedModOutcome outcome) =>
        Assert.Equal(outcome, ModExpectation.Compute(ExtensionPresets.Mismatched(MismatchVariant.ExtraBlockedMod), ExtensionPresets.Modded(), new ModExpectationSettings(ModEnforcement.Strict, unknown)).Outcome);

    [Fact]
    public void MatchesComparesTheListsAndWorkshopIds()
    {
        var expectation = ModExpectation.Compute(ExtensionPresets.Mismatched(MismatchVariant.MissingRequiredMod), ExtensionPresets.Modded(), Strict);
        var good = new ModPolicyViolationT { Install = [new ModRefT { Id = "ws_2458720435", WorkshopId = 2458720435ul }], Enable = [], Disable = [], Update = [] };
        Assert.True(expectation.Matches(good, out _));

        var wrongList = new ModPolicyViolationT { Install = [], Enable = [new ModRefT { Id = "ws_2458720435", WorkshopId = 2458720435ul }], Disable = [], Update = [] };
        Assert.False(expectation.Matches(wrongList, out string detail));
        Assert.Contains("install", detail, StringComparison.Ordinal);

        var wrongLink = new ModPolicyViolationT { Install = [new ModRefT { Id = "ws_2458720435", WorkshopId = 7 }], Enable = [], Disable = [], Update = [] };
        Assert.False(expectation.Matches(wrongLink, out _));
        Assert.Contains("ws_2458720435", ModExpectation.Describe(good), StringComparison.Ordinal);
        Assert.Contains("filedetails/?id=2458720435", ModExpectation.Links(good), StringComparison.Ordinal);
    }

    // ---- command line

    [Fact]
    public void TheCommandLineKnowsThePresetOptions()
    {
        var r = CliParser.Parse(["swarm", "--clients", "6", "--with-authority", "--extensions-preset", "mismatch", "--authority-extensions", "modded", "--expect-enforcement", "warn", "--expect-unknown", "block"]);
        Assert.True(r.Ok, r.Error);
        var o = r.Options!;
        Assert.Equal(ExtensionPreset.Mismatch, o.ExtensionsPreset);
        Assert.Equal(ModEnforcement.Warn, o.ExpectMods.Enforcement);
        Assert.Equal(UnknownModDefault.Block, o.ExpectMods.UnknownDefault);
        Assert.Equal(ExtensionPresets.Modded().Select(e => e.Id), o.ExtensionsFor(Role.Authority, 0)!.Select(e => e.Id));
        Assert.Equal(ExtensionPresets.For(ExtensionPreset.Mismatch, 2)!.Select(e => (e.Id, e.Enabled)), o.ExtensionsFor(Role.Client, 2)!.Select(e => (e.Id, e.Enabled)));
    }

    [Fact]
    public void ExtensionsOnlyApplyToClientsInASwarm()
    {
        var path = Path.Combine(Path.GetTempPath(), "x4mp-ext-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "[{\"id\":\"ws_5\",\"version\":\"1\"}]");
        try
        {
            var swarm = CliParser.Parse(["swarm", "--with-authority", "--extensions", path]).Options!;
            Assert.Null(swarm.ExtensionsFor(Role.Authority, 0));
            Assert.Equal(["ws_5"], swarm.ExtensionsFor(Role.Client, 0)!.Select(e => e.Id));

            var both = CliParser.Parse(["swarm", "--with-authority", "--extensions", path, "--authority-extensions", "vanilla"]).Options!;
            Assert.Equal(ExtensionPresets.Vanilla().Select(e => e.Id), both.ExtensionsFor(Role.Authority, 0)!.Select(e => e.Id));

            var fromFile = CliParser.Parse(["swarm", "--with-authority", "--authority-extensions", path]).Options!;
            Assert.Equal(["ws_5"], fromFile.ExtensionsFor(Role.Authority, 0)!.Select(e => e.Id));
            Assert.Null(fromFile.ExtensionsFor(Role.Client, 0));

            var authorityCommand = CliParser.Parse(["authority", "--extensions", path]).Options!;
            Assert.Equal(["ws_5"], authorityCommand.ExtensionsFor(Role.Authority, 0)!.Select(e => e.Id));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("client", "--extensions-preset", "bogus")]
    [InlineData("client", "--extensions-preset", "none")]
    [InlineData("swarm", "--authority-extensions", "mismatch")]
    [InlineData("swarm", "--authority-extensions", "no-such-file.json")]
    [InlineData("swarm", "--expect-enforcement", "maybe")]
    [InlineData("swarm", "--expect-unknown", "maybe")]
    public void BadPresetOptionsAreRejected(string command, string option, string value) => Assert.False(CliParser.Parse([command, option, value]).Ok);

    [Fact]
    public void ExtensionsAndAPresetExcludeEachOther()
    {
        var path = Path.Combine(Path.GetTempPath(), "x4mp-ext-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "[]");
        try
        {
            Assert.False(CliParser.Parse(["client", "--extensions", path, "--extensions-preset", "modded"]).Ok);
            Assert.False(CliParser.Parse(["authority", "--extensions-preset", "mismatch"]).Ok);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
