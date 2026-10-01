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
/// The session is the <c>sessions</c> row id the actor announced through <c>ISessionModule.OnSessionBegun</c> (the world
/// mirror forwards it to <see cref="BindSession"/>). Until a session is bound, session-scoped writes are dropped and loads
/// return nothing (the galaxy cache is global and always works).
/// </para>
/// <para>
/// <c>string_table.id</c> is a global primary key and the table has no <c>kind</c> column, so the row id packs
/// <c>(session_id &lt;&lt; 40) | (kind &lt;&lt; 32) | index</c>: indices from different sessions cannot collide and the kind
/// survives a restart without a schema change. The table's <c>UNIQUE(session_id, value)</c> means a value interned twice under
/// different indices keeps the first one.
/// </para>
/// </summary>
public sealed class SqliteWorldStore(SqliteConnectionFactory factory, PersistenceWriter writer) : IWorldStore
{
    private long _sessionId;

    /// <summary>The bound <c>sessions.id</c> (0 = none yet).</summary>
    public long SessionId => Volatile.Read(ref _sessionId);

    public void BindSession(long sessionId) => Volatile.Write(ref _sessionId, sessionId);

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

    public void AppendJournal(JournalRecord record)
    {
        if (SessionId <= 0)
        {
            return;
        }

        writer.TryEnqueue(
            """
            INSERT OR IGNORE INTO journal (session_id, server_seq, ts, kind, net_id, sector_id, payload)
            VALUES (@session, @seq, @ts, @kind, @net, @sector, @payload)
            """,
            new
            {
                session = SessionId,
                seq = (long)record.Seq,
                ts = Stamp(record.At),
                kind = (int)record.Kind,
                net = (long)record.NetId,
                sector = (int)record.Sector,
                payload = record.Payload,
            });
    }

    public void TruncateJournal(ulong beforeSeq)
    {
        if (SessionId <= 0)
        {
            return;
        }

        writer.TryEnqueue(
            "DELETE FROM journal WHERE server_seq < @seq AND session_id = @session",
            new { seq = (long)beforeSeq, session = SessionId });
    }

    public IReadOnlyList<JournalRecord> LoadJournal()
    {
        if (SessionId <= 0)
        {
            return [];
        }

        using var connection = factory.Open();
        var rows = connection.Query<(long Seq, string Ts, int Kind, long? Net, int? Sector, byte[] Payload)>(
            """
            SELECT server_seq AS Seq, ts AS Ts, kind AS Kind, net_id AS Net, sector_id AS Sector, payload AS Payload
            FROM journal WHERE session_id = @session ORDER BY server_seq
            """,
            new { session = SessionId });
        return
        [
            .. rows.Select(r => new JournalRecord(
                (ulong)r.Seq, (MsgType)r.Kind, (uint)(r.Net ?? 0), (ushort)(r.Sector ?? 0), r.Payload,
                DateTimeOffset.Parse(r.Ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal))),
        ];
    }

    public void AppendStrings(IReadOnlyList<StringTableEntry> entries)
    {
        if (SessionId <= 0)
        {
            return;
        }

        foreach (var e in entries)
        {
            writer.TryEnqueue(
                "INSERT OR IGNORE INTO string_table (id, session_id, value) VALUES ((@session << 40) | (@kind << 32) | @index, @session, @value)",
                new { session = SessionId, kind = (long)e.Kind, index = (long)e.Index, value = e.Value });
        }
    }

    public IReadOnlyList<StringTableEntry> LoadStrings()
    {
        if (SessionId <= 0)
        {
            return [];
        }

        using var connection = factory.Open();
        var rows = connection.Query<(long Id, string Value)>(
            "SELECT id AS Id, value AS Value FROM string_table WHERE session_id = @session ORDER BY id",
            new { session = SessionId });
        return
        [
            .. rows.Select(r => new StringTableEntry((uint)(r.Id & 0xFFFFFFFFL), (StringKind)((r.Id >> 32) & 0xFF), r.Value)),
        ];
    }
}
