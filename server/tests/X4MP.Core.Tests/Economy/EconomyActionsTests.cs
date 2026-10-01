using X4MP.Core.Economy;
using X4MP.Core.Teams;
using X4MP.Proto;
using TeamRelation = X4MP.Core.Teams.TeamRelation;

namespace X4MP.Core.Tests.Economy;

/// <summary>M1-E3: teammate transfer, donation and pool actions (server-design 2.14).</summary>
public sealed class EconomyActionsTests
{
    // Players 1,2 = team 1; 3 = team 2 (allied); 4 = team 3 (neutral); 5 = team 4 (hostile).
    private static readonly Dictionary<int, int?> Members = new() { [1] = 1, [2] = 1, [3] = 2, [4] = 3, [5] = 4 };
    private static readonly Dictionary<int, int?> OneTeam = new() { [1] = 1, [2] = 1, [3] = 1 };

    private static EconomyKit Kit(Action<EconomyOptions>? configure = null, IReadOnlyDictionary<int, int?>? members = null, int[]? teams = null)
    {
        var options = new EconomyOptions { StartingCredits = 0 };
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

        return kit;
    }

    // ------------------------------------------------------------------ 1. scope x relation

    [Theory]
    // scope, recipient (1 is the sender), expected
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
    public void DonationScopeTimesRelation(EconomyScope scope, int to, EconomyReject expected)
    {
        var kit = Kit(o => o.DonateScope = scope);
        kit.Fund(1, 1000);

        var result = kit.Service.Donate(1, "d1", to, 100);

        Assert.Equal(expected, result.Reason);
        Assert.Equal(expected == EconomyReject.None ? 900 : 1000, kit.Balance(WalletId.Player(1)));
        Assert.Equal(expected == EconomyReject.None ? 100 : 0, kit.Balance(WalletId.Player(to)));
    }

    // ------------------------------------------------------------------ 2. relation flips

    [Fact]
    public void HostileDonationUnderAlliedScopeIsDeniedAndWorksOnceTheRelationIsAllied()
    {
        var kit = Kit(o => o.DonateScope = EconomyScope.Allied);
        kit.Fund(1, 1000);

        Assert.Equal(EconomyReject.ScopeDenied, kit.Service.Donate(1, "h1", 5, 100).Reason);

        kit.Teams.SetRelation(1, 4, TeamRelation.Allied);
        var after = kit.Service.Donate(1, "h2", 5, 100);

        Assert.True(after.Ok);
        Assert.Equal(100, kit.Balance(WalletId.Player(5)));
        Assert.Equal(900, kit.Balance(WalletId.Player(1)));

        kit.Teams.SetRelation(1, 4, TeamRelation.Hostile);
        Assert.Equal(EconomyReject.ScopeDenied, kit.Service.Donate(1, "h3", 5, 1).Reason);
    }

    [Fact]
    public void TransfersNeedTeammatesUnlessAlliedTransfersAreOn()
    {
        var kit = Kit();
        kit.Fund(1, 1000);

        Assert.True(kit.Service.Transfer(1, "t1", 2, 100).Ok);
        Assert.Equal(EconomyReject.NotTeammate, kit.Service.Transfer(1, "t2", 3, 100).Reason);

        kit.Options.AllowAlliedTransfers = true;
        Assert.True(kit.Service.Transfer(1, "t3", 3, 100).Ok);
        Assert.Equal(EconomyReject.NotTeammate, kit.Service.Transfer(1, "t4", 4, 100).Reason);
        Assert.Equal(800, kit.Balance(WalletId.Player(1)));
        Assert.Equal(0, kit.Ledger.TotalBalance());
        Assert.All(kit.Store.Transactions.Where(t => t.Kind == TxKind.Transfer), t => Assert.Equal("player:1", t.Actor));
    }

    // ------------------------------------------------------------------ validation

    [Fact]
    public void ValidationGivesOneRejectionReasonPerRequest()
    {
        var kit = Kit(o => o.MaxSingleTransfer = 500);
        kit.Fund(1, 300);

        Assert.Equal(EconomyReject.AmountInvalid, kit.Service.Donate(1, "a", 2, 0).Reason);
        Assert.Equal(EconomyReject.AmountInvalid, kit.Service.Transfer(1, "b", 2, -5).Reason);
        Assert.Equal(EconomyReject.OverMaxAmount, kit.Service.Transfer(1, "c", 2, 501).Reason);
        Assert.Equal(EconomyReject.UnknownPlayer, kit.Service.Transfer(1, "d", 99, 5).Reason);
        Assert.Equal(EconomyReject.UnknownPlayer, kit.Service.Donate(99, "e", 1, 5).Reason);
        Assert.Equal(EconomyReject.NotParty, kit.Service.Transfer(1, "f", 1, 5).Reason); // self-dealing
        Assert.Equal(EconomyReject.NotParty, kit.Service.Donate(1, "g", 1, 5).Reason);
        Assert.Equal(EconomyReject.InsufficientFunds, kit.Service.Transfer(1, "h", 2, 301).Reason);
        Assert.Equal(EconomyReject.InsufficientFunds, kit.Service.Donate(2, "i", 1, 1).Reason); // player 2 has nothing
        Assert.Equal(300, kit.Balance(WalletId.Player(1)));
        Assert.Equal(0, kit.Ledger.TotalBalance());
    }

    [Fact]
    public void AnOfflineRecipientStillReceivesAndFrozenWalletsBlock()
    {
        var kit = Kit();
        kit.Fund(1, 500);

        Assert.True(kit.Service.Transfer(1, "t1", 2, 100).Ok); // no node attached for player 2: still fine

        kit.Ledger.SetWalletFrozen(WalletId.Player(2), true, "admin");
        Assert.Equal(EconomyReject.EconomyFrozen, kit.Service.Transfer(1, "t2", 2, 10).Reason); // recipient frozen
        Assert.Equal(EconomyReject.EconomyFrozen, kit.Service.Transfer(2, "t3", 1, 10).Reason); // sender frozen
        kit.Ledger.SetWalletFrozen(WalletId.Player(2), false, null);

        kit.Ledger.Freeze("test");
        Assert.Equal(EconomyReject.EconomyFrozen, kit.Service.Donate(1, "t4", 2, 10).Reason);
        Assert.Equal(EconomyReject.EconomyFrozen, kit.Service.PoolDeposit(1, "t5", 10).Reason);
        Assert.Equal(400, kit.Balance(WalletId.Player(1)));
    }

    [Fact]
    public void AnOverdrawnWalletCannotSendUntilItIsBackAtZero()
    {
        var kit = Kit();
        kit.Fund(1, 100);
        kit.Service.BookCreditDelta(1, false, new CreditDeltaT { Amount = -150, Seq = 0 }); // the game spent more than we held
        Assert.Equal(-50, kit.Balance(WalletId.Player(1)));

        Assert.Equal(EconomyReject.InsufficientFunds, kit.Service.Transfer(1, "t1", 2, 1).Reason);
        kit.Fund(1, 50);
        Assert.Equal(EconomyReject.InsufficientFunds, kit.Service.Transfer(1, "t2", 2, 1).Reason); // exactly 0: nothing to send
        kit.Fund(1, 10);
        Assert.True(kit.Service.Transfer(1, "t3", 2, 10).Ok);
    }

    [Fact]
    public void EverySuccessIsOneBalancedTransactionAnnouncedToBothSides()
    {
        var kit = Kit(o => o.DonateScope = EconomyScope.Anyone);
        kit.Fund(1, 1000);
        kit.Changes.Clear();

        var result = kit.Service.Donate(1, "d1", 5, 250, "thanks");

        Assert.True(result.Ok);
        var tx = Assert.Single(kit.Store.Transactions, t => t.Kind == TxKind.Donate);
        Assert.Equal(0, tx.Entries.Sum(e => e.Amount));
        Assert.Equal(2, tx.Entries.Count);
        Assert.Equal("thanks", tx.Note);
        var change = Assert.Single(kit.Changes);
        Assert.Equal(LedgerReason.Donation, change.Reason);
        Assert.Contains(change.Changed, b => b.Wallet == WalletId.Player(1) && b.Balance == 750);
        Assert.Contains(change.Changed, b => b.Wallet == WalletId.Player(5) && b.Balance == 250);

        var published = Assert.Single(kit.Events.OfType<EconomyActionCompleted>());
        Assert.Equal((1, 5, 250L), ((int)published.FromPlayer, (int)published.ToPlayer!, published.Amount));
        Assert.Equal(tx.Id, published.TxId);

        // A replay announces nothing again.
        kit.Changes.Clear();
        Assert.True(kit.Service.Donate(1, "d1", 5, 250, "thanks").Outcome!.Replayed);
        Assert.Empty(kit.Changes);
        Assert.Single(kit.Events.OfType<EconomyActionCompleted>());
    }

    // ------------------------------------------------------------------ pool (with the E2 policy and limit)

    [Fact]
    public void PoolActionsKeepTheirPolicyAndLimitAndPublishEvents()
    {
        var kit = Kit(o => o.PoolWithdrawDailyLimitPerPlayer = 100);
        kit.Fund(1, 1000);

        Assert.True(kit.Service.PoolDeposit(1, "p1", 500).Ok);
        Assert.True(kit.Service.PoolWithdraw(2, "p2", 100).Ok);
        Assert.Equal(EconomyReject.DailyLimit, kit.Service.PoolWithdraw(2, "p3", 1).Reason);
        kit.Options.PoolWithdrawPolicy = PoolWithdrawPolicy.Disabled;
        Assert.Equal(EconomyReject.PoolPolicyDenied, kit.Service.PoolWithdraw(1, "p4", 1).Reason);
        Assert.Equal(EconomyReject.OverMaxAmount, kit.Service.PoolDeposit(1, "p5", long.MaxValue).Reason);
        Assert.Equal(2, kit.Events.OfType<EconomyActionCompleted>().Count);
    }

    // ------------------------------------------------------------------ 3. rate limit

    [Fact]
    public void TheSixthRequestInTenSecondsIsRateLimitedAndTheWindowSlides()
    {
        var kit = Kit();
        for (var i = 0; i < 5; i++)
        {
            Assert.Null(kit.Service.CheckRate(1));
            kit.Time.Advance(TimeSpan.FromSeconds(1)); // t = 1..5
        }

        Assert.Equal(EconomyReject.RateLimited, kit.Service.CheckRate(1)!.Reason); // 6th, 5 s after the first
        Assert.Null(kit.Service.CheckRate(2)); // per player

        kit.Time.Advance(TimeSpan.FromSeconds(4)); // t = 9
        Assert.NotNull(kit.Service.CheckRate(1));
        kit.Time.Advance(TimeSpan.FromSeconds(1)); // t = 10: the first request left the window
        Assert.Null(kit.Service.CheckRate(1));
        Assert.NotNull(kit.Service.CheckRate(1));

        kit.Time.Advance(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 5; i++)
        {
            Assert.Null(kit.Service.CheckRate(1));
        }
    }

    // ------------------------------------------------------------------ 5. idempotency

    [Fact]
    public void EveryRequestTypeReplaysTheSameResultAndRejectsAPayloadMismatch()
    {
        var kit = Kit(o => o.DonateScope = EconomyScope.Anyone);
        kit.Fund(1, 1000);
        kit.Service.PoolDeposit(1, "seed-pool", 400);

        var cases = new (string Name, Func<string, long, EconomyActionResult> Run, int Player)[]
        {
            ("transfer", (k, a) => kit.Service.Transfer(1, k, 2, a), 1),
            ("donate", (k, a) => kit.Service.Donate(1, k, 5, a, "gift"), 1),
            ("deposit", (k, a) => kit.Service.PoolDeposit(1, k, a), 1),
            ("withdraw", (k, a) => kit.Service.PoolWithdraw(2, k, a), 2),
        };
        foreach (var (name, run, player) in cases)
        {
            var first = run("key-" + name, 50);
            Assert.True(first.Ok, name);
            var balancesAfterFirst = kit.Balance(WalletId.Player(player));
            var txCount = kit.Store.Transactions.Count;

            var replay = run("key-" + name, 50);
            Assert.True(replay.Ok, name);
            Assert.True(replay.Outcome!.Replayed, name);
            Assert.Equal(first.Outcome!.TxId, replay.Outcome.TxId);
            Assert.Equal(first.Outcome.Balances, replay.Outcome.Balances);

            var mismatch = run("key-" + name, 51);
            Assert.False(mismatch.Ok, name);
            Assert.Contains("different payload", mismatch.Detail);

            Assert.Equal(balancesAfterFirst, kit.Balance(WalletId.Player(player)));
            Assert.Equal(txCount, kit.Store.Transactions.Count);
        }

        Assert.Equal(cases.Length, kit.Ledger.RequestIdReuseCount);
        Assert.Equal(0, kit.Ledger.TotalBalance());
    }

    [Fact]
    public void AReplayIsAnsweredEvenIfFundsRelationOrFreezeChangedSince()
    {
        var kit = Kit(o => o.DonateScope = EconomyScope.Allied);
        kit.Fund(1, 100);
        kit.Teams.SetRelation(1, 4, TeamRelation.Allied);
        var first = kit.Service.Donate(1, "k", 5, 100);
        Assert.True(first.Ok);

        kit.Teams.SetRelation(1, 4, TeamRelation.Hostile); // would be refused now, and player 1 has no money left
        kit.Ledger.Freeze("test");

        var replay = kit.Service.Donate(1, "k", 5, 100);
        Assert.True(replay.Ok);
        Assert.Equal(first.Outcome!.TxId, replay.Outcome!.TxId);
    }

    [Fact]
    public void ASameKeyAcrossDifferentTypesIsAMismatchNotADuplicate()
    {
        var kit = Kit(o => o.DonateScope = EconomyScope.Anyone);
        kit.Fund(1, 100);
        Assert.True(kit.Service.Transfer(1, "same", 2, 10).Ok);

        Assert.False(kit.Service.Donate(1, "same", 2, 10).Ok);
        Assert.Equal(90, kit.Balance(WalletId.Player(1)));
    }

    // ------------------------------------------------------------------ 6. shared mode

    [Fact]
    public void SharedModeRejectsSameWalletActionsPerServerDesign()
    {
        // "In Shared mode, actions between two members of the same team are rejected with SameWallet" (2.14 Action scopes);
        // the wire enum has no SameWallet, so the reason is NotApplicableInSharedMode.
        var kit = Kit(members: OneTeam, teams: [1]);
        Assert.Equal(EffectiveCreditMode.Shared, kit.Service.AppliedMode);
        kit.Fund(1, 500);

        Assert.Equal(EconomyReject.NotApplicableInSharedMode, kit.Service.Transfer(1, "a", 2, 10).Reason);
        Assert.Equal(EconomyReject.NotApplicableInSharedMode, kit.Service.Donate(1, "b", 2, 10).Reason);
        Assert.Equal(EconomyReject.NotApplicableInSharedMode, kit.Service.PoolDeposit(1, "c", 10).Reason);
        Assert.Equal(EconomyReject.NotApplicableInSharedMode, kit.Service.PoolWithdraw(1, "d", 10).Reason);
        Assert.Equal(500, kit.Balance(WalletId.TeamShared(1)));
        Assert.DoesNotContain(kit.Store.Transactions, t => t.Kind is TxKind.Donate or TxKind.Transfer);
    }

    [Fact]
    public void SharedModeDonationsBetweenTeamsMoveTheTeamWalletsAndHonourTheSpendPolicy()
    {
        var kit = Kit(o =>
        {
            o.CreditMode = CreditMode.Shared;
            o.DonateScope = EconomyScope.Anyone;
        });
        Assert.Equal(EffectiveCreditMode.Shared, kit.Service.AppliedMode);
        kit.Fund(1, 1000); // team 1 shared wallet

        Assert.True(kit.Service.Donate(2, "a", 3, 300).Ok); // member 2 spends team 1's money for team 2
        Assert.Equal(700, kit.Balance(WalletId.TeamShared(1)));
        Assert.Equal(300, kit.Balance(WalletId.TeamShared(2)));

        kit.Options.SharedWalletSpend = SharedWalletSpendPolicy.LeaderOnly; // leader of team 1 is player 1
        Assert.Equal(EconomyReject.NotParty, kit.Service.Donate(2, "b", 3, 10).Reason);
        Assert.True(kit.Service.Donate(1, "c", 3, 10).Ok);
        Assert.Equal(EconomyReject.NotApplicableInSharedMode, kit.Service.Transfer(1, "d", 2, 10).Reason);
        Assert.Equal(0, kit.Ledger.TotalBalance());
    }

    // ------------------------------------------------------------------ 4. property test

    [Theory]
    [InlineData(1, CreditMode.PerPlayer)]
    [InlineData(2, CreditMode.PerPlayer)]
    [InlineData(3, CreditMode.PerPlayer)]
    [InlineData(4, CreditMode.Shared)]
    [InlineData(5, CreditMode.Shared)]
    [InlineData(6, CreditMode.Auto)]
    public void RandomMixedActionsKeepTheLedgerBalancedAndNeverOverdraw(int seed, CreditMode mode)
    {
        var rng = new Random(seed);
        var kit = Kit(o =>
        {
            o.CreditMode = mode;
            o.DonateScope = EconomyScope.Anyone;
            o.AllowAlliedTransfers = true;
        });
        var players = Members.Keys.ToArray();
        var relations = new[] { TeamRelation.Allied, TeamRelation.Neutral, TeamRelation.Hostile };
        var ok = 0;

        for (var i = 0; i < 1500; i++)
        {
            var from = players[rng.Next(players.Length)];
            var to = players[rng.Next(players.Length)];
            var amount = rng.Next(-5, 400);
            var key = $"k{i}";
            switch (rng.Next(8))
            {
                case 0:
                    kit.Fund(from, rng.Next(1, 500));
                    break;
                case 1:
                    ok += kit.Service.Transfer(from, key, to, amount).Ok ? 1 : 0;
                    break;
                case 2:
                case 3:
                    ok += kit.Service.Donate(from, key, to, amount).Ok ? 1 : 0;
                    break;
                case 4:
                    ok += kit.Service.PoolDeposit(from, key, amount).Ok ? 1 : 0;
                    break;
                case 5:
                    ok += kit.Service.PoolWithdraw(from, key, amount).Ok ? 1 : 0;
                    break;
                case 6:
                    kit.Teams.SetRelation(1 + rng.Next(3), 4, relations[rng.Next(relations.Length)]);
                    kit.Options.DonateScope = (EconomyScope)rng.Next(4);
                    break;
                default:
                    _ = kit.Service.Donate(from, "replay", to, 7).Ok; // the same key again and again
                    break;
            }

            Assert.Equal(0, kit.Ledger.TotalBalance());
            Assert.All(kit.Ledger.Wallets.Where(w => w.Id.Kind != X4MP.Core.Economy.WalletKind.World), w => Assert.True(w.Balance >= 0, $"{w.Id} = {w.Balance} after op {i}"));
        }

        Assert.True(ok > 50, "the random run should have booked a fair number of actions, got " + ok);
        Assert.All(kit.Store.Transactions, t => Assert.Equal(0, t.Entries.Sum(e => e.Amount)));
        Assert.Equal(0, kit.Ledger.TotalBalance());
    }
}
