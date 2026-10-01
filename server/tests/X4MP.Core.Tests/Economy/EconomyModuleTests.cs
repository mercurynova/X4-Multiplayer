using Google.FlatBuffers;
using X4MP.Core.Economy;
using X4MP.Core.Tests.Session;
using X4MP.Proto;

namespace X4MP.Core.Tests.Economy;

/// <summary>The economy module attached to a real <see cref="X4MP.Core.Session.SessionActor"/> (fake connections and clock).</summary>
public sealed class EconomyModuleTests
{
    private static FlatBufferBuilder DeltaFrame(long amount, ulong seq, ushort player = 0, ushort team = 0)
    {
        var fbb = new FlatBufferBuilder(128);
        fbb.Finish(CreditDelta.Pack(fbb, new CreditDeltaT { Amount = amount, Seq = seq, PlayerId = player, TeamId = team, Source = CreditSource.Trade }).Value);
        return fbb;
    }

    private static (EconomyModule Module, InMemoryEconomyStore Store) NewModule(long startingCredits = 1000)
    {
        var store = new InMemoryEconomyStore();
        var options = new EconomyOptions { StartingCredits = startingCredits };
        return (new EconomyModule(() => options, store), store);
    }

    private static async Task<(ActorRig Rig, JoinedNode Boss, JoinedNode Pilot)> RunningSessionAsync(EconomyModule module)
    {
        var rig = new ActorRig(modules: module);
        var boss = await rig.JoinAuthorityAsync();
        var pilot = await rig.JoinAsync("Pilot");
        await rig.BringInGameAsync(boss);
        await rig.BringInGameAsync(pilot);
        return (rig, boss, pilot);
    }

    private static WalletUpdate Last(JoinedNode node) => node.Connection.SentOf(MsgType.WalletUpdate)[^1].Decode<WalletUpdate>();

    [Fact]
    public async Task JoiningNodesGetStartingCreditsAndTheirFirstWalletUpdate()
    {
        var (module, _) = NewModule(1000);
        var (rig, boss, pilot) = await RunningSessionAsync(module);
        await using (rig)
        {
            Assert.NotNull(module.Service);
            Assert.Equal(2000, module.Service.Ledger.BalanceOf(module.Service.EffectiveWallet(pilot.PlayerId))); // Shared: both players add 1000

            var update = pilot.Connection.SentOf(MsgType.WalletUpdate)[0].Decode<WalletUpdate>();
            Assert.Equal(1, update.BalancesLength);
            Assert.Equal(2000, update.Balances(0)!.Value.Balance);
            Assert.Equal(X4MP.Proto.WalletKind.TeamShared, update.Balances(0)!.Value.Wallet!.Value.Kind);
            Assert.Equal(0UL, update.AckedDeltaSeq);
            Assert.NotEmpty(boss.Connection.SentOf(MsgType.WalletUpdate));
        }
    }

    [Fact]
    public async Task ACreditDeltaFromAClientIsBookedOnceAckedAndAnswered()
    {
        var (module, store) = NewModule(0);
        var (rig, _, pilot) = await RunningSessionAsync(module);
        await using (rig)
        {
            await rig.SendAsync(pilot, MsgType.CreditDelta, DeltaFrame(250, 1));
            await rig.SendAsync(pilot, MsgType.CreditDelta, DeltaFrame(250, 1)); // resent: the node had not seen the ack yet

            var wallet = module.Service!.EffectiveWallet(pilot.PlayerId);
            Assert.Equal(250, module.Service!.Ledger.BalanceOf(wallet));
            Assert.Single(store.Transactions);
            var update = Last(pilot);
            Assert.Equal(1UL, update.AckedDeltaSeq);
            Assert.Equal(EffectiveCreditMode.Shared, update.EffectiveMode); // no team module: one implicit team, Auto resolves to Shared
        }
    }

    [Fact]
    public async Task TheAuthorityBooksForAPlayerAndBothSidesHearAboutIt()
    {
        var (module, _) = NewModule(0);
        var (rig, boss, pilot) = await RunningSessionAsync(module);
        await using (rig)
        {
            await rig.SendAsync(boss, MsgType.CreditDelta, DeltaFrame(900, 4, player: (ushort)pilot.PlayerId));

            Assert.Equal(900, module.Service!.Ledger.BalanceOf(module.Service.EffectiveWallet(pilot.PlayerId)));
            Assert.Equal(4UL, Last(boss).AckedDeltaSeq);
            Assert.Equal(0UL, Last(pilot).AckedDeltaSeq); // the pilot's own sequence is untouched
            Assert.Equal(900, Last(pilot).Balances(0)!.Value.Balance);
        }
    }

    [Fact]
    public async Task TheAuditorRunsOnTheTickAndFreezesOnCorruption()
    {
        var (module, store) = NewModule(500);
        var (rig, _, pilot) = await RunningSessionAsync(module);
        await using (rig)
        {
            store.TamperBalance(module.Service!.Ledger.SessionId, module.Service.EffectiveWallet(pilot.PlayerId), 1);

            await rig.AdvanceSecondsAsync(1);

            Assert.True(module.Service.Ledger.IsFrozen);
            Assert.False(module.Auditor!.LastReport!.Ok);
        }
    }
}
