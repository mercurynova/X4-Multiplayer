using X4MP.Core.Economy;
using X4MP.Core.Events;
using X4MP.Proto;
using LoanState = X4MP.Core.Economy.LoanState;
using WalletKind = X4MP.Core.Economy.WalletKind;

namespace X4MP.Core.Tests.Economy;

/// <summary>M1-E6: admin adjust, wallet freeze, reversal and loan cancel with a disbursement reversal (server-design 2.14, 4.4).</summary>
public sealed class EconomyAdminTests
{
    private const string Admin = "admin:root";

    private static EconomyKit Kit(Action<EconomyOptions>? configure = null)
    {
        var options = new EconomyOptions { StartingCredits = 0, CreditMode = CreditMode.PerPlayer, DonateScope = EconomyScope.Anyone, LoanScope = EconomyScope.Anyone };
        configure?.Invoke(options);
        var kit = new EconomyKit(options);
        kit.Teams.Set([1], new Dictionary<int, int?> { [1] = 1, [2] = 1, [3] = 1 });
        foreach (var player in new[] { 1, 2, 3 })
        {
            kit.Service.EnsurePlayer(player);
        }

        return kit;
    }

    private static void AssertSound(EconomyKit kit)
    {
        Assert.Equal(0, kit.Ledger.TotalBalance());
        Assert.All(kit.Store.Transactions, t => Assert.Equal(0, t.Entries.Sum(e => e.Amount)));
        var report = kit.Auditor.RunNow();
        Assert.True(report.Ok, string.Join("; ", report.Violations));
    }

    // ------------------------------------------------------------------ adjust

    [Fact]
    public void AdjustCreditsAndDebitsAgainstTheWorldAndIsAuditedWithItsReason()
    {
        var kit = Kit();
        var wallet = WalletId.Player(1);

        var credit = kit.Service.AdminAdjust(Admin, wallet, 5_000, "compensation for a lost ship");
        Assert.True(credit.Ok);
        Assert.Equal(TxKind.AdminAdjust, credit.Transaction!.Kind);
        Assert.Equal(Admin, credit.Transaction.Actor);
        Assert.Equal("compensation for a lost ship", credit.Transaction.Note);
        Assert.Equal(5_000, kit.Balance(wallet));
        Assert.Equal(-5_000, kit.Balance(WalletId.World));

        var debit = kit.Service.AdminAdjust(Admin, wallet, -1_200, "refund of a dupe");
        Assert.True(debit.Ok);
        Assert.Equal(3_800, kit.Balance(wallet));

        var audited = kit.Events.OfType<AdminActionTaken>().Where(a => a.Action == "economy.adjust").ToList();
        Assert.Equal(2, audited.Count);
        Assert.All(audited, a => Assert.Equal(Admin, a.Actor));
        Assert.Equal("compensation for a lost ship", audited[0].Data!["reason"]);
        Assert.Equal("5000", audited[0].Data!["amount"]);
        Assert.Equal("wallet:Player:1", audited[0].Target);
        AssertSound(kit);
    }

    [Fact]
    public void AdjustRefusesBadInputsAndWalletsThatCannotBeAdjusted()
    {
        var kit = Kit();
        Assert.Equal(EconomyAdminError.InvalidAmount, kit.Service.AdminAdjust(Admin, WalletId.Player(1), 0, "x").Error);
        Assert.Equal(EconomyAdminError.InvalidAmount, kit.Service.AdminAdjust(Admin, WalletId.Player(1), EconomyLedger.MaxAmount + 1, "x").Error);
        Assert.Equal(EconomyAdminError.NotAdjustable, kit.Service.AdminAdjust(Admin, WalletId.World, 5, "x").Error);
        Assert.Equal(EconomyAdminError.NotAdjustable, kit.Service.AdminAdjust(Admin, WalletId.Escrow(7), 5, "x").Error);
        Assert.Equal(EconomyAdminError.UnknownWallet, kit.Service.AdminAdjust(Admin, WalletId.Player(99), 5, "x").Error);
        Assert.Equal(EconomyAdminError.UnknownWallet, kit.Service.AdminAdjust(Admin, WalletId.TeamPool(9), 5, "x").Error);
        Assert.DoesNotContain(kit.Events.OfType<AdminActionTaken>(), a => a.Action == "economy.adjust");
        AssertSound(kit);
    }

    [Fact]
    public void ADebitBelowZeroNeedsForceAndThenFlagsTheWalletOverdrawn()
    {
        var kit = Kit();
        kit.Fund(1, 100);

        var refused = kit.Service.AdminAdjust(Admin, WalletId.Player(1), -250, "claw back");
        Assert.Equal(EconomyAdminError.WouldOverdraw, refused.Error);
        Assert.Equal(100, kit.Balance(WalletId.Player(1)));

        var forced = kit.Service.AdminAdjust(Admin, WalletId.Player(1), -250, "claw back", force: true);
        Assert.True(forced.Ok);
        Assert.Equal(-150, kit.Balance(WalletId.Player(1)));
        Assert.True(kit.Ledger.Find(WalletId.Player(1))!.Overdrawn);

        // a pool can never go negative, not even with force
        kit.Service.EnsurePlayer(1);
        Assert.True(kit.Service.AdminAdjust(Admin, WalletId.TeamPool(1), 50, "seed the pool").Ok);
        Assert.Equal(EconomyAdminError.WouldOverdraw, kit.Service.AdminAdjust(Admin, WalletId.TeamPool(1), -80, "x", force: true).Error);
        AssertSound(kit);
    }

    [Fact]
    public void CreditingAnOverdrawnWalletIsAllowedEvenWhileItStaysNegative()
    {
        var kit = Kit();
        kit.Fund(1, 100);
        var debit = kit.Service.AdminAdjust(Admin, WalletId.Player(1), -400, "debt", force: true);
        Assert.Equal(-300, kit.Balance(WalletId.Player(1)));

        Assert.True(kit.Service.AdminAdjust(Admin, WalletId.Player(1), 50, "partial relief").Ok); // still -250: only debits are limited
        Assert.Equal(-250, kit.Balance(WalletId.Player(1)));
        Assert.True(kit.Service.AdminReverse(Admin, debit.Transaction!.Id, "the debit was wrong").Ok); // +400 back
        Assert.Equal(150, kit.Balance(WalletId.Player(1)));
        Assert.False(kit.Ledger.Find(WalletId.Player(1))!.Overdrawn);
        AssertSound(kit);
    }

    [Fact]
    public void AnAdjustWithAnIdempotencyKeyBooksOnceAndRefusesAnotherPayload()
    {
        var kit = Kit();
        var first = kit.Service.AdminAdjust(Admin, WalletId.Player(1), 700, "bonus", idempotencyKey: "abc");
        var again = kit.Service.AdminAdjust(Admin, WalletId.Player(1), 700, "bonus", idempotencyKey: "abc");
        Assert.True(first.Ok);
        Assert.True(again.Ok);
        Assert.Equal(first.Transaction!.Id, again.Transaction!.Id);
        Assert.Equal(700, kit.Balance(WalletId.Player(1)));
        Assert.Single(kit.Events.OfType<AdminActionTaken>(), a => a.Action == "economy.adjust");

        var reused = kit.Service.AdminAdjust(Admin, WalletId.Player(1), 701, "bonus", idempotencyKey: "abc");
        Assert.Equal(EconomyAdminError.RequestIdReuse, reused.Error);
        Assert.Equal(700, kit.Balance(WalletId.Player(1)));
        AssertSound(kit);
    }

    // ------------------------------------------------------------------ freeze

    [Fact]
    public void AFrozenWalletRejectsPlayerRequestsButGameIncomeStillBooks()
    {
        var kit = Kit();
        kit.Fund(1, 1_000);
        kit.Fund(2, 1_000);

        var frozen = kit.Service.AdminSetFrozen(Admin, WalletId.Player(1), true, "investigating a dupe");
        Assert.True(frozen.Ok);
        Assert.True(frozen.Wallet!.Frozen);
        Assert.Equal("investigating a dupe", frozen.Wallet.FrozenReason);

        // player requests: the frozen wallet can neither send nor receive
        Assert.Equal(EconomyReject.EconomyFrozen, kit.Service.Donate(1, "d1", 2, 10).Reason);
        Assert.Equal(EconomyReject.EconomyFrozen, kit.Service.Donate(2, "d2", 1, 10).Reason);
        Assert.Equal(EconomyReject.EconomyFrozen, kit.Service.Transfer(1, "t1", 2, 10).Reason);

        // game deltas (the authority's income and spend) still book
        var income = kit.Service.BookCreditDelta(1, senderIsAuthority: false, new CreditDeltaT { Amount = 400, Seq = 1 });
        Assert.True(income.Booked);
        var spend = kit.Service.BookCreditDelta(1, senderIsAuthority: false, new CreditDeltaT { Amount = -150, Seq = 2 });
        Assert.True(spend.Booked);
        Assert.Equal(1_250, kit.Balance(WalletId.Player(1)));

        var lifted = kit.Service.AdminSetFrozen(Admin, WalletId.Player(1), false, "cleared");
        Assert.True(lifted.Ok);
        Assert.True(kit.Service.Donate(1, "d3", 2, 10).Ok);

        var actions = kit.Events.OfType<AdminActionTaken>().Select(a => a.Action).ToList();
        Assert.Contains("economy.wallet.freeze", actions);
        Assert.Contains("economy.wallet.unfreeze", actions);
        AssertSound(kit);
    }

    [Fact]
    public void FreezeRefusesUnknownAndInternalWallets()
    {
        var kit = Kit();
        Assert.Equal(EconomyAdminError.UnknownWallet, kit.Service.AdminSetFrozen(Admin, WalletId.Player(77), true, "x").Error);
        Assert.Equal(EconomyAdminError.NotAdjustable, kit.Service.AdminSetFrozen(Admin, WalletId.World, true, "x").Error);
    }

    // ------------------------------------------------------------------ reverse

    [Fact]
    public void AReversalNegatesTheEntriesAndLinksBothTransactions()
    {
        var kit = Kit();
        kit.Fund(1, 1_000);
        Assert.True(kit.Service.Donate(1, "d1", 2, 300).Ok);
        var donation = kit.Store.Transactions.Single(t => t.Kind == TxKind.Donate);

        var reversed = kit.Service.AdminReverse(Admin, donation.Id, "sent to the wrong player");
        Assert.True(reversed.Ok);
        var reversal = reversed.Transaction!;
        Assert.Equal(TxKind.Reversal, reversal.Kind);
        Assert.Equal(donation.Id, reversal.Reverses);
        Assert.Equal(Admin, reversal.Actor);
        Assert.Equal("sent to the wrong player", reversal.Note);
        Assert.Equal(1_000, kit.Balance(WalletId.Player(1)));
        Assert.Equal(0, kit.Balance(WalletId.Player(2)));

        // both ends of the link are visible
        Assert.Equal(reversal.Id, kit.Ledger.FindTransaction(donation.Id)!.ReversedBy);
        Assert.Equal(donation.Id, kit.Ledger.FindTransaction(reversal.Id)!.Reverses);

        var audit = kit.Events.OfType<AdminActionTaken>().Single(a => a.Action == "economy.reverse");
        Assert.Equal("sent to the wrong player", audit.Data!["reason"]);
        Assert.Equal(reversal.Id, audit.Data["reversal"]);
        Assert.Equal("tx:" + donation.Id, audit.Target);
        AssertSound(kit);
    }

    [Fact]
    public void ASecondReversalIsRefusedAndAReversalCannotBeReversed()
    {
        var kit = Kit();
        kit.Fund(1, 1_000);
        Assert.True(kit.Service.Donate(1, "d1", 2, 300).Ok);
        var donation = kit.Store.Transactions.Single(t => t.Kind == TxKind.Donate);
        var first = kit.Service.AdminReverse(Admin, donation.Id, "first");
        Assert.True(first.Ok);

        var second = kit.Service.AdminReverse(Admin, donation.Id, "second");
        Assert.Equal(EconomyAdminError.AlreadyReversed, second.Error);
        Assert.Contains(first.Transaction!.Id, second.Detail, StringComparison.Ordinal);
        Assert.Equal(EconomyAdminError.NotReversible, kit.Service.AdminReverse(Admin, first.Transaction.Id, "undo the undo").Error);
        Assert.Equal(EconomyAdminError.UnknownTransaction, kit.Service.AdminReverse(Admin, "01NOSUCHTRANSACTION00000000", "x").Error);
        Assert.Single(kit.Store.Transactions, t => t.Kind == TxKind.Reversal);
        AssertSound(kit);
    }

    [Fact]
    public void AReversalThatWouldOverdrawNeedsForce()
    {
        var kit = Kit();
        kit.Fund(1, 1_000);
        Assert.True(kit.Service.Donate(1, "d1", 2, 300).Ok);
        Assert.True(kit.Service.Donate(2, "d2", 3, 300).Ok); // player 2 spent the money on
        var donation = kit.Store.Transactions.First(t => t.Kind == TxKind.Donate);

        var refused = kit.Service.AdminReverse(Admin, donation.Id, "claw back");
        Assert.Equal(EconomyAdminError.WouldOverdraw, refused.Error);
        Assert.Equal(0, kit.Balance(WalletId.Player(2)));
        Assert.Null(kit.Ledger.FindTransaction(donation.Id)!.ReversedBy); // nothing changed

        var forced = kit.Service.AdminReverse(Admin, donation.Id, "claw back", force: true);
        Assert.True(forced.Ok);
        Assert.Equal(-300, kit.Balance(WalletId.Player(2)));
        Assert.True(kit.Ledger.Find(WalletId.Player(2))!.Overdrawn);
        Assert.Equal(1_000, kit.Balance(WalletId.Player(1)));
        AssertSound(kit);
    }

    [Fact]
    public void ReversalIgnoresWalletFreezesAndRefusesAFrozenEconomy()
    {
        var kit = Kit();
        kit.Fund(1, 1_000);
        Assert.True(kit.Service.Donate(1, "d1", 2, 300).Ok);
        var donation = kit.Store.Transactions.Single(t => t.Kind == TxKind.Donate);
        Assert.True(kit.Service.AdminSetFrozen(Admin, WalletId.Player(2), true, "x").Ok);

        kit.Ledger.Freeze("test breach");
        Assert.Equal(EconomyAdminError.EconomyFrozen, kit.Service.AdminReverse(Admin, donation.Id, "x").Error);
        kit.Ledger.Unfreeze(Admin);

        Assert.True(kit.Service.AdminReverse(Admin, donation.Id, "frozen wallets do not block an admin").Ok);
        AssertSound(kit);
    }

    [Fact]
    public void MigrationsAndEscrowMovementsAreNotReversible()
    {
        var kit = Kit();
        kit.Fund(1, 5_000);
        var offered = kit.Service.OfferLoan(1, "o1", 2, 1_000, 1_100, 0, 300, 0, null);
        Assert.True(offered.Ok);
        var escrow = kit.Store.Transactions.Single(t => t.Kind == TxKind.LoanEscrow);
        var result = kit.Service.AdminReverse(Admin, escrow.Id, "x");
        Assert.Equal(EconomyAdminError.NotReversible, result.Error);

        Assert.True(kit.Service.RespondLoan(2, "a1", offered.Loan!.Id, true).Ok);
        var disbursement = kit.Store.Transactions.Single(t => t.Kind == TxKind.LoanDisburse);
        Assert.Equal(EconomyAdminError.NotReversible, kit.Service.AdminReverse(Admin, disbursement.Id, "x").Error);
        AssertSound(kit);
    }

    [Fact]
    public void EveryLoanLinkedTransactionIsNotReversibleEvenWithForceAndTheLoanChecksStayClean()
    {
        var kit = Kit();
        kit.Auditor.AddCheck(EconomyService.AuditLoans);
        kit.Fund(1, 10_000);
        var offered = kit.Service.OfferLoan(1, "o1", 2, 1_000, 1_100, 0, 300, 25, null); // 25% auto-repay
        Assert.True(kit.Service.RespondLoan(2, "a1", offered.Loan!.Id, true).Ok);
        Assert.True(kit.Service.RepayLoan(2, "r1", offered.Loan.Id, 100).Ok);
        kit.Service.BookCreditDelta(2, senderIsAuthority: false, new CreditDeltaT { Amount = 400, Seq = 1 }); // 100 goes to the lender inside the income
        var declined = kit.Service.OfferLoan(1, "o2", 3, 500, 550, 0, 300, 0, null);
        Assert.True(kit.Service.RespondLoan(3, "a2", declined.Loan!.Id, false).Ok); // refund of the escrow

        var linked = kit.Store.Transactions
            .Where(t => t.Kind is TxKind.LoanEscrow or TxKind.LoanDisburse or TxKind.LoanRepay or TxKind.LoanRefund
                || (t.Kind == TxKind.GameIncome && t.Entries.Count > 2))
            .ToList();
        Assert.Contains(linked, t => t.Kind == TxKind.LoanRepay);
        Assert.Contains(linked, t => t.Kind == TxKind.LoanRefund);
        Assert.Contains(linked, t => t.Kind == TxKind.GameIncome);
        var before = kit.Store.Transactions.Count;
        foreach (var tx in linked)
        {
            foreach (var force in new[] { false, true })
            {
                var result = kit.Service.AdminReverse(Admin, tx.Id, "try", force);
                Assert.Equal(EconomyAdminError.NotReversible, result.Error);
                Assert.Contains("loan admin actions", result.Detail, StringComparison.Ordinal);
            }
        }

        Assert.Equal(before, kit.Store.Transactions.Count);
        var loan = kit.Service.FindLoan(offered.Loan.Id)!;
        Assert.Equal(1_100 - 100 - 100, loan.Outstanding);
        AssertSound(kit);

        // the supported way keeps the books consistent
        Assert.True(kit.Service.AdminCancelLoan(Admin, loan.Id, "cleanup", reverseDisbursement: true, force: true).Ok);
        AssertSound(kit);
    }

    [Fact]
    public void AReversalWithAnIdempotencyKeyReplaysInsteadOfReportingAlreadyReversed()
    {
        var kit = Kit();
        kit.Fund(1, 1_000);
        Assert.True(kit.Service.Donate(1, "d1", 2, 300).Ok);
        var donation = kit.Store.Transactions.Single(t => t.Kind == TxKind.Donate);

        var first = kit.Service.AdminReverse(Admin, donation.Id, "oops", idempotencyKey: "rev-1");
        var replay = kit.Service.AdminReverse(Admin, donation.Id, "oops", idempotencyKey: "rev-1");
        Assert.True(first.Ok);
        Assert.True(replay.Ok);
        Assert.Equal(first.Transaction!.Id, replay.Transaction!.Id);
        Assert.Equal(EconomyAdminError.RequestIdReuse, kit.Service.AdminReverse(Admin, donation.Id, "other reason", idempotencyKey: "rev-1").Error);
        Assert.Single(kit.Store.Transactions, t => t.Kind == TxKind.Reversal);
        AssertSound(kit);
    }

    // ------------------------------------------------------------------ loans

    [Fact]
    public void CancellingALoanCanTakeTheDisbursedPrincipalBack()
    {
        var kit = Kit();
        kit.Fund(1, 5_000);
        var offered = kit.Service.OfferLoan(1, "o1", 2, 1_000, 1_100, 0, 300, 0, null);
        Assert.True(kit.Service.RespondLoan(2, "a1", offered.Loan!.Id, true).Ok);
        Assert.Equal(1_000, kit.Balance(WalletId.Player(2)));
        Assert.Equal(4_000, kit.Balance(WalletId.Player(1)));

        // the borrower spent it: refused, loan untouched
        Assert.True(kit.Service.Donate(2, "d1", 3, 900).Ok);
        var refused = kit.Service.AdminCancelLoan(Admin, offered.Loan.Id, "bad loan", reverseDisbursement: true, force: false);
        Assert.Equal(EconomyAdminError.WouldOverdraw, refused.Error);
        Assert.Equal(LoanState.Active, kit.Service.FindLoan(offered.Loan.Id)!.State);
        Assert.DoesNotContain(kit.Store.Transactions, t => t.Kind == TxKind.Reversal);

        var forced = kit.Service.AdminCancelLoan(Admin, offered.Loan.Id, "bad loan", reverseDisbursement: true, force: true);
        Assert.True(forced.Ok);
        Assert.Equal(LoanState.Cancelled, forced.Loan!.State);
        Assert.Equal(0, forced.Loan.Outstanding);
        Assert.Equal(5_000, kit.Balance(WalletId.Player(1))); // the lender has the principal back
        Assert.Equal(-900, kit.Balance(WalletId.Player(2)));
        var reversal = kit.Store.Transactions.Single(t => t.Kind == TxKind.Reversal);
        Assert.Contains("loan #" + offered.Loan.Id, reversal.Note, StringComparison.Ordinal);
        Assert.Equal(0, kit.Balance(offered.Loan.EscrowWallet));
        AssertSound(kit);
    }

    [Fact]
    public void CancellingALoanWithoutTheReversalKeepsTheMoneyWhereItIs()
    {
        var kit = Kit();
        kit.Fund(1, 5_000);
        var offered = kit.Service.OfferLoan(1, "o1", 2, 1_000, 1_100, 0, 300, 0, null);
        Assert.True(kit.Service.RespondLoan(2, "a1", offered.Loan!.Id, true).Ok);

        var cancelled = kit.Service.AdminCancelLoan(Admin, offered.Loan.Id, "closed by the staff", reverseDisbursement: false, force: false);
        Assert.True(cancelled.Ok);
        Assert.Equal(1_000, kit.Balance(WalletId.Player(2)));
        Assert.Equal(EconomyAdminError.WrongState, kit.Service.AdminCancelLoan(Admin, offered.Loan.Id, "again", false, false).Error);
        Assert.Equal(EconomyAdminError.UnknownLoan, kit.Service.AdminCancelLoan(Admin, 999, "x", false, false).Error);
        AssertSound(kit);
    }

    // ------------------------------------------------------------------ totals and preview

    [Fact]
    public void TotalsCountMoneySupplyEscrowDebtAndFrozenWallets()
    {
        var kit = Kit();
        kit.Fund(1, 5_000);
        var offered = kit.Service.OfferLoan(1, "o1", 2, 1_000, 1_100, 0, 300, 0, null); // principal sits in escrow
        Assert.True(offered.Ok);
        Assert.True(kit.Service.AdminSetFrozen(Admin, WalletId.Player(3), true, "x").Ok);

        var totals = kit.Service.Totals();
        Assert.Equal(5_000, totals.MoneySupply);
        Assert.Equal(1_000, totals.InEscrow);
        Assert.Equal(1, totals.OpenLoans);
        Assert.Equal(0, totals.OutstandingDebt);
        Assert.Equal(1, totals.FrozenWallets);

        Assert.True(kit.Service.RespondLoan(2, "a1", offered.Loan!.Id, true).Ok);
        totals = kit.Service.Totals();
        Assert.Equal(0, totals.InEscrow);
        Assert.Equal(1_100, totals.OutstandingDebt);
    }

    [Fact]
    public void ThePreviewForAnotherModeChangesNothing()
    {
        var kit = Kit(o => o.CreditMode = CreditMode.PerPlayer);
        kit.Fund(1, 600);
        kit.Fund(2, 400);
        var before = kit.Store.Transactions.Count;

        var preview = kit.Service.PreviewMigration(CreditMode.Shared);
        Assert.True(preview.Needed);
        Assert.True(preview.RequiresConfirm);
        Assert.Equal(EffectiveCreditMode.PerPlayer, preview.From);
        Assert.Equal(EffectiveCreditMode.Shared, preview.To);
        Assert.Equal(1_000, preview.TotalMoved);
        Assert.Equal(before, kit.Store.Transactions.Count);
        Assert.Equal(600, kit.Balance(WalletId.Player(1)));
        Assert.Equal(EffectiveCreditMode.PerPlayer, kit.Service.AppliedMode);
        Assert.False(kit.Service.PreviewMigration(CreditMode.PerPlayer).Needed);
    }

    // ------------------------------------------------------------------ the ledger stays sound

    [Fact]
    public void TheLedgerSumsToZeroAfterAnyMixOfAdjustReverseAndForcedOperations()
    {
        var kit = Kit();
        var random = new Random(20261002);
        var wallets = new[] { WalletId.Player(1), WalletId.Player(2), WalletId.Player(3), WalletId.TeamPool(1) };
        kit.Fund(1, 10_000);
        kit.Fund(2, 10_000);
        var reversible = new List<string>();
        for (var i = 0; i < 400; i++)
        {
            var wallet = wallets[random.Next(wallets.Length)];
            var force = random.Next(3) == 0;
            switch (random.Next(4))
            {
                case 0:
                    var amount = random.NextInt64(1, 2_000) * (random.Next(2) == 0 ? 1 : -1);
                    var adjusted = kit.Service.AdminAdjust(Admin, wallet, amount, "random " + i, force);
                    if (adjusted.Ok)
                    {
                        reversible.Add(adjusted.Transaction!.Id);
                    }

                    break;
                case 1 when reversible.Count > 0:
                    var id = reversible[random.Next(reversible.Count)];
                    kit.Service.AdminReverse(Admin, id, "random reverse " + i, force);
                    break;
                case 2:
                    kit.Service.Donate(random.Next(1, 4), "d" + i, random.Next(1, 4), random.NextInt64(1, 500));
                    break;
                default:
                    kit.Service.AdminSetFrozen(Admin, wallet.Kind == WalletKind.TeamPool ? WalletId.Player(1) : wallet, random.Next(4) == 0, "random freeze");
                    break;
            }

            Assert.Equal(0, kit.Ledger.TotalBalance());
        }

        Assert.All(kit.Ledger.Wallets.Where(w => w.Id.Kind is WalletKind.Escrow or WalletKind.TeamPool), w => Assert.True(w.Balance >= 0));
        AssertSound(kit);
        Assert.Contains(kit.Store.Transactions, t => t.Kind == TxKind.Reversal);
        foreach (var reversal in kit.Store.Transactions.Where(t => t.Kind == TxKind.Reversal))
        {
            Assert.Equal(reversal.Id, kit.Ledger.FindTransaction(reversal.Reverses!)!.ReversedBy);
        }
    }

    [Fact]
    public void TheStoreFiltersAndPagesTheLedger()
    {
        var kit = Kit();
        kit.Fund(1, 1_000);
        for (var i = 0; i < 5; i++)
        {
            Assert.True(kit.Service.AdminAdjust(Admin, WalletId.Player(2), 10 + i, "n" + i).Ok);
        }

        Assert.True(kit.Service.Donate(1, "d", 3, 20).Ok);

        var forPlayer2 = kit.Ledger.QueryTransactions(new LedgerQuery(Wallet: WalletId.Player(2)));
        Assert.Equal(5, forPlayer2.Count);
        Assert.True(string.CompareOrdinal(forPlayer2[0].Id, forPlayer2[1].Id) > 0); // newest first

        var page1 = kit.Ledger.QueryTransactions(new LedgerQuery(Kind: TxKind.AdminAdjust, Limit: 2));
        var page2 = kit.Ledger.QueryTransactions(new LedgerQuery(Kind: TxKind.AdminAdjust, Before: page1[^1].Id, Limit: 10));
        Assert.Equal(2, page1.Count);
        Assert.Equal(3, page2.Count);
        Assert.Empty(page1.Select(t => t.Id).Intersect(page2.Select(t => t.Id)));

        Assert.Single(kit.Ledger.QueryTransactions(new LedgerQuery(Kind: TxKind.Donate)));
        Assert.Equal(5, kit.Ledger.QueryTransactions(new LedgerQuery(Actor: Admin)).Count);
        var ascending = kit.Ledger.QueryTransactions(new LedgerQuery(Ascending: true, Limit: 3));
        Assert.True(string.CompareOrdinal(ascending[0].Id, ascending[1].Id) < 0);
    }
}
