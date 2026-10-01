using Google.FlatBuffers;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode.Tests;

/// <summary>M1-E5: <c>--trade</c>, <c>--trade-fail</c> and <c>--trade-timeout</c>: the CLI, the fake authority's executor and the trading client.</summary>
public sealed class TradeTests
{
    [Fact]
    public void TradeOptionsParse()
    {
        var o = CliParser.Parse(["swarm", "--clients", "3", "--with-authority", "--trade", "--trade-fail", "10%", "--trade-timeout=5"]).Options!;

        Assert.True(o.Trade);
        Assert.Equal(10, o.TradeFailPercent);
        Assert.Equal(5, o.TradeTimeoutPercent);
        var plain = CliParser.Parse(["swarm"]).Options!;
        Assert.False(plain.Trade);
        Assert.Equal((0, 0), (plain.TradeFailPercent, plain.TradeTimeoutPercent));
    }

    [Theory]
    [InlineData("--trade-fail", "101")]
    [InlineData("--trade-fail", "-1")]
    [InlineData("--trade-timeout", "many")]
    public void TradePercentagesAreValidated(string key, string value) =>
        Assert.False(CliParser.Parse(["authority", key, value]).Ok);

    private static AssetTransferOrderT Order(ulong id, params (uint Ship, ushort Team, ushort Player)[] ships) => new()
    {
        TradeId = new Id128T { Lo = id },
        Lines = [.. ships.Select(s => new AssetTransferLineT { Kind = AssetTransferKind.OwnerChange, Asset = s.Ship, ToTeam = s.Team, ToPlayer = s.Player })],
    };

    private static Frame FrameOf(OutMessage message) =>
        new(message.Type, FrameOptions.None, MessageRegistry.Default.GetDescriptor(message.Type).Lane, message.Payload);

    private static AssetTransferConfirmT AsConfirm(OutMessage message) => MessageRegistry.Default.Decode<AssetTransferConfirm>(FrameOf(message)).UnPack();

    [Fact]
    public void WithoutInjectionEveryOrderIsAppliedAndConfirmedOnceWithTheOwnerChange()
    {
        var authority = new FakeTradeAuthority(42, 0, 0);

        var replies = authority.OnOrder(Order(1, (100, 1, 2)));

        Assert.Equal([MsgType.EntityChange, MsgType.AssetTransferConfirm], replies.Select(r => r.Type));
        var change = MessageRegistry.Default.Decode<EntityChange>(FrameOf(replies[0])).UnPack();
        Assert.Equal((100u, (ushort)1, (ushort)2), (change.NetId, change.OwnerTeam, change.OwnerPlayer));
        Assert.Equal(1ul, change.CauseTradeId.Lo);
        var confirm = AsConfirm(replies[1]);
        Assert.True(confirm.Ok);
        Assert.Equal(1ul, confirm.TradeId.Lo);
        Assert.Equal(1, authority.Applied);
    }

    [Fact]
    public void ARepeatedOrderGetsTheSameAnswerAndChangesNothing()
    {
        var authority = new FakeTradeAuthority(42, 0, 0);
        authority.OnOrder(Order(7, (100, 1, 2)));

        var again = authority.OnOrder(Order(7, (100, 1, 2)));

        Assert.Equal([MsgType.AssetTransferConfirm], again.Select(r => r.Type)); // no second EntityChange
        Assert.True(AsConfirm(again[0]).Ok);
        Assert.Equal(1, authority.DuplicateOrders);
        Assert.Single(authority.Outcomes);
    }

    [Fact]
    public void FailuresAndTimeoutsFollowTheConfiguredShareAndAreDeterministic()
    {
        var a = new FakeTradeAuthority(42, 10, 20);
        var b = new FakeTradeAuthority(42, 10, 20);
        for (ulong id = 1; id <= 2000; id++)
        {
            a.OnOrder(Order(id, (100, 1, 2)));
            b.OnOrder(Order(id, (100, 1, 2)));
        }

        Assert.Equal(a.Outcomes.OrderBy(o => o.Key).Select(o => o.Value), b.Outcomes.OrderBy(o => o.Key).Select(o => o.Value));
        var failed = a.Outcomes.Values.Count(o => !o.Withheld && !o.Applied);
        Assert.InRange(failed, 150, 250);                      // 10 % of 2000
        Assert.InRange(a.Withheld, 330, 470);                  // 20 %
        Assert.InRange(a.Silent, a.Withheld / 5, a.Withheld / 2); // about a third of the withheld ones
        Assert.All(a.Outcomes.Values.Where(o => o.Silent), o => Assert.True(o.Withheld));
    }

    [Fact]
    public void AWithheldConfirmIsAnsweredByTheQueryUnlessTheTradeIsSilent()
    {
        var authority = new FakeTradeAuthority(42, 0, 100); // every trade times out
        for (ulong id = 1; id <= 60; id++)
        {
            var replies = authority.OnOrder(Order(id, (100, 1, 2)));
            Assert.DoesNotContain(replies, r => r.Type == MsgType.AssetTransferConfirm); // withheld

            var query = authority.OnQuery(new TradeQueryT { TradeId = new Id128T { Lo = id } });
            var outcome = authority.Outcomes[id];
            if (outcome.Silent)
            {
                Assert.Empty(query);
            }
            else
            {
                Assert.Equal(outcome.Applied, AsConfirm(Assert.Single(query)).Ok);
            }
        }

        Assert.True(authority.QueriesAnswered > 0 && authority.QueriesIgnored > 0, authority.Summary());
    }

    [Fact]
    public void AQueryForATradeItNeverGotIsUnknown()
    {
        var authority = new FakeTradeAuthority(42, 0, 0);

        var confirm = AsConfirm(Assert.Single(authority.OnQuery(new TradeQueryT { TradeId = new Id128T { Lo = 99 } })));

        Assert.False(confirm.Ok);
        Assert.Equal((short)-1, confirm.FailedLine);
        Assert.Equal("Unknown", confirm.Error);
        Assert.Equal(1, authority.UnknownQueries);
    }

    [Fact]
    public void AFailedOrderIsCompensatedAndEmitsNoOwnerChange()
    {
        var authority = new FakeTradeAuthority(42, 100, 0);

        var replies = authority.OnOrder(Order(5, (100, 1, 2)));

        var confirm = AsConfirm(Assert.Single(replies));
        Assert.False(confirm.Ok);
        Assert.True(confirm.Compensated);
        Assert.Equal((short)0, confirm.FailedLine);
    }

    // ------------------------------------------------------------------ the trading client

    private static Frame Roster(params (int Id, ushort Team)[] players)
    {
        var update = new RosterUpdateT
        {
            Full = true,
            Players = [.. players.Select(p => new PlayerInfoT { PlayerId = (ushort)p.Id, Roles = Role.Client, Phase = NodePhase.InGame, TeamId = p.Team })],
            Removed = [],
        };
        return Encode(MsgType.RosterUpdate, b => RosterUpdate.Pack(b, update).Value);
    }

    private static Frame Status(ulong id, TradeState state, uint version, int initiator, uint initiatorAccepted, int counterparty, uint counterAccepted) =>
        Encode(MsgType.TradeStatus, b => TradeStatus.Pack(b, new TradeStatusT
        {
            TradeId = new Id128T { Lo = id },
            Version = version,
            State = state,
            Initiator = new TradeSideT { PlayerId = (ushort)initiator, AcceptedVersion = initiatorAccepted, Give = [] },
            Counterparty = new TradeSideT { PlayerId = (ushort)counterparty, AcceptedVersion = counterAccepted, Give = [] },
        }).Value);

    private static Frame Encode(MsgType type, Func<FlatBufferBuilder, int> pack)
    {
        var payload = MessageEncoder.EncodePayload(b => new Offset<int>(pack(b)), 256);
        return new Frame(type, FrameOptions.None, MessageRegistry.Default.GetDescriptor(type).Lane, payload);
    }

    [Fact]
    public void ATraderProposesToAnInGamePeerAndAcceptsIncomingOffers()
    {
        var trader = new FakeTrader(2, 42);
        Assert.Null(trader.NextProposal(100)); // nobody to trade with yet
        trader.Handle(Roster((1, 1), (2, 1), (3, 1)), 0);
        Assert.Equal([1, 3], trader.Peers);

        var proposal = trader.NextProposal(100)!;
        var decoded = MessageRegistry.Default.Decode<TradeProposal>(FrameOf(proposal)).UnPack();
        Assert.Contains(decoded.Counterparty, new ushort[] { 1, 3 });
        Assert.Equal(TradeItemKind.Ship, decoded.Give[0].Kind);
        Assert.Equal(100u, decoded.Give[0].Asset);
        Assert.Equal(TradeItemKind.Credits, decoded.Want[0].Kind);
        Assert.True(decoded.Want[0].Amount > 0);
        Assert.Equal(1, trader.ProposalsSent);

        // an offer from player 1 to me (2): accept exactly that version
        var replies = trader.Handle(Status(9, TradeState.Proposed, 1, 1, 1, 2, 0), 10);
        var accept = MessageRegistry.Default.Decode<TradeAccept>(FrameOf(Assert.Single(replies))).UnPack();
        Assert.Equal((9ul, 1u), (accept.TradeId.Lo, accept.Version));
        Assert.Empty(trader.Handle(Status(9, TradeState.Proposed, 1, 1, 1, 2, 0), 11)); // not again right away
        Assert.Single(trader.Retries(15));                                                // but after the retry delay (a dropped accept)
        Assert.Empty(trader.Handle(Status(9, TradeState.Transferring, 1, 1, 1, 2, 1), 16)); // nothing to accept once it moved on

        // my own proposal and offers to others are none of my business
        Assert.Empty(trader.Handle(Status(10, TradeState.Proposed, 1, 2, 1, 3, 0), 20));
        Assert.Empty(trader.Handle(Status(11, TradeState.Proposed, 1, 1, 1, 3, 0), 20));
    }

    [Fact]
    public void ATraderCountsResultsAndRejections()
    {
        var trader = new FakeTrader(2, 42);
        trader.Handle(Status(1, TradeState.Transferring, 1, 2, 1, 1, 1), 0);
        trader.Handle(Encode(MsgType.TradeResult, b => TradeResult.Pack(b, new TradeResultT { TradeId = new Id128T { Lo = 1 }, State = TradeState.Completed, Detail = string.Empty }).Value), 1);
        trader.Handle(Status(1, TradeState.Completed, 1, 2, 1, 1, 1), 1);
        trader.Handle(Encode(MsgType.EconomyResult, b => EconomyResult.Pack(b, new EconomyResultT
        {
            RequestKey = new Id128T(), Status = EconomyStatus.Rejected, Reason = EconomyReject.AssetUnavailable, Detail = string.Empty, Balances = [],
        }).Value), 2);

        Assert.Equal(1, trader.Results[TradeState.Completed]);
        Assert.Equal(1, trader.RequestsRejected);
        Assert.Equal(1, trader.RejectReasons[EconomyReject.AssetUnavailable]);
        Assert.Equal(0, trader.Open);
        Assert.Contains("completed=1", trader.Summary());
    }

    [Fact]
    public void AClientGhostFollowsAnOwnerChangeAndOffersItsTeamsShips()
    {
        var world = new FakeWorld(FakeGalaxy.Generate(42));
        var authority = new FakeAuthority(world, new FakeAuthorityOptions { TeamAssets = true });
        var client = new FakeClientSession(new FakeWorld(FakeGalaxy.Generate(42)), verify: false);
        ushort sector = (ushort)world.Galaxy.Entities.Where(e => !e.IsStation).GroupBy(e => e.HomeSector).OrderByDescending(g => g.Count()).First().Key;
        authority.OnCaptureSet(new CaptureSetT { Epoch = 1, Sectors = [new CaptureSectorT { Sector = sector, RateHz = 5 }] }, 0);
        for (long tick = 0; tick < 200 && client.Ghosts == 0; tick++)
        {
            foreach (var m in authority.Tick(tick).Where(m => m.Type == MsgType.EntitySpawn))
                client.Handle(FrameOf(m));
        }

        Assert.True(client.Ghosts > 0);
        var mine = client.PickTeamAsset(1, 0);
        Assert.NotNull(mine);

        // the ship goes to team 2: it is no longer offered by team 1 clients, and team 2 sees it
        var change = new EntityChangeT { NetId = mine!.Value.NetId, Fields = ChangeField.OwnerTeam | ChangeField.OwnerPlayer, OwnerTeam = 2, OwnerPlayer = 7 };
        client.Handle(Encode(MsgType.EntityChange, b => EntityChange.Pack(b, change).Value));

        Assert.DoesNotContain(Enumerable.Range(0, 50).Select(n => client.PickTeamAsset(1, n)), p => p?.NetId == mine.Value.NetId);
        Assert.Contains(Enumerable.Range(0, 50).Select(n => client.PickTeamAsset(2, n)), p => p?.NetId == mine.Value.NetId);
        Assert.Null(client.PickTeamAsset(0, 0));
    }
}
