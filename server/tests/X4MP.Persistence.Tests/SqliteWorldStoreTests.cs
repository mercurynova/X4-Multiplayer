using Dapper;
using Microsoft.Data.Sqlite;
using X4MP.Core.World;
using X4MP.Proto;

namespace X4MP.Persistence.Tests;

public sealed class SqliteWorldStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-worldstore-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    private readonly PersistenceWriter _writer;
    private readonly Guid _guid = Guid.NewGuid();
    private readonly SqliteWorldStore _store;
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public SqliteWorldStoreTests()
    {
        _factory = new SqliteConnectionFactory(new PersistenceOptions { DataDir = _dir });
        new MigrationRunner(_factory).Migrate();
        _writer = new PersistenceWriter(_factory, new PersistenceOptions { DataDir = _dir });
        new SqliteSessionStore(_factory, _writer).BeginSessionAsync("s", _guid, T0, default).AsTask().GetAwaiter().GetResult();
        _store = new SqliteWorldStore(_factory, _writer, () => _guid);
    }

    public void Dispose()
    {
        _writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort temp cleanup
        }
    }

    [Fact]
    public async Task GalaxyMetadataIsCachedBySaveShaAndSurvivesAReload()
    {
        Assert.Null(_store.TryLoadGalaxy("abc"));
        _store.SaveGalaxy("abc", [1, 2, 3, 4], T0);
        await _writer.FlushAsync();

        var reopened = new SqliteWorldStore(new SqliteConnectionFactory(new PersistenceOptions { DataDir = _dir }), _writer, () => _guid);
        Assert.Equal([1, 2, 3, 4], reopened.TryLoadGalaxy("abc"));
        Assert.Null(reopened.TryLoadGalaxy("other"));

        // Saving the same sha again replaces the blob (the cache never grows duplicates).
        _store.SaveGalaxy("abc", [9], T0);
        await _writer.FlushAsync();
        Assert.Equal([9], reopened.TryLoadGalaxy("abc"));
    }

    [Fact]
    public async Task JournalRowsRoundTripInOrderAndTruncate()
    {
        var journal = new Journal(_store);
        journal.Append(MsgType.EntitySpawn, 7, 3, [1], T0);
        journal.AppendMarker(1, new CheckpointId(5, 6), 12.5, 99, T0.AddSeconds(1));
        journal.Append(MsgType.EntityChange, 7, 3, [2, 2], T0.AddSeconds(2));
        await _writer.FlushAsync();

        var loaded = _store.LoadJournal();
        Assert.Equal([1UL, 2, 3], loaded.Select(r => r.Seq));
        Assert.Equal([MsgType.EntitySpawn, MsgType.SaveStarted, MsgType.EntityChange], loaded.Select(r => r.Kind));
        Assert.Equal((7u, (ushort)3), (loaded[0].NetId, loaded[0].Sector));
        Assert.Equal(T0.AddSeconds(2), loaded[2].At);

        var restored = new Journal(_store);
        restored.Restore();
        Assert.Equal(3UL, restored.LastSeq);
        Assert.Equal(new CheckpointId(5, 6), Assert.Single(restored.Markers).Checkpoint);

        _store.TruncateJournal(3);
        await _writer.FlushAsync();
        Assert.Equal([3UL], _store.LoadJournal().Select(r => r.Seq));
    }

    [Fact]
    public async Task JournalOfAnotherSessionIsInvisible()
    {
        var otherGuid = Guid.NewGuid();
        await new SqliteSessionStore(_factory, _writer).BeginSessionAsync("other", otherGuid, T0, default);
        var other = new SqliteWorldStore(_factory, _writer, () => otherGuid);

        _store.AppendJournal(new JournalRecord(1, MsgType.EntitySpawn, 1, 1, [1], T0));
        other.AppendJournal(new JournalRecord(1, MsgType.EntitySpawn, 2, 1, [2], T0));
        await _writer.FlushAsync();

        Assert.Equal(1u, Assert.Single(_store.LoadJournal()).NetId);
        Assert.Equal(2u, Assert.Single(other.LoadJournal()).NetId);
    }

    [Fact]
    public async Task StringTableKeepsIndexAndKindWithoutASchemaChange()
    {
        _store.AppendStrings([new StringTableEntry(1, StringKind.Macro, "m"), new StringTableEntry(40000, StringKind.Text, "t")]);
        await _writer.FlushAsync();

        var loaded = _store.LoadStrings();
        Assert.Equal([new StringTableEntry(1, StringKind.Macro, "m"), new StringTableEntry(40000, StringKind.Text, "t")], loaded);

        // The same value interned again is ignored (UNIQUE(session_id, value)).
        _store.AppendStrings([new StringTableEntry(1, StringKind.Macro, "m")]);
        await _writer.FlushAsync();
        using var c = _factory.Open();
        Assert.Equal(2, c.ExecuteScalar<int>("SELECT COUNT(*) FROM string_table"));
    }
}
