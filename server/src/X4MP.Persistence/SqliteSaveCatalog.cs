using System.Globalization;
using System.Text.Json;
using Dapper;
using X4MP.Core.Saves;

namespace X4MP.Persistence;

/// <summary>
/// SQLite implementation of <see cref="ISaveCatalog"/> over <c>saves</c>, <c>checkpoints</c> and <c>sessions.save_id/current_save_id</c>
/// (migrations 0001 and 0004). Writes go through the write-behind <see cref="PersistenceWriter"/> and resolve ids with sub-selects, so
/// they stay ordered without waiting for the database; reads open a connection.
/// </summary>
public sealed class SqliteSaveCatalog(SqliteConnectionFactory factory, PersistenceWriter writer) : ISaveCatalog
{
    private sealed record Row(
        string Sha256, long SizeBytes, string DisplayName, string? OriginalFileName, string Source, string? UploadedBy, string UploadedAt,
        string? GameVersion, string? SaveTime, string? PlayerName, string? MetaJson, long Pinned, long GhostsCleaned);

    private const string Columns =
        "sha256 AS Sha256, size_bytes AS SizeBytes, display_name AS DisplayName, original_file_name AS OriginalFileName, source AS Source, " +
        "uploaded_by AS UploadedBy, uploaded_at AS UploadedAt, game_version AS GameVersion, save_time AS SaveTime, player_name AS PlayerName, " +
        "meta_json AS MetaJson, pinned AS Pinned, ghosts_cleaned AS GhostsCleaned";

    private static string Stamp(DateTimeOffset value) => SaveStamp.Format(value);

    public void AddSave(SaveRecord save) =>
        writer.TryEnqueue(
            """
            INSERT OR IGNORE INTO saves
              (sha256, size_bytes, display_name, original_file_name, source, uploaded_by, uploaded_at, game_version, save_time, player_name, meta_json, pinned, ghosts_cleaned)
            VALUES (@sha, @size, @name, @original, @source, @by, @at, @version, @time, @player, @meta, @pinned, @cleaned)
            """,
            new
            {
                sha = save.Sha256,
                size = save.SizeBytes,
                name = save.DisplayName,
                original = save.OriginalFileName,
                source = save.Source,
                by = save.UploadedBy,
                at = Stamp(save.UploadedAt),
                version = save.Meta.GameVersion,
                time = save.Meta.SaveTime,
                player = save.Meta.PlayerName,
                meta = save.Meta.PlayerMoney is { } money ? JsonSerializer.Serialize(new { playerMoney = money }) : null,
                pinned = save.Pinned ? 1 : 0,
                cleaned = save.GhostsCleaned ? 1 : 0,
            });

    public SaveRecord? Find(string sha256)
    {
        using var connection = factory.Open();
        var row = connection.QuerySingleOrDefault<Row>($"SELECT {Columns} FROM saves WHERE sha256 = @sha", new { sha = sha256 });
        return row is null ? null : ToRecord(row);
    }

    public IReadOnlyList<SaveRecord> List()
    {
        using var connection = factory.Open();
        return [.. connection.Query<Row>($"SELECT {Columns} FROM saves ORDER BY uploaded_at DESC, id DESC").Select(ToRecord)];
    }

    public bool Update(string sha256, string? displayName, bool? pinned)
    {
        if (Find(sha256) is null)
        {
            return false;
        }

        writer.TryEnqueue(
            "UPDATE saves SET display_name = COALESCE(@name, display_name), pinned = COALESCE(@pinned, pinned) WHERE sha256 = @sha",
            new { sha = sha256, name = displayName, pinned = pinned is { } p ? (p ? 1 : 0) : (int?)null });
        return true;
    }

    public IReadOnlyList<string> Delete(string sha256)
    {
        List<string> manifests;
        using (var connection = factory.Open())
        {
            if (connection.ExecuteScalar<long>("SELECT COUNT(*) FROM saves WHERE sha256 = @sha", new { sha = sha256 }) == 0)
            {
                return [];
            }

            manifests =
            [
                .. connection.Query<string>(
                    "SELECT DISTINCT manifest_sha256 FROM checkpoints WHERE save_sha256 = @sha AND manifest_sha256 IS NOT NULL", new { sha = sha256 }),
            ];
        }

        writer.TryEnqueue((connection, transaction) =>
        {
            var p = new { sha = sha256 };
            connection.Execute("DELETE FROM checkpoints WHERE save_id = (SELECT id FROM saves WHERE sha256 = @sha)", p, transaction);
            connection.Execute("UPDATE sessions SET save_id = NULL WHERE save_id = (SELECT id FROM saves WHERE sha256 = @sha)", p, transaction);
            connection.Execute("UPDATE sessions SET current_save_id = NULL WHERE current_save_id = (SELECT id FROM saves WHERE sha256 = @sha)", p, transaction);
            connection.Execute("DELETE FROM saves WHERE sha256 = @sha", p, transaction);
        });
        return manifests;
    }

    public void AddCheckpoint(long sessionId, CheckpointRecord checkpoint) =>
        writer.TryEnqueue(
            """
            INSERT INTO checkpoints
              (session_id, save_id, save_sha256, journal_seq, game_time, next_net_id, ghosts_cleaned, created_at, checkpoint_id, manifest_sha256, manifest_size)
            VALUES (@session, (SELECT id FROM saves WHERE sha256 = @sha), @sha, @seq, @time, @next, @cleaned, @at, @cp, @manifest, @manifestSize)
            """,
            new
            {
                session = sessionId,
                sha = checkpoint.SaveSha256,
                seq = (long)checkpoint.JournalSeq,
                time = checkpoint.GameTime,
                next = (long)checkpoint.NextNetId,
                cleaned = checkpoint.GhostsCleaned ? 1 : 0,
                at = Stamp(checkpoint.At),
                cp = checkpoint.Id.ToString(),
                manifest = checkpoint.ManifestSha256,
                manifestSize = checkpoint.ManifestSize,
            });

    public void SetSessionSave(long sessionId, string sha256, bool initial) =>
        writer.TryEnqueue(
            """
            UPDATE sessions SET
              current_save_id = (SELECT id FROM saves WHERE sha256 = @sha),
              save_id = CASE WHEN @initial = 1 AND save_id IS NULL THEN (SELECT id FROM saves WHERE sha256 = @sha) ELSE save_id END
            WHERE id = @session
            """,
            new { session = sessionId, sha = sha256, initial = initial ? 1 : 0 });

    public IReadOnlySet<string> ReferencedSha256()
    {
        using var connection = factory.Open();
        return connection.Query<string>(
            """
            SELECT sha256 FROM saves WHERE id IN (
              SELECT current_save_id FROM sessions WHERE state <> 'Ended' AND current_save_id IS NOT NULL
              UNION
              SELECT save_id FROM sessions WHERE state <> 'Ended' AND save_id IS NOT NULL)
            """).ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlySet<string> ManifestSha256()
    {
        using var connection = factory.Open();
        return connection.Query<string>(
            "SELECT manifest_sha256 FROM checkpoints WHERE save_id IS NOT NULL AND manifest_sha256 IS NOT NULL").ToHashSet(StringComparer.Ordinal);
    }

    private static SaveRecord ToRecord(Row r)
    {
        long? money = null;
        if (r.MetaJson is { Length: > 0 })
        {
            try
            {
                using var doc = JsonDocument.Parse(r.MetaJson);
                if (doc.RootElement.TryGetProperty("playerMoney", out var m) && m.TryGetInt64(out var value))
                {
                    money = value;
                }
            }
            catch (JsonException)
            {
                // ignore a hand-edited value
            }
        }

        return new SaveRecord(
            r.Sha256, r.SizeBytes, r.DisplayName, r.Source, r.UploadedBy,
            DateTimeOffset.Parse(r.UploadedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            new SaveMeta(r.GameVersion, r.SaveTime, r.PlayerName, money), r.GhostsCleaned != 0, r.Pinned != 0, r.OriginalFileName);
    }
}

internal static class SaveStamp
{
    public static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
