using System.Globalization;
using Dapper;
using X4MP.Core.World;
using X4MP.Proto;

namespace X4MP.Persistence;

/// <summary>
/// SQLite implementation of <see cref="IWorldStore"/> over the <c>galaxy_cache</c>, <c>journal</c> and <c>string_table</c> tables
/// of migration 0001 (no schema change). Writes go through the write-behind <see cref="PersistenceWriter"/>; loads are
/// synchronous (once per session start or galaxy activation).
/// <para>
/// The session is identified by its GUID (read through the supplied function on every call, as the gateway may change it): the world module does not know the database id of the session row (the actor keeps it),
/// so every statement resolves the row from <c>sessions.settings_json</c>, where <see cref="SqliteSessionStore"/> stored the GUID.
/// </para>
/// <para>
/// <c>string_table.id</c> is a global primary key and the table has no <c>kind</c> column, so the row id packs
/// <c>(session_id &lt;&lt; 40) | (kind &lt;&lt; 32) | index</c>: indices from different sessions cannot collide and the kind
/// survives a restart without a schema change. The table's <c>UNIQUE(session_id, value)</c> means a value interned twice under
/// different indices keeps the first one.
/// </para>
/// </summary>
public sealed class SqliteWorldStore(SqliteConnectionFactory factory, PersistenceWriter writer, Func<Guid> sessionGuid) : IWorldStore
{
    private string Pattern => "%" + sessionGuid().ToString("D") + "%";

    private static string Stamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    public byte[]? TryLoadGalaxy(string saveSha256Hex)
    {
        using var connection = factory.Open();
        return connection.QuerySingleOrDefault<byte[]?>(
            "SELECT metadata_blob FROM galaxy_cache WHERE save_sha256 = @sha", new { sha = saveSha256Hex });
    }

    public void SaveGalaxy(string saveSha256Hex, byte[] payload, DateTimeOffset at) =>
        writer.TryEnqueue(
            "INSERT OR REPLACE INTO galaxy_cache (save_sha256, metadata_blob, created_at) VALUES (@sha, @blob, @at)",
            new { sha = saveSha256Hex, blob = payload, at = Stamp(at) });

    public void AppendJournal(JournalRecord record) =>
        writer.TryEnqueue(
            """
            INSERT OR IGNORE INTO journal (session_id, server_seq, ts, kind, net_id, sector_id, payload)
            SELECT id, @seq, @ts, @kind, @net, @sector, @payload FROM sessions WHERE settings_json LIKE @pattern
            """,
            new
            {
                seq = (long)record.Seq,
                ts = Stamp(record.At),
                kind = (int)record.Kind,
                net = (long)record.NetId,
                sector = (int)record.Sector,
                payload = record.Payload,
                pattern = Pattern,
            });

    public void TruncateJournal(ulong beforeSeq) =>
        writer.TryEnqueue(
            "DELETE FROM journal WHERE server_seq < @seq AND session_id IN (SELECT id FROM sessions WHERE settings_json LIKE @pattern)",
            new { seq = (long)beforeSeq, pattern = Pattern });

    public IReadOnlyList<JournalRecord> LoadJournal()
    {
        using var connection = factory.Open();
        var rows = connection.Query<(long Seq, string Ts, int Kind, long? Net, int? Sector, byte[] Payload)>(
            """
            SELECT j.server_seq AS Seq, j.ts AS Ts, j.kind AS Kind, j.net_id AS Net, j.sector_id AS Sector, j.payload AS Payload
            FROM journal j JOIN sessions s ON s.id = j.session_id
            WHERE s.settings_json LIKE @pattern ORDER BY j.server_seq
            """,
            new { pattern = Pattern });
        return
        [
            .. rows.Select(r => new JournalRecord(
                (ulong)r.Seq, (MsgType)r.Kind, (uint)(r.Net ?? 0), (ushort)(r.Sector ?? 0), r.Payload,
                DateTimeOffset.Parse(r.Ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal))),
        ];
    }

    public void AppendStrings(IReadOnlyList<StringTableEntry> entries)
    {
        foreach (var e in entries)
        {
            writer.TryEnqueue(
                """
                INSERT OR IGNORE INTO string_table (id, session_id, value)
                SELECT (id << 40) | (@kind << 32) | @index, id, @value FROM sessions WHERE settings_json LIKE @pattern
                """,
                new { kind = (long)e.Kind, index = (long)e.Index, value = e.Value, pattern = Pattern });
        }
    }

    public IReadOnlyList<StringTableEntry> LoadStrings()
    {
        using var connection = factory.Open();
        var rows = connection.Query<(long Id, string Value)>(
            """
            SELECT t.id AS Id, t.value AS Value FROM string_table t JOIN sessions s ON s.id = t.session_id
            WHERE s.settings_json LIKE @pattern ORDER BY t.id
            """,
            new { pattern = Pattern });
        return
        [
            .. rows.Select(r => new StringTableEntry((uint)(r.Id & 0xFFFFFFFFL), (StringKind)((r.Id >> 32) & 0xFF), r.Value)),
        ];
    }
}
