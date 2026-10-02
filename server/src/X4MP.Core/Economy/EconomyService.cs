using System.Globalization;
using X4MP.Core.Events;
using X4MP.Core.Session;
using X4MP.Core.Teams;
using X4MP.Proto;

namespace X4MP.Core.Economy;

/// <summary>Outcome of an economy action: a wire <see cref="EconomyReject"/> reason (None = success) and the posting.</summary>
public sealed record EconomyActionResult(EconomyReject Reason, PostOutcome? Outcome, string? Detail = null)
{
    public bool Ok => Reason == EconomyReject.None;

    public static EconomyActionResult Rejected(EconomyReject reason, string? detail = null) => new(reason, null, detail);
}

/// <summary>Balances that changed, to be sent as <c>WalletUpdate</c> frames by the module.</summary>
/// <param name="Changed">Every wallet touched.</param>
/// <param name="Reason">The wire reason.</param>
/// <param name="Ref">Request key or 0.</param>
/// <param name="AckTo">The node that sent a <c>CreditDelta</c>: it always gets an update carrying <c>acked_delta_seq</c>.</param>
public sealed record WalletChange(IReadOnlyList<WalletBalanceAfter> Changed, LedgerReason Reason, Id128T Ref, int? AckTo);

/// <summary>What a mode switch or team move would do (the GUI shows it before the admin confirms).</summary>
/// <param name="Needed">The layout differs from the desired one.</param>
/// <param name="RequiresConfirm">Applying it while the session is live needs an explicit confirm.</param>
/// <param name="Changes">Wallet balances before and after (only wallets that change).</param>
public sealed record MigrationPreview(
    bool Needed,
    bool RequiresConfirm,
    EffectiveCreditMode From,
    EffectiveCreditMode To,
    TxKind Kind,
    IReadOnlyList<WalletPreview> Changes,
    long TotalMoved,
    IReadOnlyList<string> TeamMoves);

public sealed record WalletPreview(WalletId Wallet, long Before, long After);

public enum MigrationStatus
{
    NotNeeded,
    Applied,

    /// <summary>The 409-equivalent: the session is live, nothing changed, <see cref="MigrationResult.Preview"/> says what confirming does.</summary>
    ConfirmationRequired,
    Failed,
}

public sealed record MigrationResult(MigrationStatus Status, MigrationPreview Preview, PostOutcome? Outcome);

/// <summary>An applied migration (the audit trail and GUI feed).</summary>
public sealed record EconomyMigrated(
    DateTimeOffset At, long? Session, string Actor, string From, string To, long TotalMoved, string TxId) : DomainEvent(At, Session);

/// <summary>Result of booking one <c>CreditDelta</c>.</summary>
/// <param name="Booked">Money moved in this call.</param>
/// <param name="Duplicate">The <c>seq</c> was already booked; nothing was done.</param>
/// <param name="AckedSeq">The highest <c>seq</c> of the sending node the ledger holds (what <c>WalletUpdate.acked_delta_seq</c> carries).</param>
public sealed record CreditDeltaResult(bool Booked, bool Duplicate, ulong AckedSeq, PostOutcome? Outcome, string? Detail);

/// <summary>Lets M1-E4 divert a share of booked game income (loan auto-repay) inside the same transaction.</summary>
public interface IIncomeSplitter
{
    /// <summary>Transfers out of <paramref name="target"/> (to another wallet) for <paramref name="income"/> credits just earned.</summary>
    IReadOnlyList<(WalletId To, long Amount)> Split(int? playerId, WalletId target, long income);

    /// <summary>Loan changes (auto-repay) computed by the last <see cref="Split"/>; posted in the same transaction as the income. Clears them.</summary>
    IReadOnlyList<LoanRecord>? TakeChanges() => null;
}

/// <summary>
/// Credit modes and the operations on top of the ledger (server-design 2.14, M1-E2): mode resolution, starting credits,
/// the team pool, mode and team-move migrations with preview/confirm, <c>CreditDelta</c> booking with per-node
/// <c>seq</c> dedupe, and the overdraft rules. Runs on the actor thread.
/// </summary>
public sealed partial class EconomyService
{
    private readonly EconomyLedger _ledger;
    private readonly IEconomyStore _store;
    private readonly ITeamDirectory? _teams;
    private readonly Func<EconomyOptions> _options;
    private readonly TimeProvider _time;
    private readonly IEventPublisher? _events;
    private readonly Func<SessionPhase> _phase;
    private readonly Func<int, int?> _leaderOf;
    private readonly EconomyRateLimiter _rate;
    private bool _pendingAlertRaised;

    /// <param name="teams">The team directory; null means a single implicit team 1 containing everybody (no team module).</param>
    /// <param name="leaderOf">The leader of a team (for <see cref="PoolWithdrawPolicy.LeaderOnly"/> and the Shared-to-PerPlayer remainder). Default: the lowest member id.</param>
    public EconomyService(
        EconomyLedger ledger,
        IEconomyStore store,
        ITeamDirectory? teams,
        Func<EconomyOptions> options,
        TimeProvider? time = null,
        IEventPublisher? events = null,
        Func<SessionPhase>? phase = null,
        Func<int, int?>? leaderOf = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        _ledger = ledger;
        _store = store;
        _teams = teams;
        _options = options;
        _time = time ?? TimeProvider.System;
        _events = events;
        _phase = phase ?? (() => SessionPhase.Idle);
        _leaderOf = leaderOf ?? DefaultLeader;
        _rate = new EconomyRateLimiter(_time);
        InitLoans();
    }

    public EconomyLedger Ledger => _ledger;

    /// <summary>Set by M1-E4 to divert income (loan auto-repay).</summary>
    public IIncomeSplitter? IncomeSplitter { get; set; }

    /// <summary>Receives every change to wallet balances (the module turns it into <c>WalletUpdate</c> frames).</summary>
    public Action<WalletChange>? Changed { get; set; }

    /// <summary>True while a mode switch or team move waits for an admin confirm.</summary>
    public bool MigrationPending { get; private set; }

    /// <summary>The mode the balances currently follow (before the first reconcile: the desired one).</summary>
    public EffectiveCreditMode AppliedMode =>
        Enum.TryParse<EffectiveCreditMode>(_ledger.Layout.AppliedMode, out var mode) ? mode : DesiredMode;

    /// <summary>The mode the settings and the number of teams ask for.</summary>
    public EffectiveCreditMode DesiredMode => Resolve(_options().CreditMode, TeamCount);

    /// <summary>Auto resolves to Shared iff exactly one team exists.</summary>
    public static EffectiveCreditMode Resolve(CreditMode mode, int teamCount) => mode switch
    {
        CreditMode.PerPlayer => EffectiveCreditMode.PerPlayer,
        CreditMode.Shared => EffectiveCreditMode.Shared,
        _ => teamCount == 1 ? EffectiveCreditMode.Shared : EffectiveCreditMode.PerPlayer,
    };

    // ------------------------------------------------------------------ start and layout

    /// <summary>Loads the ledger and aligns the layout with the directory (applies a pending migration when nothing is live).</summary>
    public void Start()
    {
        _ledger.Load();
        Reconcile(confirm: false, actor: "system");
    }

    /// <summary>The wallet a player's game money lives in under the layout the balances currently follow.</summary>
    public WalletId EffectiveWallet(int playerId)
    {
        if (AppliedMode == EffectiveCreditMode.Shared && AppliedTeamOf(playerId) is { } team)
        {
            return WalletId.TeamShared(team);
        }

        return WalletId.Player(playerId);
    }

    /// <summary>The team of a player as the balances currently see it (null = unassigned or unknown).</summary>
    public int? AppliedTeamOf(int playerId) =>
        _ledger.Layout.PlayerTeams.TryGetValue(playerId, out var team) ? team : null;

    /// <summary>True when the player was registered with the economy.</summary>
    public bool IsKnown(int playerId) => _ledger.Layout.PlayerTeams.ContainsKey(playerId);

    /// <summary>
    /// Registers a player on first sight and credits <see cref="EconomyOptions.StartingCredits"/> once to the effective
    /// wallet (in Shared mode that is the team wallet: each member adds the starting credits). Idempotent.
    /// </summary>
    public PostOutcome EnsurePlayer(int playerId)
    {
        if (IsKnown(playerId))
        {
            return new PostOutcome(PostStatus.Committed, PostReject.None, null, []) { Replayed = true };
        }

        var team = TeamOf(playerId);
        var layout = WithPlayer(_ledger.Layout, playerId, team);
        var starting = _options().StartingCredits;
        var target = AppliedMode == EffectiveCreditMode.Shared && team is { } t ? WalletId.TeamShared(t) : WalletId.Player(playerId);
        PostOutcome outcome;
        if (starting > 0)
        {
            outcome = _ledger.Post(new PostRequest
            {
                Kind = TxKind.StartingCredits,
                Actor = "system",
                PlayerId = playerId,
                RequestId = "starting-credits",
                RequestType = "StartingCredits",
                PayloadHash = PayloadHasher.Hash("StartingCredits", playerId),
                Entries = [new(WalletId.World, -starting), new(target, starting)],
                Layout = layout,
                Flags = PostOptions.BypassWalletFreeze,
            });
        }
        else
        {
            outcome = _ledger.Post(new PostRequest { Kind = TxKind.StartingCredits, Actor = "system", Entries = [], Layout = layout });
        }

        if (outcome.Ok && !outcome.Replayed && outcome.Balances.Count > 0)
        {
            Notify(outcome, LedgerReason.GameIncome, default, null);
        }

        return outcome;
    }

    /// <summary>The balances a player may see: own wallet plus the team wallets of the layout.</summary>
    public IReadOnlyList<WalletBalanceAfter> VisibleBalances(int playerId)
    {
        var list = new List<WalletBalanceAfter>();
        foreach (var wallet in _ledger.Wallets.OrderBy(w => w.Id.Kind).ThenBy(w => w.Id.OwnerId))
        {
            if (IsVisibleTo(playerId, wallet.Id))
            {
                list.Add(new WalletBalanceAfter(wallet.Id, wallet.Balance, wallet.Version));
            }
        }

        return list;
    }

    /// <summary>Own player wallet, and the shared wallet and pool of the player's team.</summary>
    public bool IsVisibleTo(int playerId, WalletId wallet) => wallet.Kind switch
    {
        WalletKind.Player => wallet.OwnerId == playerId,
        WalletKind.TeamShared or WalletKind.TeamPool => AppliedTeamOf(playerId) is { } team && team == wallet.OwnerId,
        _ => false,
    };

    // ------------------------------------------------------------------ overdraft gate

    /// <summary>
    /// Null when the player may start an outgoing action; otherwise why not: the economy is frozen, the effective wallet is
    /// frozen, or it is overdrawn (the game spent more than the ledger held; blocked until it is back at 0).
    /// </summary>
    public EconomyActionResult? CheckOutgoing(int playerId)
    {
        if (_ledger.IsFrozen)
        {
            return EconomyActionResult.Rejected(EconomyReject.EconomyFrozen, _ledger.FreezeReason);
        }

        var wallet = _ledger.Find(EffectiveWallet(playerId));
        if (wallet is null)
        {
            return null;
        }

        if (wallet.Frozen)
        {
            return EconomyActionResult.Rejected(EconomyReject.EconomyFrozen, "wallet frozen");
        }

        return wallet.Overdrawn
            ? EconomyActionResult.Rejected(EconomyReject.InsufficientFunds, "wallet overdrawn")
            : null;
    }

    // ------------------------------------------------------------------ pool

    public EconomyActionResult PoolDeposit(int playerId, string requestKey, long amount)
    {
        var hash = PayloadHasher.Hash("PoolDeposit", amount);
        if (IsStoredRequest(playerId, requestKey))
        {
            return Finish(_ledger.Post(Build(TxKind.PoolDeposit, "PoolDeposit", playerId, requestKey, hash, [], null)), LedgerReason.PoolDeposit, requestKey, null);
        }

        var gate = PoolGate(playerId, amount, out var team);
        if (gate is not null)
        {
            return gate;
        }

        var outgoing = CheckOutgoing(playerId);
        if (outgoing is not null)
        {
            return outgoing;
        }

        var outcome = _ledger.Post(new PostRequest
        {
            Kind = TxKind.PoolDeposit,
            Actor = Actor(playerId),
            PlayerId = playerId,
            RequestId = requestKey,
            RequestType = "PoolDeposit",
            PayloadHash = hash,
            Entries = [new(WalletId.Player(playerId), -amount), new(WalletId.TeamPool(team), amount)],
        });
        var result = Finish(outcome, LedgerReason.PoolDeposit, requestKey, null);
        if (outcome.Ok && !outcome.Replayed)
        {
            PublishCompleted("PoolDeposit", playerId, null, amount, outcome, requestKey);
        }

        return result;
    }

    public EconomyActionResult PoolWithdraw(int playerId, string requestKey, long amount)
    {
        var hash = PayloadHasher.Hash("PoolWithdraw", amount);
        if (IsStoredRequest(playerId, requestKey))
        {
            return Finish(_ledger.Post(Build(TxKind.PoolWithdraw, "PoolWithdraw", playerId, requestKey, hash, [], null)), LedgerReason.PoolWithdraw, requestKey, null);
        }

        var gate = PoolGate(playerId, amount, out var team);
        if (gate is not null)
        {
            return gate;
        }

        var options = _options();
        if (options.PoolWithdrawPolicy == PoolWithdrawPolicy.Disabled
            || (options.PoolWithdrawPolicy == PoolWithdrawPolicy.LeaderOnly && _leaderOf(team) != playerId))
        {
            return EconomyActionResult.Rejected(EconomyReject.PoolPolicyDenied, options.PoolWithdrawPolicy.ToString());
        }

        // A replay must return the stored result even when the daily limit has been used up since.
        var limit = options.PoolWithdrawDailyLimitPerPlayer;
        if (limit > 0)
        {
            var used = _store.SumInflow(_ledger.SessionId, TxKind.PoolWithdraw, Actor(playerId), _time.GetUtcNow() - TimeSpan.FromHours(24));
            if (used + amount > limit)
            {
                return EconomyActionResult.Rejected(EconomyReject.DailyLimit, $"{used} of {limit} used in the last 24 h");
            }
        }

        var outcome = _ledger.Post(new PostRequest
        {
            Kind = TxKind.PoolWithdraw,
            Actor = Actor(playerId),
            PlayerId = playerId,
            RequestId = requestKey,
            RequestType = "PoolWithdraw",
            PayloadHash = hash,
            Entries = [new(WalletId.TeamPool(team), -amount), new(WalletId.Player(playerId), amount)],
        });
        var result = Finish(outcome, LedgerReason.PoolWithdraw, requestKey, null);
        if (outcome.Ok && !outcome.Replayed)
        {
            PublishCompleted("PoolWithdraw", playerId, null, amount, outcome, requestKey);
        }

        return result;
    }

    private bool IsStoredRequest(int playerId, string requestKey) =>
        _store.FindRequest(_ledger.SessionId, playerId, requestKey) is not null;

    private EconomyActionResult? PoolGate(int playerId, long amount, out int team)
    {
        team = 0;
        if (_ledger.IsFrozen)
        {
            return EconomyActionResult.Rejected(EconomyReject.EconomyFrozen, _ledger.FreezeReason);
        }

        if (amount <= 0)
        {
            return EconomyActionResult.Rejected(EconomyReject.AmountInvalid);
        }

        if (amount > _options().MaxSingleTransfer)
        {
            return EconomyActionResult.Rejected(EconomyReject.OverMaxAmount);
        }

        if (!IsKnown(playerId))
        {
            return EconomyActionResult.Rejected(EconomyReject.UnknownPlayer);
        }

        if (AppliedMode == EffectiveCreditMode.Shared)
        {
            return EconomyActionResult.Rejected(EconomyReject.NotApplicableInSharedMode);
        }

        if (AppliedTeamOf(playerId) is not { } t)
        {
            return EconomyActionResult.Rejected(EconomyReject.NotTeammate, "player has no team");
        }

        if (!_options().TeamPoolEnabled)
        {
            return EconomyActionResult.Rejected(EconomyReject.PoolDisabled);
        }

        team = t;
        return null;
    }

    // ------------------------------------------------------------------ CreditDelta

    /// <summary>
    /// Books a <c>CreditDelta</c> (game income or spend) to the effective wallet of the player, or to the team wallet for
    /// team-owned income. A <c>seq</c> already booked is ignored (booked once); the result always carries the acked
    /// sequence. Frozen wallets still book; a frozen economy does not (the delta stays unacked and the node resends it).
    /// </summary>
    /// <param name="senderPlayerId">The sending node (derived from the connection).</param>
    /// <param name="senderIsAuthority">Only the authority may name another player or a team.</param>
    public CreditDeltaResult BookCreditDelta(int senderPlayerId, bool senderIsAuthority, CreditDeltaT delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        var last = _ledger.LastDeltaSeq(senderPlayerId);
        if (delta.Seq != 0 && delta.Seq <= last)
        {
            Notify(null, LedgerReason.GameIncome, delta.RequestKey, senderPlayerId);
            return new CreditDeltaResult(false, true, last, null, "duplicate seq");
        }

        int? player = null;
        WalletId target;
        if (senderIsAuthority && delta.PlayerId == 0 && delta.TeamId != 0)
        {
            target = AppliedMode == EffectiveCreditMode.Shared ? WalletId.TeamShared(delta.TeamId) : WalletId.TeamPool(delta.TeamId);
        }
        else
        {
            var who = senderIsAuthority && delta.PlayerId != 0 ? delta.PlayerId : senderPlayerId;
            player = who;
            target = EffectiveWallet(who);
        }

        (int, ulong)? seqUpdate = delta.Seq != 0 ? (senderPlayerId, delta.Seq) : null;
        var amount = delta.Amount;
        var reason = amount >= 0 ? LedgerReason.GameIncome : LedgerReason.GameSpend;
        var invalid = amount == long.MinValue || Math.Abs(amount) > EconomyLedger.MaxAmount;
        if (amount == 0 || invalid)
        {
            // Nothing to book; still advance the sequence so the node stops resending it.
            var ackOnly = _ledger.Post(new PostRequest
            {
                Kind = TxKind.GameIncome, Actor = Actor(senderPlayerId), Entries = [], DeltaSeq = seqUpdate,
            });
            if (invalid)
            {
                _events?.Publish(new AlertRaised(_time.GetUtcNow(), _ledger.SessionId, AlertSeverity.Warning, "economy_delta_invalid",
                    $"CreditDelta from player {senderPlayerId} with amount {amount.ToString(CultureInfo.InvariantCulture)} ignored"));
            }

            Notify(null, reason, delta.RequestKey, senderPlayerId);
            return new CreditDeltaResult(false, false, _ledger.LastDeltaSeq(senderPlayerId), ackOnly, invalid ? "amount out of range" : "zero amount");
        }

        var entries = new List<PostEntry>();
        if (amount > 0)
        {
            entries.Add(new PostEntry(WalletId.World, -amount));
            entries.Add(new PostEntry(target, amount));
            if (IncomeSplitter?.Split(player, target, amount) is { } extras)
            {
                var diverted = 0L;
                foreach (var (to, share) in extras)
                {
                    if (share > 0 && share <= amount - diverted)
                    {
                        entries.Add(new PostEntry(to, share));
                        diverted += share;
                    }
                }

                if (diverted > 0)
                {
                    entries[1] = new PostEntry(target, amount - diverted);
                    if (entries[1].Amount == 0)
                    {
                        entries.RemoveAt(1);
                    }
                }
            }
        }
        else
        {
            entries.Add(new PostEntry(target, amount));
            entries.Add(new PostEntry(WalletId.World, -amount));
        }

        var outcome = _ledger.Post(new PostRequest
        {
            Kind = amount > 0 ? TxKind.GameIncome : TxKind.GameSpend,
            Actor = Actor(senderPlayerId),
            PlayerId = senderPlayerId,
            Entries = entries,
            DeltaSeq = seqUpdate,
            RefType = "CreditDelta",
            RefId = (long)delta.RefEventSeq,
            Note = delta.Source.ToString(),
            Flags = PostOptions.AllowOverdraw | PostOptions.BypassWalletFreeze,
            Loans = amount > 0 ? IncomeSplitter?.TakeChanges() : null,
        });
        if (!outcome.Ok)
        {
            return new CreditDeltaResult(false, false, _ledger.LastDeltaSeq(senderPlayerId), outcome, outcome.Reason.ToString());
        }

        Notify(outcome, reason, delta.RequestKey, senderPlayerId);
        return new CreditDeltaResult(true, false, _ledger.LastDeltaSeq(senderPlayerId), outcome, null);
    }

    // ------------------------------------------------------------------ save money

    /// <summary>
    /// Seeds the money stored in the save once per session (ADR-033: <c>inherit_team</c>). It goes to the inheriting team's
    /// shared wallet (Shared) or pool (PerPlayer), or is split among the known players per
    /// <see cref="EconomyOptions.SaveMoneyDistribution"/>. A second call returns the stored result.
    /// </summary>
    /// <param name="authorityPlayerId">Used when <see cref="EconomyOptions.InheritTeam"/> is 0.</param>
    public EconomyActionResult SeedSaveMoney(long amount, int? authorityPlayerId, string actor)
    {
        if (amount <= 0 || amount > EconomyLedger.MaxAmount)
        {
            return EconomyActionResult.Rejected(EconomyReject.AmountInvalid);
        }

        var options = _options();
        var team = options.InheritTeam != 0 ? options.InheritTeam : authorityPlayerId is { } a ? AppliedTeamOf(a) ?? TeamOf(a) : null;
        var entries = new List<PostEntry>();
        long remaining = amount;
        if (options.SaveMoneyDistribution == SaveMoneyDistribution.SplitAmongPlayers && _ledger.Layout.PlayerTeams.Count > 0)
        {
            var players = _ledger.Layout.PlayerTeams.Keys.Order().ToList();
            var share = amount / players.Count;
            if (share > 0)
            {
                var perWallet = new SortedDictionary<WalletId, long>(Comparer<WalletId>.Create(CompareWallets));
                foreach (var p in players)
                {
                    var wallet = EffectiveWallet(p);
                    perWallet[wallet] = perWallet.GetValueOrDefault(wallet) + share;
                    remaining -= share;
                }

                entries.AddRange(perWallet.Select(kv => new PostEntry(kv.Key, kv.Value)));
            }
        }

        if (remaining > 0)
        {
            WalletId dest;
            if (team is { } t)
            {
                dest = AppliedMode == EffectiveCreditMode.Shared ? WalletId.TeamShared(t) : WalletId.TeamPool(t);
            }
            else if (authorityPlayerId is { } p)
            {
                dest = EffectiveWallet(p);
            }
            else
            {
                return EconomyActionResult.Rejected(EconomyReject.UnknownPlayer, "no inheriting team or authority");
            }

            var existing = entries.FindIndex(e => e.Wallet == dest);
            if (existing >= 0)
            {
                entries[existing] = new PostEntry(dest, entries[existing].Amount + remaining);
            }
            else
            {
                entries.Add(new PostEntry(dest, remaining));
            }
        }

        entries.Insert(0, new PostEntry(WalletId.World, -amount));
        var outcome = _ledger.Post(new PostRequest
        {
            Kind = TxKind.SaveMoney,
            Actor = actor,
            PlayerId = 0,
            RequestId = "save-money",
            RequestType = "SaveMoney",
            PayloadHash = PayloadHasher.Hash("SaveMoney", amount),
            Entries = entries,
            Flags = PostOptions.BypassWalletFreeze,
            Note = "inherit_team",
        });
        return Finish(outcome, LedgerReason.GameIncome, string.Empty, null);
    }

    // ------------------------------------------------------------------ migrations

    /// <summary>What switching to the desired mode and moving players to their current teams would do.</summary>
    public MigrationPreview PreviewMigration() => BuildPlan().Preview;

    /// <summary>
    /// Brings the balances in line with the desired mode and the directory. While the session is live (Running, Paused) a
    /// change of mode, or a team move that moves money, needs <paramref name="confirm"/>; without it nothing changes and
    /// <see cref="MigrationStatus.ConfirmationRequired"/> carries the preview (the REST layer answers 409). Called with
    /// <c>confirm: false</c> from the module on every team or settings change.
    /// </summary>
    public MigrationResult Reconcile(bool confirm, string actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var plan = BuildPlan();
        if (!plan.Preview.Needed)
        {
            ClearPending();
            return new MigrationResult(MigrationStatus.NotNeeded, plan.Preview, null);
        }

        if (plan.Preview.RequiresConfirm && IsLive && !confirm)
        {
            MigrationPending = true;
            if (!_pendingAlertRaised)
            {
                _pendingAlertRaised = true;
                _events?.Publish(new AlertRaised(_time.GetUtcNow(), _ledger.SessionId, AlertSeverity.Warning, "economy_migration_pending",
                    $"Credit layout change ({plan.Preview.From} to {plan.Preview.To}) waits for an admin to confirm."));
            }

            return new MigrationResult(MigrationStatus.ConfirmationRequired, plan.Preview, null);
        }

        var outcome = _ledger.Post(new PostRequest
        {
            Kind = plan.Preview.Kind,
            Actor = actor,
            Entries = plan.Entries,
            Layout = plan.Layout,
            Flags = PostOptions.AllowOverdraw | PostOptions.BypassWalletFreeze,
            Note = plan.Preview.Kind == TxKind.ModeMigration ? $"{plan.Preview.From} -> {plan.Preview.To}" : "team move",
        });
        if (!outcome.Ok)
        {
            return new MigrationResult(MigrationStatus.Failed, plan.Preview, outcome);
        }

        ClearPending();
        if (plan.Entries.Count > 0)
        {
            Notify(outcome, LedgerReason.ModeMigration, default, null);
        }

        var now = _time.GetUtcNow();
        if (plan.Entries.Count > 0 || plan.Preview.From != plan.Preview.To)
        {
            _events?.Publish(new EconomyMigrated(now, _ledger.SessionId, actor, plan.Preview.From.ToString(), plan.Preview.To.ToString(),
                plan.Preview.TotalMoved, outcome.TxId ?? string.Empty));
        }

        if (actor.StartsWith("admin:", StringComparison.Ordinal))
        {
            _events?.Publish(new AdminActionTaken(now, _ledger.SessionId, actor, "economy.migrate", null,
                new Dictionary<string, string?>
                {
                    ["from"] = plan.Preview.From.ToString(),
                    ["to"] = plan.Preview.To.ToString(),
                    ["moved"] = plan.Preview.TotalMoved.ToString(CultureInfo.InvariantCulture),
                }, null));
        }

        return new MigrationResult(MigrationStatus.Applied, plan.Preview, outcome);
    }

    private bool IsLive => _phase() is SessionPhase.Running or SessionPhase.Paused;

    private void ClearPending()
    {
        if (_pendingAlertRaised)
        {
            _pendingAlertRaised = false;
            _events?.Publish(new AlertCleared(_time.GetUtcNow(), _ledger.SessionId, "economy_migration_pending"));
        }

        MigrationPending = false;
    }

    private sealed record Plan(MigrationPreview Preview, IReadOnlyList<PostEntry> Entries, EconomyLayout Layout);

    private Plan BuildPlan()
    {
        var options = _options();
        var layout = _ledger.Layout;
        var desired = DesiredMode;
        var firstTime = layout.AppliedMode is null;
        var applied = firstTime ? desired : AppliedMode;
        var poolEnabled = options.TeamPoolEnabled;

        var balances = new Dictionary<WalletId, long>();
        long Get(WalletId id) => balances.TryGetValue(id, out var v) ? v : _ledger.BalanceOf(id);
        void Add(WalletId id, long delta) => balances[id] = Get(id) + delta;

        var newTeams = new Dictionary<int, int?>();
        foreach (var player in layout.PlayerTeams.Keys)
        {
            newTeams[player] = TeamOf(player);
        }

        // 1. Team moves under the mode the balances follow now.
        var teamMoves = new List<string>();
        foreach (var (player, oldTeam) in layout.PlayerTeams.OrderBy(kv => kv.Key))
        {
            var newTeam = newTeams[player];
            if (oldTeam == newTeam)
            {
                continue;
            }

            teamMoves.Add($"player {player}: team {oldTeam?.ToString(CultureInfo.InvariantCulture) ?? "none"} -> {newTeam?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
            if (applied == EffectiveCreditMode.Shared && oldTeam is null && newTeam is { } joined)
            {
                // First team of a player in Shared mode: whatever sits in their own wallet joins the team wallet.
                var own = Get(WalletId.Player(player));
                if (own != 0)
                {
                    Add(WalletId.Player(player), -own);
                    Add(WalletId.TeamShared(joined), own);
                }
            }

            // PerPlayer: the wallet goes with the player. Shared (team to team, or leaving): nothing moves.
        }

        // 2. Mode change.
        var modeChanged = !firstTime && applied != desired;
        if (modeChanged)
        {
            var walletTeams = _ledger.Wallets
                .Where(w => w.Id.Kind is WalletKind.TeamPool or WalletKind.TeamShared)
                .Select(w => (int)w.Id.OwnerId);
            var teams = newTeams.Values.OfType<int>().Concat(TeamIds).Concat(walletTeams).Distinct().Order().ToList();
            foreach (var team in teams)
            {
                var members = newTeams.Where(kv => kv.Value == team).Select(kv => kv.Key).Order().ToList();
                if (desired == EffectiveCreditMode.Shared)
                {
                    var total = Get(WalletId.TeamPool(team));
                    Add(WalletId.TeamPool(team), -total);
                    foreach (var member in members)
                    {
                        var own = Get(WalletId.Player(member));
                        total += own;
                        Add(WalletId.Player(member), -own);
                    }

                    Add(WalletId.TeamShared(team), total);
                }
                else
                {
                    var shared = Get(WalletId.TeamShared(team));
                    Add(WalletId.TeamShared(team), -shared);
                    if (members.Count == 0)
                    {
                        Add(WalletId.TeamPool(team), shared);
                        continue;
                    }

                    var share = shared / members.Count;
                    var remainder = shared - (share * members.Count);
                    foreach (var member in members)
                    {
                        Add(WalletId.Player(member), share);
                    }

                    if (remainder != 0)
                    {
                        // Positive remainder to the pool, or to the leader if the pool is off; a debt cannot go to a pool.
                        if (poolEnabled && remainder > 0)
                        {
                            Add(WalletId.TeamPool(team), remainder);
                        }
                        else
                        {
                            Add(WalletId.Player(_leaderOf(team) is { } l && members.Contains(l) ? l : members[0]), remainder);
                        }
                    }
                }
            }
        }

        var entries = balances
            .Select(kv => new PostEntry(kv.Key, kv.Value - _ledger.BalanceOf(kv.Key)))
            .Where(e => e.Amount != 0)
            .OrderBy(e => e.Wallet.Kind)
            .ThenBy(e => e.Wallet.OwnerId)
            .ToList();
        var changes = entries
            .Select(e => new WalletPreview(e.Wallet, _ledger.BalanceOf(e.Wallet), _ledger.BalanceOf(e.Wallet) + e.Amount))
            .ToList();
        var moved = entries.Where(e => e.Amount > 0).Sum(e => e.Amount);

        var layoutChanged = firstTime || modeChanged || teamMoves.Count > 0;
        var needsConfirm = modeChanged; // team moves never need one: PerPlayer wallets follow the player, Shared moves nothing
        var kind = modeChanged ? TxKind.ModeMigration : TxKind.TeamMove;
        var preview = new MigrationPreview(layoutChanged, needsConfirm, applied, desired, kind, changes, moved, teamMoves);
        return new Plan(preview, entries, new EconomyLayout(desired.ToString(), newTeams));
    }

    private static int CompareWallets(WalletId a, WalletId b)
    {
        var kind = a.Kind.CompareTo(b.Kind);
        return kind != 0 ? kind : a.OwnerId.CompareTo(b.OwnerId);
    }

    // ------------------------------------------------------------------ helpers

    private int TeamCount => _teams?.Teams.Count ?? 1;

    private IEnumerable<int> TeamIds => _teams is null ? [1] : _teams.Teams.Select(t => t.TeamId);

    private int? TeamOf(int playerId) => _teams is null ? 1 : _teams.TeamOf(playerId);

    private int? DefaultLeader(int teamId)
    {
        var members = _ledger.Layout.PlayerTeams.Where(kv => kv.Value == teamId).Select(kv => kv.Key).ToList();
        return members.Count == 0 ? null : members.Min();
    }

    private static string Actor(int playerId) => string.Create(CultureInfo.InvariantCulture, $"player:{playerId}");

    private static EconomyLayout WithPlayer(EconomyLayout layout, int playerId, int? team)
    {
        var teams = new Dictionary<int, int?>(layout.PlayerTeams) { [playerId] = team };
        return layout with { PlayerTeams = teams };
    }

    private EconomyActionResult Finish(PostOutcome outcome, LedgerReason reason, string requestKey, int? ackTo)
    {
        if (outcome.Ok)
        {
            if (!outcome.Replayed)
            {
                Notify(outcome, reason, default, ackTo);
            }

            return new EconomyActionResult(EconomyReject.None, outcome);
        }

        var code = outcome.Reason switch
        {
            PostReject.InsufficientFunds => EconomyReject.InsufficientFunds,
            PostReject.EconomyFrozen or PostReject.WalletFrozen => EconomyReject.EconomyFrozen,
            PostReject.AmountInvalid or PostReject.Unbalanced or PostReject.InvalidEntries => EconomyReject.AmountInvalid,
            PostReject.PayloadMismatch => EconomyReject.RequestIdReuse,
            _ => EconomyReject.Timeout,
        };
        return new EconomyActionResult(code, outcome, outcome.Detail ?? requestKey);
    }

    private void Notify(PostOutcome? outcome, LedgerReason reason, Id128T? key, int? ackTo) =>
        Changed?.Invoke(new WalletChange(outcome?.Balances ?? [], reason, key ?? new Id128T(), ackTo));
}
