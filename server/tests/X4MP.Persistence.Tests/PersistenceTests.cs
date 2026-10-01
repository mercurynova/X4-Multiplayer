using Dapper;
using Microsoft.Data.Sqlite;

namespace X4MP.Persistence.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-persist-" + Guid.NewGuid().ToString("N"));

    private PersistenceOptions Options(Action<PersistenceOptions>? configure = null)
    {
        var options = new PersistenceOptions { DataDir = _dir };
        configure?.Invoke(options);
        return options;
    }

    public void Dispose()
    {
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

    private static MigrationRunner Runner(SqliteConnectionFactory factory) => new(factory);

    private static T Scalar<T>(SqliteConnectionFactory factory, string sql)
    {
        using var connection = factory.Open();
        return connection.ExecuteScalar<T>(sql)!;
    }

    [Fact]
    public void EmptyDirectoryCreatesDatabaseAtSchemaV1()
    {
        var factory = new SqliteConnectionFactory(Options());
        Assert.False(Directory.Exists(_dir));

        var version = Runner(factory).Migrate();

        Assert.Equal(1, version);
        Assert.True(File.Exists(Path.Combine(_dir, "x4mp.db")));
        var tables = Scalar<long>(factory,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN " +
            "('schema_version','players','bans','saves','sessions','session_players','session_events'," +
            "'chat_messages','galaxy_cache','config_overrides','admin_users','api_tokens','audit_log'," +
            "'journal','string_table','checkpoints')");
        Assert.Equal(16, tables);
        // deferred tables must not exist yet
        Assert.Equal(0, Scalar<long>(factory,
            "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('teams','wallets','ledger_tx','loans')"));
    }

    [Fact]
    public void SecondStartIsNoOp()
    {
        var factory = new SqliteConnectionFactory(Options());
        Assert.Equal(1, Runner(factory).Migrate());
        Assert.Equal(1, Runner(factory).Migrate());

        Assert.Equal(1, Scalar<long>(factory, "SELECT COUNT(*) FROM schema_version"));
    }

    [Fact]
    public void FailedMigrationRollsBackAndKeepsPreviousVersion()
    {
        var factory = new SqliteConnectionFactory(Options());
        var good = new Migration(1, "a", "CREATE TABLE schema_version (version INTEGER NOT NULL); CREATE TABLE a (x INTEGER);");
        var bad = new Migration(2, "b", "CREATE TABLE b (x INTEGER); THIS IS NOT SQL;");

        Assert.Throws<InvalidOperationException>(() => new MigrationRunner(factory, [good, bad]).Migrate());

        Assert.Equal(1, Scalar<int>(factory, "SELECT MAX(version) FROM schema_version"));
        Assert.Equal(0, Scalar<long>(factory, "SELECT COUNT(*) FROM sqlite_master WHERE name='b'"));
    }

    [Fact]
    public void ConnectionsUseWalPragmas()
    {
        var factory = new SqliteConnectionFactory(Options());
        using var connection = factory.Open();

        Assert.Equal("wal", connection.ExecuteScalar<string>("PRAGMA journal_mode"));
        Assert.Equal(1, connection.ExecuteScalar<int>("PRAGMA synchronous")); // NORMAL
        Assert.Equal(1, connection.ExecuteScalar<int>("PRAGMA foreign_keys"));
        Assert.Equal(5000, connection.ExecuteScalar<int>("PRAGMA busy_timeout"));
    }

    private static SqliteConnectionFactory CreateEventsDb(PersistenceOptions options)
    {
        var factory = new SqliteConnectionFactory(options);
        Runner(factory).Migrate();
        return factory;
    }

    private const string InsertAudit =
        "INSERT INTO audit_log (ts, actor, action) VALUES ('2026-01-01T00:00:00Z', @Actor, 'test')";

    [Fact]
    public async Task ThousandBatchedInsertsLandInOneTransaction()
    {
        var options = Options(o =>
        {
            o.MaxBatchItems = 1000;
            o.FlushInterval = TimeSpan.FromSeconds(30); // only the item cap may trigger the flush
        });
        var factory = CreateEventsDb(options);
        var batches = new List<BatchCommitted>();

        await using (var writer = new PersistenceWriter(factory, options))
        {
            writer.BatchCommitted += b => { lock (batches) { batches.Add(b); } };
            for (var i = 0; i < 1000; i++)
            {
                Assert.True(writer.TryEnqueue(InsertAudit, new { Actor = "a" + i }));
            }
            await writer.FlushAsync();
        }

        Assert.Equal(1000, Scalar<long>(factory, "SELECT COUNT(*) FROM audit_log"));
        var batch = Assert.Single(batches);
        Assert.Equal(1000, batch.ItemCount);
        Assert.True(batch.Duration < TimeSpan.FromSeconds(5), $"batch took {batch.Duration}");
    }

    [Fact]
    public async Task BatchesFlushOnIntervalWithoutDispose()
    {
        var options = Options(o => o.FlushInterval = TimeSpan.FromMilliseconds(50));
        var factory = CreateEventsDb(options);

        await using var writer = new PersistenceWriter(factory, options);
        await writer.EnqueueAsync(InsertAudit, new { Actor = "x" });

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Scalar<long>(factory, "SELECT COUNT(*) FROM audit_log") == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, Scalar<long>(factory, "SELECT COUNT(*) FROM audit_log"));
    }

    [Fact]
    public async Task DisposeFlushesPendingItems()
    {
        var options = Options(o =>
        {
            o.MaxBatchItems = 10_000;
            o.FlushInterval = TimeSpan.FromMinutes(5); // nothing flushes on its own
        });
        var factory = CreateEventsDb(options);

        var writer = new PersistenceWriter(factory, options);
        for (var i = 0; i < 250; i++)
        {
            await writer.EnqueueAsync(InsertAudit, new { Actor = "p" + i });
        }
        await writer.DisposeAsync();

        Assert.Equal(250, Scalar<long>(factory, "SELECT COUNT(*) FROM audit_log"));
        Assert.False(writer.TryEnqueue(InsertAudit, new { Actor = "late" }));
    }

    [Fact]
    public async Task FailingItemDoesNotDropTheRestOfTheBatch()
    {
        var options = Options(o =>
        {
            o.MaxBatchItems = 3;
            o.FlushInterval = TimeSpan.FromSeconds(30);
        });
        var factory = CreateEventsDb(options);
        var failures = 0;

        await using (var writer = new PersistenceWriter(factory, options))
        {
            writer.WriteFailed += _ => Interlocked.Increment(ref failures);
            writer.TryEnqueue(InsertAudit, new { Actor = "ok1" });
            writer.TryEnqueue("INSERT INTO audit_log (ts) VALUES ('missing columns')");
            writer.TryEnqueue(InsertAudit, new { Actor = "ok2" });
            await writer.FlushAsync();
        }

        Assert.Equal(1, failures);
        Assert.Equal(2, Scalar<long>(factory, "SELECT COUNT(*) FROM audit_log"));
    }
}
