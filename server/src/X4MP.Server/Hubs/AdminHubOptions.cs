namespace X4MP.Server.Hubs;

/// <summary>Tunables of the admin hub and its broadcaster (<c>X4MP:AdminHub</c>; server-design 4.6). The defaults are the documented rates.</summary>
public sealed class AdminHubOptions
{
    public const string SectionName = "X4MP:AdminHub";

    /// <summary>Pushes the dashboard snapshot this often to the dashboard topic.</summary>
    public int DashboardIntervalMs { get; set; } = 1000;

    public int GalaxyIntervalMs { get; set; } = 1000;

    /// <summary>The sector map rate: 4 Hz.</summary>
    public int SectorIntervalMs { get; set; } = 250;

    public int DiagnosticsIntervalMs { get; set; } = 1000;

    /// <summary>Log lines are batched for this long (at most <see cref="LogBatchMax"/> per batch).</summary>
    public int LogBatchIntervalMs { get; set; } = 250;

    public int LogBatchMax { get; set; } = 200;

    /// <summary>Transfer progress rate while a transfer is active: 2 Hz.</summary>
    public int TransferIntervalMs { get; set; } = 500;

    /// <summary>Entities in one <c>SectorFrame</c>; players and stations are always included.</summary>
    public int MapMaxEntities { get; set; } = 3000;

    /// <summary>Sector views one admin connection may hold.</summary>
    public int MaxSectorsPerAdmin { get; set; } = 2;

    /// <summary>Wallet changes are collected for this long and sent once per wallet: 4 Hz.</summary>
    public int EconomyWalletIntervalMs { get; set; } = 250;

    /// <summary>The economy overview rate while the economy topic has members: 1 Hz.</summary>
    public int EconomySummaryIntervalMs { get; set; } = 1000;

    /// <summary><c>EconomyEvent</c> pushes per second (the rest are dropped; they stay in the event log).</summary>
    public int EconomyEventsPerSecond { get; set; } = 10;

    /// <summary>Sends waiting per connection. When full the oldest event is dropped.</summary>
    public int ClientQueueCapacity { get; set; } = 256;

    /// <summary>A send that does not finish in this long marks the browser as stalled and its connection is closed.</summary>
    public int StallSeconds { get; set; } = 15;

    /// <summary><c>PermissionDenied</c> pushes per second (the rest are dropped).</summary>
    public int PermissionDeniedPerSecond { get; set; } = 5;

    /// <summary>Log lines sent as backfill when a client subscribes to the log tail.</summary>
    public int LogBackfill { get; set; } = 500;
}
