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

    /// <summary>Hops of the adjacent prefetch tier (0 = no prefetch).</summary>
    [Setting("Prefetch depth: gate/highway hops around the player's sector that are pre-warmed", Scope = SettingScope.Live, Min = 0, Max = SectorGraph.MaxHops)]
    public int PrefetchDepth { get; set; } = 1;

    /// <summary>How long a sector the player just left stays in interest (hysteresis against gate ping-pong).</summary>
    [Setting("Linger: keep the previous sector in interest for (s)", Scope = SettingScope.Live, Min = 0, Max = 600)]
    public int LingerSeconds { get; set; } = 20;

    /// <summary>A sector nobody needs stays in the capture set this long before it is dropped and its entities evicted.</summary>
    [Setting("Capture eviction: keep an unneeded sector captured for (s)", Scope = SettingScope.Live, Min = 0, Max = 3600)]
    public int CaptureEvictSeconds { get; set; } = 60;

    /// <summary>Ghosts a node materialises at most (ADR-042: 250; the in-game cost is rendering, about 5 ms per 250 inert ships).</summary>
    [Setting("Ghost budget: max ghosts per client", Scope = SettingScope.Live, Min = 10, Max = 100000)]
    public int MaxGhosts { get; set; } = 250;

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
