using Google.FlatBuffers;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Economy;
using X4MP.Core.Tests.Session;
using X4MP.Proto;

namespace X4MP.Core.Tests.Economy;

/// <summary>The player money actions through a real session actor: results, wallet updates, rate limit (M1-E3).</summary>
public sealed class EconomyActionsModuleTests
{
    private static FlatBufferBuilder Donate(ulong key, int to, long amount)
    {
        var fbb = new FlatBufferBuilder(128);
        fbb.Finish(DonateRequest.Pack(fbb, new DonateRequestT { RequestKey = new Id128T { Lo = key }, ToPlayer = (ushort)to, Amount = amount, Memo = "m" }).Value);
        return fbb;
    }

    private static FlatBufferBuilder Transfer(ulong key, int to, long amount)
    {
        var fbb = new FlatBufferBuilder(128);
        fbb.Finish(CreditTransferRequest.Pack(fbb, new CreditTransferRequestT { RequestKey = new Id128T { Lo = key }, ToPlayer = (ushort)to, Amount = amount }).Value);
        return fbb;
    }

    private static FlatBufferBuilder PoolDeposit(ulong key, long amount)
    {
        var fbb = new FlatBufferBuilder(128);
        fbb.Finish(PoolDepositRequest.Pack(fbb, new PoolDepositRequestT { RequestKey = new Id128T { Lo = key }, Amount = amount }).Value);
        return fbb;
    }

    private static FlatBufferBuilder PoolWithdraw(ulong key, long amount)
    {
        var fbb = new FlatBufferBuilder(128);
        fbb.Finish(PoolWithdrawRequest.Pack(fbb, new PoolWithdrawRequestT { RequestKey = new Id128T { Lo = key }, Amount = amount }).Value);
        return fbb;
    }

    private static EconomyResult LastResult(JoinedNode node) => node.Connection.SentOf(MsgType.EconomyResult)[^1].Decode<EconomyResult>();

    private static async Task<(ActorRig Rig, EconomyModule Module, JoinedNode Boss, JoinedNode Pilot, FakeTeamDirectory Teams)> StartAsync(Action<EconomyOptions>? configure = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var options = new EconomyOptions { StartingCredits = 1000, DonateScope = EconomyScope.Allied };
        configure?.Invoke(options);
        var teams = new FakeTeamDirectory();
        teams.Set([1, 2], new Dictionary<int, int?> { [1] = 1, [2] = 2 });
        var module = new EconomyModule(() => options, new InMemoryEconomyStore(), teams, time);
        var rig = new ActorRig(time, null, null, [module]);
        var boss = await rig.JoinAuthorityAsync();
        var pilot = await rig.JoinAsync("Pilot");
        await rig.BringInGameAsync(boss);
        await rig.BringInGameAsync(pilot);
        Assert.Equal((1, 2), (boss.PlayerId, pilot.PlayerId));
        module.OnSessionPhaseChanged(SessionPhase.AuthorityLoading, SessionPhase.Running); // the rig has no world checkpoint, so tell the module
        return (rig, module, boss, pilot, teams);
    }

    [Fact]
    public async Task ADonationAnswersTheRequesterAndUpdatesBothWallets()
    {
        var (rig, module, boss, pilot, teams) = await StartAsync();
        await using (rig)
        {
            teams.SetRelation(1, 2, X4MP.Core.Teams.TeamRelation.Allied);

            await rig.SendAsync(boss, MsgType.DonateRequest, Donate(1, pilot.PlayerId, 250));

            var result = LastResult(boss);
            Assert.Equal(EconomyStatus.Ok, result.Status);
            Assert.Equal(EconomyReject.None, result.Reason);
            Assert.Equal(1000 - 250, Assert.Single(Enumerable.Range(0, result.BalancesLength).Select(i => result.Balances(i)!.Value), b => b.Wallet!.Value.Kind == X4MP.Proto.WalletKind.Player && b.Wallet!.Value.OwnerId == boss.PlayerId).Balance);
            Assert.Equal(1250, module.Service!.Ledger.BalanceOf(WalletId.Player(pilot.PlayerId)));

            var pilotUpdate = pilot.Connection.SentOf(MsgType.WalletUpdate)[^1].Decode<WalletUpdate>();
            Assert.Equal(LedgerReason.Donation, pilotUpdate.Reason);
            Assert.Equal(1250, pilotUpdate.Balances(0)!.Value.Balance);
            Assert.Equal(EffectiveCreditMode.PerPlayer, pilotUpdate.EffectiveMode);
        }
    }

    [Fact]
    public async Task ARejectedRequestGetsAReasonAndAReplayGetsTheIdenticalResult()
    {
        var (rig, module, boss, pilot, _) = await StartAsync();
        await using (rig)
        {
            await rig.SendAsync(boss, MsgType.DonateRequest, Donate(1, pilot.PlayerId, 10)); // neutral teams, Allied scope
            Assert.Equal(EconomyReject.ScopeDenied, LastResult(boss).Reason);
            Assert.Equal(EconomyStatus.Rejected, LastResult(boss).Status);

            await rig.SendAsync(boss, MsgType.CreditTransferRequest, Transfer(2, pilot.PlayerId, 10));
            Assert.Equal(EconomyReject.NotTeammate, LastResult(boss).Reason);

            module.Service!.Ledger.Find(WalletId.Player(boss.PlayerId));
            await rig.SendAsync(boss, MsgType.PoolDepositRequest, PoolDeposit(3, 100));
            var first = boss.Connection.SentOf(MsgType.EconomyResult)[^1];
            Assert.Equal(EconomyStatus.Ok, first.Decode<EconomyResult>().Status);
            await rig.SendAsync(boss, MsgType.PoolDepositRequest, PoolDeposit(3, 100));
            var replay = boss.Connection.SentOf(MsgType.EconomyResult)[^1];

            Assert.Equal(first.Payload, replay.Payload);
            Assert.Equal(900, module.Service.Ledger.BalanceOf(WalletId.Player(boss.PlayerId)));
            Assert.Equal(100, module.Service.Ledger.BalanceOf(WalletId.TeamPool(1)));

            await rig.SendAsync(boss, MsgType.PoolWithdrawRequest, PoolWithdraw(3, 100)); // same key, other request type
            Assert.Equal(EconomyStatus.Rejected, LastResult(boss).Status);
        }
    }

    [Fact]
    public async Task TheSixthRequestInTenSecondsIsRateLimitedOverTheWire()
    {
        var (rig, _, boss, pilot, _) = await StartAsync();
        await using (rig)
        {
            for (ulong i = 1; i <= 5; i++)
            {
                await rig.SendAsync(boss, MsgType.DonateRequest, Donate(i, pilot.PlayerId, 1));
                Assert.NotEqual(EconomyReject.RateLimited, LastResult(boss).Reason);
            }

            await rig.SendAsync(boss, MsgType.DonateRequest, Donate(6, pilot.PlayerId, 1));
            Assert.Equal(EconomyReject.RateLimited, LastResult(boss).Reason);

            await rig.AdvanceSecondsAsync(10);
            await rig.SendAsync(boss, MsgType.DonateRequest, Donate(7, pilot.PlayerId, 1));
            Assert.NotEqual(EconomyReject.RateLimited, LastResult(boss).Reason);
        }
    }

    [Fact]
    public async Task RequestsWhileTheSessionIsNotRunningAreRefused()
    {
        var (rig, module, boss, pilot, _) = await StartAsync();
        await using (rig)
        {
            module.OnSessionPhaseChanged(SessionPhase.Running, SessionPhase.Paused);

            await rig.SendAsync(boss, MsgType.DonateRequest, Donate(1, pilot.PlayerId, 5));

            Assert.Equal(EconomyReject.SessionNotRunning, LastResult(boss).Reason);
            Assert.Equal(1000, module.Service!.Ledger.BalanceOf(WalletId.Player(boss.PlayerId)));
        }
    }
}
