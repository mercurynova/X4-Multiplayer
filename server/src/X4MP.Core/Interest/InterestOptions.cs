using X4MP.Core.Settings;
using X4MP.Core.World;
using X4MP.Proto;

namespace X4MP.Core.Interest;

/// <summary>
/// Interest-management tunables (server-design 2.5, protocol.md 12; ADR-042 for the ghost budget). All Live: the manager reads them
/// through a provider on every use, so an admin edit applies on the next tick.
/// </summary>
[SettingsSection(SectionName, "Interest")]
public sealed class InterestOptions
{
    public const string SectionName = "X4MP:Interest";

    /// <summary>Radius of the Near tier and of the capture focus sphere (metres).</summary>
    [Setting("Near radius: full-rate tier around each player ship (m)", Scope = SettingScope.Live, Min = 500, Max = 200000)]
    public int NearRadiusM { get; set; } = 15_000;

    /// <summary>
    /// Leave radius of the Near tier (metres): an entity that is already Near stays Near until it is farther than this, so an object
    /// hovering on the edge of <see cref="NearRadiusM"/> does not flip between Near and its sector tier every tick (M4-24). The
    /// dead-band is therefore outside the enter radius; a value at or below <see cref="NearRadiusM"/> (0 included) means "no
    /// dead-band".
    /// </summary>
    [Setting("Near leave radius: hysteresis dead-band for the Near tier (m, 0 = off)", Scope = SettingScope.Live, Min = 0, Max = 200000)]
    public int NearLeaveRadiusM { get; set; } = 16_500;

    /// <summary>Hops of the adjacent prefetch tier (0 = no prefetch).</summary>
    [Setting("Prefetch depth: gate/highway hops around the player's sector that are pre-warmed", Scope = SettingScope.Live, Min = 0, Max = SectorGraph.MaxHops)]
    public int PrefetchDepth { get; set; } = 1;

    /// <summary>How long a sector the player just left stays in interest (hysteresis against gate ping-pong).</summary>
    [Setting("Linger: keep the previous sector in interest for (s)", Scope = SettingScope.Live, Min = 0, Max = 600)]
    public int LingerSeconds { get; set; } = 20;

    /// <summary>
    /// How long a ghost that is no longer admitted is kept before it is despawned (M14). An entity sitting on an admission
    /// boundary - the Near edge, the budget level flipping between ticks - would otherwise be spawned and despawned on
    /// every change, which the client draws as ships appearing and disappearing on the map. 0 keeps the old immediate
    /// behaviour. Hard-cap enforcement and a sector leaving interest still despawn at once: those are deliberate cuts.
    /// </summary>
    [Setting("Interest despawn grace: keep an unadmitted ghost for (s, 0 = off)", Scope = SettingScope.Live, Min = 0, Max = 120)]
    public int DespawnGraceSeconds { get; set; } = 5;

    /// <summary>A sector nobody needs stays in the capture set this long before it is dropped and its entities evicted.</summary>
    [Setting("Capture eviction: keep an unneeded sector captured for (s)", Scope = SettingScope.Live, Min = 0, Max = 3600)]
    public int CaptureEvictSeconds { get; set; } = 60;

    /// <summary>
    /// Ghosts a node materialises at most (ADR-042: 500). The cost is rendering, not repositioning: V03 measured ~15.4 ms/frame
    /// at 250 inert ships and ~25.2 ms at 500 on the test PC. 500 is the default so a busy current sector is not visibly
    /// truncated; raise it only after measuring client FPS.
    /// </summary>
    [Setting("Ghost budget: max ghosts per client", Scope = SettingScope.Live, Min = 10, Max = 100000, PushToNodes = true)]
    public int MaxGhosts { get; set; } = 500;

    /// <summary>Lower bound between two <c>CaptureSet</c> messages to the authority.</summary>
    [Setting("Capture set: minimum interval between updates to the authority (ms)", Scope = SettingScope.Live, Min = 100, Max = 10000)]
    public int CaptureSetMinIntervalMs { get; set; } = 500;

    [Setting("Capture rate of the Near focus sphere (Hz)", Scope = SettingScope.Live, Min = 1, Max = 60)]
    public int NearRateHz { get; set; } = 20;

    [Setting("Capture rate of a player's current sector (Hz)", Scope = SettingScope.Live, Min = 1, Max = 60)]
    public int SectorRateHz { get; set; } = 5;

    [Setting("Capture rate of Adjacent, Linger and admin-view sectors (Hz)", Scope = SettingScope.Live, Min = 1, Max = 60)]
    public int AdjacentRateHz { get; set; } = 1;

    /// <summary>Entities per <c>EntitySpawn</c> frame when a sector is delivered.</summary>
    [Setting("Entities per spawn frame when a sector is delivered", Scope = SettingScope.Live, Min = 1, Max = 2000)]
    public int SpawnBatchEntities { get; set; } = 100;

    /// <summary>Spawn frames per client and tick (spreads big sectors over ticks so the control lane is not flooded).</summary>
    [Setting("Spawn frames per client per tick", Scope = SettingScope.Live, Min = 1, Max = 1000)]
    public int SpawnFramesPerTick { get; set; } = 8;

    /// <summary>Act on <c>InterestHint</c> from clients that negotiated the capability.</summary>
    [Setting("Honor InterestHint prefetch hints", Scope = SettingScope.Live)]
    public bool HonorInterestHints { get; set; } = true;

    /// <summary>Longest a hint prefetches a non-adjacent sector.</summary>
    [Setting("InterestHint: longest prefetch of a hinted sector (s)", Scope = SettingScope.Live, Min = 1, Max = 600)]
    public int HintMaxSeconds { get; set; } = 60;

    public int RateHz(InterestTier tier) => tier switch
    {
        InterestTier.Near => NearRateHz,
        InterestTier.Sector => SectorRateHz,
        InterestTier.Adjacent or InterestTier.Linger => AdjacentRateHz,
        _ => 0,
    };
}
