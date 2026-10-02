using X4MP.Core.Economy;
using X4MP.Core.Events;
using X4MP.Proto;
using LoanState = X4MP.Core.Economy.LoanState;
using TeamRelation = X4MP.Core.Teams.TeamRelation;
using WalletKind = X4MP.Core.Economy.WalletKind;

namespace X4MP.Core.Tests.Economy;

/// <summary>M1-E4: loans (server-design 2.14). Everything runs on the fake clock of <see cref="EconomyKit"/>.</summary>
public sealed class LoanTests
{
    // Players 1,2 = team 1; 3 = team 2 (allied); 4 = team 3 (neutral); 5 = team 4 (hostile).
    private static readonly Dictionary<int, int?> Members = new() { [1] = 1, [2] = 1, [3] = 2, [4] = 3, [5] = 4 };

    private static EconomyKit Kit(Action<EconomyOptions>? configure = null, IReadOnlyDictionary<int, int?>? members = null, int[]? teams = null, bool funded = true)
    {
        var options = new EconomyOptions { StartingCredits = 0, CreditMode = CreditMode.PerPlayer };
        configure?.Invoke(options);
        var kit = new EconomyKit(options);
        var map = members ?? Members;
        kit.Teams.Set(teams ?? [1, 2, 3, 4], map);
        kit.Teams.SetRelation(1, 2, TeamRelation.Allied);
        kit.Teams.SetRelation(1, 3, TeamRelation.Neutral);
        kit.Teams.SetRelation(1, 4, TeamRelation.Hostile);
        foreach (var player in map.Keys)
        {
            kit.Service.EnsurePlayer(player);
        }

        if (funded)
        {
            kit.Fund(1, 10_000);
        }

        return kit;
    }

    private static long Offer(EconomyKit kit, string key = "o1", int lender = 1, int borrower = 2, long principal = 1000, long total = 1100, int due = 3600, int ttl = 300, int pct = 0)
    {
        var result = kit.Service.OfferLoan(lender, key, borrower, principal, total, due, ttl, pct, "memo");
        Assert.True(result.Ok, result.Reason + " " + result.Detail);
        return result.Loan!.Id;
    }

    private static long Accepted(EconomyKit kit, string key = "o1", int lender = 1, int borrower = 2, long principal = 1000, long total = 1100, int due = 3600, int pct = 0)
    {
        var id = Offer(kit, key, lender, borrower, principal, total, due, 300, pct);
        Assert.True(kit.Service.RespondLoan(borrower, "a" + key, id, true).Ok);
        return id;
    }

    private static void AssertSound(EconomyKit kit)
    {
        Assert.Equal(0, kit.Ledger.TotalBalance());
        Assert.All(kit.Store.Transactions, t => Assert.Equal(0, t.Entries.Sum(e => e.Amount)));
        Assert.All(kit.Ledger.Wallets.Where(w => w.Id.Kind != WalletKind.World), w => Assert.True(w.Balance >= 0, $"{w.Id} = {w.Balance}"));
        Assert.Empty(EconomyService.AuditLoans(kit.Ledger));
    }

    // ------------------------------------------------------------------ 1. state machine

    [Fact]
    public void OfferEscrowsThePrincipalImmediately()
    {
        var kit = Kit();
        var id = Offer(kit);

        var loan = kit.Service.FindLoan(id)!;
        Assert.Equal(LoanState.Offered, loan.State);
        Assert.Equal(9000, kit.Balance(WalletId.Player(1)));
        Assert.Equal(1000, kit.Balance(LoanWallets.Escrow(id)));
        Assert.Equal(0, kit.Balance(WalletId.Player(2)));
        Assert.Equal(1000, loan.Principal);
        Assert.Equal(1100, loan.RepayTotal);
        Assert.Equal(1000, loan.InterestBasisPoints);
        Assert.Equal(kit.Time.GetUtcNow().AddSeconds(300), loan.OfferExpiresAt);
        AssertSound(kit);
    }

    [Fact]
    public void AcceptMovesEscrowToTheBorrowerAndFixesWhatIsOwed()
    {
        var kit = Kit();
        var id = Offer(kit);
        kit.Time.Advance(TimeSpan.FromSeconds(10));

        var result = kit.Service.RespondLoan(2, "r1", id, accept: true);

        Assert.True(result.Ok, result.Reason.ToString());
        var loan = kit.Service.FindLoan(id)!;
        Assert.Equal(LoanState.Active, loan.State);
        Assert.Equal(1100, loan.Outstanding);
        Assert.Equal(kit.Time.GetUtcNow().AddSeconds(3600), loan.DueAt);
        Assert.Equal(0, kit.Balance(LoanWallets.Escrow(id)));
        Assert.Equal(1000, kit.Balance(WalletId.Player(2)));
        Assert.Equal(9000, kit.Balance(WalletId.Player(1)));
        Assert.Contains(kit.Store.Transactions, t => t.Kind == TxKind.LoanDisburse && t.RefId == id);
        AssertSound(kit);
    }

    [Fact]
    public void DeclineRefundsTheEscrowExactly()
    {
        var kit = Kit();
        var before = kit.Balance(WalletId.Player(1));
        var id = Offer(kit, principal: 777, total: 777);

        Assert.True(kit.Service.RespondLoan(2, "r1", id, accept: false).Ok);

        Assert.Equal(LoanState.Declined, kit.Service.FindLoan(id)!.State);
        Assert.Equal(before, kit.Balance(WalletId.Player(1)));
        Assert.Equal(0, kit.Balance(LoanWallets.Escrow(id)));
        Assert.Equal(0, kit.Balance(WalletId.Player(2)));
        Assert.Contains(kit.Store.Transactions, t => t.Kind == TxKind.LoanRefund);
        AssertSound(kit);
    }

    [Fact]
    public void TheLenderWithdrawsAnOfferAndOnlyTheLenderMay()
    {
        var kit = Kit();
        var id = Offer(kit);

        Assert.Equal(EconomyReject.NotParty, kit.Service.WithdrawLoan(2, "c0", id).Reason);
        Assert.True(kit.Service.WithdrawLoan(1, "c1", id).Ok);

        Assert.Equal(LoanState.Withdrawn, kit.Service.FindLoan(id)!.State);
        Assert.Equal(10_000, kit.Balance(WalletId.Player(1)));
        Assert.Equal(0, kit.Balance(LoanWallets.Escrow(id)));
        Assert.Equal(EconomyReject.WrongState, kit.Service.RespondLoan(2, "r1", id, true).Reason);
        AssertSound(kit);
    }

    [Fact]
    public void AnOfferExpiresAndRefundsOnTheTimer()
    {
        var kit = Kit();
        var id = Offer(kit, ttl: 60);

        kit.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(0, kit.Service.ProcessLoanTimers());
        Assert.Equal(LoanState.Offered, kit.Service.FindLoan(id)!.State);

        kit.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, kit.Service.ProcessLoanTimers());

        Assert.Equal(LoanState.Expired, kit.Service.FindLoan(id)!.State);
        Assert.Equal(10_000, kit.Balance(WalletId.Player(1)));
        Assert.Equal(0, kit.Balance(LoanWallets.Escrow(id)));
        Assert.Equal(0, kit.Service.ProcessLoanTimers()); // nothing left to do
        AssertSound(kit);
    }

    [Fact]
    public void AcceptingAnExpiredOfferBeforeTheTimerRanExpiresItInstead()
    {
        var kit = Kit();
        var id = Offer(kit, ttl: 60);
        kit.Time.Advance(TimeSpan.FromSeconds(61));

        var result = kit.Service.RespondLoan(2, "r1", id, accept: true);

        Assert.Equal(EconomyReject.WrongState, result.Reason);
        Assert.Equal(LoanState.Expired, kit.Service.FindLoan(id)!.State);
        Assert.Equal(10_000, kit.Balance(WalletId.Player(1)));
        Assert.Equal(0, kit.Balance(WalletId.Player(2)));
        AssertSound(kit);
    }

    [Fact]
    public void OffersWithoutATtlUseTheDefaultLifetime()
    {
        var kit = Kit(o => o.OfferDefaultTtlMinutes = 10);
        var id = Offer(kit, ttl: 0);

        Assert.Equal(kit.Time.GetUtcNow().AddMinutes(10), kit.Service.FindLoan(id)!.OfferExpiresAt);
    }

    [Fact]
    public void PartialRepaymentsThenTheRestCloseTheLoanAndAreCapped()
    {
        var kit = Kit();
        var id = Accepted(kit);
        kit.Fund(2, 500); // the borrower holds 1500

        var first = kit.Service.RepayLoan(2, "p1", id, 400);
        Assert.True(first.Ok);
        Assert.Equal(400, first.Moved);
        var loan = kit.Service.FindLoan(id)!;
        Assert.Equal((700, 400, LoanState.Active), (loan.Outstanding, loan.Repaid, loan.State));
        Assert.Equal(9400, kit.Balance(WalletId.Player(1)));

        var last = kit.Service.RepayLoan(2, "p2", id, 5000); // far more than owed
        Assert.True(last.Ok);
        Assert.Equal(700, last.Moved);
        loan = kit.Service.FindLoan(id)!;
        Assert.Equal((0, 1100, LoanState.Repaid), (loan.Outstanding, loan.Repaid, loan.State));
        Assert.Equal(10_100, kit.Balance(WalletId.Player(1)));
        Assert.Equal(400, kit.Balance(WalletId.Player(2)));
        Assert.Equal(EconomyReject.WrongState, kit.Service.RepayLoan(2, "p3", id, 1).Reason);
        AssertSound(kit);
    }

    [Fact]
    public void RepayNeedsTheBorrowersMoneyAndAValidAmount()
    {
        var kit = Kit();
        var id = Accepted(kit); // borrower holds exactly the principal 1000, owes 1100

        Assert.Equal(EconomyReject.InsufficientFunds, kit.Service.RepayLoan(2, "p1", id, 1100).Reason);
        Assert.Equal(EconomyReject.AmountInvalid, kit.Service.RepayLoan(2, "p2", id, 0).Reason);
        Assert.Equal(EconomyReject.AmountInvalid, kit.Service.RepayLoan(2, "p3", id, -5).Reason);
        Assert.Equal(EconomyReject.NotParty, kit.Service.RepayLoan(1, "p4", id, 10).Reason);
        Assert.Equal(EconomyReject.UnknownLoan, kit.Service.RepayLoan(2, "p5", 999, 10).Reason);
        Assert.Equal(1100, kit.Service.FindLoan(id)!.Outstanding);
    }

    [Fact]
    public void ALoanPastItsDueTimeBecomesOverdueAndCanStillBeRepaid()
    {
        var kit = Kit();
        var id = Accepted(kit, due: 600);
        kit.Fund(2, 500);
        var changes = new List<LoanChange>();
        kit.Service.LoanChanged = changes.Add;

        kit.Time.Advance(TimeSpan.FromSeconds(599));
        Assert.Equal(0, kit.Service.ProcessLoanTimers());
        Assert.Equal(LoanState.Active, kit.Service.FindLoan(id)!.State);

        kit.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, kit.Service.ProcessLoanTimers());

        Assert.Equal(LoanState.Overdue, kit.Service.FindLoan(id)!.State);
        var change = Assert.Single(changes);
        Assert.Equal(LoanState.Active, change.Previous!.State);
        Assert.Equal([1, 2], change.Notices.Select(n => n.Player).Order()); // both parties are told
        var evt = Assert.Single(kit.Events.All.OfType<LoanStateChanged>(), e => e.To == LoanState.Overdue);
        Assert.Equal((id, 1, 2, LoanState.Active), (evt.LoanId, evt.Lender, evt.Borrower, evt.From));
        Assert.Equal(0, kit.Service.ProcessLoanTimers()); // flagged once

        Assert.True(kit.Service.RepayLoan(2, "p1", id, 1100).Ok);
        Assert.Equal(LoanState.Repaid, kit.Service.FindLoan(id)!.State);
        AssertSound(kit);
    }

    [Fact]
    public void ALoanWithoutADueTimeNeverGoesOverdue()
    {
        var kit = Kit();
        var id = Accepted(kit, due: 0);

        kit.Time.Advance(TimeSpan.FromDays(400));
        kit.Service.ProcessLoanTimers();

        Assert.Equal(LoanState.Active, kit.Service.FindLoan(id)!.State);
        Assert.Null(kit.Service.FindLoan(id)!.DueAt);
    }

    [Fact]
    public void TheLenderForgivesPartAndThenTheRest()
    {
        var kit = Kit();
        var id = Accepted(kit);

        var part = kit.Service.ForgiveLoan(1, "f1", id, 300);
        Assert.True(part.Ok);
        var loan = kit.Service.FindLoan(id)!;
        Assert.Equal((800, 300, LoanState.Active), (loan.Outstanding, loan.Forgiven, loan.State));

        Assert.Equal(EconomyReject.NotParty, kit.Service.ForgiveLoan(2, "f2", id, 0).Reason);
        Assert.True(kit.Service.ForgiveLoan(1, "f3", id, 0).Ok); // 0 = everything left

        loan = kit.Service.FindLoan(id)!;
        Assert.Equal((0, 1100, LoanState.Forgiven), (loan.Outstanding, loan.Forgiven, loan.State));
        Assert.Equal(1000, kit.Balance(WalletId.Player(2))); // forgiving moves no money
        Assert.Equal(EconomyReject.WrongState, kit.Service.ForgiveLoan(1, "f4", id, 0).Reason);
        AssertSound(kit);
    }

    [Fact]
    public void AdminForgiveClosesAnActiveAndAnOverdueLoanAndIsAudited()
    {
        var kit = Kit();
        var active = Accepted(kit, "x1", due: 100);
        var overdue = Accepted(kit, "x2", due: 10);
        kit.Time.Advance(TimeSpan.FromSeconds(20));
        kit.Service.ProcessLoanTimers();
        Assert.Equal(LoanState.Overdue, kit.Service.FindLoan(overdue)!.State);

        Assert.True(kit.Service.AdminForgiveLoan("admin:root", active, "goodwill").Ok);
        Assert.True(kit.Service.AdminForgiveLoan("admin:root", overdue).Ok);

        Assert.All([active, overdue], id =>
        {
            var loan = kit.Service.FindLoan(id)!;
            Assert.Equal((0, LoanState.Forgiven, 1100), (loan.Outstanding, loan.State, loan.Forgiven));
        });
        var audit = kit.Events.All.OfType<AdminActionTaken>().Where(a => a.Action == "economy.loan.forgive").ToList();
        Assert.Equal(2, audit.Count);
        Assert.Equal("admin:root", audit[0].Actor);
        Assert.Equal("goodwill", audit[0].Data!["reason"]);
        Assert.Equal(EconomyReject.WrongState, kit.Service.AdminForgiveLoan("admin:root", active).Reason);
        Assert.Equal(EconomyReject.UnknownLoan, kit.Service.AdminForgiveLoan("admin:root", 77).Reason);
        AssertSound(kit);
    }

    [Fact]
    public void AdminCancelRefundsAnOfferAndClosesARunningLoan()
    {
        var kit = Kit();
        var offered = Offer(kit, "x1");
        var running = Accepted(kit, "x2");
        Assert.Equal(8000 - 0, kit.Balance(WalletId.Player(1)));

        Assert.True(kit.Service.AdminCancelLoan("admin:root", offered, "cleanup").Ok);
        Assert.Equal(LoanState.Cancelled, kit.Service.FindLoan(offered)!.State);
        Assert.Equal(0, kit.Balance(LoanWallets.Escrow(offered)));
        Assert.Equal(9000, kit.Balance(WalletId.Player(1)));

        Assert.True(kit.Service.AdminCancelLoan("admin:root", running).Ok);
        var loan = kit.Service.FindLoan(running)!;
        Assert.Equal((LoanState.Cancelled, 0), (loan.State, loan.Outstanding));
        Assert.Equal(1000, kit.Balance(WalletId.Player(2))); // the disbursement is not reversed

        Assert.Equal(EconomyReject.WrongState, kit.Service.AdminCancelLoan("admin:root", running).Reason);
        Assert.Equal(2, kit.Events.All.OfType<AdminActionTaken>().Count(a => a.Action == "economy.loan.cancel"));
        AssertSound(kit);
    }

    [Fact]
    public void EveryStateChangeIsPublishedAsAnEvent()
    {
        var kit = Kit();
        var id = Accepted(kit, due: 5);
        kit.Time.Advance(TimeSpan.FromSeconds(6));
        kit.Service.ProcessLoanTimers();
        kit.Fund(2, 500);
        kit.Service.RepayLoan(2, "p1", id, 1100);

        var states = kit.Events.All.OfType<LoanStateChanged>().Where(e => e.LoanId == id).Select(e => e.To).ToList();

        Assert.Equal([LoanState.Offered, LoanState.Active, LoanState.Overdue, LoanState.Repaid], states);
        Assert.Contains(kit.Events.All.OfType<EconomyActionCompleted>(), e => e.Kind == "LoanOffer" && e.Amount == 1000);
        Assert.Contains(kit.Events.All.OfType<EconomyActionCompleted>(), e => e.Kind == "LoanAccept");
        Assert.Contains(kit.Events.All.OfType<EconomyActionCompleted>(), e => e.Kind == "LoanRepay" && e.Amount == 1100);
    }

    // ------------------------------------------------------------------ 2. auto-repay

    [Fact]
    public void AutoRepayTakesTheAgreedShareOfGameIncome()
    {
        var kit = Kit();
        var id = Accepted(kit, pct: 25);

        kit.Fund(2, 1000); // 1,000 of game income: 250 goes to the lender

        var loan = kit.Service.FindLoan(id)!;
        Assert.Equal((850, 250), (loan.Outstanding, loan.Repaid));
        Assert.Equal(1000 + 750, kit.Balance(WalletId.Player(2)));
        Assert.Equal(9000 + 250, kit.Balance(WalletId.Player(1)));
        var income = kit.Store.Transactions.Last(t => t.Kind == TxKind.GameIncome);
        Assert.Equal(3, income.Entries.Count); // world, borrower, lender: all in the income transaction
        AssertSound(kit);
    }

    [Fact]
    public void AutoRepayNeverTakesMoreThanIsOutstandingAndClosesTheLoan()
    {
        var kit = Kit();
        var id = Accepted(kit, pct: 50, total: 1100);
        kit.Fund(2, 1000); // 500 repaid, 600 left
        kit.Fund(2, 1000); // 500 repaid, 100 left
        Assert.Equal(100, kit.Service.FindLoan(id)!.Outstanding);

        kit.Fund(2, 10_000); // 50% would be 5,000: only 100 is owed

        var loan = kit.Service.FindLoan(id)!;
        Assert.Equal((0, 1100, LoanState.Repaid), (loan.Outstanding, loan.Repaid, loan.State));
        Assert.Equal(1000 + 500 + 500 + 9900, kit.Balance(WalletId.Player(2)));

        kit.Fund(2, 1000); // nothing left to divert
        Assert.Equal(1100, kit.Service.FindLoan(id)!.Repaid);
        AssertSound(kit);
    }

    [Fact]
    public void AutoRepayIgnoresSpendingZeroPercentAndOtherPlayersIncome()
    {
        var kit = Kit(o => o.LoanScope = EconomyScope.Anyone);
        var id = Accepted(kit, pct: 0);
        var other = Accepted(kit, "z", 1, 3, pct: 100); // a loan of player 3

        kit.Fund(2, 1000);
        Assert.Equal(1100, kit.Service.FindLoan(id)!.Outstanding);

        kit.Fund(4, 1000); // nobody lent to 4
        kit.Fund(1, 1000); // the lender's own income is not repaid anywhere
        Assert.Equal(1100, kit.Service.FindLoan(other)!.Outstanding);

        var spend = new CreditDeltaT { Amount = -300, Seq = 0 };
        Assert.True(kit.Service.BookCreditDelta(3, false, spend).Booked);
        Assert.Equal(1100, kit.Service.FindLoan(other)!.Outstanding);
        AssertSound(kit);
    }

    [Fact]
    public void AutoRepayAcrossTwoLoansOfOneLenderIsOneEntryOldestFirst()
    {
        var kit = Kit();
        var a = Accepted(kit, "a", principal: 100, total: 100, pct: 100);
        var b = Accepted(kit, "b", principal: 500, total: 500, pct: 100);

        kit.Fund(2, 250); // 100% each: the first loan takes 100, the second the remaining 150

        Assert.Equal(LoanState.Repaid, kit.Service.FindLoan(a)!.State);
        Assert.Equal(350, kit.Service.FindLoan(b)!.Outstanding);
        var income = kit.Store.Transactions.Last(t => t.Kind == TxKind.GameIncome);
        Assert.Equal(2, income.Entries.Count(e => e.Amount > 0 || e.Wallet == WalletId.World)); // lender's two shares merged
        AssertSound(kit);
    }

    [Fact]
    public void AutoRepayAlsoWorksOnAnOverdueLoan()
    {
        var kit = Kit();
        var id = Accepted(kit, due: 10, pct: 10);
        kit.Time.Advance(TimeSpan.FromSeconds(11));
        kit.Service.ProcessLoanTimers();

        kit.Fund(2, 1000);

        var loan = kit.Service.FindLoan(id)!;
        Assert.Equal((LoanState.Overdue, 1000), (loan.State, loan.Outstanding));
    }

    // ------------------------------------------------------------------ 3. idempotency

    [Fact]
    public void ReplayedOfferReturnsTheSameLoanAndDoesNothingTwice()
    {
        var kit = Kit();
        var first = kit.Service.OfferLoan(1, "k", 2, 1000, 1100, 3600, 300, 0, "m");
        kit.Fund(1, 0 + 1); // unrelated booking in between
        var replay = kit.Service.OfferLoan(1, "k", 2, 1000, 1100, 3600, 300, 0, "m");

        Assert.True(replay.Ok);
        Assert.Equal(first.Loan!.Id, replay.Loan!.Id);
        Assert.Single(kit.Service.Loans);
        Assert.Equal(1000, kit.Balance(LoanWallets.Escrow(first.Loan.Id)));
        Assert.Equal(9001, kit.Balance(WalletId.Player(1)));

        var mismatch = kit.Service.OfferLoan(1, "k", 2, 2000, 2200, 3600, 300, 0, "m");
        Assert.Equal(EconomyReject.RequestIdReuse, mismatch.Reason);
        Assert.Contains("different payload", mismatch.Detail);
        Assert.Equal(1, kit.Ledger.RequestIdReuseCount);
        Assert.Single(kit.Service.Loans);
        AssertSound(kit);
    }

    [Fact]
    public void ReplayedRespondRepayForgiveAndCancelDoNothingTwice()
    {
        var kit = Kit();
        var id = Offer(kit);
        var spare = Offer(kit, "o2");

        // respond
        Assert.True(kit.Service.RespondLoan(2, "r", id, true).Ok);
        Assert.True(kit.Service.RespondLoan(2, "r", id, true).Ok);
        Assert.Equal(1000, kit.Balance(WalletId.Player(2)));
        Assert.Equal(EconomyReject.RequestIdReuse, kit.Service.RespondLoan(2, "r", id, false).Reason);

        // repay
        kit.Fund(2, 500);
        Assert.True(kit.Service.RepayLoan(2, "p", id, 100).Ok);
        Assert.True(kit.Service.RepayLoan(2, "p", id, 100).Ok);
        Assert.Equal(1000, kit.Service.FindLoan(id)!.Outstanding);
        Assert.Equal(EconomyReject.RequestIdReuse, kit.Service.RepayLoan(2, "p", id, 200).Reason);

        // forgive
        Assert.True(kit.Service.ForgiveLoan(1, "f", id, 100).Ok);
        Assert.True(kit.Service.ForgiveLoan(1, "f", id, 100).Ok);
        Assert.Equal(900, kit.Service.FindLoan(id)!.Outstanding);
        Assert.Equal(EconomyReject.RequestIdReuse, kit.Service.ForgiveLoan(1, "f", id, 50).Reason);

        // withdraw
        Assert.True(kit.Service.WithdrawLoan(1, "c", spare).Ok);
        var balance = kit.Balance(WalletId.Player(1));
        Assert.True(kit.Service.WithdrawLoan(1, "c", spare).Ok);
        Assert.Equal(balance, kit.Balance(WalletId.Player(1)));
        Assert.Equal(EconomyReject.RequestIdReuse, kit.Service.WithdrawLoan(1, "c", id).Reason);
        AssertSound(kit);
    }

    [Fact]
    public void ARejectedLoanRequestIsNotStoredSoItCanBeRetried()
    {
        var kit = Kit();

        Assert.Equal(EconomyReject.UnknownPlayer, kit.Service.OfferLoan(1, "k", 99, 100, 110, 0, 300, 0).Reason);
        Assert.Empty(kit.Service.Loans);
        Assert.True(kit.Service.OfferLoan(1, "k", 2, 100, 110, 0, 300, 0).Ok);
    }

    // ------------------------------------------------------------------ 4. scope

    [Theory]
    [InlineData(EconomyScope.Off, 2, EconomyReject.ScopeDisabled)]
    [InlineData(EconomyScope.Off, 3, EconomyReject.ScopeDisabled)]
    [InlineData(EconomyScope.Off, 4, EconomyReject.ScopeDisabled)]
    [InlineData(EconomyScope.Off, 5, EconomyReject.ScopeDisabled)]
    [InlineData(EconomyScope.Teammates, 2, EconomyReject.None)]
    [InlineData(EconomyScope.Teammates, 3, EconomyReject.ScopeDenied)]
    [InlineData(EconomyScope.Teammates, 4, EconomyReject.ScopeDenied)]
    [InlineData(EconomyScope.Teammates, 5, EconomyReject.ScopeDenied)]
    [InlineData(EconomyScope.Allied, 2, EconomyReject.None)]
    [InlineData(EconomyScope.Allied, 3, EconomyReject.None)]
    [InlineData(EconomyScope.Allied, 4, EconomyReject.ScopeDenied)]
    [InlineData(EconomyScope.Allied, 5, EconomyReject.ScopeDenied)]
    [InlineData(EconomyScope.Anyone, 2, EconomyReject.None)]
    [InlineData(EconomyScope.Anyone, 3, EconomyReject.None)]
    [InlineData(EconomyScope.Anyone, 4, EconomyReject.None)]
    [InlineData(EconomyScope.Anyone, 5, EconomyReject.None)]
    public void LoanScopeTimesRelation(EconomyScope scope, int borrower, EconomyReject expected)
    {
        var kit = Kit(o => o.LoanScope = scope);

        var result = kit.Service.OfferLoan(1, "k", borrower, 100, 110, 0, 300, 0);

        Assert.Equal(expected, result.Reason);
        Assert.Equal(expected == EconomyReject.None ? 9900 : 10_000, kit.Balance(WalletId.Player(1)));
        Assert.Equal(expected == EconomyReject.None ? 1 : 0, kit.Service.Loans.Count);
        AssertSound(kit);
    }

    [Fact]
    public void TheScopeIsCheckedAgainAtAcceptAndAnOfferThatNoLongerFitsIsCancelledAndRefunded()
    {
        var kit = Kit(o => o.LoanScope = EconomyScope.Allied);
        var id = Offer(kit, borrower: 3);

        kit.Teams.SetRelation(1, 2, TeamRelation.Hostile); // the alliance ended while the offer was open
        var result = kit.Service.RespondLoan(3, "r1", id, accept: true);

        Assert.Equal(EconomyReject.ScopeDenied, result.Reason);
        Assert.Equal(LoanState.Cancelled, kit.Service.FindLoan(id)!.State);
        Assert.Equal(10_000, kit.Balance(WalletId.Player(1)));
        Assert.Equal(0, kit.Balance(LoanWallets.Escrow(id)));
        Assert.Equal(0, kit.Balance(WalletId.Player(3)));
        AssertSound(kit);
    }

    [Fact]
    public void DecliningIsStillPossibleWhenTheScopeChanged()
    {
        var kit = Kit(o => o.LoanScope = EconomyScope.Anyone);
        var id = Offer(kit, borrower: 5);
        kit.Options.LoanScope = EconomyScope.Off;

        Assert.True(kit.Service.RespondLoan(5, "r1", id, accept: false).Ok);
        Assert.Equal(10_000, kit.Balance(WalletId.Player(1)));
    }

    [Fact]
    public void RepayingWorksWhateverTheScopeBecame()
    {
        var kit = Kit(o => o.LoanScope = EconomyScope.Anyone);
        var id = Accepted(kit, borrower: 5);
        kit.Fund(5, 500);
        kit.Options.LoanScope = EconomyScope.Off;

        Assert.True(kit.Service.RepayLoan(5, "p1", id, 1100).Ok);
        Assert.Equal(LoanState.Repaid, kit.Service.FindLoan(id)!.State);
    }

    // ------------------------------------------------------------------ 5. limits and validation

    [Fact]
    public void TheOpenLoanLimitCountsLenderAndBorrowerRolesAndFreesUpWhenLoansClose()
    {
        var kit = Kit(o => o.MaxOpenLoansPerPlayer = 2);
        kit.Fund(2, 5000);
        var one = Offer(kit, "a", 1, 2, 100, 100);
        Offer(kit, "b", 1, 2, 100, 100);

        Assert.Equal(EconomyReject.TooManyOpen, kit.Service.OfferLoan(1, "c", 2, 100, 100, 0, 300, 0).Reason); // lender is at the limit
        Assert.Equal(EconomyReject.TooManyOpen, kit.Service.OfferLoan(2, "d", 1, 100, 100, 0, 300, 0).Reason); // so is player 2, as borrower

        Assert.True(kit.Service.RespondLoan(2, "r", one, accept: false).Ok); // closes one slot for both
        Assert.True(kit.Service.OfferLoan(1, "g", 2, 100, 100, 0, 300, 0).Ok);
        Assert.Equal(2, kit.Service.OpenLoanCount(1));
    }

    [Fact]
    public void ARunningLoanKeepsUsingASlotUntilItIsClosed()
    {
        var kit = Kit(o => o.MaxOpenLoansPerPlayer = 1);
        var id = Accepted(kit);

        Assert.Equal(EconomyReject.TooManyOpen, kit.Service.OfferLoan(1, "x", 2, 10, 10, 0, 300, 0).Reason);

        kit.Service.AdminForgiveLoan("admin:a", id);
        Assert.True(kit.Service.OfferLoan(1, "y", 2, 10, 10, 0, 300, 0).Ok);
    }

    [Fact]
    public void OfferValidation()
    {
        var kit = Kit(o =>
        {
            o.MaxLoanPrincipal = 5000;
            o.MaxLoanInterestBp = 1000;
        });
        var s = kit.Service;

        Assert.Equal(EconomyReject.AmountInvalid, s.OfferLoan(1, "a", 2, 0, 0, 0, 300, 0).Reason);
        Assert.Equal(EconomyReject.AmountInvalid, s.OfferLoan(1, "b", 2, -5, 10, 0, 300, 0).Reason);
        Assert.Equal(EconomyReject.AmountInvalid, s.OfferLoan(1, "c", 2, 100, 99, 0, 300, 0).Reason); // repay_total < principal
        Assert.Equal(EconomyReject.AmountInvalid, s.OfferLoan(1, "d", 2, 100, 111, 0, 300, 0).Reason); // 11% > cap of 10%
        Assert.True(s.OfferLoan(1, "e", 2, 100, 110, 0, 300, 0).Ok); // exactly 10%
        Assert.Equal(EconomyReject.AmountInvalid, s.OfferLoan(1, "f", 2, 100, 100, 0, 300, 101).Reason);
        Assert.Equal(EconomyReject.AmountInvalid, s.OfferLoan(1, "g", 2, 100, 100, -1, 300, 0).Reason);
        Assert.Equal(EconomyReject.OverMaxAmount, s.OfferLoan(1, "h", 2, 5001, 5001, 0, 300, 0).Reason);
        Assert.Equal(EconomyReject.NotParty, s.OfferLoan(1, "i", 1, 100, 100, 0, 300, 0).Reason);
        Assert.Equal(EconomyReject.UnknownPlayer, s.OfferLoan(1, "j", 99, 100, 100, 0, 300, 0).Reason);
        Assert.Equal(EconomyReject.UnknownPlayer, s.OfferLoan(98, "k", 2, 100, 100, 0, 300, 0).Reason);
        Assert.Equal(EconomyReject.InsufficientFunds, s.OfferLoan(2, "l", 1, 100, 100, 0, 300, 0).Reason); // 2 has nothing
        Assert.Single(s.Loans);
        AssertSound(kit);
    }

    [Fact]
    public void ResponsesCheckTheLoanTheStateAndTheParty()
    {
        var kit = Kit();
        var id = Offer(kit);

        Assert.Equal(EconomyReject.UnknownLoan, kit.Service.RespondLoan(2, "a", 55, true).Reason);
        Assert.Equal(EconomyReject.NotParty, kit.Service.RespondLoan(1, "b", id, true).Reason); // the lender cannot accept
        Assert.Equal(EconomyReject.NotParty, kit.Service.RespondLoan(3, "c", id, true).Reason);
        Assert.Equal(EconomyReject.WrongState, kit.Service.RepayLoan(2, "d", id, 10).Reason); // still only an offer
        Assert.Equal(EconomyReject.WrongState, kit.Service.ForgiveLoan(1, "e", id, 0).Reason);
        Assert.True(kit.Service.RespondLoan(2, "f", id, true).Ok);
        Assert.Equal(EconomyReject.WrongState, kit.Service.RespondLoan(2, "g", id, true).Reason);
        Assert.Equal(EconomyReject.WrongState, kit.Service.WithdrawLoan(1, "h", id).Reason);
    }

    [Fact]
    public void LenderAndBorrowerInOneSharedWalletCannotLendToEachOther()
    {
        var kit = Kit(o => o.CreditMode = CreditMode.Shared);

        Assert.Equal(EconomyReject.SameWallet, kit.Service.OfferLoan(1, "k", 2, 100, 100, 0, 300, 0).Reason);
    }

    [Fact]
    public void LoansBetweenTeamsInSharedModeUseTheTeamWallets()
    {
        var kit = Kit(o =>
        {
            o.CreditMode = CreditMode.Shared;
            o.LoanScope = EconomyScope.Allied;
        });
        var id = Offer(kit, borrower: 3);
        Assert.True(kit.Service.RespondLoan(3, "r", id, true).Ok);

        Assert.Equal(9000, kit.Balance(WalletId.TeamShared(1)));
        Assert.Equal(1000, kit.Balance(WalletId.TeamShared(2)));
        AssertSound(kit);
    }

    // ------------------------------------------------------------------ 6. frozen wallets and economy

    [Fact]
    public void FrozenWalletsRejectLoanActions()
    {
        var kit = Kit();

        kit.Ledger.SetWalletFrozen(WalletId.Player(1), true, "audit");
        Assert.Equal(EconomyReject.EconomyFrozen, kit.Service.OfferLoan(1, "a", 2, 100, 100, 0, 300, 0).Reason); // lender frozen
        kit.Ledger.SetWalletFrozen(WalletId.Player(1), false, null);

        kit.Ledger.SetWalletFrozen(WalletId.Player(2), true, "audit");
        Assert.Equal(EconomyReject.EconomyFrozen, kit.Service.OfferLoan(1, "b", 2, 100, 100, 0, 300, 0).Reason); // borrower frozen
        kit.Ledger.SetWalletFrozen(WalletId.Player(2), false, null);

        var id = Offer(kit, "c");
        kit.Ledger.SetWalletFrozen(WalletId.Player(2), true, "audit");
        Assert.Equal(EconomyReject.EconomyFrozen, kit.Service.RespondLoan(2, "d", id, true).Reason);
        Assert.Equal(LoanState.Offered, kit.Service.FindLoan(id)!.State);
        kit.Ledger.SetWalletFrozen(WalletId.Player(2), false, null);
        Assert.True(kit.Service.RespondLoan(2, "e", id, true).Ok);

        kit.Fund(2, 500);
        kit.Ledger.SetWalletFrozen(WalletId.Player(2), true, "audit");
        Assert.Equal(EconomyReject.EconomyFrozen, kit.Service.RepayLoan(2, "f", id, 10).Reason); // borrower frozen
        kit.Ledger.SetWalletFrozen(WalletId.Player(2), false, null);
        kit.Ledger.SetWalletFrozen(WalletId.Player(1), true, "audit");
        Assert.Equal(EconomyReject.EconomyFrozen, kit.Service.RepayLoan(2, "g", id, 10).Reason); // lender frozen
        AssertSound(kit);
    }

    [Fact]
    public void ARefundGoesThroughEvenWhenTheLendersWalletIsFrozen()
    {
        var kit = Kit();
        var id = Offer(kit);
        kit.Ledger.SetWalletFrozen(WalletId.Player(1), true, "audit");

        Assert.True(kit.Service.RespondLoan(2, "r", id, accept: false).Ok);

        Assert.Equal(10_000, kit.Balance(WalletId.Player(1)));
        Assert.Equal(0, kit.Balance(LoanWallets.Escrow(id)));
    }

    [Fact]
    public void AFrozenEconomyStopsLoanActionsAndPostponesTheTimers()
    {
        var kit = Kit();
        var id = Offer(kit, ttl: 10);
        kit.Ledger.Freeze("test");

        Assert.Equal(EconomyReject.EconomyFrozen, kit.Service.OfferLoan(1, "b", 2, 100, 100, 0, 300, 0).Reason);
        Assert.Equal(EconomyReject.EconomyFrozen, kit.Service.RespondLoan(2, "c", id, true).Reason);
        Assert.Equal(EconomyReject.EconomyFrozen, kit.Service.WithdrawLoan(1, "d", id).Reason);
        kit.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, kit.Service.ProcessLoanTimers());
        Assert.Equal(LoanState.Offered, kit.Service.FindLoan(id)!.State);

        kit.Ledger.Unfreeze("admin:a");
        Assert.Equal(1, kit.Service.ProcessLoanTimers());
        Assert.Equal(LoanState.Expired, kit.Service.FindLoan(id)!.State);
        AssertSound(kit);
    }

    [Fact]
    public void AnOverdrawnLenderCannotOffer()
    {
        var kit = Kit();
        Assert.True(kit.Service.BookCreditDelta(1, false, new CreditDeltaT { Amount = -11_000, Seq = 0 }).Booked); // game spent more than the ledger had

        Assert.Equal(EconomyReject.InsufficientFunds, kit.Service.OfferLoan(1, "a", 2, 100, 100, 0, 300, 0).Reason);
    }

    [Fact]
    public void AFailedCommitLeavesNeitherMoneyNorLoanStateBehind()
    {
        var kit = Kit();
        var id = Offer(kit);

        kit.Store.FailCommits = true;
        Assert.False(kit.Service.OfferLoan(1, "n", 2, 100, 100, 0, 300, 0).Ok);
        Assert.False(kit.Service.RespondLoan(2, "r", id, true).Ok);
        kit.Store.FailCommits = false;

        Assert.Single(kit.Service.Loans);
        Assert.Equal(LoanState.Offered, kit.Service.FindLoan(id)!.State);
        Assert.Equal(1000, kit.Balance(LoanWallets.Escrow(id)));
        Assert.Equal(0, kit.Balance(WalletId.Player(2)));
        Assert.True(kit.Service.RespondLoan(2, "r", id, true).Ok); // the same key works once the disk is back
        AssertSound(kit);
    }

    // ------------------------------------------------------------------ 7. auditor

    [Fact]
    public void TheAuditorFlagsAnEscrowThatDoesNotMatchTheLoan()
    {
        var kit = Kit();
        var id = Offer(kit);
        kit.Auditor.AddCheck(EconomyService.AuditLoans);
        Assert.True(kit.Auditor.RunNow().Ok);

        kit.Store.TamperBalance(EconomyKit.Session, LoanWallets.Escrow(id), 5); // simulated corruption

        var report = kit.Auditor.RunNow();
        Assert.False(report.Ok);
        Assert.True(kit.Ledger.IsFrozen);
    }

    // ------------------------------------------------------------------ 8. property test

    [Theory]
    [InlineData(1, CreditMode.PerPlayer)]
    [InlineData(2, CreditMode.PerPlayer)]
    [InlineData(3, CreditMode.PerPlayer)]
    [InlineData(4, CreditMode.Shared)]
    [InlineData(5, CreditMode.Auto)]
    public void RandomLoanTrafficKeepsTheLedgerBalancedAndEscrowsExact(int seed, CreditMode mode)
    {
        var rng = new Random(seed);
        var kit = Kit(o =>
        {
            o.CreditMode = mode;
            o.LoanScope = EconomyScope.Anyone;
            o.MaxOpenLoansPerPlayer = 4;
            o.MaxLoanInterestBp = 100_000;
        });
        var players = Members.Keys.ToArray();
        var relations = new[] { TeamRelation.Allied, TeamRelation.Neutral, TeamRelation.Hostile };
        var loans = new List<long>();
        var booked = 0;

        for (var i = 0; i < 1200; i++)
        {
            var a = players[rng.Next(players.Length)];
            var b = players[rng.Next(players.Length)];
            var key = $"k{i}";
            var loan = loans.Count == 0 ? 0 : loans[rng.Next(loans.Count)];
            switch (rng.Next(13))
            {
                case 0:
                case 1:
                    kit.Fund(a, rng.Next(1, 800));
                    break;
                case 2:
                case 3:
                {
                    var principal = rng.Next(-5, 600);
                    var result = kit.Service.OfferLoan(a, key, b, principal, principal + rng.Next(0, 80), rng.Next(0, 4) * 30, rng.Next(0, 3) * 60, rng.Next(0, 4) * 25, "m");
                    if (result.Ok)
                    {
                        loans.Add(result.Loan!.Id);
                        booked++;
                    }

                    break;
                }

                case 4:
                case 5:
                    booked += kit.Service.RespondLoan(a, key, loan, rng.Next(3) != 0).Ok ? 1 : 0;
                    break;
                case 6:
                case 7:
                    booked += kit.Service.RepayLoan(a, key, loan, rng.Next(-3, 700)).Ok ? 1 : 0;
                    break;
                case 8:
                    booked += kit.Service.ForgiveLoan(a, key, loan, rng.Next(0, 100)).Ok ? 1 : 0;
                    break;
                case 9:
                    booked += kit.Service.WithdrawLoan(a, key, loan).Ok ? 1 : 0;
                    break;
                case 10:
                    kit.Time.Advance(TimeSpan.FromSeconds(rng.Next(1, 90)));
                    kit.Service.ProcessLoanTimers();
                    break;
                case 11:
                    if (rng.Next(2) == 0)
                    {
                        kit.Service.AdminCancelLoan("admin:fuzz", loan);
                    }
                    else
                    {
                        kit.Service.AdminForgiveLoan("admin:fuzz", loan);
                    }

                    break;
                default:
                    kit.Teams.SetRelation(1 + rng.Next(3), 4, relations[rng.Next(relations.Length)]);
                    kit.Options.LoanScope = (EconomyScope)rng.Next(4);
                    _ = kit.Service.OfferLoan(1, "replay", 2, 5, 5, 0, 300, 0).Ok;
                    break;
            }

            Assert.Equal(0, kit.Ledger.TotalBalance());
            Assert.All(kit.Ledger.Wallets.Where(w => w.Id.Kind != WalletKind.World), w => Assert.True(w.Balance >= 0, $"{w.Id} = {w.Balance} after op {i}"));
            Assert.Empty(EconomyService.AuditLoans(kit.Ledger));
            Assert.All(kit.Service.Loans, l => Assert.InRange(l.Outstanding, 0, l.RepayTotal));
        }

        Assert.True(booked > 30, "the random run should have booked a fair number of loan actions, got " + booked);
        foreach (var loan in kit.Service.Loans.Where(l => !l.IsOpen))
        {
            Assert.Equal(0, kit.Balance(loan.EscrowWallet));
        }

        AssertSound(kit);
        kit.Auditor.AddCheck(EconomyService.AuditLoans);
        Assert.True(kit.Auditor.RunNow().Ok);
    }
}
