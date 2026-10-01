using X4MP.Core.Economy;
using X4MP.Proto;
using TeamRelation = X4MP.Core.Teams.TeamRelation;

namespace X4MP.Core.Tests.Economy;

/// <summary>M1-E5: escrowed trades (proposal, counter, accept, escrow, locks, authority order, settle once, timeline, admin).</summary>
public sealed class EconomyTradeTests
{
    // ------------------------------------------------------------------ happy path

    [Fact]
    public void AShipForCreditsGoesThroughProposalAcceptEscrowOrderAndSettlement()
    {
        var kit = new TradeKit();

        var proposed = kit.Sell(1, 2, TradeKit.ShipOfOne, 2500);
        Assert.True(proposed.Ok, proposed.Detail);
        var trade = proposed.Trade!;
        Assert.Equal(TradeState.Proposed, trade.State);
        Assert.Equal(1u, trade.Version);
        Assert.Equal(1u, trade.InitiatorAccepted);
        Assert.Equal(trade.Id, kit.Service.LockHolder(TradeKit.ShipOfOne));
        Assert.Empty(kit.Orders); // nothing is escrowed or sent before both accepted
        Assert.Equal(0, kit.Escrow(trade.Id));

        var accepted = kit.Service.AcceptTrade(2, kit.Key(), trade.Id, 1, 0);
        Assert.True(accepted.Ok, accepted.Detail);
        Assert.Equal(TradeState.Transferring, trade.State);
        Assert.Equal(2500, kit.Escrow(trade.Id));
        Assert.Equal(10_000 - 2500, kit.Balance(2));
        var order = Assert.Single(kit.Orders);
        Assert.Equal((ulong)trade.Id, order.TradeId.Lo);
        var line = Assert.Single(order.Lines);
        Assert.Equal(AssetTransferKind.OwnerChange, line.Kind);
        Assert.Equal(TradeKit.ShipOfOne, line.Asset);
        Assert.Equal((ushort)1, line.ToTeam);
        Assert.Equal((ushort)2, line.ToPlayer);

        Assert.Equal(TradeConfirmOutcome.Settled, kit.Confirm(trade.Id));

        Assert.Equal(TradeState.Completed, trade.State);
        Assert.Equal(10_000 + 2500, kit.Balance(1));
        Assert.Equal(10_000 - 2500, kit.Balance(2));
        var change = Assert.Single(kit.World.OwnerChanges);
        Assert.Equal((TradeKit.ShipOfOne, 1, 2), (change.NetId, change.Team, change.Player));
        Assert.Equal((ulong)trade.Id, change.Cause.Lo);
        Assert.False(kit.Service.IsAssetLocked(TradeKit.ShipOfOne));
        kit.AssertSound();
    }

    [Fact]
    public void TheProposerMayBeThePayer()
    {
        var kit = new TradeKit();

        var proposed = kit.Buy(2, 1, TradeKit.ShipOfOne, 700);
        Assert.True(proposed.Ok, proposed.Detail);
        Assert.True(kit.Service.AcceptTrade(1, kit.Key(), proposed.Trade!.Id, 1, 0).Ok);

        Assert.Equal(700, kit.Escrow(proposed.Trade.Id)); // the proposer's credits are escrowed only now, on accept
        Assert.Equal(10_000 - 700, kit.Balance(2));
        kit.Confirm(proposed.Trade.Id);
        Assert.Equal(10_700, kit.Balance(1));
        kit.AssertSound();
    }

    [Fact]
    public void WaresMoveFromTheGiversShipIntoTheReceiversShip()
    {
        var kit = new TradeKit();
        kit.World.Cargo[(TradeKit.CargoShipOfOne, 7)] = 50;

        var proposed = kit.Service.ProposeTrade(
            1, kit.Key(), 2, [TradeItemModel.Ware(TradeKit.CargoShipOfOne, 7, 30)], [TradeItemModel.Credits(900)], 300, null);
        Assert.True(proposed.Ok, proposed.Detail);
        Assert.True(kit.Service.AcceptTrade(2, kit.Key(), proposed.Trade!.Id, 1, 901).Ok); // into player 2's own ship

        var line = Assert.Single(kit.Orders[0].Lines);
        Assert.Equal(AssetTransferKind.WareMove, line.Kind);
        Assert.Equal((TradeKit.CargoShipOfOne, 7u, 30L, 901u), (line.Asset, line.WareRef, line.Amount, line.DestAsset));
        kit.Confirm(proposed.Trade.Id);
        Assert.Empty(kit.World.OwnerChanges); // cargo does not change an owner
        Assert.Equal(10_900, kit.Balance(1));
        kit.AssertSound();
    }

    [Fact]
    public void WareAmountsAboveTheCargoAreRefused()
    {
        var kit = new TradeKit();
        kit.World.Cargo[(TradeKit.CargoShipOfOne, 7)] = 10;

        var result = kit.Service.ProposeTrade(1, kit.Key(), 2, [TradeItemModel.Ware(TradeKit.CargoShipOfOne, 7, 30)], [TradeItemModel.Credits(900)], 300, null);

        Assert.Equal(EconomyReject.AssetUnavailable, result.Reason);
    }

    // ------------------------------------------------------------------ versions

    [Fact]
    public void AnAcceptOnAStaleVersionIsRejectedAndTheCounterBumpsTheVersion()
    {
        var kit = new TradeKit();
        var trade = kit.Sell(1, 2, TradeKit.ShipOfOne, 2500).Trade!;

        // player 2 counters: 2000 instead of 2500 (own perspective: gives credits, wants the ship)
        var counter = kit.Service.CounterTrade(2, kit.Key(), trade.Id, 1, [TradeItemModel.Credits(2000)], [TradeItemModel.Ship(TradeKit.ShipOfOne)]);
        Assert.True(counter.Ok, counter.Detail);
        Assert.Equal(2u, trade.Version);
        Assert.Equal(TradeState.Countered, trade.State);
        Assert.Equal((0u, 2u), (trade.InitiatorAccepted, trade.CounterpartyAccepted));

        var stale = kit.Service.AcceptTrade(1, kit.Key(), trade.Id, 1, 0); // player 1 still has version 1 on screen
        Assert.Equal(EconomyReject.StaleVersion, stale.Reason);
        Assert.Equal(TradeState.Countered, trade.State);
        Assert.Equal(0, kit.Escrow(trade.Id));

        var staleCounter = kit.Service.CounterTrade(1, kit.Key(), trade.Id, 1, [TradeItemModel.Ship(TradeKit.ShipOfOne)], [TradeItemModel.Credits(2400)]);
        Assert.Equal(EconomyReject.StaleVersion, staleCounter.Reason);

        var good = kit.Service.AcceptTrade(1, kit.Key(), trade.Id, 2, 0);
        Assert.True(good.Ok, good.Detail);
        Assert.Equal(TradeState.Transferring, trade.State);
        Assert.Equal(2000, kit.Escrow(trade.Id));
        Assert.Single(Assert.Single(kit.Orders).Lines);
        kit.Confirm(trade.Id);
        kit.AssertSound();
    }

    [Fact]
    public void ACounterWithAnotherAssetMovesTheLockAndTheOldAssetIsFreeAgain()
    {
        var kit = new TradeKit();
        var trade = kit.Sell(1, 2, TradeKit.ShipOfOne, 100).Trade!;
        Assert.True(kit.Service.IsAssetLocked(TradeKit.ShipOfOne));

        var counter = kit.Service.CounterTrade(1, kit.Key(), trade.Id, 1, [TradeItemModel.Ship(TradeKit.CommonShip)], [TradeItemModel.Credits(100)]);

        Assert.True(counter.Ok, counter.Detail);
        Assert.False(kit.Service.IsAssetLocked(TradeKit.ShipOfOne));
        Assert.True(kit.Service.IsAssetLocked(TradeKit.CommonShip));
        Assert.True(kit.Sell(1, 2, TradeKit.ShipOfOne, 50).Ok);
    }

    [Fact]
    public void TheFirstAcceptOnlyRecordsTheAcceptanceAndTheLastOneExecutes()
    {
        var kit = new TradeKit();
        var trade = kit.Sell(1, 2, TradeKit.ShipOfOne, 100).Trade!;
        // player 2 counters, then player 1 accepts that version: the counterparty (who made the counter) is the one waiting.
        Assert.True(kit.Service.CounterTrade(2, kit.Key(), trade.Id, 1, [TradeItemModel.Credits(90)], [TradeItemModel.Ship(TradeKit.ShipOfOne)]).Ok);
        Assert.Equal(TradeState.Countered, trade.State);
        Assert.Empty(kit.Orders);

        Assert.True(kit.Service.AcceptTrade(1, kit.Key(), trade.Id, 2, 0).Ok);

        Assert.Equal(TradeState.Transferring, trade.State);
        Assert.Equal(90, kit.Escrow(trade.Id));
    }

    // ------------------------------------------------------------------ locks

    [Fact]
    public void AnAssetCannotBeInTwoOpenTrades()
    {
        var kit = new TradeKit();
        var first = kit.Sell(1, 2, TradeKit.ShipOfOne, 100);
        Assert.True(first.Ok);

        var second = kit.Sell(1, 3, TradeKit.ShipOfOne, 100);

        Assert.Equal(EconomyReject.AssetUnavailable, second.Reason);
        Assert.Contains($"open trade {first.Trade!.Id}", second.Detail);
        Assert.Single(kit.Service.Trades);
    }

    [Fact]
    public void TheSecondTradeOnALockedShipIsRefusedEvenAsACounter()
    {
        var kit = new TradeKit();
        Assert.True(kit.Sell(1, 2, TradeKit.ShipOfOne, 100).Ok);
        var other = kit.Sell(1, 2, TradeKit.CommonShip, 100).Trade!;

        var counter = kit.Service.CounterTrade(1, kit.Key(), other.Id, 1, [TradeItemModel.Ship(TradeKit.ShipOfOne)], [TradeItemModel.Credits(100)]);

        Assert.Equal(EconomyReject.AssetUnavailable, counter.Reason);
        Assert.True(kit.Service.IsAssetLocked(TradeKit.CommonShip)); // the counter did not change anything
        Assert.Equal(1u, other.Version);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("decline")]
    [InlineData("expire")]
    [InlineData("admin")]
    public void LocksAreReleasedWhenAnOfferEnds(string how)
    {
        var kit = new TradeKit();
        var trade = kit.Sell(1, 2, TradeKit.ShipOfOne, 100).Trade!;

        switch (how)
        {
            case "cancel":
                Assert.True(kit.Service.CancelTrade(1, kit.Key(), trade.Id).Ok);
                Assert.Equal(TradeState.Cancelled, trade.State);
                break;
            case "decline":
                Assert.True(kit.Service.CancelTrade(2, kit.Key(), trade.Id).Ok);
                Assert.Equal(TradeState.Rejected, trade.State);
                break;
            case "expire":
                kit.Kit.Time.Advance(TimeSpan.FromSeconds(301));
                kit.Service.TickTrades();
                Assert.Equal(TradeState.Expired, trade.State);
                break;
            default:
                Assert.True(kit.Service.AdminCancelTrade(trade.Id, "admin:root", "test").Ok);
                Assert.Equal(TradeState.Cancelled, trade.State);
                break;
        }

        Assert.False(kit.Service.IsAssetLocked(TradeKit.ShipOfOne));
        Assert.Empty(kit.Store.Locks);
        Assert.True(kit.Sell(1, 2, TradeKit.ShipOfOne, 100).Ok);
        kit.AssertSound();
    }

    // ------------------------------------------------------------------ validation

    [Fact]
    public void ProximityIsCheckedAtProposalAndAtAccept()
    {
        var kit = new TradeKit(o => o.TradeRequiresProximity = true);
        kit.World.PlayerShips[2] = (901, 9); // player 2 is in another sector

        var far = kit.Sell(1, 2, TradeKit.ShipOfOne, 100);
        Assert.Equal(EconomyReject.OutOfRange, far.Reason);
        Assert.False(kit.Service.IsAssetLocked(TradeKit.ShipOfOne));

        kit.World.PlayerShips[2] = (901, 5);
        var trade = kit.Sell(1, 2, TradeKit.ShipOfOne, 100).Trade!;
        kit.World.PlayerShips[2] = (901, 9); // flies away before accepting

        var accept = kit.Service.AcceptTrade(2, kit.Key(), trade.Id, 1, 0);

        Assert.Equal(EconomyReject.OutOfRange, accept.Reason);
        Assert.Equal(TradeState.Proposed, trade.State); // not fatal: come back and accept again
        Assert.Equal(0, kit.Escrow(trade.Id));
        Assert.Empty(kit.Orders);
        kit.World.PlayerShips[2] = (901, 5);
        Assert.True(kit.Service.AcceptTrade(2, kit.Key(), trade.Id, 1, 0).Ok);
    }

    [Fact]
    public void ProximityCanBeSwitchedOff()
    {
        var kit = new TradeKit(o => o.TradeRequiresProximity = false);
        kit.World.PlayerShips[2] = (901, 9);

        Assert.True(kit.Sell(1, 2, TradeKit.ShipOfOne, 100).Ok);
    }

    [Fact]
    public void AnAssetsPositionMustBeKnownForTheProximityRule()
    {
        var kit = new TradeKit();
        kit.World.Assets[TradeKit.ShipOfOne] = kit.World.Assets[TradeKit.ShipOfOne] with { Sector = 0 };

        Assert.Equal(EconomyReject.OutOfRange, kit.Sell(1, 2, TradeKit.ShipOfOne, 100).Reason);
    }

    [Theory]
    [InlineData(EconomyScope.Off, 2, EconomyReject.ScopeDisabled)]
    [InlineData(EconomyScope.Off, 3, EconomyReject.ScopeDisabled)]
    [InlineData(EconomyScope.Teammates, 2, EconomyReject.None)]
    [InlineData(EconomyScope.Teammates, 3, EconomyReject.ScopeDenied)]
    [InlineData(EconomyScope.Allied, 2, EconomyReject.None)]
    [InlineData(EconomyScope.Allied, 3, EconomyReject.None)]
    [InlineData(EconomyScope.Allied, 4, EconomyReject.ScopeDenied)]
    [InlineData(EconomyScope.Allied, 5, EconomyReject.ScopeDenied)]
    [InlineData(EconomyScope.Anyone, 4, EconomyReject.None)]
    [InlineData(EconomyScope.Anyone, 5, EconomyReject.None)]
    public void TradeScopeTimesRelation(EconomyScope scope, int counterparty, EconomyReject expected)
    {
        var kit = new TradeKit(o =>
        {
            o.TradeScope = scope;
            o.TradeRequiresProximity = false;
        });
        kit.World.AllowAssetTransfer = true;
        // the credits side proposes, so the seller owns nothing across teams: use a credits-for-ship buy from player 1's perspective
        var result = kit.Service.ProposeTrade(1, kit.Key(), counterparty, [TradeItemModel.Credits(100)], [TradeItemModel.Ship(CounterpartyShip(counterparty))], 300, null);

        Assert.Equal(expected, result.Reason);
    }

    private static uint CounterpartyShip(int player) => player switch
    {
        2 => TradeKit.ShipOfTwo,
        3 => TradeKit.ForeignShip,
        4 => TradeKit.ShipOfThree,
        _ => TradeKit.ShipOfFour,
    };

    [Fact]
    public void AScopeThatTightensBeforeTheAcceptRejectsTheTradeAndFreesTheAsset()
    {
        var kit = new TradeKit(o =>
        {
            o.TradeScope = EconomyScope.Allied;
            o.TradeRequiresProximity = false;
        });
        kit.World.AllowAssetTransfer = true;
        var trade = kit.Sell(1, 3, TradeKit.ShipOfOne, 100).Trade!; // team 2 is allied

        kit.Kit.Teams.SetRelation(1, 2, TeamRelation.Hostile);
        var accept = kit.Service.AcceptTrade(3, kit.Key(), trade.Id, 1, 0);

        Assert.Equal(EconomyReject.ScopeDenied, accept.Reason);
        Assert.Equal(TradeState.Rejected, trade.State);
        Assert.False(kit.Service.IsAssetLocked(TradeKit.ShipOfOne));
        Assert.Equal(0, kit.Escrow(trade.Id));
        kit.AssertSound();
    }

    [Fact]
    public void TheTickCancelsNegotiatingTradesWhoseScopeNoLongerHolds()
    {
        var kit = new TradeKit(o => o.TradeRequiresProximity = false);
        kit.World.AllowAssetTransfer = true;
        var trade = kit.Sell(1, 3, TradeKit.ShipOfOne, 100).Trade!;

        kit.Kit.Options.TradeScope = EconomyScope.Teammates;
        kit.Service.TickTrades();

        Assert.Equal(TradeState.Cancelled, trade.State);
        Assert.Equal(EconomyReject.ScopeDenied, trade.Reason);
        Assert.False(kit.Service.IsAssetLocked(TradeKit.ShipOfOne));
    }

    [Fact]
    public void OwnershipRulesComeFromTheWorld()
    {
        var kit = new TradeKit(o => o.TradeRequiresProximity = false);

        Assert.Equal(EconomyReject.AssetNotOwned, kit.Sell(1, 2, TradeKit.ForeignShip, 100).Reason); // team 2's ship
        Assert.Equal(EconomyReject.AssetUnavailable, kit.Sell(1, 2, 9999, 100).Reason);              // unknown entity
        Assert.Equal(EconomyReject.AssetUnavailable, kit.Sell(1, 2, 900, 100).Reason);               // the ship a player is flying
        Assert.Equal(EconomyReject.AssetNotOwned, kit.Sell(1, 3, TradeKit.ShipOfOne, 100).Reason);   // across teams needs allow_asset_transfer
        kit.World.AllowAssetTransfer = true;
        Assert.True(kit.Sell(1, 3, TradeKit.ShipOfOne, 100).Ok);
    }

    [Fact]
    public void ShipTradesCanBeDisabledAndStationsAreNotSupported()
    {
        var kit = new TradeKit(o => o.TradeShipsEnabled = false);
        Assert.Equal(EconomyReject.ScopeDisabled, kit.Sell(1, 2, TradeKit.ShipOfOne, 100).Reason);

        var stations = new TradeKit();
        var station = stations.Service.ProposeTrade(1, "s", 2, [new TradeItemModel(TradeItemKind.Station, 0, 0, 5)], [TradeItemModel.Credits(5)], 300, null);
        Assert.Equal(EconomyReject.AssetUnavailable, station.Reason);
    }

    [Fact]
    public void ShapeRulesNeedExactlyOneCreditsItemAndAnAsset()
    {
        var kit = new TradeKit();
        var ship = TradeItemModel.Ship(TradeKit.ShipOfOne);

        Assert.Equal(EconomyReject.AmountInvalid, kit.Service.ProposeTrade(1, "a", 2, [ship], [], 300, null).Reason);                                  // no credits
        Assert.Equal(EconomyReject.AmountInvalid, kit.Service.ProposeTrade(1, "b", 2, [ship, TradeItemModel.Credits(5)], [TradeItemModel.Credits(5)], 300, null).Reason); // two credits
        Assert.Equal(EconomyReject.AmountInvalid, kit.Service.ProposeTrade(1, "c", 2, [TradeItemModel.Credits(5)], [], 300, null).Reason);            // no asset
        Assert.Equal(EconomyReject.AmountInvalid, kit.Service.ProposeTrade(1, "d", 2, [ship], [TradeItemModel.Credits(0)], 300, null).Reason);       // zero credits
        Assert.Equal(EconomyReject.OverMaxAmount, kit.Service.ProposeTrade(1, "e", 2, [ship], [TradeItemModel.Credits(2_000_000_000_000)], 300, null).Reason);
        Assert.Equal(EconomyReject.NotParty, kit.Service.ProposeTrade(1, "f", 1, [ship], [TradeItemModel.Credits(5)], 300, null).Reason);               // with yourself
        Assert.Equal(EconomyReject.UnknownPlayer, kit.Service.ProposeTrade(1, "g", 77, [ship], [TradeItemModel.Credits(5)], 300, null).Reason);
        Assert.Equal(EconomyReject.AmountInvalid, kit.Service.ProposeTrade(1, "h", 2, [TradeItemModel.Ware(TradeKit.CargoShipOfOne, 0, 5)], [TradeItemModel.Credits(5)], 300, null).Reason);
    }

    [Fact]
    public void TheNumberOfOpenTradesPerPlayerIsLimited()
    {
        var kit = new TradeKit(o => o.MaxOpenTradesPerPlayer = 2);
        Assert.True(kit.Sell(1, 2, TradeKit.ShipOfOne, 1).Ok);
        Assert.True(kit.Sell(1, 2, TradeKit.CommonShip, 1).Ok);

        var third = kit.Sell(1, 2, TradeKit.CargoShipOfOne, 1);

        Assert.Equal(EconomyReject.TooManyOpen, third.Reason);
        // the counterparty's count counts too
        Assert.Equal(EconomyReject.TooManyOpen, kit.Service.ProposeTrade(2, "x", 1, [TradeItemModel.Ship(TradeKit.ShipOfTwo)], [TradeItemModel.Credits(1)], 300, null).Reason);
    }

    [Fact]
    public void SameWalletInSharedModeIsRejected()
    {
        var kit = new TradeKit(o => o.CreditMode = CreditMode.Shared);

        Assert.Equal(EconomyReject.SameWallet, kit.Sell(1, 2, TradeKit.ShipOfOne, 100).Reason);
    }

    [Fact]
    public void TradesNeedFundsAndAWorkingEconomy()
    {
        var kit = new TradeKit();
        var proposed = kit.Sell(1, 2, TradeKit.ShipOfOne, 99_999).Trade!; // player 2 has 10,000

        var poor = kit.Service.AcceptTrade(2, kit.Key(), proposed.Id, 1, 0);

        Assert.Equal(EconomyReject.InsufficientFunds, poor.Reason);
        Assert.Equal(TradeState.Proposed, proposed.State);
        Assert.Equal(10_000, kit.Balance(2));
        Assert.Equal(0, kit.Escrow(proposed.Id));

        kit.Kit.Ledger.Freeze("test");
        Assert.Equal(EconomyReject.EconomyFrozen, kit.Sell(1, 2, TradeKit.CommonShip, 1).Reason);
    }

    [Fact]
    public void WithoutAnAuthorityTheAcceptIsRefusedBeforeAnyCreditsMove()
    {
        var kit = new TradeKit();
        var trade = kit.Sell(1, 2, TradeKit.ShipOfOne, 500).Trade!;
        kit.AuthorityUp = false;

        var accept = kit.Service.AcceptTrade(2, kit.Key(), trade.Id, 1, 0);

        Assert.Equal(EconomyReject.AuthorityUnavailable, accept.Reason);
        Assert.Equal(TradeState.Proposed, trade.State);
        Assert.Equal(10_000, kit.Balance(2));
        kit.AssertSound();
    }

    [Fact]
    public void AnOrderThatCannotBeSentRollsTheAcceptBack()
    {
        var kit = new TradeKit();
        var trade = kit.Sell(1, 2, TradeKit.ShipOfOne, 500).Trade!;
        kit.Service.SendTransferOrder = _ => false; // the authority dropped between the check and the send

        var accept = kit.Service.AcceptTrade(2, kit.Key(), trade.Id, 1, 0);

        Assert.Equal(EconomyReject.AuthorityUnavailable, accept.Reason);
        Assert.Equal(TradeState.RolledBack, trade.State);
        Assert.Equal(10_000, kit.Balance(2));
        Assert.False(kit.Service.IsAssetLocked(TradeKit.ShipOfOne));
        kit.AssertSound();
    }

    [Fact]
    public void OnlyThePartiesMayActAndNothingActsOnAnUnknownOrFinishedTrade()
    {
        var kit = new TradeKit();
        var trade = kit.Sell(1, 2, TradeKit.ShipOfOne, 100).Trade!;

        Assert.Equal(EconomyReject.NotParty, kit.Service.AcceptTrade(3, kit.Key(), trade.Id, 1, 0).Reason);
        Assert.Equal(EconomyReject.NotParty, kit.Service.CancelTrade(3, kit.Key(), trade.Id).Reason);
        Assert.Equal(EconomyReject.UnknownTrade, kit.Service.AcceptTrade(2, kit.Key(), 999, 1, 0).Reason);

        Assert.True(kit.Service.CancelTrade(1, kit.Key(), trade.Id).Ok);
        Assert.Equal(EconomyReject.WrongState, kit.Service.AcceptTrade(2, kit.Key(), trade.Id, 1, 0).Reason);
        Assert.Equal(EconomyReject.WrongState, kit.Service.CounterTrade(2, kit.Key(), trade.Id, 1, [TradeItemModel.Credits(5)], [TradeItemModel.Ship(TradeKit.ShipOfOne)]).Reason);
    }

    // ------------------------------------------------------------------ idempotency

    [Fact]
    public void AReplayedRequestKeyReturnsTheSameTradeAndDoesNothingElse()
    {
        var kit = new TradeKit();
        var first = kit.Sell(1, 2, TradeKit.ShipOfOne, 100, "same");
        var replay = kit.Sell(1, 2, TradeKit.ShipOfOne, 100, "same");

        Assert.True(replay.Ok);
        Assert.Same(first.Trade, replay.Trade);
        Assert.Single(kit.Service.Trades);

        var reuse = kit.Sell(1, 2, TradeKit.CommonShip, 100, "same");
        Assert.Equal(EconomyReject.RequestIdReuse, reuse.Reason);

        var accept = kit.Service.AcceptTrade(2, "acc", first.Trade!.Id, 1, 0);
        var acceptReplay = kit.Service.AcceptTrade(2, "acc", first.Trade.Id, 1, 0);
        Assert.True(accept.Ok && acceptReplay.Ok);
        Assert.Equal(100, kit.Escrow(first.Trade.Id));
        Assert.Single(kit.Orders);
        Assert.Equal(10_000 - 100, kit.Balance(2));
    }

    [Fact]
    public void AnAcceptKeyAlreadyUsedForAnotherRequestIsAReuse()
    {
        var kit = new TradeKit();
        var trade = kit.Sell(1, 2, TradeKit.ShipOfOne, 100).Trade!;
        Assert.True(kit.Service.Donate(2, "shared-key", 1, 5).Ok);

        var accept = kit.Service.AcceptTrade(2, "shared-key", trade.Id, 1, 0);

        Assert.Equal(TradeState.Proposed, trade.State);
        Assert.False(accept.Ok);
        Assert.Equal(EconomyReject.RequestIdReuse, accept.Reason);
        Assert.Equal(0, kit.Escrow(trade.Id));
    }

    // ------------------------------------------------------------------ settle once, rollback

    [Fact]
    public void ADuplicateConfirmSettlesNothingTwice()
    {
        var kit = new TradeKit();
        var trade = kit.Run(1, 2, TradeKit.ShipOfOne, 800);

        Assert.Equal(TradeConfirmOutcome.Settled, kit.Confirm(trade.Id));
        var balance = kit.Balance(1);
        var txCount = kit.Kit.Store.Transactions.Count;

        Assert.Equal(TradeConfirmOutcome.Ignored, kit.Confirm(trade.Id));
        Assert.Equal(TradeConfirmOutcome.Ignored, kit.Confirm(trade.Id, ok: false, compensated: true));

        Assert.Equal(balance, kit.Balance(1));
        Assert.Equal(txCount, kit.Kit.Store.Transactions.Count);
        Assert.Single(kit.World.OwnerChanges);
        Assert.Equal(TradeState.Completed, trade.State);
        Assert.Equal(2, kit.Service.DuplicateConfirms);
        kit.AssertSound();
    }

    [Fact]
    public void AConfirmForAnUnknownOrNotYetTransferringTradeDoesNothing()
    {
        var kit = new TradeKit();
        var proposed = kit.Sell(1, 2, TradeKit.ShipOfOne, 100).Trade!;

        Assert.Equal(TradeConfirmOutcome.UnknownTrade, kit.Confirm(4242));
        Assert.Equal(TradeConfirmOutcome.Ignored, kit.Confirm(proposed.Id)); // nothing was ordered yet
        Assert.Equal(TradeState.Proposed, proposed.State);
        Assert.Equal(1, kit.Service.UnknownConfirms);
        Assert.Empty(kit.World.OwnerChanges);
    }

    [Fact]
    public void AFailedTransferRefundsThePayerAndUnlocks()
    {
        var kit = new TradeKit();
        var trade = kit.Run(1, 2, TradeKit.ShipOfOne, 800);
        Assert.Equal(9_200, kit.Balance(2));

        Assert.Equal(TradeConfirmOutcome.RolledBack, kit.Confirm(trade.Id, ok: false, failedLine: 0, compensated: true, error: "ship is docked"));

        Assert.Equal(TradeState.RolledBack, trade.State);
        Assert.Equal(EconomyReject.AuthorityRejected, trade.Reason);
        Assert.Contains("docked", trade.Detail);
        Assert.Equal(10_000, kit.Balance(2));
        Assert.Equal(10_000, kit.Balance(1));
        Assert.False(kit.Service.IsAssetLocked(TradeKit.ShipOfOne));
        Assert.Empty(kit.World.OwnerChanges);
        Assert.Equal(TradeConfirmOutcome.Ignored, kit.Confirm(trade.Id)); // a late ok after the rollback is ignored
        kit.AssertSound();
        Assert.True(kit.Sell(1, 2, TradeKit.ShipOfOne, 1).Ok); // the ship can be traded again
    }

    [Fact]
    public void APartiallyAppliedTransferIsInDoubtNotRefunded()
    {
        var kit = new TradeKit();
        var trade = kit.Run(1, 2, TradeKit.ShipOfOne, 800);

        Assert.Equal(TradeConfirmOutcome.InDoubt, kit.Confirm(trade.Id, ok: false, failedLine: 2, compensated: false, error: "boom"));

        Assert.Equal(TradeState.InDoubt, trade.State);
        Assert.Equal(800, kit.Escrow(trade.Id));
        Assert.True(kit.Service.IsAssetLocked(TradeKit.ShipOfOne));
    }

    [Fact]
    public void AnUnknownAnswerToTheQueryMeansTheOrderNeverArrivedAndRollsBack()
    {
        var kit = new TradeKit();
        var trade = kit.Run(1, 2, TradeKit.ShipOfOne, 800);

        Assert.Equal(TradeConfirmOutcome.RolledBack, kit.Confirm(trade.Id, ok: false, failedLine: -1, error: "Unknown"));

        Assert.Equal(10_000, kit.Balance(2));
        Assert.Equal(TradeState.RolledBack, trade.State);
    }

    // ------------------------------------------------------------------ the 30 s / x3 timeline (fake clock)

    [Fact]
    public void ThirtySecondsWithoutAConfirmStartsThreeQueriesThenTheTradeIsInDoubt()
    {
        var kit = new TradeKit();
        var trade = kit.Run(1, 2, TradeKit.ShipOfOne, 800);

        kit.Kit.Time.Advance(TimeSpan.FromSeconds(29));
        kit.Service.TickTrades();
        Assert.Empty(kit.Queries);
        Assert.Equal(TradeState.Transferring, trade.State);

        kit.Kit.Time.Advance(TimeSpan.FromSeconds(1)); // t = 30
        kit.Service.TickTrades();
        Assert.Equal([trade.Id], kit.Queries);

        kit.Kit.Time.Advance(TimeSpan.FromSeconds(9));
        kit.Service.TickTrades();
        Assert.Single(kit.Queries); // the next one is due 10 s after the first

        kit.Kit.Time.Advance(TimeSpan.FromSeconds(1)); // t = 40
        kit.Service.TickTrades();
        Assert.Equal(2, kit.Queries.Count);

        kit.Kit.Time.Advance(TimeSpan.FromSeconds(10)); // t = 50
        kit.Service.TickTrades();
        Assert.Equal(3, kit.Queries.Count);
        Assert.Equal(TradeState.Transferring, trade.State);

        kit.Kit.Time.Advance(TimeSpan.FromSeconds(10)); // t = 60: three queries unanswered
        kit.Service.TickTrades();
        Assert.Equal(3, kit.Queries.Count);
        Assert.Equal(TradeState.InDoubt, trade.State);
        Assert.Equal(800, kit.Escrow(trade.Id));
        Assert.True(kit.Service.IsAssetLocked(TradeKit.ShipOfOne));
        Assert.Contains(kit.Kit.Events.All, e => e is X4MP.Core.Events.AlertRaised { Code: var code } && code.StartsWith("economy_trade_in_doubt", StringComparison.Ordinal));
        kit.AssertSound();
    }

    [Fact]
    public void AnAnswerToAQueryEndsTheTimelineEitherWay()
    {
        var kit = new TradeKit();
        var settled = kit.Run(1, 2, TradeKit.ShipOfOne, 800);
        var failed = kit.Run(1, 2, TradeKit.CommonShip, 300);

        kit.Kit.Time.Advance(TimeSpan.FromSeconds(30));
        kit.Service.TickTrades();
        Assert.Equal([settled.Id, failed.Id], kit.Queries);

        kit.Confirm(settled.Id); // the answer to the query is an ordinary confirm
        kit.Confirm(failed.Id, ok: false, failedLine: 0, compensated: true, error: "no");
        kit.Kit.Time.Advance(TimeSpan.FromSeconds(120));
        kit.Service.TickTrades();

        Assert.Equal(TradeState.Completed, settled.State);
        Assert.Equal(TradeState.RolledBack, failed.State);
        Assert.Equal(2, kit.Queries.Count); // no further queries
        kit.AssertSound();
    }

    [Fact]
    public void ALateConfirmSettlesAnInDoubtTrade()
    {
        var kit = new TradeKit();
        var trade = kit.Run(1, 2, TradeKit.ShipOfOne, 800);
        for (var i = 0; i < 8; i++)
        {
            kit.Kit.Time.Advance(TimeSpan.FromSeconds(10));
            kit.Service.TickTrades();
        }

        Assert.Equal(TradeState.InDoubt, trade.State);

        Assert.Equal(TradeConfirmOutcome.Settled, kit.Confirm(trade.Id));

        Assert.Equal(TradeState.Completed, trade.State);
        Assert.Equal(10_800, kit.Balance(1));
        kit.AssertSound();
    }

    [Fact]
    public void LosingTheAuthorityParksTransferringTradesAndItsReturnAsksAgain()
    {
        var kit = new TradeKit();
        var trade = kit.Run(1, 2, TradeKit.ShipOfOne, 800);

        kit.Service.OnAuthorityGone();
        Assert.Equal(TradeState.InDoubt, trade.State);
        Assert.Equal(EconomyReject.AuthorityUnavailable, trade.Reason);

        kit.Service.OnAuthorityAttached();
        Assert.Equal([trade.Id], kit.Queries);
        kit.Confirm(trade.Id, ok: false, failedLine: -1, error: "Unknown");
        Assert.Equal(TradeState.RolledBack, trade.State);
        Assert.Equal(10_000, kit.Balance(2));
        kit.AssertSound();
    }

    // ------------------------------------------------------------------ admin

    [Fact]
    public void AdminResolveCompleteSettlesAnInDoubtTradeAndRefundDoesTheOtherSide()
    {
        var kit = new TradeKit();
        var complete = kit.Run(1, 2, TradeKit.ShipOfOne, 800);
        var refund = kit.Run(1, 2, TradeKit.CommonShip, 300);
        kit.Service.OnAuthorityGone();
        Assert.All([complete, refund], t => Assert.Equal(TradeState.InDoubt, t.State));

        Assert.True(kit.Service.AdminResolveTrade(complete.Id, complete: true, "admin:root", "the ship changed hands").Ok);
        Assert.True(kit.Service.AdminResolveTrade(refund.Id, complete: false, "admin:root").Ok);

        Assert.Equal(TradeState.Completed, complete.State);
        Assert.Equal(TradeState.RolledBack, refund.State);
        Assert.Equal("admin:root", complete.ResolvedBy);
        Assert.Equal(10_000 + 800, kit.Balance(1));
        Assert.Equal(10_000 - 800, kit.Balance(2));
        Assert.Single(kit.World.OwnerChanges); // only the completed ship's owner changed
        Assert.Equal(TradeKit.ShipOfOne, kit.World.OwnerChanges[0].NetId);
        Assert.False(kit.Service.IsAssetLocked(TradeKit.ShipOfOne));
        Assert.False(kit.Service.IsAssetLocked(TradeKit.CommonShip));
        Assert.Contains(kit.Kit.Events.All, e => e is X4MP.Core.Events.AdminActionTaken { Action: "economy.trade.resolve_complete" });
        Assert.Contains(kit.Kit.Events.All, e => e is X4MP.Core.Events.AlertCleared);
        kit.AssertSound();

        // a second resolve, and a resolve of a trade that is not in doubt, are refused
        Assert.Equal(EconomyReject.WrongState, kit.Service.AdminResolveTrade(complete.Id, complete: false, "admin:root").Reason);
        Assert.Equal(EconomyReject.UnknownTrade, kit.Service.AdminResolveTrade(77, complete: true, "admin:root").Reason);
        Assert.Equal(10_800, kit.Balance(1));
    }

    [Fact]
    public void AdminCancelOnlyWorksBeforeTheTransferStarted()
    {
        var kit = new TradeKit();
        var open = kit.Sell(1, 2, TradeKit.ShipOfOne, 100).Trade!;
        var transferring = kit.Run(1, 2, TradeKit.CommonShip, 100);

        Assert.True(kit.Service.AdminCancelTrade(open.Id, "admin:root", "spam").Ok);
        var refused = kit.Service.AdminCancelTrade(transferring.Id, "admin:root");

        Assert.Equal(EconomyReject.WrongState, refused.Reason);
        Assert.Contains("resolve", refused.Detail);
        Assert.Equal(TradeState.Cancelled, open.State);
        Assert.Equal(TradeState.Transferring, transferring.State);
        Assert.Equal(EconomyReject.WrongState, kit.Service.AdminCancelTrade(open.Id, "admin:root").Reason);
    }

    [Fact]
    public void AdminResolveWhileTheLedgerIsFrozenLeavesTheTradeInDoubt()
    {
        var kit = new TradeKit();
        var trade = kit.Run(1, 2, TradeKit.ShipOfOne, 800);
        kit.Service.OnAuthorityGone();
        kit.Kit.Ledger.Freeze("audit");

        var result = kit.Service.AdminResolveTrade(trade.Id, complete: false, "admin:root");

        Assert.False(result.Ok);
        Assert.Equal(TradeState.InDoubt, trade.State);
        Assert.Equal(800, kit.Escrow(trade.Id));
        kit.Kit.Ledger.Unfreeze("admin:root");
        Assert.True(kit.Service.AdminResolveTrade(trade.Id, complete: false, "admin:root").Ok);
        Assert.Equal(10_000, kit.Balance(2));
    }

    // ------------------------------------------------------------------ persistence and recovery

    [Fact]
    public void TradesLocksAndRequestKeysSurviveARestart()
    {
        var kit = new TradeKit();
        var open = kit.Sell(1, 2, TradeKit.ShipOfOne, 100, "keep").Trade!;
        var transferring = kit.Run(1, 2, TradeKit.CommonShip, 300);

        var fresh = new EconomyService(kit.Kit.Ledger, kit.Kit.Store, kit.Kit.Teams, () => kit.Kit.Options, kit.Kit.Time, kit.Kit.Events, () => kit.Kit.Phase)
        {
            TradeStore = kit.Store,
        };
        fresh.LoadTrades();

        Assert.Equal(TradeState.Proposed, fresh.FindTrade(open.Id)!.State);
        Assert.Equal(TradeState.Transferring, fresh.FindTrade(transferring.Id)!.State);
        Assert.True(fresh.IsAssetLocked(TradeKit.ShipOfOne));
        Assert.True(fresh.IsAssetLocked(TradeKit.CommonShip));
        Assert.Equal(open.Id, fresh.LockHolder(TradeKit.ShipOfOne));
        Assert.Equal(open.Items(), fresh.FindTrade(open.Id)!.Items());
        fresh.TradeWorld = kit.World;
        Assert.Same(fresh.FindTrade(open.Id), fresh.ProposeTrade(1, "keep", 2, [TradeItemModel.Ship(TradeKit.ShipOfOne)], [TradeItemModel.Credits(100)], 300, "t").Trade); // replay by key
        var next = fresh.ProposeTrade(1, "new", 2, [TradeItemModel.Ship(TradeKit.CargoShipOfOne)], [TradeItemModel.Credits(1)], 300, null);
        Assert.True(next.Ok, next.Detail);
        Assert.True(next.Trade!.Id > transferring.Id); // ids continue
    }

    [Fact]
    public void ACrashBetweenTheEscrowPostingAndTheTradeSaveIsRefundedOnStart()
    {
        var kit = new TradeKit();
        var trade = kit.Sell(1, 2, TradeKit.ShipOfOne, 600).Trade!;
        // the escrow posting is durable, the trade save (state Transferring) never happened
        Assert.True(kit.Kit.Ledger.Post(new PostRequest
        {
            Kind = TxKind.TradeEscrow,
            Actor = "player:2",
            Entries = [new(WalletId.Player(2), -600), new(TradeRecord.EscrowOf(trade.Id), 600)],
        }).Ok);

        var fresh = new EconomyService(kit.Kit.Ledger, kit.Kit.Store, kit.Kit.Teams, () => kit.Kit.Options, kit.Kit.Time, kit.Kit.Events, () => kit.Kit.Phase)
        {
            TradeStore = kit.Store,
        };
        fresh.LoadTrades();

        Assert.Equal(TradeState.RolledBack, fresh.FindTrade(trade.Id)!.State);
        Assert.Equal(10_000, kit.Balance(2));
        Assert.Equal(0, kit.Escrow(trade.Id));
        Assert.False(fresh.IsAssetLocked(TradeKit.ShipOfOne));
        Assert.Empty(kit.Store.Locks);
    }

    [Fact]
    public void AFailingStoreRefusesAProposalWithoutLeavingALock()
    {
        var kit = new TradeKit();
        kit.Store.FailSaves = true;

        var result = kit.Sell(1, 2, TradeKit.ShipOfOne, 100);

        Assert.Equal(EconomyReject.Timeout, result.Reason);
        Assert.False(kit.Service.IsAssetLocked(TradeKit.ShipOfOne));
        Assert.Empty(kit.Service.Trades);
        kit.Store.FailSaves = false;
        Assert.True(kit.Sell(1, 2, TradeKit.ShipOfOne, 100).Ok);
    }

    [Fact]
    public void TheStoreRefusesASecondOpenTradeOnTheSameAsset()
    {
        var store = new InMemoryTradeStore();
        var a = new TradeRecord { Id = 1, InitiatorGives = [TradeItemModel.Ship(5)] };
        var b = new TradeRecord { Id = 2, InitiatorGives = [TradeItemModel.Ship(5)] };
        store.Save(7, a, [5u]);

        var conflict = Assert.Throws<TradeLockConflictException>(() => store.Save(7, b, [5u]));

        Assert.Equal((5u, 1L), (conflict.Asset, conflict.Holder));
        store.Save(7, a, []); // released
        store.Save(7, b, [5u]);
        Assert.Equal(2, store.Locks[(7, 5u)]);
    }

    [Fact]
    public void TheAuditorFlagsEscrowThatDoesNotMatchTheTradeState()
    {
        var kit = new TradeKit();
        var trade = kit.Sell(1, 2, TradeKit.ShipOfOne, 100).Trade!;
        Assert.True(kit.Kit.Auditor.RunNow().Ok);

        Assert.True(kit.Kit.Ledger.Post(new PostRequest
        {
            Kind = TxKind.AdminAdjust,
            Actor = "admin:root",
            Entries = [new(WalletId.Player(1), -5), new(TradeRecord.EscrowOf(trade.Id), 5)],
        }).Ok);

        var report = kit.Kit.Auditor.RunNow();
        Assert.False(report.Ok);
        Assert.Contains(report.Violations, v => v.Contains($"trade {trade.Id}", StringComparison.Ordinal));
        Assert.True(kit.Kit.Ledger.IsFrozen);
    }

    [Fact]
    public void EveryChangeIsReportedWithItsPreviousState()
    {
        var kit = new TradeKit();
        var trade = kit.Run(1, 2, TradeKit.ShipOfOne, 100);
        kit.Confirm(trade.Id);

        Assert.Equal(
            [(trade.Id, TradeState.Proposed, TradeState.Proposed), (trade.Id, TradeState.Proposed, TradeState.Transferring), (trade.Id, TradeState.Transferring, TradeState.Completed)],
            kit.Changes);
        Assert.Contains(kit.Kit.Events.All, e => e is TradeStateChanged { To: TradeState.Completed });
    }

    // ------------------------------------------------------------------ the conservation property

    [Fact]
    public void ManyTradesWithEveryOutcomeKeepTheLedgerAtZeroAndTheLocksEmpty()
    {
        var kit = new TradeKit(o =>
        {
            o.TradeRequiresProximity = false;
            o.MaxOpenTradesPerPlayer = 50;
        });
        var ships = new[] { TradeKit.ShipOfOne, TradeKit.CommonShip, TradeKit.CargoShipOfOne };
        var random = new Random(5);
        var open = new List<TradeRecord>();
        for (var i = 0; i < 300; i++)
        {
            var ship = ships[random.Next(ships.Length)];
            var seller = random.Next(2) + 1;
            var buyer = 3 - seller;
            if (kit.Service.IsAssetLocked(ship) || kit.World.Assets[ship].OwnerTeam != 1)
            {
                continue;
            }

            var proposed = kit.Sell(seller, buyer, ship, random.Next(1, 400));
            if (!proposed.Ok)
            {
                continue;
            }

            var trade = proposed.Trade!;
            switch (random.Next(6))
            {
                case 0:
                    kit.Service.CancelTrade(seller, kit.Key(), trade.Id);
                    break;
                case 1:
                    kit.Service.CancelTrade(buyer, kit.Key(), trade.Id);
                    break;
                default:
                    if (kit.Service.AcceptTrade(buyer, kit.Key(), trade.Id, trade.Version, 0).Ok)
                    {
                        open.Add(trade);
                    }

                    break;
            }

            if (random.Next(3) == 0 && open.Count > 0)
            {
                var pick = open[random.Next(open.Count)];
                open.Remove(pick);
                switch (random.Next(4))
                {
                    case 0:
                        kit.Confirm(pick.Id);
                        break;
                    case 1:
                        kit.Confirm(pick.Id, ok: false, failedLine: 0, compensated: true);
                        break;
                    case 2:
                        kit.Confirm(pick.Id);
                        kit.Confirm(pick.Id); // duplicate
                        break;
                    default:
                        kit.Service.OnAuthorityGone();
                        kit.Service.AdminResolveTrade(pick.Id, random.Next(2) == 0, "admin:root");
                        break;
                }
            }

            Assert.Equal(0, kit.Kit.Ledger.TotalBalance());
        }

        foreach (var trade in kit.Service.Trades.Where(t => t.State == TradeState.Transferring).ToList())
        {
            kit.Confirm(trade.Id, ok: false, failedLine: 0, compensated: true);
        }

        foreach (var trade in kit.Service.Trades.Where(t => t.State == TradeState.InDoubt).ToList())
        {
            kit.Service.AdminResolveTrade(trade.Id, complete: false, "admin:root");
        }

        foreach (var trade in kit.Service.Trades.Where(t => t.IsNegotiating).ToList())
        {
            kit.Service.AdminCancelTrade(trade.Id, "admin:root");
        }

        Assert.DoesNotContain(kit.Service.Trades, t => t.IsOpen);
        Assert.Empty(kit.Store.Locks);
        kit.AssertSound();
        Assert.Equal(20_000, kit.Balance(1) + kit.Balance(2));
    }
}

internal static class TradeRecordTestExtensions
{
    /// <summary>The items of both sides as comparable text.</summary>
    public static string Items(this TradeRecord trade) =>
        string.Join(',', trade.InitiatorGives.Concat(trade.CounterpartyGives).Select(i => $"{i.Kind}:{i.Amount}:{i.WareRef}:{i.Asset}"));
}
