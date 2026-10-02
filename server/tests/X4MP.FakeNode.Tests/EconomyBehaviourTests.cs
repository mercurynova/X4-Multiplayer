using Google.FlatBuffers;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode.Tests;

/// <summary>M1-F4: <c>--economy</c>, <c>--dupe-attack</c>, <c>--loan-default</c>, <c>--income-rate</c>: the CLI, the economy behaviour, the income emitter and the reconciliation model.</summary>
public sealed class EconomyBehaviourTests
{
    // ------------------------------------------------------------------ CLI

    [Fact]
    public void EconomyOptionsParse()
    {
        var o = CliParser.Parse(["swarm", "--clients", "6", "--economy", "heavy", "--dupe-attack", "--loan-default", "--income-rate", "0.5",
            "--admin-url", "http://127.0.0.1:47790/", "--admin-user", "boss", "--admin-password", "secret"]).Options!;

        Assert.Equal(EconomyMode.Heavy, o.Economy);
        Assert.True(o.DupeAttack);
        Assert.True(o.LoanDefault);
        Assert.Equal(0.5, o.IncomeRate);
        Assert.Equal("http://127.0.0.1:47790", o.AdminUrl);
        Assert.Equal(("boss", "secret"), (o.AdminUser, o.AdminPassword));
        Assert.True(o.EconomyActive);
        Assert.True(o.TradesEnabled);
        var plain = CliParser.Parse(["swarm"]).Options!;
        Assert.Equal((EconomyMode.Off, false, false, 0.0), (plain.Economy, plain.DupeAttack, plain.LoanDefault, plain.IncomeRate));
        Assert.False(plain.EconomyActive);
        Assert.Equal("admin", plain.AdminUser);
    }

    [Theory]
    [InlineData("--dupe-attack")]
    [InlineData("--loan-default")]
    public void AttackAndDefaultFlagsRunAtLeastACasualEconomy(string flag)
    {
        var o = CliParser.Parse(["client", flag]).Options!;

        Assert.Equal(EconomyMode.Off, o.Economy);
        Assert.Equal(EconomyMode.Casual, o.EffectiveEconomy);
        Assert.True(o.TradesEnabled);
    }

    [Fact]
    public void IdleReconcilesButDoesNotTradeAndAnIncomeRateAloneOnlyReconciles()
    {
        var idle = CliParser.Parse(["client", "--economy", "idle"]).Options!;
        Assert.True(idle.EconomyActive);
        Assert.False(idle.TradesEnabled);

        var income = CliParser.Parse(["client", "--income-rate", "2"]).Options!;
        Assert.Equal(EconomyMode.Off, income.EffectiveEconomy);
        Assert.True(income.EconomyActive);
        Assert.False(income.TradesEnabled);
        Assert.True(CliParser.Parse(["authority", "--income-rate", "2"]).Ok);
    }

    [Theory]
    [InlineData("client", "--economy", "wild")]
    [InlineData("client", "--income-rate", "-1")]
    [InlineData("client", "--income-rate", "100")]
    [InlineData("client", "--income-rate", "lots")]
    [InlineData("client", "--admin-url", "not a url")]
    [InlineData("client", "--admin-url", "ftp://host")]
    [InlineData("fuzz", "--economy", "heavy")]
    [InlineData("authority", "--economy", "heavy")]
    [InlineData("authority", "--dupe-attack", "true")]
    [InlineData("inspect", "--loan-default", "true")]
    [InlineData("authority", "--admin-url", "http://127.0.0.1:1")]
    public void EconomyOptionsAreValidated(params string[] args) => Assert.False(CliParser.Parse(args).Ok);

    // ------------------------------------------------------------------ frames

    private static Frame Encode(MsgType type, Func<FlatBufferBuilder, int> pack)
    {
        var payload = MessageEncoder.EncodePayload(b => new Offset<int>(pack(b)), 256);
        return new Frame(type, FrameOptions.None, MessageRegistry.Default.GetDescriptor(type).Lane, payload);
    }

    private static Frame FrameOf(OutMessage m) => new(m.Type, FrameOptions.None, MessageRegistry.Default.GetDescriptor(m.Type).Lane, m.Payload);

    private static WalletBalanceT Balance(WalletKind kind, int owner, long balance, ulong version) =>
        new() { Wallet = new WalletRefT { Kind = kind, OwnerId = (ushort)owner }, Balance = balance, Version = version };

    private static WalletUpdateT Update(LedgerReason reason, ulong ack, EffectiveCreditMode mode, params WalletBalanceT[] balances) =>
        new() { Balances = [.. balances], Reason = reason, RefId = new Id128T(), EffectiveMode = mode, AckedDeltaSeq = ack };

    private static Frame UpdateFrame(WalletUpdateT update) => Encode(MsgType.WalletUpdate, b => WalletUpdate.Pack(b, update).Value);

    private static Frame ResultFrame(Id128T key, EconomyStatus status, EconomyReject reason, params WalletBalanceT[] balances) =>
        Encode(MsgType.EconomyResult, b => EconomyResult.Pack(b, new EconomyResultT
        {
            RequestKey = key, Status = status, Reason = reason, Detail = string.Empty, RefId = key, Balances = [.. balances],
        }).Value);

    private static Frame Roster(params (int Id, ushort Team)[] players) =>
        Encode(MsgType.RosterUpdate, b => RosterUpdate.Pack(b, new RosterUpdateT
        {
            Full = true,
            Players = [.. players.Select(p => new PlayerInfoT { PlayerId = (ushort)p.Id, Roles = Role.Client, Phase = NodePhase.InGame, TeamId = p.Team })],
            Removed = [],
        }).Value);

    private static Frame LoanStatusFrame(ulong id, int lender, int borrower, LoanState state, long principal = 1000, long repayTotal = 1100, long repaid = 0) =>
        Encode(MsgType.LoanStatus, b => LoanStatus.Pack(b, new LoanStatusT
        {
            LoanId = new Id128T { Lo = id }, Lender = (ushort)lender, Borrower = (ushort)borrower, State = state, Principal = principal,
            RepayTotal = repayTotal, Repaid = repaid, Memo = string.Empty,
        }).Value);

    // ------------------------------------------------------------------ the reconciliation model

    [Fact]
    public void AnAcknowledgedDeltaThatMovedTheWalletByItsAmountAddsUp()
    {
        var model = new EconomyReconciler(3, seesAllWallets: false);
        model.OnWalletUpdate(Update(LedgerReason.GameIncome, 0, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 3, 1000, 1)));
        model.NoteDeltaSent(1, 250);
        model.NoteDeltaSent(2, -100);

        model.OnWalletUpdate(Update(LedgerReason.GameIncome, 1, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 3, 1250, 2)));
        model.OnWalletUpdate(Update(LedgerReason.GameSpend, 2, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 3, 1150, 3)));

        Assert.Equal(0, model.Drifts);
        Assert.Equal(2, model.StepsChecked);
        Assert.Equal((ulong)2, model.LastAck);
        Assert.Equal(0, model.UnackedDeltas);
    }

    [Fact]
    public void AnAckWhoseWalletChangeIsNotTheDeltasAmountIsDrift()
    {
        var model = new EconomyReconciler(3, seesAllWallets: false);
        model.OnWalletUpdate(Update(LedgerReason.GameIncome, 0, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 3, 1000, 1)));
        model.NoteDeltaSent(1, 250);

        model.OnWalletUpdate(Update(LedgerReason.GameIncome, 1, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 3, 1500, 2))); // booked twice

        Assert.Equal(1, model.Drifts);
        Assert.Contains("should have changed the wallets by 250", Assert.Single(model.Notes));
    }

    [Fact]
    public void ADuplicateSeqThatChangesNothingIsFineAndOneThatMovesMoneyIsNot()
    {
        var model = new EconomyReconciler(3, seesAllWallets: false);
        model.OnWalletUpdate(Update(LedgerReason.GameIncome, 0, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 3, 1000, 1)));
        model.NoteDeltaSent(1, 250);
        model.OnWalletUpdate(Update(LedgerReason.GameIncome, 1, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 3, 1250, 2)));

        model.OnWalletUpdate(Update(LedgerReason.GameIncome, 1, EffectiveCreditMode.PerPlayer)); // the server's answer to a resend: the ack alone
        Assert.Equal(0, model.Drifts);

        // a resend that is booked again shows as a stale or repeated version with another balance
        model.OnWalletUpdate(Update(LedgerReason.GameIncome, 1, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 3, 1500, 2)));
        Assert.Equal(1, model.Drifts);
    }

    [Fact]
    public void AnAckThatGoesBackwardsOrPassesWhatWasSentIsDrift()
    {
        var model = new EconomyReconciler(3, seesAllWallets: false);
        model.NoteDeltaSent(1, 5);
        model.NoteDeltaSent(2, 5);
        model.OnWalletUpdate(Update(LedgerReason.GameIncome, 2, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 3, 10, 1)));
        Assert.Equal(0, model.Drifts);

        model.OnWalletUpdate(Update(LedgerReason.GameIncome, 1, EffectiveCreditMode.PerPlayer));
        Assert.Equal(1, model.Drifts);
        model.OnWalletUpdate(Update(LedgerReason.GameIncome, 9, EffectiveCreditMode.PerPlayer));
        Assert.Equal(2, model.Drifts);
    }

    [Fact]
    public void AMissingVersionIsUnverifiableNotDrift()
    {
        var model = new EconomyReconciler(3, seesAllWallets: false);
        model.NoteDeltaSent(1, 250);
        model.OnWalletUpdate(Update(LedgerReason.GameIncome, 0, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 3, 1000, 1)));

        model.OnWalletUpdate(Update(LedgerReason.GameIncome, 1, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 3, 5000, 4))); // versions 2 and 3 never arrived

        Assert.Equal(0, model.Drifts);
        Assert.True(model.Unverifiable >= 1); // the gap, and the ack that came with it
        Assert.Equal((5000L, (ulong)4), model.Wallet(WalletKind.Player, 3));
    }

    [Fact]
    public void ANegativePlayerWalletIsOnlyAllowedAfterGameSpending()
    {
        var model = new EconomyReconciler(3, seesAllWallets: false);
        model.OnWalletUpdate(Update(LedgerReason.GameSpend, 0, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 3, -50, 1)));
        Assert.Equal(0, model.Drifts);

        model.OnWalletUpdate(Update(LedgerReason.Transfer, 0, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 3, -80, 2)));
        Assert.Equal(1, model.Drifts);
        Assert.Contains("negative", model.Notes[0]);
    }

    [Fact]
    public void ANodeThatSeesEveryWalletChecksThatTransfersConserveCredits()
    {
        var authority = new EconomyReconciler(1, seesAllWallets: true);
        authority.OnWalletUpdate(Update(LedgerReason.GameIncome, 0, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 2, 1000, 1), Balance(WalletKind.Player, 3, 1000, 1)));

        authority.OnWalletUpdate(Update(LedgerReason.Transfer, 0, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 2, 900, 2), Balance(WalletKind.Player, 3, 1100, 2)));
        Assert.Equal(0, authority.Drifts);
        Assert.Equal(1, authority.StepsChecked);

        authority.OnWalletUpdate(Update(LedgerReason.PoolDeposit, 0, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 2, 800, 3), Balance(WalletKind.TeamPool, 1, 150, 1)));
        Assert.Equal(1, authority.Drifts); // 100 left the player, 150 arrived in the pool

        var client = new EconomyReconciler(2, seesAllWallets: false);
        client.OnWalletUpdate(Update(LedgerReason.Transfer, 0, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 2, 1, 1)));
        client.OnWalletUpdate(Update(LedgerReason.Transfer, 0, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 2, 0, 2)));
        Assert.Equal(0, client.Drifts); // a client sees one side only: nothing to conserve
    }

    [Fact]
    public void EscrowWalletsAreLeftOutBecauseTheirWireIdIsNotUnique()
    {
        var authority = new EconomyReconciler(1, seesAllWallets: true);
        authority.OnWalletUpdate(Update(LedgerReason.TradeEscrow, 0, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 2, 90, 1), Balance(WalletKind.Escrow, 65535, 10, 1)));
        authority.OnWalletUpdate(Update(LedgerReason.TradeEscrow, 0, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 3, 80, 1), Balance(WalletKind.Escrow, 65535, 20, 1)));

        Assert.Equal(0, authority.Drifts);
        Assert.Equal(2, authority.EscrowSkipped);
        Assert.Null(authority.Wallet(WalletKind.Escrow, 65535));
    }

    [Fact]
    public void AResultMustMatchTheUpdateOfTheSameWalletVersionAndAReplayMatchesTheOldOne()
    {
        var model = new EconomyReconciler(3, seesAllWallets: false);
        model.OnWalletUpdate(Update(LedgerReason.Transfer, 0, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 3, 900, 1)));
        model.OnWalletUpdate(Update(LedgerReason.Transfer, 0, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 3, 800, 2)));

        model.OnResult(new EconomyResultT { Balances = [Balance(WalletKind.Player, 3, 800, 2)] });
        model.OnResult(new EconomyResultT { Balances = [Balance(WalletKind.Player, 3, 900, 1)] }); // a replay answers with the old state
        Assert.Equal(0, model.Drifts);
        Assert.Equal(2, model.ResultsChecked);

        model.OnResult(new EconomyResultT { Balances = [Balance(WalletKind.Player, 3, 700, 2)] });
        Assert.Equal(1, model.Drifts);
        model.OnResult(new EconomyResultT { Balances = [Balance(WalletKind.Player, 3, 500, 9)] }); // a version the updates have not shown yet: nothing to compare
        Assert.Equal(1, model.Drifts);
    }

    [Fact]
    public void TheEffectiveWalletFollowsTheCreditMode()
    {
        var model = new EconomyReconciler(3, seesAllWallets: false);
        Assert.Null(model.EffectiveWallet());
        model.OnWalletUpdate(Update(LedgerReason.GameIncome, 0, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 3, 500, 1), Balance(WalletKind.TeamPool, 2, 9, 1)));
        Assert.Equal((WalletKind.Player, (ushort)3, 500L), model.EffectiveWallet() is { } own ? (own.Kind, own.Owner, own.Balance) : default);

        model.OnWalletUpdate(Update(LedgerReason.ModeMigration, 0, EffectiveCreditMode.Shared, Balance(WalletKind.TeamShared, 2, 4000, 1)));
        Assert.Equal((WalletKind.TeamShared, (ushort)2, 4000L), model.EffectiveWallet() is { } shared ? (shared.Kind, shared.Owner, shared.Balance) : default);
    }

    [Fact]
    public void ComparingWithTheServerSeparatesDriftFromANodeThatIsBehind()
    {
        var model = new EconomyReconciler(3, seesAllWallets: false);
        model.OnWalletUpdate(Update(LedgerReason.GameIncome, 0, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, 3, 500, 4)));

        Assert.Equal(WalletCheck.Match, model.Compare(WalletKind.Player, 3, 500, 4));
        Assert.Equal(WalletCheck.Drift, model.Compare(WalletKind.Player, 3, 501, 4));
        Assert.Equal(WalletCheck.Behind, model.Compare(WalletKind.Player, 3, 999, 5));
        Assert.Equal(WalletCheck.Ahead, model.Compare(WalletKind.Player, 3, 499, 3));
        Assert.Equal(WalletCheck.Unknown, model.Compare(WalletKind.Player, 9, 1, 1));
    }

    // ------------------------------------------------------------------ the income emitter

    private static List<CreditDeltaT> Deltas(IEnumerable<OutMessage> messages) =>
        [.. messages.Select(m => MessageRegistry.Default.Decode<CreditDelta>(FrameOf(m)).UnPack())];

    [Fact]
    public void TheAuthorityBooksSequencedIncomeAndSpendForTheGivenPlayersAtTheRate()
    {
        var model = new EconomyReconciler(1, seesAllWallets: true);
        var income = new FakeIncomeSource(1, 42, 2.0, dupe: false, model);
        var sent = new List<OutMessage>();
        income.Due(0, [2, 3]); // the first call only starts the clock
        for (int step = 1; step <= 100; step++)
            sent.AddRange(income.Due(step * 0.1, [2, 3]));

        var deltas = Deltas(sent);
        Assert.InRange(deltas.Count, 38, 42); // 2/s per player for 10 s with two players
        Assert.Equal(Enumerable.Range(1, deltas.Count).Select(i => (ulong)i), deltas.Select(d => d.Seq));
        Assert.All(deltas, d => Assert.True(d.PlayerId is 2 or 3));
        Assert.Contains(deltas, d => d.Amount > 0);
        Assert.Contains(deltas, d => d.Amount < 0);
        Assert.Equal(deltas.Sum(d => d.Amount), income.SentSum);
        Assert.Equal((ulong)deltas.Count, model.MaxSeqSent);
        Assert.Equal(0, income.Resends);
    }

    [Fact]
    public void AClientBooksItsOwnChangesWithoutNamingAPlayer()
    {
        var income = new FakeIncomeSource(5, 42, 3.0, dupe: false);
        income.DueOwn(0);
        var deltas = Deltas(income.DueOwn(2));

        Assert.NotEmpty(deltas);
        Assert.All(deltas, d => Assert.Equal(0, d.PlayerId));
    }

    [Fact]
    public void TheEmitterIsDeterministicFromTheSeed()
    {
        static List<(long, ulong)> Run(ulong seed)
        {
            var income = new FakeIncomeSource(1, seed, 3.0, dupe: true);
            var all = new List<OutMessage>();
            for (int i = 0; i <= 50; i++)
                all.AddRange(income.Due(i * 0.2, [2, 3, 4]));
            return [.. Deltas(all).Select(d => (d.Amount, d.Seq))];
        }

        Assert.Equal(Run(7), Run(7));
        Assert.NotEqual(Run(7), Run(8));
    }

    [Fact]
    public void WithDupeTheEmitterResendsAnAlreadySentSeq()
    {
        var income = new FakeIncomeSource(1, 42, 5.0, dupe: true);
        var all = new List<OutMessage>();
        for (int i = 0; i <= 200; i++)
            all.AddRange(income.Due(i * 0.1, [2, 3]));
        var deltas = Deltas(all);

        Assert.True(income.Resends > 0, income.Summary());
        var seen = new HashSet<ulong>();
        int repeats = 0;
        foreach (var d in deltas)
        {
            if (!seen.Add(d.Seq))
                repeats++;
        }

        Assert.Equal(income.Resends, repeats);
        Assert.Equal(income.DeltasSent, seen.Count);
    }

    [Fact]
    public void WhatTheLedgerBookedMustBeAPrefixSumOfWhatWasSent()
    {
        var income = new FakeIncomeSource(1, 42, 4.0, dupe: false);
        var deltas = new List<CreditDeltaT>();
        income.Due(0, [2]);
        for (int i = 1; i <= 60; i++)
            deltas.AddRange(Deltas(income.Due(i * 0.25, [2])));
        Assert.True(deltas.Count > 10);

        long upToTen = deltas.Take(10).Sum(d => d.Amount);
        Assert.True(income.IsBookedPrefix(upToTen, 10, income.LastSeq));
        Assert.True(income.IsBookedPrefix(upToTen, 5, 12));
        Assert.False(income.IsBookedPrefix(upToTen, 11, income.LastSeq)); // fewer than the acknowledged ones cannot be booked
        Assert.False(income.IsBookedPrefix(upToTen + deltas[3].Amount, 10, income.LastSeq)); // one booked twice
    }

    // ------------------------------------------------------------------ the economy behaviour

    private static FakeEconomist Economist(int id, EconomyMode mode, bool dupe = false, bool loanDefault = false, ulong seed = 42)
    {
        var economist = new FakeEconomist(id, mode, seed, dupe, loanDefault, new EconomyReconciler(id, seesAllWallets: false));
        economist.Handle(Roster((1, 1), (2, 1), (3, 2), (4, 2)), 0);
        economist.Handle(UpdateFrame(Update(LedgerReason.GameIncome, 0, EffectiveCreditMode.PerPlayer, Balance(WalletKind.Player, id, 100_000, 1))), 0);
        return economist;
    }

    private static Id128T KeyOf(OutMessage m)
    {
        var f = FrameOf(m);
        var r = MessageRegistry.Default;
        return m.Type switch
        {
            MsgType.CreditTransferRequest => r.Decode<CreditTransferRequest>(f).UnPack().RequestKey,
            MsgType.DonateRequest => r.Decode<DonateRequest>(f).UnPack().RequestKey,
            MsgType.PoolDepositRequest => r.Decode<PoolDepositRequest>(f).UnPack().RequestKey,
            MsgType.PoolWithdrawRequest => r.Decode<PoolWithdrawRequest>(f).UnPack().RequestKey,
            MsgType.LoanOffer => r.Decode<LoanOffer>(f).UnPack().RequestKey,
            MsgType.LoanRespond => r.Decode<LoanRespond>(f).UnPack().RequestKey,
            MsgType.LoanRepay => r.Decode<LoanRepay>(f).UnPack().RequestKey,
            MsgType.LoanForgive => r.Decode<LoanForgive>(f).UnPack().RequestKey,
            MsgType.LoanCancel => r.Decode<LoanCancel>(f).UnPack().RequestKey,
            _ => throw new InvalidOperationException(m.Type.ToString()),
        };
    }

    private static List<OutMessage> Run(FakeEconomist economist, double from, double to, double step = 0.1, bool quiet = false)
    {
        var sent = new List<OutMessage>();
        for (double now = from; now < to; now += step)
            sent.AddRange(economist.Tick(now, quiet));
        return sent;
    }

    [Fact]
    public void AnIdleClientSendsNothing()
    {
        var economist = Economist(2, EconomyMode.Idle, dupe: true);

        Assert.Empty(Run(economist, 0, 300));
        Assert.Equal(0, economist.RequestsSent);
    }

    [Fact]
    public void AHeavyClientSendsAMixOfRequestsWithUniqueKeysOfItsOwnKeyDomain()
    {
        var economist = Economist(2, EconomyMode.Heavy);

        var sent = Run(economist, 0, 300);

        Assert.InRange(sent.Count, 50, 150); // one every ~3 s (+ loan answers)
        var keys = sent.Select(KeyOf).ToList();
        Assert.Equal(keys.Count, keys.Select(k => (k.Hi, k.Lo)).Distinct().Count());
        Assert.All(keys, k => Assert.Equal((ulong)0xEC0, k.Hi >> 32));
        Assert.All(keys, k => Assert.Equal((ulong)2, k.Hi & 0xFFFFFFFF));
        var kinds = sent.Select(m => m.Type).Distinct().ToList();
        Assert.Contains(MsgType.CreditTransferRequest, kinds);
        Assert.Contains(MsgType.DonateRequest, kinds);
        Assert.Contains(MsgType.PoolDepositRequest, kinds);
        Assert.Contains(MsgType.LoanOffer, kinds);
        Assert.True(economist.RequestsSent >= 50);
    }

    [Fact]
    public void ACasualClientIsSlowerThanAHeavyOne()
    {
        int casual = Run(Economist(2, EconomyMode.Casual), 0, 300).Count;
        int heavy = Run(Economist(2, EconomyMode.Heavy), 0, 300).Count;

        Assert.True(casual > 10, casual.ToString());
        Assert.True(heavy > casual * 1.8, $"{heavy} vs {casual}");
    }

    [Fact]
    public void TheMixIsDeterministicFromTheSeed()
    {
        static List<(MsgType, string)> Of(ulong seed) =>
            [.. Run(Economist(2, EconomyMode.Heavy, dupe: true, seed: seed), 0, 120).Select(m => (m.Type, Convert.ToHexString(m.Payload)))];

        Assert.Equal(Of(42), Of(42));
        Assert.NotEqual(Of(42), Of(43));
    }

    [Fact]
    public void RequestsAreOnlyAimedAtInGameClientsAndTransfersOnlyAtTeammates()
    {
        var economist = Economist(2, EconomyMode.Heavy);

        var sent = Run(economist, 0, 400);

        var transfers = sent.Where(m => m.Type == MsgType.CreditTransferRequest).Select(m => MessageRegistry.Default.Decode<CreditTransferRequest>(FrameOf(m)).UnPack()).ToList();
        Assert.NotEmpty(transfers);
        Assert.All(transfers, t => Assert.Equal(1, t.ToPlayer)); // the only other member of team 1
        var donations = sent.Where(m => m.Type == MsgType.DonateRequest).Select(m => MessageRegistry.Default.Decode<DonateRequest>(FrameOf(m)).UnPack()).ToList();
        Assert.All(donations, d => Assert.True(d.ToPlayer is 1 or 3 or 4));
        Assert.All(transfers.Select(t => t.Amount).Concat(donations.Select(d => d.Amount)), a => Assert.InRange(a, 10, 4000));
    }

    [Fact]
    public void QuietStopsNewRequestsButNotTheAnswerToALoanOffer()
    {
        var economist = Economist(2, EconomyMode.Heavy);
        economist.Handle(LoanStatusFrame(7, lender: 1, borrower: 2, LoanState.Offered), 100);

        var quiet = Run(economist, 100, 105, quiet: true);

        var only = Assert.Single(quiet);
        Assert.Equal(MsgType.LoanRespond, only.Type);
    }

    [Fact]
    public void ALoanOfferedToTheClientIsAnsweredAfterAPauseAndTheLoanIsRepaid()
    {
        var economist = Economist(2, EconomyMode.Casual);
        economist.Handle(LoanStatusFrame(7, lender: 1, borrower: 2, LoanState.Offered), 10);
        Assert.Empty(economist.Tick(10.1, quiet: true)); // it thinks about it first

        var answer = Run(economist, 10.2, 16, quiet: true).Where(m => m.Type == MsgType.LoanRespond).ToList();
        Assert.NotEmpty(answer);
        var respond = MessageRegistry.Default.Decode<LoanRespond>(FrameOf(answer[0])).UnPack();
        Assert.Equal((ulong)7, respond.LoanId.Lo);

        economist.Handle(LoanStatusFrame(7, lender: 1, borrower: 2, LoanState.Active), 20);
        var repay = Run(economist, 20, 40).Where(m => m.Type == MsgType.LoanRepay).Select(m => MessageRegistry.Default.Decode<LoanRepay>(FrameOf(m)).UnPack()).ToList();
        Assert.NotEmpty(repay);
        Assert.All(repay, r => Assert.InRange(r.Amount, 1, 1100));
    }

    [Fact]
    public void WithLoanDefaultTheBorrowerAcceptsButNeverRepaysAndOffersFallDueInSeconds()
    {
        var economist = Economist(2, EconomyMode.Casual, loanDefault: true);
        economist.Handle(LoanStatusFrame(7, lender: 1, borrower: 2, LoanState.Offered), 10);
        var sent = Run(economist, 10, 14, quiet: true);
        var respond = MessageRegistry.Default.Decode<LoanRespond>(FrameOf(Assert.Single(sent, m => m.Type == MsgType.LoanRespond))).UnPack();
        Assert.True(respond.Accept);

        economist.Handle(LoanStatusFrame(7, lender: 1, borrower: 2, LoanState.Active), 20);
        economist.Handle(LoanStatusFrame(7, lender: 1, borrower: 2, LoanState.Overdue), 30);
        var later = Run(economist, 20, 400);

        Assert.DoesNotContain(later, m => m.Type == MsgType.LoanRepay);
        var offers = later.Where(m => m.Type == MsgType.LoanOffer).Select(m => MessageRegistry.Default.Decode<LoanOffer>(FrameOf(m)).UnPack()).ToList();
        Assert.True(offers.Count >= 10, offers.Count.ToString());
        Assert.All(offers, o => Assert.InRange(o.DueInS, 4u, 6u));
        Assert.Equal(1, economist.OverdueBorrowed);
    }

    [Fact]
    public void ARejectedRequestIsCountedByReasonAndAFrozenWalletIsReportedOnce()
    {
        var economist = Economist(2, EconomyMode.Heavy);
        var first = Run(economist, 0, 20).First();
        var key = KeyOf(first);

        economist.Handle(ResultFrame(key, EconomyStatus.Rejected, EconomyReject.EconomyFrozen), 5);

        Assert.Equal(1, economist.RequestsRejected);
        Assert.Equal(1, economist.RejectReasons[EconomyReject.EconomyFrozen]);
        var note = Assert.Single(economist.DrainNotes());
        Assert.Contains("EconomyFrozen", note);
        Assert.Empty(economist.DrainNotes());
    }

    // ---- the attacks, against a model server that is right and ones that are wrong

    /// <summary>A stand-in for the server's idempotency: a new key is booked (and answered with new balances), the same bytes get the stored answer, other bytes under that key are a reuse.</summary>
    private sealed class FakeServer(bool reuseSucceeds = false, bool replayBooksAgain = false)
    {
        private readonly Dictionary<(ulong, ulong), (string Payload, ulong Version)> _stored = [];
        private ulong _version = 1;

        public int Booked { get; private set; }

        public IReadOnlyList<Frame> Answer(OutMessage message)
        {
            var key = KeyOf(message);
            string payload = Convert.ToHexString(message.Payload);
            var id = (key.Hi, key.Lo);
            if (_stored.TryGetValue(id, out var existing))
            {
                if (existing.Payload != payload)
                    return reuseSucceeds ? [Result(key, ++_version)] : [ResultFrame(key, EconomyStatus.Rejected, EconomyReject.RequestIdReuse)];
                return [Result(key, replayBooksAgain ? ++_version : existing.Version)];
            }

            Booked++;
            _stored[id] = (payload, ++_version);
            return [Result(key, _version)];
        }

        private static Frame Result(Id128T key, ulong version) =>
            ResultFrame(key, EconomyStatus.Ok, EconomyReject.None, Balance(WalletKind.Player, 2, 100_000 - (long)version, version));
    }

    private static FakeEconomist Attack(FakeServer server, double seconds = 600)
    {
        var economist = Economist(2, EconomyMode.Heavy, dupe: true);
        for (double now = 0; now < seconds; now += 0.1)
        {
            foreach (var message in economist.Tick(now, quiet: false))
            {
                foreach (var answer in server.Answer(message))
                    economist.Handle(answer, now);
            }
        }

        return economist;
    }

    [Fact]
    public void AServerThatIsIdempotentLeavesNoDuplicateEffectsWhateverTheAttack()
    {
        var economist = Attack(new FakeServer());

        Assert.True(economist.ReplaysSent >= 10, economist.Summary());
        Assert.True(economist.ReusesSent >= 10, economist.Summary());
        Assert.True(economist.RacesSent >= 5, economist.Summary());
        Assert.Equal(economist.ReplaysSent, economist.ReplaysIdempotent);
        Assert.Equal(economist.ReusesSent, economist.ReusesRejected);
        Assert.Equal(0, economist.DuplicateEffects);
        Assert.Empty(economist.DrainNotes());
    }

    [Fact]
    public void AReuseThatTheServerAcceptsIsADuplicateEffect()
    {
        var economist = Attack(new FakeServer(reuseSucceeds: true));

        Assert.True(economist.DuplicateEffects > 0, economist.Summary());
        Assert.Contains(economist.DrainNotes(), n => n.Contains("reused with another payload", StringComparison.Ordinal));
    }

    [Fact]
    public void AReplayThatBooksAgainAnswersWithNewBalancesAndIsADuplicateEffect()
    {
        var economist = Attack(new FakeServer(replayBooksAgain: true));

        Assert.True(economist.DuplicateEffects > 0, economist.Summary());
        Assert.Contains(economist.DrainNotes(), n => n.Contains("different balances", StringComparison.Ordinal));
    }

    [Fact]
    public void RateLimitedAnswersToAnAttackAreNotDuplicateEffects()
    {
        var economist = Economist(2, EconomyMode.Heavy, dupe: true);
        var originals = new Dictionary<(ulong, ulong), int>();
        for (double now = 0; now < 200; now += 0.1)
        {
            foreach (var message in economist.Tick(now, quiet: false))
            {
                var key = KeyOf(message);
                bool first = originals.TryAdd((key.Hi, key.Lo), 1);
                // the first sending of a key goes through; everything after it is over the budget
                economist.Handle(
                    first ? ResultFrame(key, EconomyStatus.Ok, EconomyReject.None, Balance(WalletKind.Player, 2, 1, 1))
                          : ResultFrame(key, EconomyStatus.Rejected, EconomyReject.RateLimited), now);
            }
        }

        Assert.Equal(0, economist.DuplicateEffects);
        Assert.True(economist.ReplaysRateLimited > 0 && economist.ReusesRateLimited > 0, economist.Summary());
    }

    [Fact]
    public void ATraderIgnoresTheAnswersToTheEconomyBehavioursRequests()
    {
        var trader = new FakeTrader(2, 42);
        var key = new Id128T { Hi = (0xEC0UL << 32) | 2, Lo = 5 };

        trader.Handle(ResultFrame(key, EconomyStatus.Rejected, EconomyReject.RateLimited), 0);
        Assert.Equal(0, trader.RequestsRejected);

        trader.Handle(ResultFrame(new Id128T { Hi = 2, Lo = 1 }, EconomyStatus.Rejected, EconomyReject.RateLimited), 0);
        Assert.Equal(1, trader.RequestsRejected);
    }
}
