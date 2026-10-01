using System.Globalization;
using Dapper;

namespace X4MP.Persistence;

/// <summary>A <c>players</c> row.</summary>
public sealed record PlayerRecord(
    long Id, string Name, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, long TotalSeconds, string? LastIp, string? Notes,
    bool Muted, DateTimeOffset? MuteUntil, string KeyHashHex);

/// <summary>A <c>bans</c> row (player_id set = a ban of that player's key hash; ip_cidr set = a network ban; both = either matches).</summary>
public sealed record BanRecord(
    long Id, long? PlayerId, string? PlayerName, string? IpCidr, string Reason, string CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt)
{
    public bool IsActive(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}

/// <summary>A <c>sessions</c> row with the save names and the number of distinct players that joined.</summary>
public sealed record SessionRecord(
    long Id, string Name, string State, string? SaveSha256, string? SaveName, long? AuthorityPlayerId, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string? EndReason, int Players);

/// <summary>A <c>session_events</c> row.</summary>
public sealed record SessionEventRecord(long Id, long SessionId, DateTimeOffset At, long? ServerSeq, string Type, long? PlayerId, long? SectorId, string? DataJson);

/// <summary>One session a player took part in.</summary>
public sealed record PlayerSessionRecord(long SessionId, string SessionName, DateTimeOffset JoinedAt, DateTimeOffset? LeftAt, string? LeaveReason, string Role);

/// <summary>A <c>chat_messages</c> row.</summary>
public sealed record ChatRecord(long Id, DateTimeOffset At, long? SessionId, long? FromPlayerId, string From, bool FromAdmin, string Channel, string Text);

/// <summary>An <c>audit_log</c> row.</summary>
public sealed record AuditRecord(long Id, DateTimeOffset At, string Actor, string Action, string? Target, string? DataJson, string? RemoteIp);

/// <summary>
/// Read and admin-write queries behind the admin REST API (players, bans, sessions, session events, chat history, audit).
/// Synchronous short-lived connections: admin traffic is tiny, and a ban or note must be durable before the response is sent
/// (so these do not go through the write-behind <see cref="PersistenceWriter"/>). Timestamps use the same text format as the
/// rest of the schema, so the ban lookup of <see cref="SqliteNodeStore"/> compares them correctly.
/// </summary>
public sealed class SqliteAdminQueries(SqliteConnectionFactory factory)
{
    private static string Stamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static DateTimeOffset? ParseOrNull(string? text) => text is null ? null : Parse(text);

    // ------------------------------------------------------------------ players

    private sealed class PlayerRow
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string FirstSeen { get; set; } = string.Empty;

        public string LastSeen { get; set; } = string.Empty;

        public long TotalSeconds { get; set; }

        public string? LastIp { get; set; }

        public string? Notes { get; set; }

        public long IsMuted { get; set; }

        public string? MuteUntil { get; set; }

        public byte[] KeyHash { get; set; } = [];

        public PlayerRecord ToRecord(DateTimeOffset now)
        {
            DateTimeOffset? until = ParseOrNull(MuteUntil);
            bool muted = IsMuted != 0 && (until is null || until > now);
            return new PlayerRecord(
                Id, Name, Parse(FirstSeen), Parse(LastSeen), TotalSeconds, LastIp, Notes, muted, muted ? until : null, Convert.ToHexStringLower(KeyHash));
        }
    }

    private const string PlayerSelect =
        "SELECT id AS Id, name AS Name, first_seen AS FirstSeen, last_seen AS LastSeen, total_seconds AS TotalSeconds, last_ip AS LastIp, " +
        "notes AS Notes, is_muted AS IsMuted, mute_until AS MuteUntil, key_hash AS KeyHash FROM players";

    /// <summary>Players by name (substring, case-insensitive) and/or a fixed id set, newest activity first.</summary>
    public IReadOnlyList<PlayerRecord> ListPlayers(string? nameContains, IReadOnlyCollection<long>? onlyIds, int limit, DateTimeOffset now)
    {
        using var db = factory.Open();
        var sql = PlayerSelect + " WHERE 1 = 1";
        var parameters = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            sql += " AND name LIKE @q ESCAPE '\\'";
            parameters.Add("q", "%" + nameContains.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%");
        }

        if (onlyIds is not null)
        {
            if (onlyIds.Count == 0)
            {
                return [];
            }

            sql += " AND id IN @ids";
            parameters.Add("ids", onlyIds.ToArray());
        }

        sql += " ORDER BY last_seen DESC, id DESC LIMIT @limit";
        parameters.Add("limit", limit);
        return [.. db.Query<PlayerRow>(sql, parameters).Select(r => r.ToRecord(now))];
    }

    public PlayerRecord? FindPlayer(long id, DateTimeOffset now)
    {
        using var db = factory.Open();
        return db.QuerySingleOrDefault<PlayerRow>(PlayerSelect + " WHERE id = @id", new { id })?.ToRecord(now);
    }

    public PlayerRecord? FindPlayerByKeyHash(byte[] keyHash, DateTimeOffset now)
    {
        using var db = factory.Open();
        return db.QueryFirstOrDefault<PlayerRow>(PlayerSelect + " WHERE key_hash = @keyHash", new { keyHash })?.ToRecord(now);
    }

    /// <summary>Sets (or clears, with null) the admin notes. False when the player does not exist.</summary>
    public bool SetNotes(long id, string? notes)
    {
        using var db = factory.Open();
        return db.Execute("UPDATE players SET notes = @notes WHERE id = @id", new { id, notes }) > 0;
    }

    /// <summary>Renames the player to a placeholder so its name is free for another key (the row, history and bans stay). Returns the placeholder.</summary>
    public string? ReleaseName(long id)
    {
        using var db = factory.Open();
        string placeholder = "released-" + id.ToString(CultureInfo.InvariantCulture);
        return db.Execute("UPDATE players SET name = @placeholder WHERE id = @id", new { id, placeholder }) > 0 ? placeholder : null;
    }

    public IReadOnlyList<PlayerSessionRecord> PlayerHistory(long playerId, int limit)
    {
        using var db = factory.Open();
        return
        [
            .. db.Query<(long SessionId, string SessionName, string JoinedAt, string? LeftAt, string? LeaveReason, string Role)>(
                """
                SELECT sp.session_id, s.name, sp.joined_at, sp.left_at, sp.leave_reason, sp.role
                FROM session_players sp JOIN sessions s ON s.id = sp.session_id
                WHERE sp.player_id = @playerId ORDER BY sp.joined_at DESC LIMIT @limit
                """,
                new { playerId, limit })
                .Select(r => new PlayerSessionRecord(r.SessionId, r.SessionName, Parse(r.JoinedAt), ParseOrNull(r.LeftAt), r.LeaveReason, r.Role)),
        ];
    }

    // ------------------------------------------------------------------ bans

    private sealed class BanRow
    {
        public long Id { get; set; }

        public long? PlayerId { get; set; }

        public string? PlayerName { get; set; }

        public string? IpCidr { get; set; }

        public string Reason { get; set; } = string.Empty;

        public string CreatedBy { get; set; } = string.Empty;

        public string CreatedAt { get; set; } = string.Empty;

        public string? ExpiresAt { get; set; }

        public string? RevokedAt { get; set; }

        public BanRecord ToRecord() => new(Id, PlayerId, PlayerName, IpCidr, Reason, CreatedBy, Parse(CreatedAt), ParseOrNull(ExpiresAt), ParseOrNull(RevokedAt));
    }

    private const string BanSelect =
        "SELECT b.id AS Id, b.player_id AS PlayerId, p.name AS PlayerName, b.ip_cidr AS IpCidr, b.reason AS Reason, b.created_by AS CreatedBy, " +
        "b.created_at AS CreatedAt, b.expires_at AS ExpiresAt, b.revoked_at AS RevokedAt FROM bans b LEFT JOIN players p ON p.id = b.player_id";

    /// <summary>Every ban, newest first; <paramref name="activeOnly"/> keeps the ones that are neither revoked nor expired.</summary>
    public IReadOnlyList<BanRecord> ListBans(bool? activeOnly, DateTimeOffset now, int limit = 1000)
    {
        using var db = factory.Open();
        var all = db.Query<BanRow>(BanSelect + " ORDER BY b.id DESC LIMIT @limit", new { limit }).Select(r => r.ToRecord());
        return [.. activeOnly is { } only ? all.Where(b => b.IsActive(now) == only) : all];
    }

    public BanRecord? FindBan(long id)
    {
        using var db = factory.Open();
        return db.QuerySingleOrDefault<BanRow>(BanSelect + " WHERE b.id = @id", new { id })?.ToRecord();
    }

    public long InsertBan(long? playerId, string? ipCidr, string reason, string createdBy, DateTimeOffset now, DateTimeOffset? expires)
    {
        using var db = factory.Open();
        return db.ExecuteScalar<long>(
            "INSERT INTO bans (player_id, ip_cidr, reason, created_by, created_at, expires_at) VALUES (@playerId, @ipCidr, @reason, @createdBy, @created, @expires); SELECT last_insert_rowid();",
            new { playerId, ipCidr, reason, createdBy, created = Stamp(now), expires = expires is { } e ? Stamp(e) : null });
    }

    /// <summary>Revokes an active ban. False when it does not exist or was already revoked.</summary>
    public bool RevokeBan(long id, DateTimeOffset now)
    {
        using var db = factory.Open();
        return db.Execute("UPDATE bans SET revoked_at = @at WHERE id = @id AND revoked_at IS NULL", new { id, at = Stamp(now) }) > 0;
    }

    // ------------------------------------------------------------------ sessions

    private sealed class SessionRow
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public string? SaveSha256 { get; set; }

        public string? SaveName { get; set; }

        public long? AuthorityPlayerId { get; set; }

        public string CreatedAt { get; set; } = string.Empty;

        public string? StartedAt { get; set; }

        public string? EndedAt { get; set; }

        public string? EndReason { get; set; }

        public long Players { get; set; }

        public SessionRecord ToRecord() => new(
            Id, Name, State, SaveSha256, SaveName, AuthorityPlayerId, Parse(CreatedAt), ParseOrNull(StartedAt), ParseOrNull(EndedAt), EndReason, (int)Players);
    }

    private const string SessionSelect =
        """
        SELECT s.id AS Id, s.name AS Name, s.state AS State, sv.sha256 AS SaveSha256, sv.display_name AS SaveName,
               s.authority_player_id AS AuthorityPlayerId, s.created_at AS CreatedAt, s.started_at AS StartedAt,
               s.ended_at AS EndedAt, s.end_reason AS EndReason,
               (SELECT COUNT(DISTINCT player_id) FROM session_players WHERE session_id = s.id) AS Players
        FROM sessions s LEFT JOIN saves sv ON sv.id = COALESCE(s.current_save_id, s.save_id)
        """;

    public IReadOnlyList<SessionRecord> ListSessions(string? state, int limit)
    {
        using var db = factory.Open();
        var sql = SessionSelect + (state is null ? string.Empty : " WHERE s.state = @state COLLATE NOCASE") + " ORDER BY s.id DESC LIMIT @limit";
        return [.. db.Query<SessionRow>(sql, new { state, limit }).Select(r => r.ToRecord())];
    }

    public SessionRecord? FindSession(long id)
    {
        using var db = factory.Open();
        return db.QuerySingleOrDefault<SessionRow>(SessionSelect + " WHERE s.id = @id", new { id })?.ToRecord();
    }

    public IReadOnlyList<SessionEventRecord> SessionEvents(long sessionId, string? type, DateTimeOffset? since, int limit)
    {
        using var db = factory.Open();
        var sql = "SELECT id, session_id, ts, server_seq, type, player_id, sector_id, data_json FROM session_events WHERE session_id = @sessionId";
        if (type is not null)
        {
            sql += " AND type = @type COLLATE NOCASE";
        }

        if (since is not null)
        {
            sql += " AND ts > @since";
        }

        sql += " ORDER BY id DESC LIMIT @limit";
        var rows = db.Query<(long Id, long SessionId, string Ts, long? ServerSeq, string Type, long? PlayerId, long? SectorId, string? DataJson)>(
            sql, new { sessionId, type, since = since is { } s ? Stamp(s) : null, limit });
        return [.. rows.Select(r => new SessionEventRecord(r.Id, r.SessionId, Parse(r.Ts), r.ServerSeq, r.Type, r.PlayerId, r.SectorId, r.DataJson)).Reverse()];
    }

    // ------------------------------------------------------------------ chat and audit

    /// <summary>The newest <paramref name="limit"/> chat lines with id below <paramref name="beforeId"/> (all when null), oldest first.</summary>
    public IReadOnlyList<ChatRecord> ChatHistory(long? sessionId, long? beforeId, int limit)
    {
        using var db = factory.Open();
        var sql =
            """
            SELECT c.id, c.ts, c.session_id, c.from_player_id, COALESCE(c.from_admin, p.name, 'server') AS from_name,
                   CASE WHEN c.from_admin IS NOT NULL THEN 1 ELSE 0 END AS from_admin, c.channel, c.text
            FROM chat_messages c LEFT JOIN players p ON p.id = c.from_player_id WHERE 1 = 1
            """;
        if (sessionId is not null)
        {
            sql += " AND c.session_id = @sessionId";
        }

        if (beforeId is not null)
        {
            sql += " AND c.id < @beforeId";
        }

        sql += " ORDER BY c.id DESC LIMIT @limit";
        var rows = db.Query<(long Id, string Ts, long? SessionId, long? FromPlayerId, string FromName, long FromAdmin, string Channel, string Text)>(
            sql, new { sessionId, beforeId, limit });
        return [.. rows.Select(r => new ChatRecord(r.Id, Parse(r.Ts), r.SessionId, r.FromPlayerId, r.FromName, r.FromAdmin != 0, r.Channel, r.Text)).Reverse()];
    }

    /// <summary>The newest audit rows first.</summary>
    public IReadOnlyList<AuditRecord> AuditEntries(int limit)
    {
        using var db = factory.Open();
        return
        [
            .. db.Query<(long Id, string Ts, string Actor, string Action, string? Target, string? DataJson, string? RemoteIp)>(
                "SELECT id, ts, actor, action, target, data_json, remote_ip FROM audit_log ORDER BY id DESC LIMIT @limit", new { limit })
                .Select(r => new AuditRecord(r.Id, Parse(r.Ts), r.Actor, r.Action, r.Target, r.DataJson, r.RemoteIp)),
        ];
    }
}
