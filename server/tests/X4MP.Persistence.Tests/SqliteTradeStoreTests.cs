using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Economy;
using X4MP.Proto;

namespace X4MP.Persistence.Tests;

/// <summary>The trades table and its lock table (<c>0007_trades.sql</c>), and a trade engine running on top of them.</summary>
public sealed class SqliteTradeStoreTests : IDisposable
{
    private const long Session = 1;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-trades-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;

    public SqliteTradeStoreTests()
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

    private static TradeRecord Trade(long id, TradeState state = TradeState.Proposed, params uint[] ships) => new()
    {
        Id = id,
        Initiator = 1,
        Counterparty = 2,
        State = state,
        InitiatorGives = [.. ships.Select(TradeItemModel.Ship)],
        CounterpartyGives = [TradeItemModel.Credits(500)],
        CreatedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
        ExpiresAt = new DateTimeOffset(2026, 10, 1, 12, 5, 0, TimeSpan.Zero),
        Requests = [new TradeRequestMark(1, "k" + id, "abc")],
    };

    [Fact]
    public void ATradeRoundTripsWithItsItemsRequestsAndLocks()
    {
        using var store = new SqliteTradeStore(_factory);
        var trade = Trade(1, TradeState.Transferring, 10, 11);
        trade.EscrowAmount = 500;
        trade.Payer = 2;
        trade.NextCheckAt = trade.CreatedAt.AddSeconds(30);

        store.Save(Session, trade, trade.LockedAssets);
        var loaded = Assert.Single(store.Load(Session, 10));

        Assert.Equal(trade.ToJson(), loaded.ToJson());
        Assert.Equal(TradeState.Transferring, loaded.State);
        Assert.Equal([10u, 11u], loaded.LockedAssets);
        Assert.Equal(1, store.MaxId(Session));
        Assert.Equal(2, Scalar<long>("SELECT COUNT(*) FROM trade_locks"));
    }

    [Fact]
    public void TheUniqueLockKeyRefusesASecondOpenTradeOnTheSameAssetAndRollsEverythingBack()
    {
        using var store = new SqliteTradeStore(_factory);
        store.Save(Session, Trade(1, TradeState.Proposed, 10), [10u]);

        var conflict = Assert.Throws<TradeLockConflictException>(() => store.Save(Session, Trade(2, TradeState.Proposed, 11, 10), [11u, 10u]));

        Assert.Equal((10u, 1L), (conflict.Asset, conflict.Holder));
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM trades"));     // trade 2 was not stored
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM trade_locks")); // and 11 is not locked
        store.Save(Session, Trade(1, TradeState.Completed, 10), []);       // the first one ends: the asset is free
        store.Save(Session, Trade(2, TradeState.Proposed, 11, 10), [11u, 10u]);
        Assert.Equal(2, Scalar<long>("SELECT COUNT(*) FROM trade_locks"));
    }

    [Fact]
    public void LoadReturnsOpenTradesAndTheNewestFinishedOnes()
    {
        using var store = new SqliteTradeStore(_factory);
        for (var i = 1; i <= 5; i++)
        {
            store.Save(Session, Trade(i, TradeState.Completed), []);
        }

        store.Save(Session, Trade(6, TradeState.InDoubt, 60), [60u]);
        store.Save(Session, Trade(7, TradeState.Proposed, 70), [70u]);

        var ids = store.Load(Session, 2).Select(t => t.Id).ToList();

        Assert.Equal([4L, 5L, 6L, 7L], ids);
        Assert.Equal(7, store.MaxId(Session));
        Assert.Empty(store.Load(2, 10)); // another session sees nothing
    }

    [Fact]
    public void ATradeSurvivesAServerRestartThroughTheEngine()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var economyStore = new SqliteEconomyStore(_factory);
        var ledger = new EconomyLedger(Session, economyStore, time);
        var options = new EconomyOptions { StartingCredits = 1000, CreditMode = CreditMode.PerPlayer, TradeScope = EconomyScope.Anyone, TradeRequiresProximity = false };
        var service = new EconomyService(ledger, economyStore, null, () => options, time) { TradeStore = new SqliteTradeStore(_factory), TradeWorld = new OneShipWorld() };
        service.SendTransferOrder = _ => true;
        service.Start();
        service.EnsurePlayer(1);
        service.EnsurePlayer(2);
        service.LoadTrades();

        // players share implicit team 1: Shared mode would reject; PerPlayer is forced above
        var proposed = service.ProposeTrade(1, "p", 2, [TradeItemModel.Ship(10)], [TradeItemModel.Credits(300)], 300, null);
        Assert.True(proposed.Ok, proposed.Reason + " " + proposed.Detail);
        Assert.True(service.AcceptTrade(2, "a", proposed.Trade!.Id, 1, 0).Ok);
        Assert.Equal(300, ledger.BalanceOf(TradeRecord.EscrowOf(proposed.Trade.Id)));

        // "restart": new store connections, new ledger, new engine over the same database
        var ledger2 = new EconomyLedger(Session, new SqliteEconomyStore(_factory), time);
        var service2 = new EconomyService(ledger2, new SqliteEconomyStore(_factory), null, () => options, time) { TradeStore = new SqliteTradeStore(_factory), TradeWorld = new OneShipWorld() };
        service2.Start();
        service2.LoadTrades();

        var again = Assert.Single(service2.Trades);
        Assert.Equal(TradeState.Transferring, again.State);
        Assert.True(service2.IsAssetLocked(10));
        Assert.Equal(300, ledger2.BalanceOf(TradeRecord.EscrowOf(again.Id)));
        Assert.Equal(TradeConfirmOutcome.Settled, service2.OnAssetTransferConfirm(new AssetTransferConfirmT { TradeId = new Id128T { Lo = (ulong)again.Id }, Ok = true }));
        Assert.Equal(1300, ledger2.BalanceOf(WalletId.Player(1)));
        Assert.Equal(0, ledger2.TotalBalance());
        Assert.False(service2.IsAssetLocked(10));
        Assert.Equal(0, Scalar<long>("SELECT COUNT(*) FROM trade_locks"));
    }

    private T Scalar<T>(string sql)
    {
        using var db = _factory.Open();
        return db.ExecuteScalar<T>(sql)!;
    }

    private sealed class OneShipWorld : ITradeWorld
    {
        public bool TryGetAsset(uint netId, out TradeAssetInfo asset)
        {
            asset = new TradeAssetInfo(netId, EntityKind.ShipM, 1, 1, 5, false);
            return netId == 10;
        }

        public bool TryGetPlayerShip(int playerId, out uint netId, out ushort sector)
        {
            netId = (uint)(900 + playerId);
            sector = 5;
            return true;
        }

        public long? CargoAmount(uint container, uint wareRef) => null;

        public string? DenyGive(int player, in TradeAssetInfo asset, bool crossTeam, bool shipTransfer) => null;

        public string? DenyReceive(int player, in TradeAssetInfo asset) => null;

        public void SetOwner(uint netId, int team, int player, Id128T cause)
        {
        }
    }
}
