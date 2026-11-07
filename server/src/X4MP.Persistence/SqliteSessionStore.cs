using System.Globalization;
using Dapper;
using X4MP.Core.Session;
using X4MP.Proto;

namespace X4MP.Persistence;

/// <summary>
/// SQLite implementation of <see cref="ISessionStore"/> over the <c>sessions</c> and <c>session_players</c> tables of
/// migration 0001 (no schema change; <c>session_events</c> is filled by <see cref="SessionEventSink"/> from the event bus). The session row is created synchronously
/// once (the actor needs its id); everything else goes through the write-behind <see cref="PersistenceWriter"/>,
/// which keeps statements in order, so a <c>PlayerLeft</c> never overtakes its <c>PlayerJoined</c>. A full
/// queue drops the write instead of blocking the actor.
/// </summary>
public sealed class SqliteSessionStore(SqliteConnectionFactory factory, PersistenceWriter writer) : ISessionStore
{
    private static string Stamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    public ValueTask<long> BeginSessionAsync(string name, Guid sessionGuid, DateTimeOffset at, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = factory.Open();
        long id = connection.ExecuteScalar<long>(
            "INSERT INTO sessions (name, state, settings_json, created_at) VALUES (@name, 'Idle', @settings, @at); SELECT last_insert_rowid();",
            new { name, settings = $"{{\"sessionGuid\":\"{sessionGuid:D}\"}}", at = Stamp(at) });
        return ValueTask.FromResult(id);
    }

    public void RecordPhase(long sessionId, SessionPhase phase, int authorityPlayerId, DateTimeOffset at, string? reason) =>
        writer.TryEnqueue(
            """
            UPDATE sessions SET
              state = @state,
              authority_player_id = CASE WHEN @authority > 0 THEN @authority ELSE authority_player_id END,
              started_at = CASE WHEN started_at IS NULL AND @state <> 'Idle' THEN @at ELSE started_at END,
              ended_at = CASE WHEN @state = 'Ended' THEN @at WHEN @state = 'WaitingForAuthority' THEN NULL ELSE ended_at END,
              end_reason = CASE WHEN @state = 'Ended' THEN @reason WHEN @state = 'WaitingForAuthority' THEN NULL ELSE end_reason END
            WHERE id = @id
            """,
            new { id = sessionId, state = phase.ToString(), authority = authorityPlayerId, at = Stamp(at), reason });

    /// <summary>The end reason stored on sessions a previous server run left open (K9).</summary>
    public const string ServerRestartedReason = "server restarted";

    /// <summary>
    /// K9: at server start no session is live, so every row not Ended belongs to an earlier run (the process died or was stopped without ending it).
    /// They are marked Ended with <see cref="ServerRestartedReason"/>, and their players as left. Resume after a restart is not supported (ADR-052).
    /// Returns the number of sessions closed. Also frees the saves such a row kept pinned (the janitor protects the saves of every non-Ended session).
    /// </summary>
    public static int EndOpenSessions(SqliteConnectionFactory factory, DateTimeOffset at)
    {
        using var connection = factory.Open();
        string stamp = Stamp(at);
        connection.Execute(
            "UPDATE session_players SET left_at = @at, leave_reason = @reason WHERE left_at IS NULL AND session_id IN (SELECT id FROM sessions WHERE state <> 'Ended')",
            new { at = stamp, reason = ServerRestartedReason });
        return connection.Execute(
            "UPDATE sessions SET state = 'Ended', ended_at = @at, end_reason = @reason WHERE state <> 'Ended'",
            new { at = stamp, reason = ServerRestartedReason });
    }

    public void RenameSession(long sessionId, string name) =>
        writer.TryEnqueue("UPDATE sessions SET name = @name WHERE id = @id", new { id = sessionId, name });

    public void PlayerJoined(long sessionId, int playerId, Role roles, DateTimeOffset at) =>
        writer.TryEnqueue(
            "INSERT INTO session_players (session_id, player_id, joined_at, role) VALUES (@sessionId, @playerId, @at, @role)",
            new { sessionId, playerId, at = Stamp(at), role = roles.ToString() });

    public void PlayerLeft(long sessionId, int playerId, DateTimeOffset at, string reason) =>
        writer.TryEnqueue(
            "UPDATE session_players SET left_at = @at, leave_reason = @reason WHERE session_id = @sessionId AND player_id = @playerId AND left_at IS NULL",
            new { sessionId, playerId, at = Stamp(at), reason });
}
