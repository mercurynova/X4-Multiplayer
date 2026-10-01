using Google.FlatBuffers;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Economy;
using X4MP.Core.Tests.Session;
using X4MP.Proto;
using LoanState = X4MP.Core.Economy.LoanState;

namespace X4MP.Core.Tests.Economy;

/// <summary>Loan requests through a real session actor: results, <c>LoanStatus</c>, notices and the overdue timer (M1-E4).</summary>
public sealed class LoanModuleTests
{
    private static FlatBufferBuilder Offer(ulong key, int borrower, long principal, long total, uint dueIn, uint ttl = 300, byte pct = 0)
    {
        var fbb = new FlatBufferBuilder(128);
        fbb.Finish(LoanOffer.Pack(fbb, new LoanOfferT
        {
            RequestKey = new Id128T { Lo = key },
            Borrower = (ushort)borrower,
            Principal = principal,
            RepayTotal = total,
            DueInS = dueIn,
            OfferTtlS = ttl,
            AutoRepayPct = pct,
            Memo = "m",
        }).Value);
        return fbb;
    }

    private static FlatBufferBuilder Respond(ulong key, ulong loan, bool accept)
    {
        var fbb = new FlatBufferBuilder(64);
        fbb.Finish(LoanRespond.Pack(fbb, new LoanRespondT { RequestKey = new Id128T { Lo = key }, LoanId = new Id128T { Lo = loan }, Accept = accept }).Value);
        return fbb;
    }

    private static FlatBufferBuilder Repay(ulong key, ulong loan, long amount)
    {
        var fbb = new FlatBufferBuilder(64);
        fbb.Finish(LoanRepay.Pack(fbb, new LoanRepayT { RequestKey = new Id128T { Lo = key }, LoanId = new Id128T { Lo = loan }, Amount = amount }).Value);
        return fbb;
    }

    private static FlatBufferBuilder Forgive(ulong key, ulong loan)
    {
        var fbb = new FlatBufferBuilder(64);
        fbb.Finish(LoanForgive.Pack(fbb, new LoanForgiveT { RequestKey = new Id128T { Lo = key }, LoanId = new Id128T { Lo = loan } }).Value);
        return fbb;
    }

    private static FlatBufferBuilder Cancel(ulong key, ulong loan)
    {
        var fbb = new FlatBufferBuilder(64);
        fbb.Finish(LoanCancel.Pack(fbb, new LoanCancelT { RequestKey = new Id128T { Lo = key }, LoanId = new Id128T { Lo = loan } }).Value);
        return fbb;
    }

    private static EconomyResult LastResult(JoinedNode node) => node.Connection.SentOf(MsgType.EconomyResult)[^1].Decode<EconomyResult>();

    private static LoanStatus LastStatus(JoinedNode node) => node.Connection.SentOf(MsgType.LoanStatus)[^1].Decode<LoanStatus>();

    private static async Task<(ActorRig Rig, EconomyModule Module, JoinedNode Boss, JoinedNode Pilot)> StartAsync(Action<EconomyOptions>? configure = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var options = new EconomyOptions { StartingCredits = 1000, LoanScope = EconomyScope.Anyone };
        configure?.Invoke(options);
        var teams = new FakeTeamDirectory();
        teams.Set([1, 2], new Dictionary<int, int?> { [1] = 1, [2] = 2 });
        var module = new EconomyModule(() => options, new InMemoryEconomyStore(), teams, time);
        var rig = new ActorRig(time, null, null, [module]);
        var boss = await rig.JoinAuthorityAsync();
        var pilot = await rig.JoinAsync("Pilot");
        await rig.BringInGameAsync(boss);
        await rig.BringInGameAsync(pilot);
        module.OnSessionPhaseChanged(SessionPhase.AuthorityLoading, SessionPhase.Running);
        return (rig, module, boss, pilot);
    }

    [Fact]
    public async Task AnOfferIsAnsweredWithTheLoanIdAndBothPartiesGetStatusAndNotices()
    {
        var (rig, module, boss, pilot) = await StartAsync();
        await using (rig)
        {
            await rig.SendAsync(boss, MsgType.LoanOffer, Offer(1, pilot.PlayerId, 500, 550, 600));

            var result = LastResult(boss);
            Assert.Equal(EconomyStatus.Ok, result.Status);
            var loanId = result.RefId!.Value.Lo;
            Assert.True(loanId > 0);
            Assert.Equal(500, module.Service!.Ledger.BalanceOf(LoanWallets.Escrow((long)loanId)));

            Assert.Equal(X4MP.Proto.LoanState.Offered, LastStatus(boss).State);
            var status = LastStatus(pilot);
            Assert.Equal((loanId, (ushort)boss.PlayerId, (ushort)pilot.PlayerId), (status.LoanId!.Value.Lo, status.Lender, status.Borrower));
            Assert.Equal((500, 550), (status.Principal, status.RepayTotal));
            Assert.NotEmpty(pilot.Connection.SentOf(MsgType.ServerNotice)); // "loan offered"

            await rig.SendAsync(pilot, MsgType.LoanRespond, Respond(1, loanId, accept: true));
            Assert.Equal(EconomyStatus.Ok, LastResult(pilot).Status);
            Assert.Equal(X4MP.Proto.LoanState.Active, LastStatus(boss).State);
            Assert.Equal(1500, module.Service.Ledger.BalanceOf(WalletId.Player(pilot.PlayerId)));
            Assert.Equal(500, module.Service.Ledger.BalanceOf(WalletId.Player(boss.PlayerId)));
            Assert.NotEmpty(boss.Connection.SentOf(MsgType.ServerNotice)); // "loan accepted"

            await rig.SendAsync(pilot, MsgType.LoanRepay, Repay(2, loanId, 200));
            Assert.Equal(EconomyStatus.Ok, LastResult(pilot).Status);
            Assert.Equal(200, LastStatus(boss).Repaid);
            Assert.Equal(700, module.Service.Ledger.BalanceOf(WalletId.Player(boss.PlayerId)));
        }
    }

    [Fact]
    public async Task ALoanBecomesOverdueWithinTenSecondsOfItsDueTimeAndBothPartiesAreNotified()
    {
        var (rig, module, boss, pilot) = await StartAsync();
        await using (rig)
        {
            await rig.SendAsync(boss, MsgType.LoanOffer, Offer(1, pilot.PlayerId, 500, 550, dueIn: 30));
            var loanId = LastResult(boss).RefId!.Value.Lo;
            await rig.SendAsync(pilot, MsgType.LoanRespond, Respond(1, loanId, accept: true));
            var notices = (boss.Connection.SentOf(MsgType.ServerNotice).Count, pilot.Connection.SentOf(MsgType.ServerNotice).Count);

            await rig.AdvanceSecondsAsync(29);
            Assert.Equal(LoanState.Active, module.Service!.FindLoan((long)loanId)!.State);

            await rig.AdvanceSecondsAsync(10); // 39 s after acceptance: due at 30 s, flagged within the next tick or two
            Assert.Equal(LoanState.Overdue, module.Service.FindLoan((long)loanId)!.State);
            Assert.Equal(X4MP.Proto.LoanState.Overdue, LastStatus(boss).State);
            Assert.Equal(X4MP.Proto.LoanState.Overdue, LastStatus(pilot).State);
            Assert.True(boss.Connection.SentOf(MsgType.ServerNotice).Count > notices.Item1);
            Assert.True(pilot.Connection.SentOf(MsgType.ServerNotice).Count > notices.Item2);
            var warning = pilot.Connection.SentOf(MsgType.ServerNotice)[^1].Decode<ServerNotice>();
            Assert.Equal(NoticeSeverity.Warning, warning.Severity);
        }
    }

    [Fact]
    public async Task OverdueIsFlaggedByTheNextTickAfterTheDueTime()
    {
        var (rig, module, boss, pilot) = await StartAsync();
        await using (rig)
        {
            await rig.SendAsync(boss, MsgType.LoanOffer, Offer(1, pilot.PlayerId, 500, 550, dueIn: 30));
            var loanId = LastResult(boss).RefId!.Value.Lo;
            await rig.SendAsync(pilot, MsgType.LoanRespond, Respond(1, loanId, accept: true));

            await rig.AdvanceSecondsAsync(30);
            await rig.AdvanceSecondsAsync(2);

            Assert.Equal(LoanState.Overdue, module.Service!.FindLoan((long)loanId)!.State);
        }
    }

    [Fact]
    public async Task ARejectedLoanRequestCarriesTheReasonAndReplaysAreIdentical()
    {
        var (rig, module, boss, pilot) = await StartAsync(o => o.LoanScope = EconomyScope.Teammates);
        await using (rig)
        {
            await rig.SendAsync(boss, MsgType.LoanOffer, Offer(1, pilot.PlayerId, 500, 550, 0));
            Assert.Equal(EconomyReject.ScopeDenied, LastResult(boss).Reason);

            await rig.SendAsync(boss, MsgType.LoanRespond, Respond(2, 99, accept: true));
            Assert.Equal(EconomyReject.UnknownLoan, LastResult(boss).Reason);

            module.Service!.Ledger.Find(WalletId.Player(boss.PlayerId));
            await rig.SendAsync(pilot, MsgType.LoanOffer, Offer(1, boss.PlayerId, 100, 100, 0)); // other player's key space
            Assert.Equal(EconomyReject.ScopeDenied, LastResult(pilot).Reason);
        }
    }

    [Fact]
    public async Task WithdrawForgiveAndDeclineWorkOverTheWireAndReplaysAreIdentical()
    {
        var (rig, module, boss, pilot) = await StartAsync();
        await using (rig)
        {
            await rig.SendAsync(boss, MsgType.LoanOffer, Offer(1, pilot.PlayerId, 100, 100, 0));
            var first = LastResult(boss).RefId!.Value.Lo;
            await rig.SendAsync(boss, MsgType.LoanOffer, Offer(2, pilot.PlayerId, 100, 100, 0));
            var second = LastResult(boss).RefId!.Value.Lo;
            await rig.SendAsync(boss, MsgType.LoanOffer, Offer(3, pilot.PlayerId, 100, 100, 0));
            var third = LastResult(boss).RefId!.Value.Lo;
            Assert.Equal(700, module.Service!.Ledger.BalanceOf(WalletId.Player(boss.PlayerId)));

            await rig.SendAsync(boss, MsgType.LoanCancel, Cancel(4, first));
            Assert.Equal(X4MP.Proto.LoanState.Cancelled, LastStatus(pilot).State); // Withdrawn shows as Cancelled on the wire
            await rig.SendAsync(pilot, MsgType.LoanRespond, Respond(1, second, accept: false));
            Assert.Equal(X4MP.Proto.LoanState.Declined, LastStatus(boss).State);
            Assert.Equal(900, module.Service.Ledger.BalanceOf(WalletId.Player(boss.PlayerId)));

            await rig.SendAsync(pilot, MsgType.LoanRespond, Respond(2, third, accept: true));
            await rig.SendAsync(boss, MsgType.LoanForgive, Forgive(5, third));
            Assert.Equal(X4MP.Proto.LoanState.Forgiven, LastStatus(pilot).State);

            await rig.AdvanceSecondsAsync(10); // the sixth request in 10 s would be rate limited
            var statuses = boss.Connection.SentOf(MsgType.LoanStatus).Count;
            await rig.SendAsync(boss, MsgType.LoanForgive, Forgive(5, third)); // replay
            Assert.Equal(EconomyStatus.Ok, LastResult(boss).Status);
            Assert.Equal(statuses, boss.Connection.SentOf(MsgType.LoanStatus).Count);
        }
    }
}
