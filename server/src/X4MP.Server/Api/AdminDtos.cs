using System.Text.Json;

namespace X4MP.Server.Api;

// DTOs of the admin REST API (server-design 4.5). Shapes follow the design; where the design uses a C# enum or TimeSpan, these use a
// string or a number of seconds so the TypeScript contract stays plain. Timestamps are ISO-8601 strings on the wire.

/// <summary>Response of <c>GET /api/v1/server</c>.</summary>
[TsContract]
public sealed record ServerInfoDto(
    string Name, string Version, ProtocolRangeDto ProtocolRange, string BuildHash, DateTimeOffset StartedAt, long UptimeSeconds,
    List<string> NodeEndpoints, List<string> AdminUrls, bool Https);

/// <summary>One session as the lists show it.</summary>
[TsContract]
public sealed record SessionSummaryDto(
    long Id, string Name, string State, string? SaveName, string? SaveSha256, DateTimeOffset? StartedAt, long UptimeSeconds, int Players);

/// <summary>The authority node of the live session. <c>Status</c> is <c>None</c>, <c>Live</c> or <c>Lost</c>.</summary>
[TsContract]
public sealed record AuthorityStatusDto(
    long PlayerId, string? Name, string Status, double? GraceRemainingSeconds, string? GameBuild, string? ModVersion);

/// <summary>A node of the live session (<c>Phase</c> is the NodePhase name, <c>Roles</c> the Role flags as text).</summary>
[TsContract]
public sealed record PlayerLiveDto(
    long PlayerId, long? ConnectionId, string Name, string Roles, string Phase, bool Connected, string? RemoteAddress, double RttMs,
    double Fps, long ConnectedSeconds, bool Muted, long? TeamId = null, string? TeamName = null);

/// <summary>
/// Response of <c>GET /api/v1/dashboard</c> and the hub's 1 Hz <c>Dashboard</c> push (server-design 4.5). <c>TickP99Ms</c> is 0 until the
/// session actor measures its tick time.
/// </summary>
[TsContract]
public sealed record DashboardSnapshotDto(
    DateTimeOffset At, SessionSummaryDto? Session, int PlayersOnline, int MaxPlayers, AuthorityStatusDto? Authority,
    List<PlayerLiveDto> Players, int EntitiesInMirror, TrafficDto Traffic, int SectorsCaptured, double TickP99Ms,
    List<AlertDto> ActiveAlerts);

/// <summary>Response of <c>GET /api/v1/sessions/current</c> and <c>POST /api/v1/sessions</c>. <c>Live</c> is true for the session the server is running now.</summary>
[TsContract]
public sealed record SessionDetailDto(
    long Id, string Name, string State, string? SaveName, string? SaveSha256, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt, string? EndReason, long UptimeSeconds, int Players, bool Live, DateTimeOffset? PhaseSince,
    AuthorityStatusDto? Authority, List<PlayerLiveDto> Nodes);

/// <summary>
/// Body of <c>POST /api/v1/sessions</c>. <c>SaveId</c> is the stored save's SHA-256 (the saves API identifies a save by it).
/// <c>Settings</c> is a <c>PATCH /settings</c> body, validated the same way.
/// </summary>
[TsContract]
public sealed record CreateSessionRequest(string? Name, string? SaveId, Dictionary<string, JsonElement>? Settings);

/// <summary>Body of <c>POST /api/v1/sessions/{id}/start</c>.</summary>
[TsContract]
public sealed record StartSessionRequest(long? AuthorityPlayerId);

/// <summary>Body of <c>POST /api/v1/sessions/{id}/stop</c>. <c>RequestFinalSave</c> defaults to true.</summary>
[TsContract]
public sealed record StopSessionRequest(bool? RequestFinalSave, string? Message);

/// <summary>One row of <c>session_events</c>; <c>Data</c> is the event's JSON text.</summary>
[TsContract]
public sealed record SessionEventDto(long Id, DateTimeOffset At, string Type, long? PlayerId, long? SectorId, long? ServerSeq, string? Data);

/// <summary>A player known to the server.</summary>
[TsContract]
public sealed record PlayerDto(
    long Id, string Name, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, long TotalPlaytimeSeconds, bool Online, bool Muted,
    DateTimeOffset? MutedUntil, BanDto? ActiveBan, string? LastIp, string? Notes, long? TeamId = null, string? TeamName = null);

/// <summary>One session a player joined.</summary>
[TsContract]
public sealed record PlayerSessionDto(
    long SessionId, string SessionName, DateTimeOffset JoinedAt, DateTimeOffset? LeftAt, string? LeaveReason, string Role);

/// <summary>Response of <c>GET /api/v1/players/{id}</c>: the player, its key hash (hex SHA-256, never the key), live state, history and bans.</summary>
[TsContract]
public sealed record PlayerDetailDto(
    PlayerDto Player, string KeyHash, PlayerLiveDto? Live, List<PlayerSessionDto> History, List<BanDto> Bans);

/// <summary>Body of <c>POST /api/v1/players/{id}/kick</c>.</summary>
[TsContract]
public sealed record KickRequest(string? Reason);

/// <summary>Body of <c>POST /api/v1/players/{id}/mute</c>; no <c>Minutes</c> = until lifted.</summary>
[TsContract]
public sealed record MuteRequest(int? Minutes, string? Reason);

/// <summary>Body of <c>PATCH /api/v1/players/{id}</c>. <c>Notes</c> of "" clears them. <c>ReleaseName</c> frees the player's name for another key.</summary>
[TsContract]
public sealed record PatchPlayerRequest(string? Notes, bool? ReleaseName);

/// <summary>A ban: of a player's key hash (<c>PlayerId</c>), of an address or network (<c>IpCidr</c>), or both (either matches).</summary>
[TsContract]
public sealed record BanDto(
    long Id, long? PlayerId, string? PlayerName, string? IpCidr, string Reason, string CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt, bool Active);

/// <summary>
/// Body of <c>POST /api/v1/bans</c>: at least one of <c>PlayerId</c>, <c>KeyHash</c> (hex SHA-256 of a known player's key) and
/// <c>IpCidr</c> (an address or CIDR block). No <c>DurationMinutes</c> = permanent.
/// </summary>
[TsContract]
public sealed record CreateBanRequest(long? PlayerId, string? KeyHash, string? IpCidr, string? Reason, int? DurationMinutes);

/// <summary>One chat line from the history.</summary>
[TsContract]
public sealed record ChatMessageDto(long Id, DateTimeOffset At, string From, bool FromAdmin, string Channel, string Text);

/// <summary>Body of <c>POST /api/v1/chat</c>: <c>Channel</c> is <c>all</c> or <c>player</c> (then <c>ToPlayerId</c> is required).</summary>
[TsContract]
public sealed record SendChatRequest(string? Text, string? Channel, long? ToPlayerId, bool? AsBroadcast);

/// <summary>Response of <c>POST /api/v1/chat</c>: how many connected nodes the message was queued for.</summary>
[TsContract]
public sealed record ChatSentDto(int Delivered);

/// <summary>One log line from the in-memory ring buffer (newest <c>Seq</c> last).</summary>
[TsContract]
public sealed record LogEntryDto(
    long Seq, DateTimeOffset At, string Level, string Source, string Message, string? Exception, Dictionary<string, string>? Props);

/// <summary>Counters of one connection lane.</summary>
[TsContract]
public sealed record LaneStatsDto(long BytesIn, long BytesOut, long Dropped, long Coalesced, long MaxQueuedBytes);

/// <summary>Per-connection counters (cumulative; rates are the difference of two reads).</summary>
[TsContract]
public sealed record ConnectionStatsDto(
    long ConnectionId, string? Player, string Roles, string Transport, string Remote, long AgeSeconds, double RttMs, long BytesIn,
    long BytesOut, long FramesIn, long FramesOut, long Coalesced, long Dropped, long Violations, long InboundDropped, double FlushAvgMs,
    double FlushMaxMs, LaneStatsDto Control, LaneStatsDto Realtime, LaneStatsDto Bulk);

/// <summary>Body of <c>POST /api/v1/diagnostics/connections/{id}/trace</c>.</summary>
[TsContract]
public sealed record TraceRequest(bool Enabled, int SampleEvery);

/// <summary>An API token (the token itself is shown once, at creation).</summary>
[TsContract]
public sealed record ApiTokenDto(long Id, string Name, string Role, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool Revoked, string? Owner);

/// <summary>Body of <c>POST /api/v1/tokens</c>.</summary>
[TsContract]
public sealed record CreateTokenRequest(string? Name, string? Role);

/// <summary>Response of <c>POST /api/v1/tokens</c>: the only time <c>Token</c> is ever returned.</summary>
[TsContract]
public sealed record ApiTokenCreatedDto(long Id, string Name, string Role, string Token);

/// <summary>One audit row. <c>Reason</c> is lifted out of <c>Data</c> for convenience.</summary>
[TsContract]
public sealed record AuditEntryDto(
    long Id, DateTimeOffset At, string Actor, string Action, string? Target, string? Reason, Dictionary<string, string>? Data, string? RemoteIp);

/// <summary>A sector cluster of the galaxy.</summary>
[TsContract]
public sealed record ClusterDto(long Id, string Macro);

/// <summary>A 2D map position.</summary>
[TsContract]
public sealed record Vec2Dto(double X, double Y);

/// <summary>A sector. <c>Id</c> is the wire sector index; <c>ClusterId</c> indexes <see cref="GalaxyDto.Clusters"/>.</summary>
[TsContract]
public sealed record SectorDto(long Id, string Macro, string Name, long ClusterId, Vec2Dto MapPos, string? OwnerFaction);

/// <summary>A connection between two sectors (<c>Kind</c>: <c>gate</c>, <c>highway</c> or <c>accelerator</c>).</summary>
[TsContract]
public sealed record GateLinkDto(long FromSector, long ToSector, string Kind);

/// <summary>Response of <c>GET /api/v1/galaxy</c>.</summary>
[TsContract]
public sealed record GalaxyDto(string SaveSha256, List<ClusterDto> Clusters, List<SectorDto> Sectors, List<GateLinkDto> Links);

/// <summary>A player ship inside a sector.</summary>
[TsContract]
public sealed record SectorPlayerDto(long PlayerId, string Name, long ShipNetId);

/// <summary>Response of <c>GET /api/v1/galaxy/sectors/{id}</c>. The ship and station counts come from the authority's <c>GalaxySummary</c> (null before the first).</summary>
[TsContract]
public sealed record SectorDetailDto(
    SectorDto Sector, List<long> Neighbors, int? Ships, int? Stations, List<SectorPlayerDto> Players);
