using Dapper;
using Microsoft.Data.Sqlite;
using X4MP.Core.Saves;
using X4MP.Core.World;

namespace X4MP.Persistence.Tests;

public sealed class SqliteSaveCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-savecatalog-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    private readonly PersistenceWriter _writer;
    private readonly SqliteSaveCatalog _catalog;
    private readonly long _session;
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string ShaA = new('a', 64);
    private static readonly string ShaB = new('b', 64);
    private static readonly string Manifest = new('c', 64);

    public SqliteSaveCatalogTests()
    {
        _factory = new SqliteConnectionFactory(new PersistenceOptions { DataDir = _dir });
        new MigrationRunner(_factory).Migrate();
        _writer = new PersistenceWriter(_factory, new PersistenceOptions { DataDir = _dir });
        _catalog = new SqliteSaveCatalog(_factory, _writer);
        _session = new SqliteSessionStore(_factory, _writer).BeginSessionAsync("s", Guid.NewGuid(), T0, default).AsTask().GetAwaiter().GetResult();
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

    private static SaveRecord Save(string sha, DateTimeOffset at, bool cleaned = true) =>
        new(sha, 1234, "name " + sha[..1], "authority", "Auth", at, new SaveMeta("900", "1700000000", "Jack", 987654321), cleaned);

    [Fact]
    public async Task ASaveRoundTripsWithItsMetadataAndAddingItTwiceKeepsTheFirstRow()
    {
        _catalog.AddSave(Save(ShaA, T0));
        _catalog.AddSave(Save(ShaA, T0.AddHours(5)) with { DisplayName = "later duplicate" });
        await _writer.FlushAsync();

        var found = _catalog.Find(ShaA)!;
        Assert.Equal("name a", found.DisplayName);
        Assert.Equal(1234, found.SizeBytes);
        Assert.Equal("authority", found.Source);
        Assert.Equal(T0, found.UploadedAt);
        Assert.Equal(new SaveMeta("900", "1700000000", "Jack", 987654321), found.Meta);
        Assert.True(found.GhostsCleaned);
        Assert.False(found.Pinned);
        Assert.Null(_catalog.Find(ShaB));
        Assert.Single(_catalog.List());
    }

    [Fact]
    public async Task ListIsNewestFirstAndGhostsCleanedFalseIsKept()
    {
        _catalog.AddSave(Save(ShaA, T0));
        _catalog.AddSave(Save(ShaB, T0.AddMinutes(10), cleaned: false));
        await _writer.FlushAsync();

        var list = _catalog.List();
        Assert.Equal([ShaB, ShaA], list.Select(s => s.Sha256));
        Assert.False(list[0].GhostsCleaned);
    }

    [Fact]
    public async Task RenameAndPinPersistAndAnUnknownSaveIsReported()
    {
        _catalog.AddSave(Save(ShaA, T0));
        await _writer.FlushAsync();

        Assert.True(_catalog.Update(ShaA, "renamed", true));
        Assert.True(_catalog.Update(ShaA, null, null)); // nothing to change is still a hit
        Assert.False(_catalog.Update(ShaB, "x", null));
        await _writer.FlushAsync();
        var row = _catalog.Find(ShaA)!;
        Assert.Equal("renamed", row.DisplayName);
        Assert.True(row.Pinned);
    }

    [Fact]
    public async Task CheckpointsAndTheSessionsCurrentSaveAreRecordedAndProtectTheSaveFromTheJanitor()
    {
        _catalog.AddSave(Save(ShaA, T0));
        _catalog.AddSave(Save(ShaB, T0.AddHours(1)));
        _catalog.AddCheckpoint(_session, new CheckpointRecord(new CheckpointId(7, 8), ShaA, Manifest, 321, 17, 60.5, 99, true, T0));
        _catalog.SetSessionSave(_session, ShaA, initial: true);
        await _writer.FlushAsync();

        using (var db = _factory.Open())
        {
            var cp = db.QuerySingle("SELECT * FROM checkpoints");
            Assert.Equal(_session, (long)cp.session_id);
            Assert.Equal(ShaA, (string)cp.save_sha256);
            Assert.Equal(Manifest, (string)cp.manifest_sha256);
            Assert.Equal(321, (long)cp.manifest_size);
            Assert.Equal(17, (long)cp.journal_seq);
            Assert.Equal(99, (long)cp.next_net_id);
            Assert.Equal(new CheckpointId(7, 8).ToString(), (string)cp.checkpoint_id);
            Assert.Equal(1, (long)cp.ghosts_cleaned);
            var session = db.QuerySingle("SELECT save_id, current_save_id FROM sessions WHERE id = @id", new { id = _session });
            Assert.NotNull(session.current_save_id);
            Assert.Equal(session.save_id, session.current_save_id);
        }

        Assert.Equal([ShaA], _catalog.ReferencedSha256());
        Assert.Equal([Manifest], _catalog.ManifestSha256());

        // a later checkpoint moves current_save_id but not the initial save_id
        _catalog.SetSessionSave(_session, ShaB, initial: false);
        await _writer.FlushAsync();
        Assert.Contains(ShaB, _catalog.ReferencedSha256());
        using (var db = _factory.Open())
        {
            Assert.NotEqual(
                db.ExecuteScalar<long>("SELECT save_id FROM sessions WHERE id = @id", new { id = _session }),
                db.ExecuteScalar<long>("SELECT current_save_id FROM sessions WHERE id = @id", new { id = _session }));
        }
    }

    [Fact]
    public async Task AnEndedSessionNoLongerProtectsItsSaves()
    {
        _catalog.AddSave(Save(ShaA, T0));
        _catalog.SetSessionSave(_session, ShaA, initial: true);
        await _writer.FlushAsync();
        Assert.Single(_catalog.ReferencedSha256());

        new SqliteSessionStore(_factory, _writer).RecordPhase(_session, X4MP.Proto.SessionPhase.Ended, 0, T0, "done");
        await _writer.FlushAsync();
        Assert.Empty(_catalog.ReferencedSha256());
    }

    [Fact]
    public async Task DeleteRemovesTheSaveItsCheckpointsAndTheSessionLinksAndReturnsTheManifests()
    {
        _catalog.AddSave(Save(ShaA, T0));
        _catalog.AddCheckpoint(_session, new CheckpointRecord(new CheckpointId(1, 1), ShaA, Manifest, 5, 1, 0, 0, true, T0));
        _catalog.SetSessionSave(_session, ShaA, initial: true);
        await _writer.FlushAsync();

        var manifests = _catalog.Delete(ShaA);
        await _writer.FlushAsync();

        Assert.Equal([Manifest], manifests);
        Assert.Null(_catalog.Find(ShaA));
        using var db = _factory.Open();
        Assert.Equal(0, db.ExecuteScalar<int>("SELECT COUNT(*) FROM checkpoints"));
        Assert.Null(db.ExecuteScalar<long?>("SELECT current_save_id FROM sessions WHERE id = @id", new { id = _session }));
        Assert.Empty(_catalog.Delete(ShaA));
    }
}
