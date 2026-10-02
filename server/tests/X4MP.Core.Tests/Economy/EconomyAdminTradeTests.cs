using X4MP.Core.Economy;
using X4MP.Proto;

namespace X4MP.Core.Tests.Economy;

/// <summary>M1-E6: reversing the credits of a settled trade (server-design 2.14 rule 8) and resolving InDoubt trades as an admin.</summary>
public sealed class EconomyAdminTradeTests
{
    private const string Admin = "admin:root";

    [Fact]
    public void ReversingTradeMoneyIsRefusedAndTheAuditorStaysClean()
    {
        var kit = new TradeKit();
        kit.World.AllowAssetTransfer = true;
        var buyerBefore = kit.Balance(3);
        var sellerBefore = kit.Balance(1);
        var trade = kit.Run(seller: 1, buyer: 3, TradeKit.ShipOfOne, price: 2_000);
        Assert.Equal(TradeConfirmOutcome.Settled, kit.Confirm(trade.Id));
        Assert.Equal(sellerBefore + 2_000, kit.Balance(1));
        Assert.Equal(buyerBefore - 2_000, kit.Balance(3));
        var settlement = kit.Kit.Store.Transactions.Single(t => t.Kind == TxKind.TradeSettle);

        foreach (var force in new[] { false, true })
        {
            var refused = kit.Service.AdminReverse(Admin, settlement.Id, "the ship was duped", force);
            Assert.Equal(EconomyAdminError.NotReversible, refused.Error);
            Assert.Contains("trade admin actions", refused.Detail, StringComparison.Ordinal);
        }

        foreach (var kind in new[] { TxKind.TradeEscrow })
        {
            var tx = kit.Kit.Store.Transactions.Single(t => t.Kind == kind);
            Assert.Equal(EconomyAdminError.NotReversible, kit.Service.AdminReverse(Admin, tx.Id, "x", force: true).Error);
        }

        Assert.DoesNotContain(kit.Kit.Store.Transactions, t => t.Kind == TxKind.Reversal);
        Assert.Equal(sellerBefore + 2_000, kit.Balance(1));
        kit.AssertSound();
    }

    [Fact]
    public void ReversingTheEscrowOfAnOpenTradeIsRefused()
    {
        var kit = new TradeKit();
        kit.World.AllowAssetTransfer = true;
        var trade = kit.Run(seller: 1, buyer: 3, TradeKit.ShipOfOne, price: 500);
        var escrow = kit.Kit.Store.Transactions.Single(t => t.Kind == TxKind.TradeEscrow);

        Assert.Equal(EconomyAdminError.NotReversible, kit.Service.AdminReverse(Admin, escrow.Id, "x").Error);
        Assert.Equal(500, kit.Escrow(trade.Id));
        kit.AssertSound();
    }

    [Fact]
    public void TheAdminSettlesOrRefundsAnInDoubtTradeAndTheTotalsCountIt()
    {
        var kit = new TradeKit();
        kit.World.AllowAssetTransfer = true;
        var trade = kit.Run(seller: 1, buyer: 3, TradeKit.ShipOfOne, price: 700);
        kit.Service.OnAuthorityGone();
        Assert.Equal(TradeState.InDoubt, kit.Service.FindTrade(trade.Id)!.State);
        Assert.Equal(1, kit.Service.Totals().InDoubtTrades);
        Assert.Equal(1, kit.Service.Totals().OpenTrades);
        Assert.Equal(700, kit.Service.Totals().InEscrow);

        var before = kit.Balance(3);
        var refunded = kit.Service.AdminResolveTrade(trade.Id, complete: false, Admin, "the authority never moved the ship");
        Assert.True(refunded.Ok);
        Assert.Equal(TradeState.RolledBack, refunded.Trade!.State);
        Assert.Equal(before + 700, kit.Balance(3));
        Assert.Equal(0, kit.Service.Totals().InDoubtTrades);

        Assert.Equal(EconomyReject.WrongState, kit.Service.AdminResolveTrade(trade.Id, true, Admin, "again").Reason);
        Assert.Equal(EconomyReject.UnknownTrade, kit.Service.AdminResolveTrade(404, true, Admin, null).Reason);
        kit.AssertSound();
    }
}
