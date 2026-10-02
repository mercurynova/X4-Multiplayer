using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

/// <summary><c>--extensions-preset</c>: a built-in, deterministic extension report (M1-X5).</summary>
public enum ExtensionPreset
{
    None,

    /// <summary>The official DLC only.</summary>
    Vanilla,

    /// <summary>The DLC plus typical mods: two allowlisted client-only libraries, a Workshop sim mod and a manually installed (Nexus) sim mod.</summary>
    Modded,

    /// <summary>Bot <c>i</c> reports <see cref="ExtensionPresets.Modded"/> changed in one way, cycling through <see cref="MismatchVariant"/>.</summary>
    Mismatch,
}

/// <summary>The one way a <see cref="ExtensionPreset.Mismatch"/> bot differs from <see cref="ExtensionPresets.Modded"/>.</summary>
public enum MismatchVariant
{
    /// <summary>A required Workshop mod is not installed (install list).</summary>
    MissingRequiredMod,

    /// <summary>An extra sim mod that is not part of the session (disable list).</summary>
    ExtraBlockedMod,

    /// <summary>A required mod is installed but switched off (enable list).</summary>
    DisabledRequiredMod,

    /// <summary>A required mod has an older version (update list).</summary>
    OutdatedVersion,

    /// <summary>A DLC the authority has is missing (install list, always rejects).</summary>
    MissingDlc,

    /// <summary>An extra DLC the authority lacks (disable list, always rejects).</summary>
    ExtraDlc,
}

/// <summary>The built-in extension sets. Deterministic: the same call always returns equal content.</summary>
public static class ExtensionPresets
{
    public const string SplitDlc = "ego_dlc_split";
    public const string TerranDlc = "ego_dlc_terran";
    public const string BoronDlc = "ego_dlc_boron";
    public const string Kuertee = "kuerteeUIExtensionsAndHUD";
    public const string SirNukesApi = "ws_2042901274";
    public const string WorkshopSim = "ws_2458720435";
    public const string NexusSim = "sn_better_traders";
    public const string ExtraSim = "ws_9000000001";

    public static IReadOnlyList<MismatchVariant> Variants { get; } = Enum.GetValues<MismatchVariant>();

    public static bool TryParse(string value, out ExtensionPreset preset) =>
        Enum.TryParse(value, ignoreCase: true, out preset) && Enum.IsDefined(preset) && preset != ExtensionPreset.None;

    private static ExtensionInfoT Dlc(string id, string name) => new()
    {
        Id = id, Name = name, Version = "900", Source = ExtensionSource.Dlc, Enabled = true, Egosoft = true, ClassHint = ExtensionClass.Dlc, Dependencies = [],
    };

    private static ExtensionInfoT Workshop(string id, string name, string version, ExtensionClass cls) => new()
    {
        Id = id, Name = name, Version = version, Source = ExtensionSource.Workshop, Enabled = true, WorkshopId = ModLinks.WorkshopIdOf(id), ClassHint = cls, Dependencies = [],
    };

    /// <summary>Split Vendetta and Cradle of Humanity (an official-DLC-only install).</summary>
    public static List<ExtensionInfoT> Vanilla() =>
    [
        Dlc(SplitDlc, "Split Vendetta"),
        Dlc(TerranDlc, "Cradle of Humanity"),
    ];

    /// <summary>The DLC, kuertee UI Extensions and SirNukes' Mod Support APIs (client-only allowlist), a Workshop sim mod and a Nexus-installed sim mod.</summary>
    public static List<ExtensionInfoT> Modded()
    {
        var list = Vanilla();
        list.Add(new ExtensionInfoT { Id = Kuertee, Name = "UI Extensions and HUD", Version = "7.5.1", Source = ExtensionSource.Install, Enabled = true, ClassHint = ExtensionClass.ClientOnly, Dependencies = [] });
        list.Add(Workshop(SirNukesApi, "Mod Support APIs", "1.93", ExtensionClass.ClientOnly));
        list.Add(Workshop(WorkshopSim, "Warehouse Fleets", "1.4", ExtensionClass.Sim));
        list.Add(new ExtensionInfoT { Id = NexusSim, Name = "Better Traders", Version = "2.0", Source = ExtensionSource.Install, Enabled = true, ClassHint = ExtensionClass.Sim, Dependencies = [] });
        return list;
    }

    /// <summary>The mismatch variant of bot number <paramref name="ordinal"/> (0-based; cycles).</summary>
    public static MismatchVariant VariantOf(int ordinal) => Variants[((ordinal % Variants.Count) + Variants.Count) % Variants.Count];

    /// <summary><see cref="Modded"/> changed in the one way <paramref name="variant"/> names.</summary>
    public static List<ExtensionInfoT> Mismatched(MismatchVariant variant)
    {
        var list = Modded();
        switch (variant)
        {
            case MismatchVariant.MissingRequiredMod:
                list.RemoveAll(e => e.Id == WorkshopSim);
                break;
            case MismatchVariant.ExtraBlockedMod:
                list.Add(Workshop(ExtraSim, "Unlisted Gadgets", "1.0", ExtensionClass.Sim));
                break;
            case MismatchVariant.DisabledRequiredMod:
                list.Single(e => e.Id == NexusSim).Enabled = false;
                break;
            case MismatchVariant.OutdatedVersion:
                list.Single(e => e.Id == WorkshopSim).Version = "1.3";
                break;
            case MismatchVariant.MissingDlc:
                list.RemoveAll(e => e.Id == TerranDlc);
                break;
            case MismatchVariant.ExtraDlc:
                list.Add(Dlc(BoronDlc, "Kingdom End"));
                break;
        }

        return list;
    }

    /// <summary>The report bot number <paramref name="ordinal"/> sends for <paramref name="preset"/> (null for <see cref="ExtensionPreset.None"/>).</summary>
    public static List<ExtensionInfoT>? For(ExtensionPreset preset, int ordinal) => preset switch
    {
        ExtensionPreset.Vanilla => Vanilla(),
        ExtensionPreset.Modded => Modded(),
        ExtensionPreset.Mismatch => Mismatched(VariantOf(ordinal)),
        _ => null,
    };

    /// <summary>The variant label printed for a bot (the preset name for the non-mismatch presets).</summary>
    public static string Label(ExtensionPreset preset, int ordinal) =>
        preset == ExtensionPreset.Mismatch ? VariantOf(ordinal).ToString() : preset.ToString().ToLowerInvariant();
}
