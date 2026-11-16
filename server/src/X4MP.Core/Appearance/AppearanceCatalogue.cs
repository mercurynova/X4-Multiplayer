namespace X4MP.Core.Appearance;

/// <summary>One selectable look: the wire ids the client sends and the character macro MD spawns.</summary>
/// <param name="Id">Wire value of the preset, stable across builds ("female_cau").</param>
/// <param name="Macro">The character macro the authority spawns for this preset.</param>
/// <param name="IsFemale">The gender the macro carries (the wire has no separate gender field).</param>
public sealed record AppearancePreset(string Id, string Macro, bool IsFemale);

/// <summary>One race and the presets it offers.</summary>
/// <param name="Id">Wire value of the race, stable across builds ("argon").</param>
/// <param name="Presets">The presets of this race, in picker order.</param>
public sealed record AppearanceRace(string Id, IReadOnlyList<AppearancePreset> Presets);

/// <summary>
/// The curated appearance catalogue, mirroring <c>mod/native/features/appearance/appearance_catalogue.cpp</c> (ADR-051, variant
/// B). The mod validates a picked look against its own copy; this one is the server's, so a hand-written client cannot store a
/// pair that no node can spawn (MD would log "no character" and the native retry loop would repeat it every 2 s).
/// <para>
/// An empty race/preset is the "default" look: the game picks a crew NPC and the server keeps the team origin (ADR-049). The
/// same applies to anything this catalogue does not know, so the rules are identical on both sides.
/// </para>
/// </summary>
public static class AppearanceCatalogue
{
    private static readonly AppearancePreset[] Argon =
    [
        new("male_cau", "character_argon_male_cau_civ_01_macro", false),
        new("female_cau", "character_argon_female_cau_civ_01_macro", true),
        new("male_afr", "character_argon_male_afr_civ_01_macro", false),
        new("female_afr", "character_argon_female_afr_civ_01_macro", true),
        new("male_asi", "character_argon_male_asi_civ_01_macro", false),
        new("female_asi", "character_argon_female_asi_civ_01_macro", true),
    ];

    private static readonly AppearancePreset[] Teladi =
    [
        new("male", "character_teladi_male_generic_01_macro", false),
        new("female", "character_teladi_female_generic_01_macro", true),
    ];

    private static readonly AppearancePreset[] Paranid =
    [
        new("generic", "character_paranid_generic_01_macro", false),
    ];

    private static readonly AppearancePreset[] Split =
    [
        new("male", "character_split_male_generic_01_macro", false),
        new("female", "character_split_female_generic_01_macro", true),
    ];

    private static readonly AppearancePreset[] Boron =
    [
        new("generic", "character_boron_generic_01_macro", false),
    ];

    /// <summary>The five races of the build, argon first - the same order and ids the mod's picker shows.</summary>
    public static IReadOnlyList<AppearanceRace> Races { get; } =
    [
        new("argon", Argon),
        new("teladi", Teladi),
        new("paranid", Paranid),
        new("split", Split),
        new("boron", Boron),
    ];

    /// <summary>The race with this wire id, or null for empty and unknown ids.</summary>
    public static AppearanceRace? FindRace(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        foreach (var race in Races)
        {
            if (string.Equals(race.Id, id, StringComparison.Ordinal))
            {
                return race;
            }
        }

        return null;
    }

    /// <summary>The preset of this race with this wire id, or null for empty and unknown ids.</summary>
    public static AppearancePreset? FindPreset(AppearanceRace race, string? id)
    {
        ArgumentNullException.ThrowIfNull(race);
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        foreach (var preset in race.Presets)
        {
            if (string.Equals(preset.Id, id, StringComparison.Ordinal))
            {
                return preset;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a pair from the wire: the race with that id and the preset of THAT race with that id. Nulls when either value is
    /// empty or unknown, which the caller treats as "default" - the same rule the mod's own catalogue applies.
    /// </summary>
    public static (AppearanceRace? Race, AppearancePreset? Preset) Resolve(string? race, string? preset)
    {
        AppearanceRace? known = FindRace(race);
        if (known is null)
        {
            return (null, null);
        }

        AppearancePreset? chosen = FindPreset(known, preset);
        return chosen is null ? (null, null) : (known, chosen);
    }

    /// <summary>"argon/female_cau", or "default" when both values are empty (the server log uses it).</summary>
    public static string Describe(string? race, string? preset)
    {
        if (string.IsNullOrEmpty(race) && string.IsNullOrEmpty(preset))
        {
            return "default";
        }

        return $"{race}/{preset}";
    }
}
