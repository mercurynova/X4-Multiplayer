using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Economy;

namespace X4MP.Persistence.Tests;

/// <summary>M1-E6: reversal links, ledger queries and the economy event query in SQLite.</summary>
public sealed class SqliteEconomyAdminStoreTests : IDisposable
{
    private const long Session = 1;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-econ-admin-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;

    public SqliteEconomyAdminStoreTests()
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

    private (SqliteEconomyStore Store, EconomyLedger Ledger, FakeTimeProvider Time) New()
    {
        var store = new SqliteEconomyStore(_factory, fullSync: false);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var ledger = new EconomyLedger(Session, store, time);
        ledger.Load();
        return (store, ledger, time);
    }

    private static PostRequest Income(WalletId to, long amount, string actor = "authority") => new()
    {
        Kind = TxKind.GameIncome,
        Actor = actor,
        Entries = [new(WalletId.World, -amount), new(to, amount)],
        RefType = "CreditDelta",
        RefId = 5,
        Note = "n",
    };

    [Fact]
    public void AReversalLinksBothTransactionsAndTheLinkSurvivesReopening()
    {
        var (store, ledger, _) = New();
        using var _ = store;
        var original = ledger.Post(Income(WalletId.Player(1), 700));
        var reversal = ledger.Post(new PostRequest
        {
            Kind = TxKind.Reversal, Actor = "admin:root", Reverses = original.TxId, Note = "oops",
            Entries = [new(WalletId.Player(1), -700), new(WalletId.World, 700)],
        });
        Assert.True(reversal.Ok);

        Assert.Equal(reversal.TxId, ledger.FindTransaction(original.TxId!)!.ReversedBy);
        Assert.Equal(original.TxId, ledger.FindTransaction(reversal.TxId!)!.Reverses);

        SqliteConnection.ClearAllPools();
        using var reopened = new SqliteEconomyStore(_factory, fullSync: false);
        Assert.Equal(reversal.TxId, reopened.GetTransaction(Session, original.TxId!)!.ReversedBy);
    }

    [Fact]
    public void ASecondReversalOrAnUnknownOriginalIsRefusedAndRollsBack()
    {
        var (store, ledger, _) = New();
        using var _ = store;
        var original = ledger.Post(Income(WalletId.Player(1), 700));
        PostRequest Reverse(string target) => new()
        {
            Kind = TxKind.Reversal, Actor = "admin:root", Reverses = target,
            Entries = [new(WalletId.Player(1), -100), new(WalletId.World, 100)],
        };

        Assert.True(ledger.Post(Reverse(original.TxId!)).Ok);
        var again = ledger.Post(Reverse(original.TxId!));
        Assert.False(again.Ok);
        Assert.Equal(PostReject.StoreFailure, again.Reason);
        var unknown = ledger.Post(Reverse("01NOSUCHTRANSACTION00000000"));
        Assert.False(unknown.Ok);

        // nothing leaked: 1 income + 1 reversal, balances follow the ledger
        Assert.Equal(2, store.QueryTransactions(Session, new LedgerQuery()).Count);
        Assert.Equal(600, ledger.BalanceOf(WalletId.Player(1)));
        Assert.Equal(0, ledger.TotalBalance());
    }

    [Fact]
    public void TheLedgerStaysAppendOnlyExceptForTheReversalLink()
    {
        var (store, ledger, _) = New();
        using var _ = store;
        var original = ledger.Post(Income(WalletId.Player(1), 700));
        using var db = _factory.Open();
        Assert.Throws<SqliteException>(() => db.Execute("UPDATE ledger_tx SET note = 'tampered' WHERE id = @id", new { id = original.TxId }));
        Assert.Throws<SqliteException>(() => db.Execute("DELETE FROM ledger_tx WHERE id = @id", new { id = original.TxId }));
        Assert.Throws<SqliteException>(() => db.Execute("UPDATE ledger_entries SET amount = 1 WHERE tx_id = @id", new { id = original.TxId }));
    }

    [Fact]
    public void QueriesFilterByWalletKindActorRefAndTimeAndPageByIdCursor()
    {
        var (store, ledger, time) = New();
        using var _ = store;
        var ids = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            ids.Add(ledger.Post(Income(WalletId.Player(i % 2 + 1), 10 + i, i % 3 == 0 ? "authority" : "player:1")).TxId!);
        }

        ledger.Post(new PostRequest
        {
            Kind = TxKind.AdminAdjust, Actor = "admin:root", Note = "gift",
            Entries = [new(WalletId.World, -5), new(WalletId.TeamPool(1), 5)],
        });

        Assert.Equal(3, store.QueryTransactions(Session, new LedgerQuery(Wallet: WalletId.Player(1))).Count);
        Assert.Single(store.QueryTransactions(Session, new LedgerQuery(Wallet: WalletId.TeamPool(1))));
        Assert.Single(store.QueryTransactions(Session, new LedgerQuery(Kind: TxKind.AdminAdjust)));
        Assert.Equal(2, store.QueryTransactions(Session, new LedgerQuery(Actor: "authority")).Count);
        Assert.Equal(6, store.QueryTransactions(Session, new LedgerQuery(RefType: "CreditDelta", RefId: 5)).Count);
        Assert.Empty(store.QueryTransactions(Session, new LedgerQuery(RefType: "CreditDelta", RefId: 6)));
        Assert.Equal(3, store.QueryTransactions(Session, new LedgerQuery(Kind: TxKind.GameIncome, Since: new DateTimeOffset(2026, 10, 1, 12, 4, 0, TimeSpan.Zero))).Count);
        Assert.Equal(2, store.QueryTransactions(Session, new LedgerQuery(Kind: TxKind.GameIncome, Until: new DateTimeOffset(2026, 10, 1, 12, 3, 0, TimeSpan.Zero))).Count);

        var newestFirst = store.QueryTransactions(Session, new LedgerQuery(Limit: 3));
        Assert.Equal(3, newestFirst.Count);
        Assert.True(string.CompareOrdinal(newestFirst[0].Id, newestFirst[1].Id) > 0);
        var next = store.QueryTransactions(Session, new LedgerQuery(Before: newestFirst[^1].Id, Limit: 100));
        Assert.Equal(4, next.Count);
        Assert.Empty(next.Select(t => t.Id).Intersect(newestFirst.Select(t => t.Id)));

        var ascending = store.QueryTransactions(Session, new LedgerQuery(Ascending: true, After: ids[1], Limit: 2));
        Assert.Equal([ids[2], ids[3]], ascending.Select(t => t.Id));

        var one = store.GetTransaction(Session, ids[0])!;
        Assert.Equal(2, one.Entries.Count);
        Assert.Equal(0, one.Entries.Sum(e => e.Amount));
        Assert.Equal(WalletId.World, one.Entries[0].Wallet);
        Assert.Equal("n", one.Note);
        Assert.Null(store.GetTransaction(Session, "NOPE"));
        Assert.Null(store.GetTransaction(2, ids[0]));
    }

    [Fact]
    public void TheEconomyEventQueryReturnsOnlyEconomyEventsNewestFirst()
    {
        var queries = new SqliteAdminQueries(_factory);
        using (var db = _factory.Open())
        {
            void Add(string type, string json, string ts) =>
                db.Execute("INSERT INTO session_events (session_id, ts, type, data_json) VALUES (1, @ts, @type, @json)", new { ts, type, json });
            Add("LoanStateChanged", "{\"loanId\":3,\"from\":\"Offered\",\"to\":\"Active\"}", "2026-10-01T12:00:01.0000000Z");
            Add("PlayerJoined", "{}", "2026-10-01T12:00:02.0000000Z");
            Add("AdminActionTaken", "{\"actor\":\"admin:root\",\"action\":\"economy.adjust\",\"target\":\"wallet:Player:1\",\"data\":{\"reason\":\"r\"}}", "2026-10-01T12:00:03.0000000Z");
            Add("AdminActionTaken", "{\"actor\":\"admin:root\",\"action\":\"player.kick\",\"target\":\"1\"}", "2026-10-01T12:00:04.0000000Z");
            Add("TradeStateChanged", "{\"tradeId\":9}", "2026-10-01T12:00:05.0000000Z");
        }

        var rows = queries.EconomyEvents(Session, null, 10);
        Assert.Equal(["TradeStateChanged", "AdminActionTaken", "LoanStateChanged"], rows.Select(r => r.Type));
        Assert.Contains("economy.adjust", rows[1].DataJson, StringComparison.Ordinal);
        Assert.Single(queries.EconomyEvents(Session, new DateTimeOffset(2026, 10, 1, 12, 0, 4, TimeSpan.Zero), 10));
        Assert.Single(queries.EconomyEvents(Session, null, 1));
        Assert.Empty(queries.EconomyEvents(2, null, 10));
    }
}
