using X4MP.Core.Economy;
using X4MP.Core.Events;
using X4MP.Core.Session;
using X4MP.Proto;

namespace X4MP.Core.Tests.Economy;

public sealed class EconomyServiceTests
{
    private static readonly Dictionary<int, int?> TwoTeams = new() { [1] = 1, [2] = 1, [3] = 2 };
    private static readonly Dictionary<int, int?> OneTeam = new() { [1] = 1, [2] = 1, [3] = 1 };

    private static EconomyKit Kit(Action<EconomyOptions>? configure = null, IReadOnlyDictionary<int, int?>? members = null, int[]? teams = null)
    {
        var options = new EconomyOptions { StartingCredits = 0 };
        configure?.Invoke(options);
        var kit = new EconomyKit(options);
        kit.Teams.Set(teams ?? [1, 2], members ?? TwoTeams);
        foreach (var player in (members ?? TwoTeams).Keys)
        {
            kit.Service.EnsurePlayer(player);
        }

        return kit;
    }

    private static CreditDeltaT Delta(long amount, ulong seq, ushort player = 0, ushort team = 0) =>
        new() { Amount = amount, Seq = seq, PlayerId = player, TeamId = team, Source = CreditSource.Trade };

    // ------------------------------------------------------------------ mode resolution

    [Theory]
    [InlineData(CreditMode.Auto, 0, EffectiveCreditMode.PerPlayer)]
    [InlineData(CreditMode.Auto, 1, EffectiveCreditMode.Shared)]
    [InlineData(CreditMode.Auto, 2, EffectiveCreditMode.PerPlayer)]
    [InlineData(CreditMode.Auto, 8, EffectiveCreditMode.PerPlayer)]
    [InlineData(CreditMode.PerPlayer, 1, EffectiveCreditMode.PerPlayer)]
    [InlineData(CreditMode.Shared, 2, EffectiveCreditMode.Shared)]
    [InlineData(CreditMode.Shared, 0, EffectiveCreditMode.Shared)]
    public void ModeResolution(CreditMode mode, int teams, EffectiveCreditMode expected) =>
        Assert.Equal(expected, EconomyService.Resolve(mode, teams));

    [Fact]
    public void StartingCreditsDefaultIs100kAndIsLiveSetting()
    {
        Assert.Equal(100_000, new EconomyOptions().StartingCredits);
        var descriptor = X4MP.Core.Settings.SettingsRegistry.Describe(typeof(EconomyOptions)).Single(d => d.Name == nameof(EconomyOptions.StartingCredits));
        Assert.Equal(X4MP.Core.Settings.SettingScope.Live, descriptor.Scope);
        Assert.Equal("Economy.StartingCredits", descriptor.Key);
    }

    [Fact]
    public void NewPlayersGetTheStartingCreditsExactlyOnce()
    {
        var kit = new EconomyKit(new EconomyOptions());
        kit.Teams.Set([1, 2], new Dictionary<int, int?> { [1] = 1, [2] = 2 });

        Assert.True(kit.Service.EnsurePlayer(1).Ok);
        kit.Service.EnsurePlayer(1);
        kit.Service.EnsurePlayer(2);

        Assert.Equal(100_000, kit.Balance(WalletId.Player(1)));
        Assert.Equal(100_000, kit.Balance(WalletId.Player(2)));
        Assert.Equal(-200_000, kit.Balance(WalletId.World));
        Assert.Equal(0, kit.Ledger.TotalBalance());
    }

    [Fact]
    public void StartingCreditsGoToTheTeamWalletInSharedMode()
    {
        var kit = new EconomyKit(new EconomyOptions { StartingCredits = 100 });
        kit.Teams.Set([1], OneTeam);

        kit.Service.EnsurePlayer(1);
        kit.Service.EnsurePlayer(2);

        Assert.Equal(EffectiveCreditMode.Shared, kit.Service.AppliedMode);
        Assert.Equal(200, kit.Balance(WalletId.TeamShared(1)));
        Assert.Equal(0, kit.Balance(WalletId.Player(1)));
    }

    // ------------------------------------------------------------------ migrations

    [Fact]
    public void CoopInAutoMergesEveryoneIntoOneSharedWalletWithTheExactSum()
    {
        var kit = Kit();
        kit.Fund(1, 1001);
        kit.Fund(2, 2002);
        kit.Fund(3, 3003);
        Assert.True(kit.Service.PoolDeposit(1, "d1", 101).Ok);
        Assert.Equal(EffectiveCreditMode.PerPlayer, kit.Service.AppliedMode);

        kit.Teams.Set([1], OneTeam); // the "everyone co-op" preset: one team

        Assert.Equal(EffectiveCreditMode.Shared, kit.Service.AppliedMode);
        Assert.Equal(1001 + 2002 + 3003, kit.Balance(WalletId.TeamShared(1)));
        Assert.All([1, 2, 3], p => Assert.Equal(0, kit.Balance(WalletId.Player(p))));
        Assert.Equal(0, kit.Balance(WalletId.TeamPool(1)));
        Assert.Equal(0, kit.Ledger.TotalBalance());
        var tx = kit.Store.Transactions[^1];
        Assert.Equal(TxKind.ModeMigration, tx.Kind);
        Assert.Equal(0, tx.Entries.Sum(e => e.Amount));
        Assert.Equal(WalletId.TeamShared(1), kit.Service.EffectiveWallet(3));
    }

    [Fact]
    public void BackToTwoTeamsSplitsEvenlyWithTheRemainderGoingToThePool()
    {
        var kit = Kit(members: OneTeam, teams: [1]);
        Assert.Equal(EffectiveCreditMode.Shared, kit.Service.AppliedMode);
        kit.Fund(1, 1001); // lands in the shared wallet

        kit.Teams.Set([1, 2], TwoTeams); // players 1,2 in team 1; player 3 in team 2

        Assert.Equal(EffectiveCreditMode.PerPlayer, kit.Service.AppliedMode);
        Assert.Equal(500, kit.Balance(WalletId.Player(1)));
        Assert.Equal(500, kit.Balance(WalletId.Player(2)));
        Assert.Equal(1, kit.Balance(WalletId.TeamPool(1)));
        Assert.Equal(0, kit.Balance(WalletId.Player(3)));
        Assert.Equal(0, kit.Balance(WalletId.TeamShared(1)));
        Assert.Equal(0, kit.Ledger.TotalBalance());
    }

    [Fact]
    public void RemainderGoesToTheLeaderWhenThePoolIsDisabled()
    {
        var kit = Kit(o => o.TeamPoolEnabled = false, OneTeam, [1]);
        kit.Fund(1, 1001);

        kit.Teams.Set([1, 2], TwoTeams);

        Assert.Equal(501, kit.Balance(WalletId.Player(1))); // lowest member id leads
        Assert.Equal(500, kit.Balance(WalletId.Player(2)));
        Assert.Equal(0, kit.Balance(WalletId.TeamPool(1)));
    }

    [Fact]
    public void RunningSwitchWithoutConfirmIsRefusedWithAPreviewThenAppliesWithConfirm()
    {
        var kit = Kit();
        kit.Fund(1, 600);
        kit.Fund(2, 400);
        kit.Fund(3, 50);
        kit.Phase = SessionPhase.Running;
        kit.Options.CreditMode = CreditMode.Shared;

        var refused = kit.Service.Reconcile(confirm: false, actor: "admin:root");

        Assert.Equal(MigrationStatus.ConfirmationRequired, refused.Status);
        Assert.True(refused.Preview.RequiresConfirm);
        Assert.Equal(EffectiveCreditMode.PerPlayer, refused.Preview.From);
        Assert.Equal(EffectiveCreditMode.Shared, refused.Preview.To);
        Assert.Equal(1050, refused.Preview.TotalMoved);
        Assert.Contains(refused.Preview.Changes, c => c.Wallet == WalletId.TeamShared(1) && c.Before == 0 && c.After == 1000);
        Assert.Contains(refused.Preview.Changes, c => c.Wallet == WalletId.Player(1) && c.Before == 600 && c.After == 0);
        Assert.Equal(600, kit.Balance(WalletId.Player(1))); // nothing moved
        Assert.Equal(EffectiveCreditMode.PerPlayer, kit.Service.AppliedMode);
        Assert.True(kit.Service.MigrationPending);
        Assert.Contains(kit.Events.OfType<AlertRaised>(), a => a.Code == "economy_migration_pending");

        var applied = kit.Service.Reconcile(confirm: true, actor: "admin:root");

        Assert.Equal(MigrationStatus.Applied, applied.Status);
        Assert.Equal(1000, kit.Balance(WalletId.TeamShared(1)));
        Assert.Equal(50, kit.Balance(WalletId.TeamShared(2)));
        Assert.False(kit.Service.MigrationPending);
        Assert.Contains(kit.Events.OfType<AlertCleared>(), a => a.Code == "economy_migration_pending");
        Assert.Contains(kit.Events.OfType<AdminActionTaken>(), a => a.Action == "economy.migrate");
        Assert.Single(kit.Events.OfType<EconomyMigrated>());
    }

    [Fact]
    public void TeamChangesWhileRunningWaitUntilConfirmedOrTheSessionStopsRunning()
    {
        var kit = Kit();
        kit.Fund(1, 100);
        kit.Phase = SessionPhase.Running;

        kit.Teams.Set([1], OneTeam); // Auto now resolves to Shared; the change is only pending

        Assert.Equal(EffectiveCreditMode.PerPlayer, kit.Service.AppliedMode);
        Assert.Equal(100, kit.Balance(WalletId.Player(1)));
        Assert.True(kit.Service.MigrationPending);

        kit.Phase = SessionPhase.Stopping;
        kit.Service.Reconcile(confirm: false, actor: "system");

        Assert.Equal(EffectiveCreditMode.Shared, kit.Service.AppliedMode);
        Assert.Equal(100, kit.Balance(WalletId.TeamShared(1)));
    }

    [Fact]
    public void PendingMigrationKeepsBookingToTheWalletsTheLedgerStillFollows()
    {
        var kit = Kit();
        kit.Phase = SessionPhase.Running;
        kit.Options.CreditMode = CreditMode.Shared;
        kit.Service.Reconcile(confirm: false, actor: "system");

        kit.Service.BookCreditDelta(1, false, Delta(70, 1));

        Assert.Equal(70, kit.Balance(WalletId.Player(1)));
        Assert.Equal(0, kit.Balance(WalletId.TeamShared(1)));
    }

    [Fact]
    public void ATeamMoveInPerPlayerModeTakesTheWalletAlongWithoutConfirm()
    {
        var kit = Kit();
        kit.Fund(3, 500);
        kit.Phase = SessionPhase.Running;

        kit.Teams.Set([1, 2], new Dictionary<int, int?> { [1] = 1, [2] = 2, [3] = 1 });

        Assert.Equal(500, kit.Balance(WalletId.Player(3)));
        Assert.Equal(1, kit.Service.AppliedTeamOf(3));
        Assert.False(kit.Service.MigrationPending);
    }

    [Fact]
    public void ATeamMoveInSharedModeBringsNothingAndTakesNothing()
    {
        var kit = Kit(members: new Dictionary<int, int?> { [1] = 1, [2] = 1, [3] = 2 }, teams: [1, 2], configure: o => o.CreditMode = CreditMode.Shared);
        kit.Fund(1, 300);
        kit.Fund(3, 50);
        Assert.Equal(300, kit.Balance(WalletId.TeamShared(1)));

        kit.Teams.Set([1, 2], new Dictionary<int, int?> { [1] = 1, [2] = 2, [3] = 1 });

        Assert.Equal(300, kit.Balance(WalletId.TeamShared(1)));
        Assert.Equal(50, kit.Balance(WalletId.TeamShared(2)));
        Assert.Equal(WalletId.TeamShared(1), kit.Service.EffectiveWallet(3));
    }

    [Fact]
    public void AnUnassignedPlayerJoiningATeamInSharedModeBringsTheirOwnWallet()
    {
        var kit = Kit(members: new Dictionary<int, int?> { [1] = 1 }, teams: [1]);
        kit.Teams.Set([1], new Dictionary<int, int?> { [1] = 1 });
        kit.Teams.Set([1], new Dictionary<int, int?> { [1] = 1, [9] = null });
        kit.Service.EnsurePlayer(9);
        kit.Options.StartingCredits = 0;
        kit.Fund(9, 40); // unassigned: their own wallet
        Assert.Equal(40, kit.Balance(WalletId.Player(9)));

        kit.Teams.Set([1], new Dictionary<int, int?> { [1] = 1, [9] = 1 });

        Assert.Equal(40, kit.Balance(WalletId.TeamShared(1)));
        Assert.Equal(0, kit.Balance(WalletId.Player(9)));
    }

    [Fact]
    public void LayoutSurvivesARestart()
    {
        var kit = Kit();
        kit.Fund(1, 10);
        kit.Teams.Set([1], OneTeam);

        var restarted = new EconomyKit(kit.Options, kit.Store, kit.Teams);

        Assert.Equal(EffectiveCreditMode.Shared, restarted.Service.AppliedMode);
        Assert.Equal(10, restarted.Balance(WalletId.TeamShared(1)));
        Assert.False(restarted.Service.Reconcile(false, "system").Preview.Needed);
    }

    // ------------------------------------------------------------------ overdraft

    [Fact]
    public void AnOverdrawnWalletBlocksOutgoingRequestsUntilItIsBackAtZero()
    {
        var kit = Kit();
        kit.Fund(1, 100);
        Assert.Null(kit.Service.CheckOutgoing(1));

        var spend = kit.Service.BookCreditDelta(1, false, Delta(-300, 1));

        Assert.True(spend.Booked); // the game already spent it: booked anyway
        Assert.Equal(-200, kit.Balance(WalletId.Player(1)));
        var blocked = kit.Service.CheckOutgoing(1);
        Assert.NotNull(blocked);
        Assert.Equal(EconomyReject.InsufficientFunds, blocked.Reason);
        Assert.Equal(EconomyReject.InsufficientFunds, kit.Service.PoolDeposit(1, "x", 10).Reason);
        Assert.Null(kit.Service.CheckOutgoing(2));

        kit.Service.BookCreditDelta(1, false, Delta(250, 2));
        Assert.Equal(50, kit.Balance(WalletId.Player(1)));
        Assert.Null(kit.Service.CheckOutgoing(1));
    }

    [Fact]
    public void OverdraftInSharedModeBlocksEveryTeamMember()
    {
        var kit = Kit(members: OneTeam, teams: [1]);
        kit.Fund(1, 10);

        kit.Service.BookCreditDelta(2, false, Delta(-30, 1));

        Assert.NotNull(kit.Service.CheckOutgoing(1));
        Assert.NotNull(kit.Service.CheckOutgoing(3));
    }

    [Fact]
    public void FrozenEconomyAndFrozenWalletBlockOutgoingRequests()
    {
        var kit = Kit();
        kit.Fund(1, 100);
        kit.Ledger.SetWalletFrozen(WalletId.Player(1), true, "x");
        Assert.Equal(EconomyReject.EconomyFrozen, kit.Service.CheckOutgoing(1)!.Reason);

        kit.Ledger.SetWalletFrozen(WalletId.Player(1), false, null);
        kit.Ledger.Freeze("audit");
        Assert.Equal(EconomyReject.EconomyFrozen, kit.Service.CheckOutgoing(2)!.Reason);
    }

    // ------------------------------------------------------------------ CreditDelta

    [Fact]
    public void ADuplicateCreditDeltaSeqIsBookedOnceAndStillAcked()
    {
        var kit = Kit();
        var first = kit.Service.BookCreditDelta(1, false, Delta(100, 5));
        var dup = kit.Service.BookCreditDelta(1, false, Delta(100, 5));
        var older = kit.Service.BookCreditDelta(1, false, Delta(100, 4));

        Assert.True(first.Booked);
        Assert.Equal(5UL, first.AckedSeq);
        Assert.False(dup.Booked);
        Assert.True(dup.Duplicate);
        Assert.Equal(5UL, dup.AckedSeq);
        Assert.True(older.Duplicate);
        Assert.Equal(100, kit.Balance(WalletId.Player(1)));
        Assert.Single(kit.Store.Transactions);

        // The duplicate still produces a WalletUpdate for the sender, carrying the ack.
        Assert.Contains(kit.Changes, c => c.AckTo == 1 && c.Changed.Count == 0);
        Assert.True(kit.Service.BookCreditDelta(1, false, Delta(7, 6)).Booked);
        Assert.Equal(107, kit.Balance(WalletId.Player(1)));
        Assert.Equal(6UL, kit.Ledger.LastDeltaSeq(1));
    }

    [Fact]
    public void DeltaSequencesAreTrackedPerSendingNodeAndSurviveARestart()
    {
        var kit = Kit();
        kit.Service.BookCreditDelta(1, false, Delta(10, 3));
        kit.Service.BookCreditDelta(2, false, Delta(20, 3)); // same seq, another node: its own sequence

        var restarted = new EconomyKit(kit.Options, kit.Store, kit.Teams);

        Assert.Equal(30, restarted.Balance(WalletId.Player(1)) + restarted.Balance(WalletId.Player(2)));
        Assert.True(restarted.Service.BookCreditDelta(1, false, Delta(10, 3)).Duplicate);
        Assert.Equal(3UL, restarted.Ledger.LastDeltaSeq(2));
    }

    [Fact]
    public void ZeroAndOutOfRangeDeltasAdvanceTheSequenceWithoutBooking()
    {
        var kit = Kit();

        var zero = kit.Service.BookCreditDelta(1, false, Delta(0, 1));
        var huge = kit.Service.BookCreditDelta(1, false, Delta(long.MinValue, 2));

        Assert.False(zero.Booked);
        Assert.False(huge.Booked);
        Assert.Equal(2UL, kit.Ledger.LastDeltaSeq(1));
        Assert.Empty(kit.Store.Transactions);
        Assert.Contains(kit.Events.OfType<AlertRaised>(), a => a.Code == "economy_delta_invalid");
    }

    [Fact]
    public void ClientsBookOnlyToThemselvesTheAuthorityMayNameOthers()
    {
        var kit = Kit();

        kit.Service.BookCreditDelta(2, false, Delta(10, 1, player: 1, team: 2));
        Assert.Equal(10, kit.Balance(WalletId.Player(2)));
        Assert.Equal(0, kit.Balance(WalletId.Player(1)));
        Assert.Equal(0, kit.Balance(WalletId.TeamPool(2)));

        kit.Service.BookCreditDelta(9, true, Delta(25, 1, player: 1));
        Assert.Equal(25, kit.Balance(WalletId.Player(1)));
        Assert.Equal(1UL, kit.Ledger.LastDeltaSeq(9));
    }

    [Fact]
    public void TeamAssetIncomeFromTheAuthorityGoesToThePoolOrTheSharedWalletByMode()
    {
        var perPlayer = Kit();
        perPlayer.Service.BookCreditDelta(9, true, Delta(500, 1, team: 2));
        Assert.Equal(500, perPlayer.Balance(WalletId.TeamPool(2)));

        var shared = Kit(members: OneTeam, teams: [1]);
        shared.Service.BookCreditDelta(9, true, Delta(500, 1, team: 1));
        Assert.Equal(500, shared.Balance(WalletId.TeamShared(1)));
        Assert.Equal(0, shared.Ledger.TotalBalance());
    }

    [Fact]
    public void AFrozenWalletStillBooksDeltasButAFrozenEconomyDoesNot()
    {
        var kit = Kit();
        kit.Fund(1, 100);
        kit.Ledger.SetWalletFrozen(WalletId.Player(1), true, "investigation");

        Assert.True(kit.Service.BookCreditDelta(1, false, Delta(5, 1)).Booked);
        Assert.Equal(105, kit.Balance(WalletId.Player(1)));

        kit.Ledger.Freeze("audit");
        var blocked = kit.Service.BookCreditDelta(1, false, Delta(5, 2));
        Assert.False(blocked.Booked);
        Assert.Equal(1UL, blocked.AckedSeq); // not acked: the node keeps resending it
        Assert.Equal(105, kit.Balance(WalletId.Player(1)));
    }

    [Fact]
    public void IncomeSplitterDivertsAShareInTheSameTransaction()
    {
        var kit = Kit();
        kit.Service.IncomeSplitter = new FixedSplitter(WalletId.Player(2), 250);

        kit.Service.BookCreditDelta(1, false, Delta(1000, 1));

        Assert.Equal(750, kit.Balance(WalletId.Player(1)));
        Assert.Equal(250, kit.Balance(WalletId.Player(2)));
        Assert.Single(kit.Store.Transactions);
        Assert.Equal(0, kit.Ledger.TotalBalance());
    }

    private sealed class FixedSplitter(WalletId to, long amount) : IIncomeSplitter
    {
        public IReadOnlyList<(WalletId To, long Amount)> Split(int? playerId, WalletId target, long income) => [(to, amount)];
    }

    // ------------------------------------------------------------------ pool

    [Fact]
    public void PoolDepositAndWithdrawMoveCreditsAndReplayIsIdempotent()
    {
        var kit = Kit();
        kit.Fund(1, 1000);

        var deposit = kit.Service.PoolDeposit(1, "dep-1", 400);
        var replay = kit.Service.PoolDeposit(1, "dep-1", 400);
        var reuse = kit.Service.PoolDeposit(1, "dep-1", 999);

        Assert.True(deposit.Ok);
        Assert.True(replay.Outcome!.Replayed);
        Assert.Equal(deposit.Outcome!.TxId, replay.Outcome.TxId);
        Assert.False(reuse.Ok);
        Assert.Equal(600, kit.Balance(WalletId.Player(1)));
        Assert.Equal(400, kit.Balance(WalletId.TeamPool(1)));

        Assert.True(kit.Service.PoolWithdraw(2, "wd-1", 150).Ok);
        Assert.Equal(150, kit.Balance(WalletId.Player(2)));
        Assert.Equal(250, kit.Balance(WalletId.TeamPool(1)));
        Assert.Equal(EconomyReject.InsufficientFunds, kit.Service.PoolWithdraw(2, "wd-2", 251).Reason);
        Assert.Equal(0, kit.Ledger.TotalBalance());
    }

    [Fact]
    public void PoolRejectionsFollowTheRules()
    {
        var kit = Kit();
        kit.Fund(1, 1000);

        Assert.Equal(EconomyReject.AmountInvalid, kit.Service.PoolDeposit(1, "a", 0).Reason);
        Assert.Equal(EconomyReject.UnknownPlayer, kit.Service.PoolDeposit(42, "b", 5).Reason);

        kit.Options.TeamPoolEnabled = false;
        Assert.Equal(EconomyReject.PoolDisabled, kit.Service.PoolDeposit(1, "c", 5).Reason);
        kit.Options.TeamPoolEnabled = true;

        kit.Options.PoolWithdrawPolicy = PoolWithdrawPolicy.Disabled;
        Assert.Equal(EconomyReject.PoolPolicyDenied, kit.Service.PoolWithdraw(1, "d", 5).Reason);
        kit.Options.PoolWithdrawPolicy = PoolWithdrawPolicy.LeaderOnly;
        kit.Service.PoolDeposit(1, "e", 100);
        Assert.Equal(EconomyReject.PoolPolicyDenied, kit.Service.PoolWithdraw(2, "f", 5).Reason);
        Assert.True(kit.Service.PoolWithdraw(1, "g", 5).Ok); // player 1 is the lowest id of team 1: the leader

        var shared = Kit(members: OneTeam, teams: [1]);
        Assert.Equal(EconomyReject.NotApplicableInSharedMode, shared.Service.PoolDeposit(1, "h", 5).Reason);
    }

    [Fact]
    public void PoolWithdrawDailyLimitIsRollingAndPerPlayer()
    {
        var kit = Kit(o => o.PoolWithdrawDailyLimitPerPlayer = 300);
        kit.Fund(1, 5000);
        kit.Service.PoolDeposit(1, "dep", 2000);

        Assert.True(kit.Service.PoolWithdraw(2, "w1", 200).Ok);
        var over = kit.Service.PoolWithdraw(2, "w2", 101);
        Assert.Equal(EconomyReject.DailyLimit, over.Reason);
        Assert.True(kit.Service.PoolWithdraw(2, "w3", 100).Ok);
        Assert.True(kit.Service.PoolWithdraw(1, "w4", 300).Ok); // another player has their own allowance
        Assert.Equal(EconomyReject.DailyLimit, kit.Service.PoolWithdraw(2, "w5", 1).Reason);

        Assert.True(kit.Service.PoolWithdraw(2, "w1", 200).Outcome!.Replayed); // a replay is never refused by the limit

        kit.Time.Advance(TimeSpan.FromHours(24.5));
        Assert.True(kit.Service.PoolWithdraw(2, "w6", 300).Ok);
    }

    // ------------------------------------------------------------------ save money

    [Fact]
    public void SaveMoneyGoesToTheAuthoritysTeamPoolOrSharedWalletOnce()
    {
        var kit = Kit();
        var seeded = kit.Service.SeedSaveMoney(5_000_000, authorityPlayerId: 3, actor: "system");
        Assert.True(seeded.Ok);
        Assert.Equal(5_000_000, kit.Balance(WalletId.TeamPool(2)));

        var again = kit.Service.SeedSaveMoney(5_000_000, 3, "system");
        Assert.True(again.Outcome!.Replayed);
        Assert.Equal(5_000_000, kit.Balance(WalletId.TeamPool(2)));

        var shared = Kit(members: OneTeam, teams: [1]);
        shared.Service.SeedSaveMoney(777, 2, "system");
        Assert.Equal(777, shared.Balance(WalletId.TeamShared(1)));
        Assert.Equal(0, shared.Ledger.TotalBalance());
    }

    [Fact]
    public void SaveMoneyHonoursTheConfiguredInheritTeamAndSplitMode()
    {
        var inherit = Kit(o => o.InheritTeam = 1);
        inherit.Service.SeedSaveMoney(100, authorityPlayerId: 3, actor: "system");
        Assert.Equal(100, inherit.Balance(WalletId.TeamPool(1)));

        var split = Kit(o => o.SaveMoneyDistribution = SaveMoneyDistribution.SplitAmongPlayers);
        split.Service.SeedSaveMoney(1000, authorityPlayerId: 1, actor: "system");
        Assert.Equal(333, split.Balance(WalletId.Player(1)));
        Assert.Equal(333, split.Balance(WalletId.Player(2)));
        Assert.Equal(333, split.Balance(WalletId.Player(3)));
        Assert.Equal(1, split.Balance(WalletId.TeamPool(1))); // remainder to the inheriting team
        Assert.Equal(0, split.Ledger.TotalBalance());
    }

    // ------------------------------------------------------------------ module-less default

    [Fact]
    public void WithoutATeamDirectoryEveryoneIsOneImplicitTeamAndAutoResolvesToShared()
    {
        var store = new InMemoryEconomyStore();
        var ledger = new EconomyLedger(1, store);
        var service = new EconomyService(ledger, store, teams: null, () => new EconomyOptions { StartingCredits = 10 });
        service.Start();

        service.EnsurePlayer(4);
        service.EnsurePlayer(5);

        Assert.Equal(EffectiveCreditMode.Shared, service.AppliedMode);
        Assert.Equal(20, ledger.BalanceOf(WalletId.TeamShared(1)));
    }
}
