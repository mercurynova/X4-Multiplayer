namespace X4MP.Server.Api;

// DTOs of the admin SignalR hub (server-design 4.5 and 4.6). Same conventions as AdminDtos: strings for enums, seconds for durations.

/// <summary>Network totals of the last second (from the metrics sampler). Part of the dashboard push.</summary>
[TsContract]
public sealed record TrafficDto(
    double KBpsIn, double KBpsOut, double FramesInPerSec, double FramesOutPerSec, long DroppedRealtimeLast60s, int SlowConsumerKicksLastHour);

/// <summary>
/// An alert as the GUI shows it. <c>Active</c> is false for the "condition is over" notice (an <c>AlertCleared</c>); the alert
/// is identified by <c>Code</c>. <c>Severity</c> is <c>Info</c>, <c>Warning</c> or <c>Critical</c>.
/// </summary>
[TsContract]
public sealed record AlertDto(DateTimeOffset At, string Severity, string Code, string Text, bool Active);

/// <summary>A 3D position in metres.</summary>
[TsContract]
public sealed record Vec3Dto(double X, double Y, double Z);

/// <summary>Filter of the hub's log tail. <c>Level</c> is a minimum (Serilog level name); <c>Source</c> and <c>Q</c> are case-insensitive substrings.</summary>
[TsContract]
public sealed record LogFilterDto(string? Level, string? Source, string? Q);

/// <summary>A player ship on the galaxy map. <c>HeadingDeg</c> is the yaw in degrees.</summary>
[TsContract]
public sealed record GalaxyPlayerDto(long Id, string Name, long SectorId, Vec3Dto Pos, double HeadingDeg);

/// <summary>Ship and station counts of a sector from the authority's <c>GalaxySummary</c>.</summary>
[TsContract]
public sealed record SectorAggDto(long SectorId, int Ships, int Stations);

/// <summary>The sectors one player is subscribed to at one interest tier (<c>Near</c>, <c>Sector</c>, <c>Adjacent</c>, <c>Linger</c>).</summary>
[TsContract]
public sealed record InterestEntryDto(long PlayerId, List<long> SectorIds, string Tier);

/// <summary>The hub's 1 Hz <c>GalaxyFrame</c>.</summary>
[TsContract]
public sealed record GalaxyFrameDto(
    DateTimeOffset At, List<GalaxyPlayerDto> Players, List<SectorAggDto> SectorAgg, List<InterestEntryDto> Interest);

/// <summary>
/// The hub's 4 Hz <c>SectorFrame</c>: parallel arrays, one slot per entity. <c>X</c> and <c>Z</c> are metres rounded to 10 m, <c>Yaw</c> is
/// degrees, <c>Cls</c> is the <c>EntityKind</c> number, <c>Flags</c> the state flags, <c>PlayerIds</c> the controlling player or null.
/// </summary>
[TsContract]
public sealed record SectorFrameDto(
    long SectorId, long Tick, List<long> Ids, List<int> X, List<int> Z, List<int> Yaw, List<int> Cls, List<int> Flags, List<long?> PlayerIds);

/// <summary>A save transfer in progress; <c>Finished</c> is set on the last push of a transfer that ended (completed or aborted).</summary>
[TsContract]
public sealed record TransferProgressDto(
    long Id, bool IsUpload, long PlayerId, string PlayerName, string Sha256, string Kind, long Size, long Done, long StartOffset,
    DateTimeOffset StartedAt, bool Finished);

/// <summary>A client action the asset permission policy refused (rate limited on the hub).</summary>
[TsContract]
public sealed record PermissionDeniedDto(DateTimeOffset At, long PlayerId, long EntityId, string Action, string Reason);

/// <summary>Method and event names of the hub, so the GUI never spells them by hand. Generated into <c>generated.ts</c>.</summary>
[TsContract]
public static class AdminHubMethods
{
    public const string Route = "/hubs/admin";
    public const string SubscribeDashboard = nameof(SubscribeDashboard);
    public const string UnsubscribeDashboard = nameof(UnsubscribeDashboard);
    public const string SubscribeGalaxy = nameof(SubscribeGalaxy);
    public const string UnsubscribeGalaxy = nameof(UnsubscribeGalaxy);
    public const string SubscribeSector = nameof(SubscribeSector);
    public const string UnsubscribeSector = nameof(UnsubscribeSector);
    public const string SubscribeLogs = nameof(SubscribeLogs);
    public const string UnsubscribeLogs = nameof(UnsubscribeLogs);
    public const string SubscribeDiagnostics = nameof(SubscribeDiagnostics);
    public const string UnsubscribeDiagnostics = nameof(UnsubscribeDiagnostics);
    public const string SubscribeChat = nameof(SubscribeChat);
    public const string UnsubscribeChat = nameof(UnsubscribeChat);
    public const string SendChat = nameof(SendChat);
}

/// <summary>Names of the server-to-client calls (the methods of <c>IAdminClient</c>).</summary>
[TsContract]
public static class AdminHubEvents
{
    public const string Dashboard = nameof(Dashboard);
    public const string PlayerChanged = nameof(PlayerChanged);
    public const string PlayerRemoved = nameof(PlayerRemoved);
    public const string SessionChanged = nameof(SessionChanged);
    public const string GalaxyFrame = nameof(GalaxyFrame);
    public const string SectorFrame = nameof(SectorFrame);
    public const string LogBatch = nameof(LogBatch);
    public const string Diagnostics = nameof(Diagnostics);
    public const string Chat = nameof(Chat);
    public const string SaveTransfer = nameof(SaveTransfer);
    public const string Alert = nameof(Alert);
    public const string SettingsChanged = nameof(SettingsChanged);
    public const string PermissionDenied = nameof(PermissionDenied);
}
