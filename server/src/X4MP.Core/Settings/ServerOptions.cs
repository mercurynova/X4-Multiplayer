namespace X4MP.Core.Settings;

/// <summary>Who may see the mod lists of players (ADR-045, MM5).</summary>
public enum ModListVisibility
{
    AdminsOnly,
    AdminsAndViewers,
    AllPlayers,
}

/// <summary>Mod-management session settings (ADR-045). Example of a live, pushed setting.</summary>
[SettingsSection("X4MP:Mods", "Mods")]
public sealed class ModManagementOptions
{
    [Setting("Who can see players' mod lists", Scope = SettingScope.Live, PushToNodes = true)]
    public ModListVisibility ModListVisibility { get; set; } = ModListVisibility.AdminsOnly;
}

/// <summary>
/// Replication tunables (server-design 2.6 and 2.9, protocol.md 10). All Live: the replication module reads them through a provider
/// on every tick, so an admin edit applies at once. <see cref="TickRateHz"/> is also pushed to nodes.
/// </summary>
[SettingsSection("X4MP:Replication", "Replication")]
public sealed class ReplicationOptions
{
    public const int DefaultBandwidthBudgetKBps = 256;

    [Setting("Replication tick rate (Hz)", Scope = SettingScope.Live, Min = 1, Max = 60, PushToNodes = true)]
    public int TickRateHz { get; set; } = 20;

    /// <summary>Per client and second, in KB (1000 bytes) of Replication frames on the wire, headers included (default 256 = 12.8 KB per tick at 20 Hz).</summary>
    [Setting("Replication byte budget per client (KB/s)", Scope = SettingScope.Live, Min = 1, Max = 100000)]
    public int BandwidthBudgetKBps { get; set; } = DefaultBandwidthBudgetKBps;

    /// <summary>Largest Replication frame payload; 1100 keeps a frame under the UDP datagram budget (protocol.md 3.3) for M1-09.</summary>
    [Setting("Largest Replication frame payload (bytes)", Scope = SettingScope.Live, Min = 128, Max = 60000)]
    public int MaxFramePayloadBytes { get; set; } = 1100;

    /// <summary>Safety net: every entity of the Near and Sector tiers gets a full-mask entry at least this often.</summary>
    [Setting("Keyframe interval for Near and Sector entities (s)", Scope = SettingScope.Live, Min = 1, Max = 600)]
    public int KeyframeNearSectorSeconds { get; set; } = 5;

    /// <summary>The same for Adjacent and Linger entities.</summary>
    [Setting("Keyframe interval for Adjacent and Linger entities (s)", Scope = SettingScope.Live, Min = 1, Max = 600)]
    public int KeyframeAdjacentSeconds { get; set; } = 15;

    /// <summary>Desync guard: <c>InterestChecksum</c> to each client this often (0 = off).</summary>
    [Setting("Interest checksum interval (s, 0 = off)", Scope = SettingScope.Live, Min = 0, Max = 600)]
    public int ChecksumIntervalSeconds { get; set; } = 5;

    /// <summary>How long a despawned id stays tombstoned: no baseline is rebuilt from a frame that was in flight when it despawned.</summary>
    [Setting("Despawn tombstone (s)", Scope = SettingScope.Live, Min = 1, Max = 600)]
    public int TombstoneSeconds { get; set; } = 5;

    /// <summary>A newly spawned entity gets no state entry for this many replication ticks (its spawn goes first on the Control lane).</summary>
    [Setting("Spawn-before-state hold (ticks)", Scope = SettingScope.Live, Min = 0, Max = 100)]
    public int SpawnHoldTicks { get; set; } = 1;

    /// <summary>A frame not confirmed delivered (TCP flush or UDP ack) after this long counts as lost: its entities are re-sent in full.</summary>
    [Setting("Unconfirmed frame counts as lost after (ms)", Scope = SettingScope.Live, Min = 100, Max = 60000)]
    public int InFlightTimeoutMs { get; set; } = 1000;

    /// <summary>A client asking again sooner than this gets no second resync (a buggy client cannot make the server re-deliver forever).</summary>
    [Setting("Minimum interval between resyncs of one client (ms)", Scope = SettingScope.Live, Min = 0, Max = 600000)]
    public int ResyncMinIntervalMs { get; set; } = 2000;
}

/// <summary>Alert rule thresholds for the alert evaluator (server-design 2.7).</summary>
[SettingsSection("X4MP:Alerts", "Alerts")]
public sealed class AlertOptions
{
    [Setting("Raise an alert when the authority FPS stays below this", Scope = SettingScope.Live, Min = 1, Max = 240)]
    public int AuthorityFpsThreshold { get; set; } = 15;

    [Setting("... for this many seconds", Scope = SettingScope.Live, Min = 1, Max = 3600)]
    public int AuthorityFpsSeconds { get; set; } = 30;
}
