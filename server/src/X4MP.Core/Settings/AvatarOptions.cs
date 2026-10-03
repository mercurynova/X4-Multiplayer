namespace X4MP.Core.Settings;

/// <summary>
/// Player avatar settings (ADR-015, M3 plan Q5/Q6). Both are Live and pushed to every node in <c>ServerSettingsUpdate</c>
/// (keys <c>Avatars.StarterShipMacro</c> and <c>Avatars.SpawnOffsetMeters</c>); the authority's avatar provisioning reads them there.
/// </summary>
[SettingsSection(SectionName, "Avatars")]
public sealed class AvatarOptions
{
    public const string SectionName = "X4MP:Avatars";

    /// <summary>The Argon Elite: the starter ship of M3 (Q5 answer 2026-10-03).</summary>
    public const string DefaultStarterShipMacro = "ship_arg_s_fighter_01_a_macro";

    public const int DefaultSpawnOffsetMeters = 300;

    /// <summary>
    /// The macro of the ship a new player's avatar is spawned as. Do not read this property to choose a ship: call
    /// <see cref="ResolveStarterShipMacro"/>, the one place a later milestone changes to pick the ship per faction or race (ADR-049/051).
    /// </summary>
    [Setting("Avatar starter ship macro", Scope = SettingScope.Live, PushToNodes = true, MaxLength = 120)]
    public string StarterShipMacro { get; set; } = DefaultStarterShipMacro;

    /// <summary>
    /// A vanilla loadout id for the starter ship (early-game equipment). Empty = the mod picks a basic early-game loadout; the avatar must
    /// never get the high-end Mk2/Mk3 parts that <c>SpawnObjectAtPos2</c> fits by default. Read it through <see cref="ResolveStarterLoadout"/>.
    /// </summary>
    [Setting("Avatar starter loadout id (empty = basic early-game loadout chosen by the mod)", Scope = SettingScope.Live, PushToNodes = true, MaxLength = 120)]
    public string StarterLoadout { get; set; } = string.Empty;

    /// <summary>Distance from the host's ship at which a new avatar appears; the authority spreads the slots between this and twice this value.</summary>
    [Setting("Avatar spawn offset from the host ship (m)", Scope = SettingScope.Live, PushToNodes = true, Min = 50, Max = 5000)]
    public int SpawnOffsetMeters { get; set; } = DefaultSpawnOffsetMeters;

    /// <summary>
    /// The ship macro for a new avatar. M3 uses the one configured macro for everybody; <paramref name="teamId"/> and
    /// <paramref name="race"/> are the hooks for the per-faction/race choice (ADR-049/051) so nothing else has to change then.
    /// A blank setting falls back to <see cref="DefaultStarterShipMacro"/>.
    /// </summary>
    public string ResolveStarterShipMacro(int teamId = 0, string? race = null) =>
        string.IsNullOrWhiteSpace(StarterShipMacro) ? DefaultStarterShipMacro : StarterShipMacro.Trim();

    /// <summary>The loadout id for a new avatar ("" = the mod's basic early-game loadout). Same hooks and same single place as <see cref="ResolveStarterShipMacro"/>.</summary>
    public string ResolveStarterLoadout(int teamId = 0, string? race = null) => StarterLoadout?.Trim() ?? string.Empty;
}
