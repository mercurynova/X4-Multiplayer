using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Economy;
using X4MP.Core.Events;

namespace X4MP.Persistence.Tests;

public sealed class SqliteEconomyStoreTests : IDisposable
{
    private const long Session = 1;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-economy-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;

    public SqliteEconomyStoreTests()
    {
        _factory = new SqliteConnectionFactory(new PersistenceOptions { DataDir = _dir });
        new MigrationRunner(_factory).Migrate();
        using var db = _factory.Open();
        db.Execute("INSERT INTO sessions (id, name, state, settings_json, created_at) VALUES (1, 'test', 'Idle', '{}', '2026-10-01T12:00:00Z')");
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

    private SqliteEconomyStore NewStore(bool fullSync = true) => new(_factory, fullSync);

    private static EconomyLedger NewLedger(IEconomyStore store, FakeTimeProvider? time = null, IEventPublisher? events = null)
    {
        var ledger = new EconomyLedger(Session, store, time ?? new FakeTimeProvider(), events);
        ledger.Load();
        return ledger;
    }

    private static PostRequest Income(WalletId to, long amount, string? requestId = null) => new()
    {
        Kind = TxKind.GameIncome,
        Actor = "authority",
        PlayerId = 1,
        RequestId = requestId,
        PayloadHash = requestId is null ? null : PayloadHasher.Hash("Income", to, amount),
        Entries = [new(WalletId.World, -amount), new(to, amount)],
    };

    private T Scalar<T>(string sql)
    {
        using var db = _factory.Open();
        return db.ExecuteScalar<T>(sql)!;
    }

    // ------------------------------------------------------------------ durability

    [Fact]
    public void AcknowledgedPostingsSurviveDisposingTheConnectionAndReopening()
    {
        var store = NewStore();
        var ledger = NewLedger(store);
        var posted = ledger.Post(Income(WalletId.Player(1), 12_345, "req-a"));
        Assert.True(posted.Ok);
        ledger.Post(new PostRequest { Kind = TxKind.GameSpend, Actor = "authority", Entries = [new(WalletId.Player(1), -45), new(WalletId.World, 45)], DeltaSeq = (1, 77) });

        // The process dies right after the acknowledgement: nothing but the database file is left.
        store.Dispose();
        SqliteConnection.ClearAllPools();

        var reopened = NewLedger(NewStore());
        Assert.Equal(12_300, reopened.BalanceOf(WalletId.Player(1)));
        Assert.Equal(-12_300, reopened.BalanceOf(WalletId.World));
        Assert.Equal(77UL, reopened.LastDeltaSeq(1));
        Assert.Equal(2, Scalar<long>("SELECT COUNT(*) FROM ledger_tx"));
        Assert.Equal(4, Scalar<long>("SELECT COUNT(*) FROM ledger_entries"));
        var replay = reopened.Post(Income(WalletId.Player(1), 12_345, "req-a"));
        Assert.True(replay.Replayed);
        Assert.Equal(posted.TxId, replay.TxId);
    }

    [Fact]
    public void ACommitIsVisibleToAnotherConnectionBeforeItIsAcknowledged()
    {
        using var store = NewStore();
        var ledger = NewLedger(store);

        ledger.Post(Income(WalletId.Player(3), 500));

        Assert.Equal(500, Scalar<long>("SELECT balance FROM wallets WHERE kind='player' AND owner_id=3"));
    }

    [Fact]
    public void AFailedCommitRollsBackEverythingAndTheLedgerStaysConsistent()
    {
        using var store = NewStore();
        var ledger = NewLedger(store);
        ledger.Post(Income(WalletId.Player(1), 100));

        // Break the delta-sequence table: the commit fails after the tx and its entries were already inserted.
        using (var db = _factory.Open())
        {
            db.Execute("DROP TABLE economy_delta_seq");
        }

        var failed = ledger.Post(Income(WalletId.Player(1), 50) with { DeltaSeq = (1, 5) });

        Assert.Equal(PostReject.StoreFailure, failed.Reason);
        Assert.Equal(100, ledger.BalanceOf(WalletId.Player(1)));
        Assert.Equal(0UL, ledger.LastDeltaSeq(1));
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM ledger_tx")); // rolled back as a whole
        Assert.Equal(2, Scalar<long>("SELECT COUNT(*) FROM ledger_entries"));
        Assert.True(new EconomyAuditor(ledger, store).RunNow().Ok);
    }

    // ------------------------------------------------------------------ idempotency

    [Fact]
    public void ReplayReturnsTheIdenticalStoredResultAcrossARestart()
    {
        var store = NewStore();
        var ledger = NewLedger(store);
        var first = ledger.Post(Income(WalletId.Player(2), 900, "key-1"));
        store.Dispose();
        SqliteConnection.ClearAllPools();

        var again = NewLedger(NewStore());
        var replay = again.Post(Income(WalletId.Player(2), 900, "key-1"));

        Assert.True(replay.Replayed);
        Assert.Equal(first.Status, replay.Status);
        Assert.Equal(first.TxId, replay.TxId);
        Assert.Equal(first.Balances, replay.Balances);
        Assert.Equal(900, again.BalanceOf(WalletId.Player(2)));
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM ledger_tx"));
    }

    [Fact]
    public void ReuseWithADifferentPayloadIsRejected()
    {
        using var store = NewStore();
        var ledger = NewLedger(store);
        ledger.Post(Income(WalletId.Player(2), 900, "key-1"));

        var reuse = ledger.Post(Income(WalletId.Player(2), 901, "key-1"));

        Assert.Equal(PostReject.PayloadMismatch, reuse.Reason);
        Assert.Equal(900, ledger.BalanceOf(WalletId.Player(2)));
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM ledger_tx"));
    }

    // ------------------------------------------------------------------ append-only

    [Fact]
    public void TriggersRefuseUpdatingOrDeletingTheLedger()
    {
        using var store = NewStore();
        var ledger = NewLedger(store);
        ledger.Post(Income(WalletId.Player(1), 100));
        using var db = _factory.Open();

        Assert.Contains("append-only", Assert.Throws<SqliteException>(() => db.Execute("UPDATE ledger_entries SET amount = 1")).Message);
        Assert.Contains("append-only", Assert.Throws<SqliteException>(() => db.Execute("DELETE FROM ledger_entries")).Message);
        Assert.Contains("append-only", Assert.Throws<SqliteException>(() => db.Execute("DELETE FROM ledger_tx")).Message);
        Assert.Contains("append-only", Assert.Throws<SqliteException>(() => db.Execute("UPDATE ledger_tx SET actor = 'mallory'")).Message);
        Assert.Contains("append-only", Assert.Throws<SqliteException>(() => db.Execute("UPDATE ledger_tx SET kind = 'AdminAdjust'")).Message);
        Assert.Equal(100, Scalar<long>("SELECT SUM(amount) FROM ledger_entries WHERE amount > 0"));
    }

    [Fact]
    public void ReversalCanBeLinkedExactlyOnce()
    {
        using var store = NewStore();
        var ledger = NewLedger(store);
        var first = ledger.Post(Income(WalletId.Player(1), 100));
        var second = ledger.Post(Income(WalletId.Player(1), 5));
        using var db = _factory.Open();

        db.Execute("UPDATE ledger_tx SET reversed_by_tx = @r WHERE id = @id", new { r = second.TxId, id = first.TxId });
        Assert.Throws<SqliteException>(() => db.Execute("UPDATE ledger_tx SET reversed_by_tx = @r WHERE id = @id", new { r = first.TxId, id = first.TxId }));
    }

    [Fact]
    public void WalletCheckConstraintKeepsPoolsAndEscrowsNonNegative()
    {
        using var db = _factory.Open();
        Assert.Throws<SqliteException>(() => db.Execute(
            "INSERT INTO wallets (session_id, kind, owner_id, balance, updated_at) VALUES (1, 'team_pool', 1, -1, 't')"));
        Assert.Throws<SqliteException>(() => db.Execute(
            "INSERT INTO wallets (session_id, kind, owner_id, balance, updated_at) VALUES (1, 'escrow', 1, -1, 't')"));
        db.Execute("INSERT INTO wallets (session_id, kind, owner_id, balance, updated_at) VALUES (1, 'player', 1, -1, 't')"); // debt is allowed
    }

    // ------------------------------------------------------------------ auditor

    [Fact]
    public void InjectedCorruptionOfAWalletBalanceFreezesTheEconomy()
    {
        using var store = NewStore();
        var events = new CapturingEvents();
        var ledger = NewLedger(store, events: events);
        ledger.Post(Income(WalletId.Player(1), 1000));
        var auditor = new EconomyAuditor(ledger, store);
        Assert.True(auditor.RunNow().Ok);

        using (var db = _factory.Open())
        {
            db.Execute("UPDATE wallets SET balance = balance + 5 WHERE kind = 'player' AND owner_id = 1");
        }

        var report = auditor.RunNow();

        Assert.False(report.Ok);
        Assert.True(ledger.IsFrozen);
        Assert.Single(events.All.OfType<EconomyFrozen>());
        Assert.Equal(PostReject.EconomyFrozen, ledger.Post(Income(WalletId.Player(1), 1)).Reason);
    }

    [Fact]
    public void InjectedCorruptionOfALedgerEntryFreezesTheEconomy()
    {
        using var store = NewStore();
        var ledger = NewLedger(store);
        ledger.Post(Income(WalletId.Player(1), 1000));
        var auditor = new EconomyAuditor(ledger, store);

        using (var db = _factory.Open())
        {
            db.Execute("DROP TRIGGER ledger_entries_no_update"); // an attacker with file access
            db.Execute("UPDATE ledger_entries SET amount = amount + 1 WHERE wallet_kind = 'player'");
        }

        var report = auditor.RunNow();

        Assert.False(report.Ok);
        Assert.True(ledger.IsFrozen);
        Assert.Contains(report.Violations, v => v.Contains("does not sum to zero", StringComparison.Ordinal));
    }

    [Fact]
    public void ADeletedWalletRowIsCaught()
    {
        using var store = NewStore();
        var ledger = NewLedger(store);
        ledger.Post(Income(WalletId.Player(1), 1000));
        using (var db = _factory.Open())
        {
            db.Execute("DELETE FROM wallets WHERE kind = 'player'");
        }

        Assert.False(new EconomyAuditor(ledger, store).RunNow().Ok);
        Assert.True(ledger.IsFrozen);
    }


    // ------------------------------------------------------------------ property test

    [Fact]
    public void HundredThousandRandomOperationsKeepTheSumAtZero()
    {
        using var store = NewStore(fullSync: false); // fsync-per-commit would make 100k commits take minutes; durability is covered above
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var ledger = NewLedger(store, time);
        var auditor = new EconomyAuditor(ledger, store, time);
        var rng = new Random(20261001);
        var wallets = new List<WalletId> { WalletId.World };
        for (var i = 1; i <= 8; i++)
        {
            wallets.Add(WalletId.Player(i));
        }

        for (var i = 1; i <= 3; i++)
        {
            wallets.Add(WalletId.TeamShared(i));
            wallets.Add(WalletId.TeamPool(i));
            wallets.Add(WalletId.Escrow(i));
        }

        long committed = 0;
        long rejected = 0;
        long replays = 0;
        for (var op = 0; op < 100_000; op++)
        {
            time.Advance(TimeSpan.FromMilliseconds(rng.Next(0, 3)));
            var legs = rng.Next(2, 5);
            var picked = wallets.OrderBy(_ => rng.Next()).Take(legs).ToList();
            var entries = new List<PostEntry>();
            long sum = 0;
            for (var i = 0; i < picked.Count - 1; i++)
            {
                var amount = rng.Next(-5_000, 5_001);
                entries.Add(new PostEntry(picked[i], amount));
                sum += amount;
            }

            // Mostly balanced; now and then wrong on purpose (the ledger must refuse, never book it).
            entries.Add(new PostEntry(picked[^1], rng.Next(40) == 0 ? -sum + 1 : -sum));
            var requestId = rng.Next(5) == 0 ? "r" + rng.Next(2_000) : null;
            var request = new PostRequest
            {
                Kind = TxKind.AdminAdjust,
                Actor = "system",
                PlayerId = 1,
                RequestId = requestId,
                PayloadHash = requestId is null ? null : PayloadHasher.Hash("fuzz", requestId),
                Entries = entries,
                Flags = rng.Next(4) == 0 ? PostOptions.AllowOverdraw : PostOptions.None,
            };

            var outcome = ledger.Post(request);
            if (outcome.Ok)
            {
                committed++;
                if (outcome.Replayed)
                {
                    replays++;
                }
            }
            else
            {
                rejected++;
            }

            if (op % 10_000 == 9_999)
            {
                Assert.Equal(0, ledger.TotalBalance());
                Assert.True(auditor.RunNow().Ok, string.Join("; ", auditor.LastReport!.Violations));
            }
        }

        Assert.True(committed > 20_000, $"only {committed} postings committed");
        Assert.True(rejected > 1_000);
        Assert.True(replays > 100);
        Assert.False(ledger.IsFrozen);
        Assert.Equal(0, ledger.TotalBalance());
        Assert.Equal(0, Scalar<long>("SELECT COALESCE(SUM(amount), 0) FROM ledger_entries"));
        Assert.Equal(0, Scalar<long>("SELECT COALESCE(SUM(balance), 0) FROM wallets"));
        Assert.Equal(0, Scalar<long>(
            "SELECT COUNT(*) FROM (SELECT tx_id FROM ledger_entries GROUP BY tx_id HAVING SUM(amount) <> 0 OR COUNT(*) < 2)"));
        Assert.Equal(0, Scalar<long>("SELECT COUNT(*) FROM wallets WHERE balance < 0 AND kind IN ('team_pool', 'escrow')"));

        // The cache equals what a fresh process would load.
        var reloaded = NewLedger(store);
        Assert.All(ledger.Wallets, w => Assert.Equal(w.Balance, reloaded.BalanceOf(w.Id)));
        Assert.True(auditor.RunNow().Ok);
    }

    private sealed class CapturingEvents : IEventPublisher
    {
        public List<DomainEvent> All { get; } = [];

        public void Publish(DomainEvent domainEvent) => All.Add(domainEvent);
    }
}
