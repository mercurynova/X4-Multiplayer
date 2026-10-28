using X4MP.Core.Economy;
using X4MP.Core.Events;
using X4MP.Core.Saves;
using X4MP.Proto;
using LoanState = X4MP.Core.Economy.LoanState;
using TeamRelation = X4MP.Core.Teams.TeamRelation;
using WalletKind = X4MP.Core.Economy.WalletKind;

namespace X4MP.Core.Tests.Economy;

/// <summary>ADR-053 (F13b): the ledger follows the checkpoint when the authority loads an older one.</summary>
public sealed class EconomyRollbackTests
{
    private static readonly Dictionary<int, int?> Members = new() { [1] = 1, [2] = 1, [3] = 2 };

    private static EconomyKit Kit(long starting = 0, bool ensure = true)
    {
        var options = new EconomyOptions { StartingCredits = starting, CreditMode = CreditMode.PerPlayer, DonateScope = EconomyScope.Anyone, LoanScope = EconomyScope.Anyone };
        var kit = new EconomyKit(options);
        kit.Teams.Set([1, 2], Members);
        kit.Teams.SetRelation(1, 2, TeamRelation.Allied);
        if (ensure)
        {
            kit.Service.EnsurePlayer(1);
            kit.Service.EnsurePlayer(2);
        }

        return kit;
    }

    private static void Spend(EconomyKit kit, int player, long amount) =>
        Assert.True(kit.Service.BookCreditDelta(player, false, new CreditDeltaT { Amount = -amount }).Booked);

    private static void Earn(EconomyKit kit, int player, long amount) =>
        Assert.True(kit.Service.BookCreditDelta(player, false, new CreditDeltaT { Amount = amount }).Booked);

    private static LedgerRollbackResult RollBack(EconomyKit kit, LedgerMark? mark, string reason = "test")
    {
        var result = kit.Service.RollBackToMark(mark, "cp1", reason);
        Assert.True(result.Performed || mark is null, result.Skipped);
        return result;
    }

    private static void AssertSound(EconomyKit kit)
    {
        Assert.Equal(0, kit.Ledger.TotalBalance());
        var report = kit.Auditor.RunNow();
        Assert.True(report.Ok, string.Join("; ", report.Violations));
        Assert.All(kit.Store.Transactions, t => Assert.Equal(0, t.Entries.Sum(e => e.Amount)));
    }

    [Fact]
    public void TenMillionSpentOnShipsAfterTheCheckpointComesBack()
    {
        var kit = Kit();
        Earn(kit, 1, 12_000_000);
        var mark = kit.Service.CaptureMark();

        Spend(kit, 1, 10_000_000);
        Assert.Equal(2_000_000, kit.Balance(WalletId.Player(1)));
        var spend = kit.Store.Transactions[^1];

        var result = RollBack(kit, mark, "authority reloads the checkpoint");

        Assert.Equal(12_000_000, kit.Balance(WalletId.Player(1)));
        Assert.Equal(1, result.Reversed);
        Assert.Empty(result.Drift);
        Assert.False(result.Aggregated);
        // Append-only: the spend is still there, linked to the reversal that says why.
        var reversal = kit.Store.Transactions[^1];
        Assert.Equal(TxKind.Reversal, reversal.Kind);
        Assert.Equal(spend.Id, reversal.Reverses);
        Assert.Contains("rollback to checkpoint cp1: authority reloads the checkpoint", reversal.Note);
        Assert.Equal(reversal.Id, kit.Store.GetTransaction(EconomyKit.Session, spend.Id)!.ReversedBy);
        Assert.True(result.NewMark!.Seq > mark.Seq);
        AssertSound(kit);
    }

    [Fact]
    public void IncomeAfterTheCheckpointIsRemoved()
    {
        var kit = Kit();
        Earn(kit, 1, 5_000);
        var mark = kit.Service.CaptureMark();
        Earn(kit, 1, 7_000);
        Earn(kit, 2, 3_000);

        RollBack(kit, mark);

        Assert.Equal(5_000, kit.Balance(WalletId.Player(1)));
        Assert.Equal(0, kit.Balance(WalletId.Player(2)));
        AssertSound(kit);
    }

    [Fact]
    public void ATeammateTransferALoanRepaymentAndAPoolDepositAreReversed()
    {
        var kit = Kit();
        Earn(kit, 1, 50_000);
        var offer = kit.Service.OfferLoan(1, "o1", 2, 1_000, 1_100, 3600, 300, 0, "m");
        Assert.True(offer.Ok, offer.Reason.ToString());
        var id = offer.Loan!.Id;
        Assert.True(kit.Service.RespondLoan(2, "a1", id, true).Ok);
        var loanAtMark = kit.Service.FindLoan(id)!;
        var mark = kit.Service.CaptureMark();
        long one = kit.Balance(WalletId.Player(1));
        long two = kit.Balance(WalletId.Player(2));

        Assert.True(kit.Service.RepayLoan(2, "r1", id, 400).Ok);
        Assert.True(kit.Service.Transfer(1, "t1", 2, 500).Ok);
        Assert.True(kit.Service.PoolDeposit(1, "p1", 300).Ok);
        Assert.NotEqual(loanAtMark, kit.Service.FindLoan(id));

        var result = RollBack(kit, mark);

        Assert.Equal(one, kit.Balance(WalletId.Player(1)));
        Assert.Equal(two, kit.Balance(WalletId.Player(2)));
        Assert.Equal(0, kit.Balance(WalletId.TeamPool(1)));
        Assert.Equal(loanAtMark, kit.Service.FindLoan(id)); // outstanding, repaid and state are what they were at the mark
        Assert.Equal(3, result.Reversed);
        Assert.Empty(result.Drift);
        AssertSound(kit);
    }

    [Fact]
    public void ALoanOfferedAfterTheCheckpointIsCancelledAndItsEscrowReturned()
    {
        var kit = Kit();
        Earn(kit, 1, 20_000);
        var mark = kit.Service.CaptureMark();
        var offer = kit.Service.OfferLoan(1, "o1", 2, 5_000, 5_500, 3600, 300, 0, "m");
        Assert.True(offer.Ok);

        RollBack(kit, mark);

        var loan = kit.Service.FindLoan(offer.Loan!.Id)!;
        Assert.Equal(LoanState.Cancelled, loan.State);
        Assert.Contains("rolled back to checkpoint cp1", loan.CloseReason);
        Assert.Equal(20_000, kit.Balance(WalletId.Player(1)));
        Assert.Equal(0, kit.Balance(loan.EscrowWallet));
        AssertSound(kit);
    }

    [Fact]
    public void AdminGrantsAndRemovalsSurviveTheRollbackAndArePresentInTheAudit()
    {
        var kit = Kit();
        Earn(kit, 1, 10_000);
        var mark = kit.Service.CaptureMark();
        Spend(kit, 1, 4_000);
        Assert.True(kit.Service.AdminAdjust("admin:root", WalletId.Player(1), 5_000, "bonus").Ok);
        Assert.True(kit.Service.AdminAdjust("admin:root", WalletId.Player(2), 700, "gift").Ok);
        Earn(kit, 1, 9_000);

        var result = RollBack(kit, mark);

        Assert.Equal(15_000, kit.Balance(WalletId.Player(1))); // the balance at the mark plus the grant
        Assert.Equal(700, kit.Balance(WalletId.Player(2)));
        Assert.Equal(2, result.Kept);
        Assert.Equal(2, result.Reversed);
        var audit = kit.Events.OfType<AdminActionTaken>().Single(e => e.Action == "economy.rollback");
        Assert.Equal("checkpoint:cp1", audit.Target);
        Assert.Equal("test", audit.Data!["reason"]);
        Assert.NotNull(audit.Data["kept"]);
        AssertSound(kit);
    }

    [Fact]
    public void AnAdminReversalOfAnOlderTransactionIsKept()
    {
        var kit = Kit();
        Earn(kit, 1, 10_000);
        Assert.True(kit.Service.Transfer(1, "t0", 2, 1_000).Ok);
        var transfer = kit.Store.Transactions[^1];
        var mark = kit.Service.CaptureMark();
        Assert.True(kit.Service.AdminReverse("admin:root", transfer.Id, "mistake").Ok);
        Earn(kit, 2, 50);

        RollBack(kit, mark);

        Assert.Equal(10_000, kit.Balance(WalletId.Player(1))); // the admin's decision stands
        Assert.Equal(0, kit.Balance(WalletId.Player(2)));
        AssertSound(kit);
    }

    [Fact]
    public void APlayerWhoJoinedAfterTheCheckpointKeepsOneSetOfStartingCreditsAndGetsNoSecondOnRejoin()
    {
        var kit = Kit(starting: 1_000, ensure: false);
        kit.Teams.Set([1, 2], new Dictionary<int, int?> { [1] = 1, [2] = 1 });
        kit.Service.EnsurePlayer(1);
        var mark = kit.Service.CaptureMark();

        kit.Service.EnsurePlayer(2);
        Assert.Equal(1_000, kit.Balance(WalletId.Player(2)));
        Spend(kit, 2, 600);
        Earn(kit, 2, 50_000);
        Earn(kit, 1, 77);

        var result = RollBack(kit, mark);

        Assert.Equal(1_000, kit.Balance(WalletId.Player(2))); // the starting credits, none of what the player did in the lost timeline
        Assert.Equal(1_000, kit.Balance(WalletId.Player(1)));
        Assert.Equal(1, result.Kept);
        Assert.True(kit.Service.IsKnown(2));

        var rejoin = kit.Service.EnsurePlayer(2); // the node comes back after the reload
        Assert.True(rejoin.Replayed);
        Assert.Equal(1_000, kit.Balance(WalletId.Player(2)));
        Assert.Single(kit.Store.Transactions, t => t.Kind == TxKind.StartingCredits && t.Entries.Any(e => e.Wallet == WalletId.Player(2)));
        AssertSound(kit);
    }

    [Fact]
    public void ACheckpointWithoutAMarkReversesNothingAndWarns()
    {
        var kit = Kit();
        Earn(kit, 1, 1_000);
        Spend(kit, 1, 400);
        int before = kit.Store.Transactions.Count;

        var result = kit.Service.RollBackToMark(null, "old", "authority reloads the checkpoint");

        Assert.False(result.Performed);
        Assert.Contains("no ledger mark", result.Skipped);
        Assert.Equal(600, kit.Balance(WalletId.Player(1)));
        Assert.Equal(before, kit.Store.Transactions.Count);
        Assert.Contains(kit.Events.OfType<AlertRaised>(), a => a.Code == "economy_rollback_unknown_mark" && a.Severity == AlertSeverity.Warning);
        AssertSound(kit);
    }

    [Fact]
    public void ARepeatedRollbackChangesNothingMore()
    {
        var kit = Kit();
        Earn(kit, 1, 8_000);
        var mark = kit.Service.CaptureMark();
        Spend(kit, 1, 3_000);
        Assert.True(kit.Service.AdminAdjust("admin:root", WalletId.Player(1), 10, "x").Ok);

        var first = RollBack(kit, mark);
        int count = kit.Store.Transactions.Count;
        var second = RollBack(kit, first.NewMark); // a second authority connection: the checkpoint now carries the restored mark
        var again = RollBack(kit, mark); // even the original mark finds nothing new to undo: the pairs cancel and the admin grant stays

        Assert.Equal(8_010, kit.Balance(WalletId.Player(1)));
        Assert.Equal(0, second.Reversed);
        Assert.Equal(0, again.Reversed);
        Assert.Equal(count, kit.Store.Transactions.Count);
        AssertSound(kit);
    }

    [Fact]
    public void DeltaSequencesAreNotRolledBackSoTheNodeNeverResendsWhatIsBooked()
    {
        var kit = Kit();
        Assert.True(kit.Service.BookCreditDelta(1, true, new CreditDeltaT { Amount = 100, Seq = 1, PlayerId = 1 }).Booked);
        var mark = kit.Service.CaptureMark();
        Assert.True(kit.Service.BookCreditDelta(1, true, new CreditDeltaT { Amount = 200, Seq = 2, PlayerId = 1 }).Booked);

        RollBack(kit, mark);

        Assert.Equal(100, kit.Balance(WalletId.Player(1)));
        Assert.Equal(2UL, kit.Ledger.LastDeltaSeq(1));
        var replay = kit.Service.BookCreditDelta(1, true, new CreditDeltaT { Amount = 200, Seq = 2, PlayerId = 1 });
        Assert.True(replay.Duplicate);
        Assert.Equal(100, kit.Balance(WalletId.Player(1)));
    }

    [Fact]
    public void ATeamMoveAfterTheCheckpointIsUndoneAndThenAppliedAgainOnTheRestoredBalances()
    {
        var kit = Kit();
        Earn(kit, 1, 4_000);
        Earn(kit, 2, 6_000);
        var mark = kit.Service.CaptureMark();
        Spend(kit, 2, 1_000);
        kit.Options.CreditMode = CreditMode.Shared; // an admin switches the mode after the checkpoint (Phase is not live: applied at once)
        kit.Service.Reconcile(confirm: true, actor: "admin:root");
        Assert.Equal(EffectiveCreditMode.Shared, kit.Service.AppliedMode);
        Assert.Equal(9_000, kit.Balance(WalletId.TeamShared(1)));

        RollBack(kit, mark);

        // The spend is gone (10,000 at the mark) and the admin's mode switch stands, applied on the restored balances.
        Assert.Equal(EffectiveCreditMode.Shared, kit.Service.AppliedMode);
        Assert.Equal(10_000, kit.Balance(WalletId.TeamShared(1)));
        Assert.Equal(0, kit.Balance(WalletId.Player(1)));
        Assert.Equal(0, kit.Balance(WalletId.Player(2)));
        AssertSound(kit);
    }

    [Fact]
    public void ASettledTradeAfterTheCheckpointIsUndoneAndTheTradeRolledBack()
    {
        var trades = new TradeKit();
        var mark = trades.Service.CaptureMark();
        var trade = trades.Run(1, 2, TradeKit.ShipOfOne, 4_000);
        Assert.Equal(TradeConfirmOutcome.Settled, trades.Confirm(trade.Id));
        Assert.Equal(14_000, trades.Balance(1));

        var result = trades.Service.RollBackToMark(mark, "cp1", "test");

        Assert.True(result.Performed);
        Assert.Equal(10_000, trades.Balance(1));
        Assert.Equal(10_000, trades.Balance(2));
        Assert.Equal(TradeState.RolledBack, trades.Service.FindTrade(trade.Id)!.State);
        Assert.Empty(result.Drift);
        trades.AssertSound();
    }

    [Fact]
    public void ATradeThatWasInFlightAtTheCheckpointIsRefundedBecauseTheAuthorityLostTheOrder()
    {
        var trades = new TradeKit();
        var trade = trades.Run(1, 2, TradeKit.ShipOfOne, 4_000);
        Assert.Equal(4_000, trades.Escrow(trade.Id));
        var mark = trades.Service.CaptureMark();
        Assert.Equal(TradeConfirmOutcome.Settled, trades.Confirm(trade.Id));

        var result = trades.Service.RollBackToMark(mark, "cp1", "test");

        Assert.True(result.Performed);
        Assert.Equal(TradeState.RolledBack, trades.Service.FindTrade(trade.Id)!.State);
        Assert.Equal(0, trades.Escrow(trade.Id));
        Assert.Equal(10_000, trades.Balance(2)); // the payer has the escrowed credits back
        Assert.Equal(10_000, trades.Balance(1));
        Assert.False(trades.Service.IsAssetLocked(TradeKit.ShipOfOne));
        trades.AssertSound();
    }

    [Fact]
    public void ATradeProposedAfterTheCheckpointNeverHappened()
    {
        var trades = new TradeKit();
        var mark = trades.Service.CaptureMark();
        var proposed = trades.Sell(1, 2, TradeKit.ShipOfOne, 2_000);
        Assert.True(proposed.Ok);

        trades.Service.RollBackToMark(mark, "cp1", "test");

        Assert.Equal(TradeState.RolledBack, trades.Service.FindTrade(proposed.Trade!.Id)!.State);
        Assert.False(trades.Service.IsAssetLocked(TradeKit.ShipOfOne));
        trades.AssertSound();
    }

    [Fact]
    public void AFrozenEconomyIsNotTouchedAndTheFailureIsAlerted()
    {
        var kit = Kit();
        Earn(kit, 1, 1_000);
        var mark = kit.Service.CaptureMark();
        Spend(kit, 1, 300);
        kit.Ledger.Freeze("test");

        var result = kit.Service.RollBackToMark(mark, "cp1", "test");

        Assert.False(result.Performed);
        Assert.Equal(700, kit.Balance(WalletId.Player(1)));
        Assert.Contains(kit.Events.OfType<AlertRaised>(), a => a.Code == "economy_rollback_failed");
    }

    [Fact]
    public void ARefusalMidwayFinishesTheRestAsOneNetTransactionSoNothingIsLeftHalfUndone()
    {
        var kit = Kit();
        Earn(kit, 1, 1_000);
        var mark = kit.Service.CaptureMark();
        Spend(kit, 1, 100);
        Spend(kit, 1, 200);
        Spend(kit, 1, 300);
        kit.Store.FailCommitsAfter(1, failures: 1); // the first compensation is posted, the second is refused once

        var result = kit.Service.RollBackToMark(mark, "cp1", "test");

        Assert.True(result.Performed, result.Skipped);
        Assert.Null(result.Skipped);
        Assert.True(result.Aggregated);
        Assert.Equal(2, result.Reversed); // one per-transaction compensation, one net transaction for the other two
        Assert.Equal(1_000, kit.Balance(WalletId.Player(1)));
        Assert.Empty(result.Drift);
        AssertSound(kit);
    }

    [Fact]
    public void ARefusalMidwayThatCannotBeWorkedAroundReportsThePartialRollbackSoTheNodesLearnTheChangedBalances()
    {
        var kit = Kit();
        Earn(kit, 1, 1_000);
        var mark = kit.Service.CaptureMark();
        Spend(kit, 1, 100);
        Spend(kit, 1, 200);
        Spend(kit, 1, 300);
        kit.Store.FailCommitsAfter(1); // the first compensation is posted, everything after is refused

        var result = kit.Service.RollBackToMark(mark, "cp1", "test");

        Assert.True(result.Performed); // the ledger did change: the module must push the balances
        Assert.NotNull(result.Skipped);
        Assert.Equal(1, result.Reversed);
        Assert.Null(result.NewMark);
        Assert.Equal(1_000 - 100 - 200, kit.Balance(WalletId.Player(1))); // the newest spend (300) was undone first
        Assert.Contains(kit.Events.OfType<AlertRaised>(), a => a.Code == "economy_rollback_failed" && a.Severity == AlertSeverity.Critical);
    }

    [Fact]
    public void TheMarkIsASequenceNumberSoPostingsInTheSameInstantAreOrderedCorrectly()
    {
        var kit = Kit();
        Earn(kit, 1, 100);
        var first = kit.Service.CaptureMark();
        Earn(kit, 1, 100);
        var second = kit.Service.CaptureMark();

        Assert.True(second.Seq > first.Seq);
        RollBack(kit, first);
        Assert.Equal(100, kit.Balance(WalletId.Player(1)));
        _ = WalletKind.Player;
    }
}
