using X4MP.Core.Appearance;

namespace X4MP.Core.Tests.Appearance;

/// <summary>
/// The server's copy of the curated catalogue (ADR-051, variant B). The mod validates the same list in
/// <c>mod/native/features/appearance/appearance_catalogue.cpp</c>; these cases pin the rules the relay relies on.
/// </summary>
public sealed class AppearanceCatalogueTests
{
    [Fact]
    public void AKnownPairResolvesToItsCanonicalIdsAndMacro()
    {
        var (race, preset) = AppearanceCatalogue.Resolve("argon", "female_cau");

        Assert.NotNull(race);
        Assert.NotNull(preset);
        Assert.Equal("argon", race!.Id);
        Assert.Equal("female_cau", preset!.Id);
        Assert.Equal("character_argon_female_cau_civ_01_macro", preset.Macro);
        Assert.True(preset.IsFemale);
    }

    [Fact]
    public void AnUnknownRaceDoesNotResolve()
    {
        var (race, preset) = AppearanceCatalogue.Resolve("khaak", "generic");

        Assert.Null(race);
        Assert.Null(preset);
    }

    [Fact]
    public void AnUnknownPresetOfAKnownRaceDoesNotResolve()
    {
        var (race, preset) = AppearanceCatalogue.Resolve("argon", "male_a");

        Assert.Null(race);
        Assert.Null(preset);
    }

    [Fact]
    public void APresetOfAnotherRaceDoesNotResolve()
    {
        // "generic" is a paranid and a boron preset; the argon never had one.
        Assert.Null(AppearanceCatalogue.Resolve("argon", "generic").Preset);
        Assert.NotNull(AppearanceCatalogue.Resolve("paranid", "generic").Preset);
    }

    [Fact]
    public void AnEmptyPairIsTheDefaultLook()
    {
        Assert.Null(AppearanceCatalogue.Resolve("", "").Race);
        Assert.Null(AppearanceCatalogue.Resolve(null, null).Race);
        Assert.Null(AppearanceCatalogue.Resolve("argon", "").Preset);
        Assert.Null(AppearanceCatalogue.Resolve("", "male").Preset);
    }

    [Fact]
    public void TheIdsAreCaseSensitiveBecauseTheyAreWireValues()
    {
        Assert.Null(AppearanceCatalogue.Resolve("Argon", "female_cau").Race);
        Assert.Null(AppearanceCatalogue.Resolve("argon", "Female_Cau").Preset);
    }

    [Fact]
    public void TheCatalogueListsTheFiveRacesOfTheBuild()
    {
        Assert.Equal("argon,teladi,paranid,split,boron", string.Join(",", AppearanceCatalogue.Races.Select(r => r.Id)));
        Assert.Equal(12, AppearanceCatalogue.Races.Sum(r => r.Presets.Count));
        Assert.All(AppearanceCatalogue.Races, r => Assert.All(r.Presets, p => Assert.StartsWith("character_", p.Macro, StringComparison.Ordinal)));
    }

    [Fact]
    public void DescribeNamesTheLookOrCallsItDefault()
    {
        Assert.Equal("argon/female_cau", AppearanceCatalogue.Describe("argon", "female_cau"));
        Assert.Equal("argon/", AppearanceCatalogue.Describe("argon", ""));
        Assert.Equal("default", AppearanceCatalogue.Describe("", ""));
    }
}
