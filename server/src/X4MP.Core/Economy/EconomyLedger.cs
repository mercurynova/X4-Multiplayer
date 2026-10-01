using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using X4MP.Core.Events;

namespace X4MP.Core.Economy;

/// <summary>Why a posting was refused.</summary>
public enum PostReject
{
    None,
    EconomyFrozen,
    WalletFrozen,
    InsufficientFunds,
    Unbalanced,
    InvalidEntries,
    AmountInvalid,
    PayloadMismatch,
    StoreFailure,
}

public enum PostStatus
{
    Committed,
    Rejected,
}

[Flags]
public enum PostOptions
{
    None = 0,

    /// <summary>The game already spent the money: a player or team-shared wallet may go negative (and is flagged overdrawn).</summary>
    AllowOverdraw = 1,

    /// <summary>Book even though a touched wallet is frozen (a frozen wallet still receives and records game income/spend).</summary>
    BypassWalletFreeze = 2,
}

/// <summary>One requested line: positive credits a wallet, negative debits it.</summary>
public readonly record struct PostEntry(WalletId Wallet, long Amount);

/// <summary>A posting the ledger validates, commits and acknowledges.</summary>
public sealed record PostRequest
{
    public required TxKind Kind { get; init; }

    /// <summary><c>player:&lt;id&gt;</c>, <c>authority</c>, <c>admin:&lt;user&gt;</c> or <c>system</c>. Derived server-side, never from a payload.</summary>
    public required string Actor { get; init; }

    /// <summary>Lines summing to zero. May be empty only when <see cref="Layout"/> (or <see cref="DeltaSeq"/>) alone is committed.</summary>
    public required IReadOnlyList<PostEntry> Entries { get; init; }

    /// <summary>Player the idempotency key is scoped to (the sender).</summary>
    public int PlayerId { get; init; }

    /// <summary>Client request key. With <see cref="PayloadHash"/> it makes the call idempotent.</summary>
    public string? RequestId { get; init; }

    public string? RequestType { get; init; }

    public byte[]? PayloadHash { get; init; }

    public string? RefType { get; init; }

    public long? RefId { get; init; }

    public string? Reverses { get; init; }

    public string? Note { get; init; }

    public PostOptions Flags { get; init; }

    /// <summary>Persisted atomically with the transaction (mode migrations and team moves).</summary>
    public EconomyLayout? Layout { get; init; }

    /// <summary>Highest <c>CreditDelta.seq</c> of a sending node covered by this posting.</summary>
    public (int PlayerId, ulong Seq)? DeltaSeq { get; init; }

    /// <summary>Loan rows (new or changed) persisted atomically with the transaction. A posting may carry only loans (no entries).</summary>
    public IReadOnlyList<LoanRecord>? Loans { get; init; }
}

/// <summary>The result of a posting. A replay carries the identical outcome with <see cref="Replayed"/> set.</summary>
public sealed record PostOutcome(
    PostStatus Status,
    PostReject Reason,
    string? TxId,
    IReadOnlyList<WalletBalanceAfter> Balances,
    string? Detail = null)
{
    [JsonIgnore]
    public bool Replayed { get; init; }

    public bool Ok => Status == PostStatus.Committed;

    public static PostOutcome Reject(PostReject reason, string? detail = null) =>
        new(PostStatus.Rejected, reason, null, [], detail);
}

/// <summary>Raised when the economy froze (auditor violation or admin action).</summary>
public sealed record EconomyFrozen(DateTimeOffset At, long? Session, string Reason, IReadOnlyList<string> Violations) : DomainEvent(At, Session);

/// <summary>An admin acknowledged the freeze and lifted it.</summary>
public sealed record EconomyUnfrozen(DateTimeOffset At, long? Session, string Actor) : DomainEvent(At, Session);

/// <summary>
/// The double-entry ledger of one session (server-design 2.14): wallet cache, validation, synchronous commit and
/// idempotency. Runs on the session actor's thread, so it has no locks. Every <see cref="Post"/> is durable before it
/// returns.
/// </summary>
public sealed class EconomyLedger
{
    public const long MaxAmount = 1_000_000_000_000_000; // 10^15, far below long.MaxValue so sums cannot overflow

    private static readonly JsonSerializerOptions ResultJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IEconomyStore _store;
    private readonly TimeProvider _time;
    private readonly IEventPublisher? _events;
    private readonly Dictionary<WalletId, WalletState> _wallets = [];
    private readonly Dictionary<int, ulong> _deltaSeqs = [];
    private readonly Dictionary<long, LoanRecord> _loans = [];
    private readonly Ulid _ulid = new();
    private EconomyLayout _layout = new(null, new Dictionary<int, int?>());

    public EconomyLedger(long sessionId, IEconomyStore store, TimeProvider? time = null, IEventPublisher? events = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        SessionId = sessionId;
        _store = store;
        _time = time ?? TimeProvider.System;
        _events = events;
    }

    public long SessionId { get; }

    public bool IsFrozen { get; private set; }

    public string? FreezeReason { get; private set; }

    /// <summary>Requests rejected because a request key was reused with a different payload.</summary>
    public long RequestIdReuseCount { get; private set; }

    public EconomyLayout Layout => _layout;

    public IReadOnlyCollection<WalletState> Wallets => _wallets.Values;

    /// <summary>Every loan of the session (open and closed), as committed.</summary>
    public IReadOnlyCollection<LoanRecord> Loans => _loans.Values;

    /// <summary>Raised after a posting that carried loans is committed and cached: (loan after, loan before or null). Not raised for replays.</summary>
    public event Action<LoanRecord, LoanRecord?>? LoanCommitted;

    public LoanRecord? FindLoan(long id) => _loans.GetValueOrDefault(id);

    /// <summary>Loads the persisted wallets, delta sequences and layout. Call once, before the first posting.</summary>
    public void Load()
    {
        var loaded = _store.Load(SessionId);
        _wallets.Clear();
        foreach (var wallet in loaded.Wallets)
        {
            _wallets[wallet.Id] = wallet;
        }

        _deltaSeqs.Clear();
        foreach (var (player, seq) in loaded.DeltaSeqs)
        {
            _deltaSeqs[player] = seq;
        }

        _layout = loaded.Layout;
        _loans.Clear();
        foreach (var loan in loaded.Loans ?? [])
        {
            _loans[loan.Id] = loan;
        }
    }

    /// <summary>The wallet, or null if nothing was ever booked to it.</summary>
    public WalletState? Find(WalletId id) => _wallets.GetValueOrDefault(id);

    /// <summary>Balance of a wallet (0 if it does not exist yet).</summary>
    public long BalanceOf(WalletId id) => _wallets.TryGetValue(id, out var w) ? w.Balance : 0;

    /// <summary>Highest <c>CreditDelta.seq</c> booked for a sending node (0 = none).</summary>
    public ulong LastDeltaSeq(int senderPlayerId) => _deltaSeqs.GetValueOrDefault(senderPlayerId);

    /// <summary>Sum of every wallet balance. Zero while the ledger is sound.</summary>
    public long TotalBalance()
    {
        long sum = 0;
        foreach (var wallet in _wallets.Values)
        {
            sum = checked(sum + wallet.Balance);
        }

        return sum;
    }

    /// <summary>Validates, commits (durably) and applies a posting.</summary>
    public PostOutcome Post(PostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scoped = request.RequestId is not null && request.PayloadHash is not null;
        if (scoped)
        {
            var existing = _store.FindRequest(SessionId, request.PlayerId, request.RequestId!);
            if (existing is not null)
            {
                if (existing.PayloadHash.AsSpan().SequenceEqual(request.PayloadHash))
                {
                    var stored = JsonSerializer.Deserialize<PostOutcome>(existing.ResultJson, ResultJson)!;
                    return stored with { Replayed = true };
                }

                RequestIdReuseCount++;
                return PostOutcome.Reject(PostReject.PayloadMismatch, "request id reused with a different payload");
            }
        }

        if (IsFrozen)
        {
            return PostOutcome.Reject(PostReject.EconomyFrozen, FreezeReason);
        }

        var entries = request.Entries;
        var movesMoney = entries.Count > 0;
        if (movesMoney)
        {
            var invalid = ValidateShape(entries);
            if (invalid is not null)
            {
                return invalid;
            }
        }
        else if (request.Layout is null && request.DeltaSeq is null && request.Loans is null)
        {
            return PostOutcome.Reject(PostReject.InvalidEntries, "empty posting");
        }

        var now = _time.GetUtcNow();
        var changed = new List<WalletState>(entries.Count);
        var ledgerEntries = new List<LedgerEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var current = _wallets.GetValueOrDefault(entry.Wallet);
            var next = current?.Clone() ?? new WalletState(entry.Wallet);
            if (next.Frozen && (request.Flags & PostOptions.BypassWalletFreeze) == 0)
            {
                return PostOutcome.Reject(PostReject.WalletFrozen, entry.Wallet.ToString());
            }

            next.Balance = checked(next.Balance + entry.Amount); // |balance| <= 10^15 * entries: cannot overflow in practice
            next.Version++;
            if (next.Balance < 0 && !MayGoNegative(next.Id.Kind, request.Flags))
            {
                return PostOutcome.Reject(PostReject.InsufficientFunds, entry.Wallet.ToString());
            }

            changed.Add(next);
            ledgerEntries.Add(new LedgerEntry(entry.Wallet, entry.Amount, next.Balance));
        }

        LedgerTransaction? transaction = null;
        if (movesMoney)
        {
            transaction = new LedgerTransaction(
                _ulid.Next(now), SessionId, now, request.Kind, request.Actor, request.RequestId, request.RefType, request.RefId,
                request.Reverses, request.Note, ledgerEntries);
        }

        var outcome = new PostOutcome(
            PostStatus.Committed, PostReject.None, transaction?.Id,
            changed.Select(w => new WalletBalanceAfter(w.Id, w.Balance, w.Version)).ToArray());

        EconomyRequestRecord? record = null;
        if (scoped)
        {
            record = new EconomyRequestRecord(
                SessionId, request.PlayerId, request.RequestId!, request.RequestType ?? request.Kind.ToString(),
                request.PayloadHash!, JsonSerializer.Serialize(outcome, ResultJson), now);
        }

        var layout = request.Layout;
        try
        {
            _store.Commit(new EconomyCommit(SessionId, transaction, changed, record, request.DeltaSeq, layout, now, request.Loans));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PostOutcome.Reject(PostReject.StoreFailure, ex.Message);
        }

        // Durable: now the cache follows.
        var wasOverdrawn = new List<bool>(changed.Count);
        foreach (var next in changed)
        {
            wasOverdrawn.Add(_wallets.TryGetValue(next.Id, out var before) && before.Overdrawn);
            _wallets[next.Id] = next;
        }

        if (request.DeltaSeq is { } delta)
        {
            _deltaSeqs[delta.PlayerId] = delta.Seq;
        }

        if (layout is not null)
        {
            _layout = layout;
        }

        for (var i = 0; i < changed.Count; i++)
        {
            PublishOverdraft(changed[i], wasOverdrawn[i], now);
        }

        var committedLoans = new List<(LoanRecord New, LoanRecord? Old)>();
        foreach (var loan in request.Loans ?? [])
        {
            committedLoans.Add((loan, _loans.GetValueOrDefault(loan.Id)));
            _loans[loan.Id] = loan;
        }

        foreach (var (loan, old) in committedLoans)
        {
            LoanCommitted?.Invoke(loan, old);
        }

        return outcome;
    }

    /// <summary>Freezes every posting until <see cref="Unfreeze"/> (the auditor calls this on a violation).</summary>
    public void Freeze(string reason, IReadOnlyList<string>? violations = null)
    {
        IsFrozen = true;
        FreezeReason = reason;
        var now = _time.GetUtcNow();
        _events?.Publish(new EconomyFrozen(now, SessionId, reason, violations ?? []));
        _events?.Publish(new AlertRaised(now, SessionId, AlertSeverity.Critical, "economy_frozen", $"Economy frozen: {reason}"));
    }

    /// <summary>An admin acknowledged the freeze.</summary>
    public void Unfreeze(string actor)
    {
        if (!IsFrozen)
        {
            return;
        }

        IsFrozen = false;
        FreezeReason = null;
        var now = _time.GetUtcNow();
        _events?.Publish(new EconomyUnfrozen(now, SessionId, actor));
        _events?.Publish(new AlertCleared(now, SessionId, "economy_frozen"));
        _events?.Publish(new AdminActionTaken(now, SessionId, actor, "economy.unfreeze", null, null, null));
    }

    /// <summary>Freezes or unfreezes one wallet (admin). A frozen wallet rejects requests but still books game deltas.</summary>
    public bool SetWalletFrozen(WalletId id, bool frozen, string? reason)
    {
        var next = _wallets.GetValueOrDefault(id)?.Clone() ?? new WalletState(id);
        next.Frozen = frozen;
        next.FrozenReason = frozen ? reason : null;
        try
        {
            _store.Commit(new EconomyCommit(SessionId, null, [next], null, null, null, _time.GetUtcNow()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        _wallets[id] = next;
        return true;
    }

    private static PostOutcome? ValidateShape(IReadOnlyList<PostEntry> entries)
    {
        if (entries.Count < 2)
        {
            return PostOutcome.Reject(PostReject.InvalidEntries, "a transaction needs at least two entries");
        }

        long sum = 0;
        var seen = new HashSet<WalletId>();
        foreach (var entry in entries)
        {
            if (entry.Amount == 0 || entry.Amount > MaxAmount || entry.Amount < -MaxAmount)
            {
                return PostOutcome.Reject(PostReject.AmountInvalid, entry.Wallet.ToString());
            }

            if (!seen.Add(entry.Wallet))
            {
                return PostOutcome.Reject(PostReject.InvalidEntries, "duplicate wallet " + entry.Wallet);
            }

            if (entry.Wallet.Kind == WalletKind.World && entry.Wallet.OwnerId != 0)
            {
                return PostOutcome.Reject(PostReject.InvalidEntries, "world wallet owner must be 0");
            }

            sum = checked(sum + entry.Amount);
        }

        return sum == 0 ? null : PostOutcome.Reject(PostReject.Unbalanced, "entries sum to " + sum.ToString(CultureInfo.InvariantCulture));
    }

    private static bool MayGoNegative(WalletKind kind, PostOptions flags) => kind switch
    {
        WalletKind.World => true,
        WalletKind.Player or WalletKind.TeamShared => (flags & PostOptions.AllowOverdraw) != 0,
        _ => false,
    };

    private void PublishOverdraft(WalletState next, bool wasOverdrawn, DateTimeOffset now)
    {
        if (_events is null || next.Overdrawn == wasOverdrawn)
        {
            return;
        }

        var code = $"economy_overdrawn_{next.Id.Kind}_{next.Id.OwnerId.ToString(CultureInfo.InvariantCulture)}";
        if (next.Overdrawn)
        {
            _events.Publish(new AlertRaised(now, SessionId, AlertSeverity.Warning, code,
                $"Wallet {next.Id} is overdrawn ({next.Balance.ToString(CultureInfo.InvariantCulture)}); outgoing actions are blocked until it is back at 0."));
        }
        else
        {
            _events.Publish(new AlertCleared(now, SessionId, code));
        }
    }
}
