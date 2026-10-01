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

/// <summary>Replication tunables (server-design 2.9). Only the tick rate exists so far; more join with the replication task.</summary>
[SettingsSection("X4MP:Replication", "Replication")]
public sealed class ReplicationOptions
{
    [Setting("Replication tick rate (Hz)", Scope = SettingScope.Live, Min = 1, Max = 60, PushToNodes = true)]
    public int TickRateHz { get; set; } = 20;
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
