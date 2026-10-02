using Google.FlatBuffers;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Economy;
using X4MP.Core.Tests.Session;
using X4MP.Proto;

namespace X4MP.Core.Tests.Economy;

/// <summary>M1-E5 over the wire through a real session actor: requests, results, status, the order to the authority, the confirm.</summary>
public sealed class EconomyTradeModuleTests
{
    private const uint Ship = 100;

    private static FlatBufferBuilder Proposal(ulong key, int counterparty, long price)
    {
        var fbb = new FlatBufferBuilder(128);
        fbb.Finish(TradeProposal.Pack(fbb, new TradeProposalT
        {
            RequestKey = new Id128T { Lo = key },
            Counterparty = (ushort)counterparty,
            Give = [new TradeItemT { Kind = TradeItemKind.Ship, Asset = Ship }],
            Want = [new TradeItemT { Kind = TradeItemKind.Credits, Amount = price }],
            TtlS = 300,
        }).Value);
        return fbb;
    }

    private static FlatBufferBuilder Accept(ulong key, ulong trade, uint version)
    {
        var fbb = new FlatBufferBuilder(96);
        fbb.Finish(TradeAccept.Pack(fbb, new TradeAcceptT { RequestKey = new Id128T { Lo = key }, TradeId = new Id128T { Lo = trade }, Version = version }).Value);
        return fbb;
    }

    private static FlatBufferBuilder Cancel(ulong key, ulong trade)
    {
        var fbb = new FlatBufferBuilder(96);
        fbb.Finish(TradeCancel.Pack(fbb, new TradeCancelT { RequestKey = new Id128T { Lo = key }, TradeId = new Id128T { Lo = trade } }).Value);
        return fbb;
    }

    private static FlatBufferBuilder Confirm(ulong trade, bool ok)
    {
        var fbb = new FlatBufferBuilder(96);
        fbb.Finish(AssetTransferConfirm.Pack(fbb, new AssetTransferConfirmT { TradeId = new Id128T { Lo = trade }, Ok = ok, FailedLine = -1, Compensated = true, Error = ok ? string.Empty : "no" }).Value);
        return fbb;
    }

    private static EconomyResult LastResult(JoinedNode node) => node.Connection.SentOf(MsgType.EconomyResult)[^1].Decode<EconomyResult>();

    private sealed record Rig(ActorRig Actor, EconomyModule Module, JoinedNode Boss, JoinedNode Pilot, FakeTradeWorld World, FakeTimeProvider Time);

    private static async Task<Rig> StartAsync(Action<EconomyOptions>? configure = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var options = new EconomyOptions { StartingCredits = 5000, CreditMode = CreditMode.PerPlayer, TradeScope = EconomyScope.Allied, TradeRequiresProximity = false };
        configure?.Invoke(options);
        var teams = new FakeTeamDirectory();
        teams.Set([1], new Dictionary<int, int?> { [1] = 1, [2] = 1 });
        var world = new FakeTradeWorld(teams);
        world.Assets[Ship] = new TradeAssetInfo(Ship, EntityKind.ShipM, 1, 2, 5, false);
        world.PlayerShips[1] = (900, 5);
        world.PlayerShips[2] = (901, 5);
        var module = new EconomyModule(() => options, new InMemoryEconomyStore(), teams, time) { TradeWorld = world };
        var rig = new ActorRig(time, null, null, [module]);
        var boss = await rig.JoinAuthorityAsync();
        var pilot = await rig.JoinAsync("Pilot");
        await rig.BringInGameAsync(boss);
        await rig.BringInGameAsync(pilot);
        module.OnSessionPhaseChanged(SessionPhase.AuthorityLoading, SessionPhase.Running);
        return new Rig(rig, module, boss, pilot, world, time);
    }

    [Fact]
    public async Task ATradeRunsFromProposalToSettlementOverTheWire()
    {
        var r = await StartAsync();
        await using (r.Actor)
        {
            await r.Actor.SendAsync(r.Pilot, MsgType.TradeProposal, Proposal(1, r.Boss.PlayerId, 800));

            var result = LastResult(r.Pilot);
            Assert.Equal(EconomyStatus.Ok, result.Status);
            var tradeId = result.RefId!.Value.Lo;
            Assert.NotEqual(0ul, tradeId);
            var bossStatus = r.Boss.Connection.SentOf(MsgType.TradeStatus)[^1].Decode<TradeStatus>();
            Assert.Equal(TradeState.Proposed, bossStatus.State);
            Assert.Equal((ushort)r.Pilot.PlayerId, bossStatus.Initiator!.Value.PlayerId);
            Assert.Equal(1u, bossStatus.Initiator.Value.AcceptedVersion);
            Assert.Equal(TradeItemKind.Ship, bossStatus.Initiator.Value.Give(0)!.Value.Kind);
            Assert.Empty(r.Boss.Connection.SentOf(MsgType.AssetTransferOrder));

            await r.Actor.SendAsync(r.Boss, MsgType.TradeAccept, Accept(2, tradeId, 1));

            Assert.Equal(EconomyStatus.Ok, LastResult(r.Boss).Status);
            var order = r.Boss.Connection.SentOf(MsgType.AssetTransferOrder).Single().Decode<AssetTransferOrder>(); // the boss is the authority
            Assert.Equal(tradeId, order.TradeId!.Value.Lo);
            Assert.Equal(1, order.LinesLength);
            Assert.Equal(AssetTransferKind.OwnerChange, order.Lines(0)!.Value.Kind);
            Assert.Equal(TradeState.Transferring, r.Pilot.Connection.SentOf(MsgType.TradeStatus)[^1].Decode<TradeStatus>().State);
            Assert.Equal(5000 - 800, r.Module.Service!.Ledger.BalanceOf(WalletId.Player(r.Boss.PlayerId)));
            Assert.Equal(800, r.Module.Service.Ledger.BalanceOf(TradeRecord.EscrowOf((long)tradeId)));

            await r.Actor.SendAsync(r.Boss, MsgType.AssetTransferConfirm, Confirm(tradeId, ok: true));
            await r.Actor.SendAsync(r.Boss, MsgType.AssetTransferConfirm, Confirm(tradeId, ok: true)); // duplicate

            var final = r.Pilot.Connection.SentOf(MsgType.TradeResult).Single().Decode<TradeResult>();
            Assert.Equal(TradeState.Completed, final.State);
            Assert.Equal(EconomyReject.None, final.Reason);
            Assert.Equal(5800, r.Module.Service.Ledger.BalanceOf(WalletId.Player(r.Pilot.PlayerId)));
            Assert.Equal(0, r.Module.Service.Ledger.TotalBalance());
            Assert.Equal(0, r.Module.Service.Ledger.BalanceOf(TradeRecord.EscrowOf((long)tradeId)));
            Assert.Equal(1, r.Module.Service.DuplicateConfirms);
            Assert.Equal(r.Boss.PlayerId, r.World.Assets[Ship].OwnerPlayer);
            Assert.Single(r.World.OwnerChanges);
            Assert.Equal(TradeState.Completed, r.Pilot.Connection.SentOf(MsgType.TradeStatus)[^1].Decode<TradeStatus>().State);
            Assert.Contains(r.Pilot.Connection.SentOf(MsgType.WalletUpdate), f => f.Decode<WalletUpdate>().Reason == LedgerReason.TradeSettle);
        }
    }

    [Fact]
    public async Task AFailedTransferRollsBackAndTheResultSaysSo()
    {
        var r = await StartAsync();
        await using (r.Actor)
        {
            await r.Actor.SendAsync(r.Pilot, MsgType.TradeProposal, Proposal(1, r.Boss.PlayerId, 800));
            var tradeId = LastResult(r.Pilot).RefId!.Value.Lo;
            await r.Actor.SendAsync(r.Boss, MsgType.TradeAccept, Accept(2, tradeId, 1));

            await r.Actor.SendAsync(r.Boss, MsgType.AssetTransferConfirm, Confirm(tradeId, ok: false));

            var final = r.Boss.Connection.SentOf(MsgType.TradeResult).Single().Decode<TradeResult>();
            Assert.Equal(TradeState.RolledBack, final.State);
            Assert.Equal(EconomyReject.AuthorityRejected, final.Reason);
            Assert.Equal(5000, r.Module.Service!.Ledger.BalanceOf(WalletId.Player(r.Boss.PlayerId)));
            Assert.Empty(r.World.OwnerChanges);
            Assert.False(r.Module.Service.IsAssetLocked(Ship));
        }
    }

    [Fact]
    public async Task TradeRequestsGetReasonsAndRespectThePhaseAndTheRateLimit()
    {
        var r = await StartAsync();
        await using (r.Actor)
        {
            await r.Actor.SendAsync(r.Pilot, MsgType.TradeProposal, Proposal(1, r.Boss.PlayerId, 800));
            var tradeId = LastResult(r.Pilot).RefId!.Value.Lo;

            await r.Actor.SendAsync(r.Boss, MsgType.TradeAccept, Accept(2, tradeId, 7));
            Assert.Equal(EconomyReject.StaleVersion, LastResult(r.Boss).Reason);

            await r.Actor.SendAsync(r.Boss, MsgType.TradeAccept, Accept(3, 999, 1));
            Assert.Equal(EconomyReject.UnknownTrade, LastResult(r.Boss).Reason);

            await r.Actor.SendAsync(r.Pilot, MsgType.TradeProposal, Proposal(2, r.Boss.PlayerId, 5)); // the ship is locked now
            Assert.Equal(EconomyReject.AssetUnavailable, LastResult(r.Pilot).Reason);

            await r.Actor.SendAsync(r.Pilot, MsgType.TradeCancel, Cancel(3, tradeId));
            Assert.Equal(EconomyStatus.Ok, LastResult(r.Pilot).Status);
            Assert.Equal(TradeState.Cancelled, r.Boss.Connection.SentOf(MsgType.TradeResult).Single().Decode<TradeResult>().State);

            r.Module.OnSessionPhaseChanged(SessionPhase.Running, SessionPhase.Paused);
            await r.Actor.SendAsync(r.Pilot, MsgType.TradeProposal, Proposal(9, r.Boss.PlayerId, 5));
            Assert.Equal(EconomyReject.SessionNotRunning, LastResult(r.Pilot).Reason);
            r.Module.OnSessionPhaseChanged(SessionPhase.Paused, SessionPhase.Running);

            for (ulong i = 20; i < 27; i++)
            {
                await r.Actor.SendAsync(r.Pilot, MsgType.TradeAccept, Accept(i, 999, 1));
            }

            Assert.Equal(EconomyReject.RateLimited, LastResult(r.Pilot).Reason);
        }
    }

    [Fact]
    public async Task AfterTheAuthorityLeftAnAcceptIsRefusedAndNothingIsEscrowed()
    {
        var r = await StartAsync();
        await using (r.Actor)
        {
            await r.Actor.SendAsync(r.Boss, MsgType.TradeProposal, Proposal(2, r.Pilot.PlayerId, 800));
            var tradeId = LastResult(r.Boss).RefId!.Value.Lo;
            await r.Actor.DisconnectAsync(r.Boss, DisconnectCode.ClientQuit);
            await r.Actor.SendAsync(r.Pilot, MsgType.TradeAccept, Accept(3, tradeId, 1));

            Assert.Equal(EconomyReject.SessionNotRunning, LastResult(r.Pilot).Reason); // losing the authority stops the session
            Assert.Equal(5000, r.Module.Service!.Ledger.BalanceOf(WalletId.Player(r.Pilot.PlayerId)));
        }
    }
}
