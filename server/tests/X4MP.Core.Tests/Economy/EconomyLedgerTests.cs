using X4MP.Core.Economy;
using X4MP.Core.Events;

namespace X4MP.Core.Tests.Economy;

public sealed class EconomyLedgerTests
{
    private static readonly WalletId A = WalletId.Player(1);
    private static readonly WalletId B = WalletId.Player(2);
    private static readonly WalletId Pool = WalletId.TeamPool(1);

    private static LedgerKit Kit() => new();

    private static PostRequest Credit(WalletId to, long amount, string? requestId = null, byte[]? hash = null, PostOptions flags = PostOptions.None) => new()
    {
        Kind = TxKind.GameIncome,
        Actor = "authority",
        PlayerId = 1,
        RequestId = requestId,
        PayloadHash = hash,
        Entries = [new(WalletId.World, -amount), new(to, amount)],
        Flags = flags,
    };

    [Fact]
    public void BalancedPostingCommitsAndUpdatesBalances()
    {
        var kit = Kit();
        var outcome = kit.Ledger.Post(Credit(A, 500));

        Assert.True(outcome.Ok);
        Assert.NotNull(outcome.TxId);
        Assert.Equal(500, kit.Balance(A));
        Assert.Equal(-500, kit.Balance(WalletId.World));
        Assert.Equal(0, kit.Ledger.TotalBalance());
        var tx = Assert.Single(kit.Store.Transactions);
        Assert.Equal(0, tx.Entries.Sum(e => e.Amount));
        Assert.Equal(500, tx.Entries.Single(e => e.Wallet == A).BalanceAfter);
    }

    [Fact]
    public void UnbalancedOrMalformedPostingsAreRejectedAndChangeNothing()
    {
        var kit = Kit();

        Assert.Equal(PostReject.Unbalanced, kit.Ledger.Post(new PostRequest { Kind = TxKind.AdminAdjust, Actor = "system", Entries = [new(A, 10), new(B, -9)] }).Reason);
        Assert.Equal(PostReject.InvalidEntries, kit.Ledger.Post(new PostRequest { Kind = TxKind.AdminAdjust, Actor = "system", Entries = [new(A, 10)] }).Reason);
        Assert.Equal(PostReject.InvalidEntries, kit.Ledger.Post(new PostRequest { Kind = TxKind.AdminAdjust, Actor = "system", Entries = [new(A, 10), new(A, -10)] }).Reason);
        Assert.Equal(PostReject.AmountInvalid, kit.Ledger.Post(new PostRequest { Kind = TxKind.AdminAdjust, Actor = "system", Entries = [new(A, 0), new(B, 0)] }).Reason);
        Assert.Equal(PostReject.AmountInvalid, kit.Ledger.Post(new PostRequest { Kind = TxKind.AdminAdjust, Actor = "system", Entries = [new(A, long.MaxValue), new(B, -long.MaxValue)] }).Reason);
        Assert.Empty(kit.Store.Transactions);
        Assert.Empty(kit.Ledger.Wallets);
    }

    [Fact]
    public void OnlyWorldMayGoNegativeWithoutAFlag()
    {
        var kit = Kit();
        kit.Ledger.Post(Credit(A, 100));

        var overdraw = kit.Ledger.Post(new PostRequest { Kind = TxKind.Donate, Actor = "player:1", Entries = [new(A, -101), new(B, 101)] });
        Assert.Equal(PostReject.InsufficientFunds, overdraw.Reason);
        Assert.Equal(100, kit.Balance(A));

        var pool = kit.Ledger.Post(new PostRequest { Kind = TxKind.PoolDeposit, Actor = "player:1", Entries = [new(Pool, -1), new(A, 1)] });
        Assert.Equal(PostReject.InsufficientFunds, pool.Reason); // a pool never goes negative, not even with the game flag
        var forced = kit.Ledger.Post(new PostRequest { Kind = TxKind.GameSpend, Actor = "authority", Flags = PostOptions.AllowOverdraw, Entries = [new(A, -150), new(WalletId.World, 150)] });
        Assert.True(forced.Ok);
        Assert.Equal(-50, kit.Balance(A));
        Assert.True(kit.Ledger.Find(A)!.Overdrawn);
    }

    [Fact]
    public void OverdrawnWalletRaisesAnAlertOnceAndClearsWhenBackAtZero()
    {
        var kit = Kit();
        kit.Ledger.Post(Credit(A, 100));
        kit.Ledger.Post(new PostRequest { Kind = TxKind.GameSpend, Actor = "authority", Flags = PostOptions.AllowOverdraw, Entries = [new(A, -150), new(WalletId.World, 150)] });
        kit.Ledger.Post(new PostRequest { Kind = TxKind.GameSpend, Actor = "authority", Flags = PostOptions.AllowOverdraw, Entries = [new(A, -10), new(WalletId.World, 10)] });

        var alert = Assert.Single(kit.Events.OfType<AlertRaised>());
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Empty(kit.Events.OfType<AlertCleared>());

        kit.Ledger.Post(Credit(A, 60));
        Assert.Single(kit.Events.OfType<AlertCleared>());
    }

    [Fact]
    public void ReplayReturnsTheIdenticalResultAndBooksOnce()
    {
        var kit = Kit();
        var hash = PayloadHasher.Hash("Test", 1, 500);

        var first = kit.Ledger.Post(Credit(A, 500, "req-1", hash));
        var replay = kit.Ledger.Post(Credit(A, 500, "req-1", hash));

        Assert.True(first.Ok);
        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.TxId, replay.TxId);
        Assert.Equal(first.Balances, replay.Balances);
        Assert.Equal(500, kit.Balance(A));
        Assert.Single(kit.Store.Transactions);
    }

    [Fact]
    public void ReusingARequestIdWithADifferentPayloadIsRejectedAndCounted()
    {
        var kit = Kit();
        kit.Ledger.Post(Credit(A, 500, "req-1", PayloadHasher.Hash("Test", 1, 500)));

        var reuse = kit.Ledger.Post(Credit(A, 900, "req-1", PayloadHasher.Hash("Test", 1, 900)));

        Assert.Equal(PostReject.PayloadMismatch, reuse.Reason);
        Assert.Equal(1, kit.Ledger.RequestIdReuseCount);
        Assert.Equal(500, kit.Balance(A));
        Assert.Single(kit.Store.Transactions);
    }

    [Fact]
    public void RequestIdsAreScopedPerPlayer()
    {
        var kit = Kit();
        var hash = PayloadHasher.Hash("Test", 5);
        Assert.True(kit.Ledger.Post(Credit(A, 5, "same", hash) with { PlayerId = 1 }).Ok);
        var other = kit.Ledger.Post(Credit(A, 5, "same", hash) with { PlayerId = 2 });
        Assert.False(other.Replayed);
        Assert.Equal(10, kit.Balance(A));
    }

    [Fact]
    public void FrozenEconomyRejectsEverythingUntilUnfrozen()
    {
        var kit = Kit();
        kit.Ledger.Freeze("test");

        Assert.Equal(PostReject.EconomyFrozen, kit.Ledger.Post(Credit(A, 5)).Reason);
        Assert.Single(kit.Events.OfType<EconomyFrozen>());
        Assert.Contains(kit.Events.OfType<AlertRaised>(), a => a.Severity == AlertSeverity.Critical);

        kit.Ledger.Unfreeze("admin:root");
        Assert.True(kit.Ledger.Post(Credit(A, 5)).Ok);
        Assert.Contains(kit.Events.OfType<AdminActionTaken>(), a => a.Action == "economy.unfreeze" && a.Actor == "admin:root");
    }

    [Fact]
    public void FrozenWalletRejectsRequestsButStillBooksWithTheBypassFlag()
    {
        var kit = Kit();
        kit.Ledger.Post(Credit(A, 100));
        Assert.True(kit.Ledger.SetWalletFrozen(A, true, "investigation"));

        var donate = kit.Ledger.Post(new PostRequest { Kind = TxKind.Donate, Actor = "player:1", Entries = [new(A, -10), new(B, 10)] });
        Assert.Equal(PostReject.WalletFrozen, donate.Reason);
        Assert.True(kit.Ledger.Post(Credit(A, 7, flags: PostOptions.BypassWalletFreeze)).Ok);
        Assert.Equal(107, kit.Balance(A));
    }

    [Fact]
    public void FailingStoreRejectsAndLeavesTheCacheUntouched()
    {
        var kit = Kit();
        kit.Ledger.Post(Credit(A, 100));
        kit.Store.FailCommits = true;

        var outcome = kit.Ledger.Post(Credit(A, 50, "r", PayloadHasher.Hash("t", 50)));

        Assert.Equal(PostReject.StoreFailure, outcome.Reason);
        Assert.Equal(100, kit.Balance(A));
        kit.Store.FailCommits = false;
        Assert.True(kit.Ledger.Post(Credit(A, 50, "r", PayloadHasher.Hash("t", 50))).Ok); // not recorded as done
        Assert.Equal(150, kit.Balance(A));
    }

    [Fact]
    public void LoadRestoresWalletsSequencesAndLayout()
    {
        var kit = Kit();
        kit.Ledger.Post(Credit(A, 100) with { DeltaSeq = (1, 9), Layout = new EconomyLayout("Shared", new Dictionary<int, int?> { [1] = 3 }) });

        var reloaded = new EconomyLedger(LedgerKit.Session, kit.Store);
        reloaded.Load();

        Assert.Equal(100, reloaded.BalanceOf(A));
        Assert.Equal(9UL, reloaded.LastDeltaSeq(1));
        Assert.Equal("Shared", reloaded.Layout.AppliedMode);
        Assert.Equal(3, reloaded.Layout.PlayerTeams[1]);
    }

    [Fact]
    public void UlidsAreUniqueAndSortInCreationOrder()
    {
        var ulid = new Ulid();
        var now = DateTimeOffset.UnixEpoch.AddYears(56);
        var ids = Enumerable.Range(0, 5000).Select(i => ulid.Next(now.AddMilliseconds(i / 50))).ToList();

        Assert.All(ids, id => Assert.Equal(26, id.Length));
        Assert.Equal(ids, ids.Order(StringComparer.Ordinal).ToList());
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
