using X4MP.Core.Economy;
using X4MP.Core.Events;

namespace X4MP.Core.Tests.Economy;

public sealed class EconomyAuditorTests
{
    private static LedgerKit KitWithMoney()
    {
        var kit = new LedgerKit();
        kit.Fund(1, 1000);
        kit.Fund(2, 250);
        return kit;
    }

    [Fact]
    public void CleanLedgerPassesAndDoesNotFreeze()
    {
        var kit = KitWithMoney();

        var report = kit.Auditor.RunNow();

        Assert.True(report.Ok);
        Assert.False(kit.Ledger.IsFrozen);
        Assert.Empty(kit.Events.OfType<EconomyFrozen>());
    }

    [Fact]
    public void InjectedBalanceCorruptionFreezesTheEconomyAndPublishesAnEvent()
    {
        var kit = KitWithMoney();
        kit.Store.TamperBalance(LedgerKit.Session, WalletId.Player(1), 999_999);

        var report = kit.Auditor.RunNow();

        Assert.False(report.Ok);
        Assert.True(kit.Ledger.IsFrozen);
        var frozen = Assert.Single(kit.Events.OfType<EconomyFrozen>());
        Assert.NotEmpty(frozen.Violations);
        Assert.Contains(kit.Events.OfType<AlertRaised>(), a => a.Code == "economy_frozen" && a.Severity == AlertSeverity.Critical);
        Assert.Equal(PostReject.EconomyFrozen, kit.Ledger.Post(new PostRequest
        {
            Kind = TxKind.AdminAdjust, Actor = "system", Entries = [new(WalletId.Player(1), 1), new(WalletId.World, -1)],
        }).Reason);
    }

    [Fact]
    public void CachedBalanceDifferingFromTheDatabaseIsAViolation()
    {
        var kit = KitWithMoney();
        // Same corruption seen from the other side: the database moved, the entries did not.
        kit.Store.TamperBalance(LedgerKit.Session, WalletId.World, -1);

        Assert.False(kit.Auditor.RunNow().Ok);
        Assert.True(kit.Ledger.IsFrozen);
    }

    [Fact]
    public void ExtraChecksCanFreezeToo()
    {
        var kit = KitWithMoney();
        kit.Auditor.AddCheck(_ => ["terminal loan 5 still holds escrow"]);

        var report = kit.Auditor.RunNow();

        Assert.False(report.Ok);
        Assert.Contains("terminal loan 5 still holds escrow", report.Violations);
        Assert.True(kit.Ledger.IsFrozen);
    }

    [Fact]
    public void TickRunsAtOnceThenOnlyAfterTheInterval()
    {
        var kit = KitWithMoney();
        var start = kit.Time.GetTimestamp();

        kit.Auditor.Tick(start);
        var first = kit.Auditor.LastReport;
        Assert.NotNull(first);

        kit.Auditor.Tick(start + (30 * kit.Time.TimestampFrequency));
        Assert.Same(first, kit.Auditor.LastReport);

        kit.Auditor.Tick(start + (61 * kit.Time.TimestampFrequency));
        Assert.NotSame(first, kit.Auditor.LastReport);
    }
}
