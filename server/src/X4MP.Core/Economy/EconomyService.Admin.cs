using System.Globalization;
using X4MP.Core.Events;
using X4MP.Proto;

namespace X4MP.Core.Economy;

/// <summary>Why an admin economy action was refused. The REST layer maps each to a 4xx problem code of the same name.</summary>
public enum EconomyAdminError
{
    None,
    InvalidAmount,
    UnknownWallet,
    UnknownTransaction,
    UnknownLoan,

    /// <summary>The wallet kind cannot be adjusted or frozen by hand (escrow and the world counter-party).</summary>
    NotAdjustable,

    /// <summary>The posting would take a non-World wallet below zero and <c>force</c> was not set (or the wallet may never go negative).</summary>
    WouldOverdraw,

    AlreadyReversed,

    /// <summary>Escrow-internal, a reversal, a migration, or a transaction tied to state the ledger cannot rewind.</summary>
    NotReversible,

    WrongState,
    EconomyFrozen,
    StoreFailure,

    /// <summary>The Idempotency-Key was used before with a different request.</summary>
    RequestIdReuse,
}

/// <summary>The result of an admin economy action: the error (None = success) and what changed.</summary>
public sealed record EconomyAdminResult(
    EconomyAdminError Error,
    string? Detail = null,
    LedgerTransaction? Transaction = null,
    WalletState? Wallet = null,
    LoanRecord? Loan = null)
{
    public bool Ok => Error == EconomyAdminError.None;

    public static EconomyAdminResult Fail(EconomyAdminError error, string? detail = null) => new(error, detail);
}

/// <summary>Figures for the admin overview (<c>GET /economy/summary</c> and the hub's 1 Hz push).</summary>
public sealed record EconomyTotals(
    long MoneySupply, long InEscrow, int OpenLoans, int OverdueLoans, long OutstandingDebt, int OpenTrades, int InDoubtTrades, int FrozenWallets);

/// <summary>
/// Admin actions on the economy (server-design 2.14, M1-E6): manual adjustments, wallet freezes, reversals, loan cancel with a
/// disbursement reversal and the figures for the overview. Called on the actor thread. Every money-changing action publishes an
/// <see cref="AdminActionTaken"/> (the audit forwarder writes the <c>audit_log</c> row, with the reason and never a secret).
/// </summary>
public sealed partial class EconomyService
{
    /// <summary>Adjusts a wallet by hand: positive credits it, negative debits it, against the World wallet (<see cref="TxKind.AdminAdjust"/>).</summary>
    /// <param name="force">Allows a debit to take a player or team-shared wallet below zero (it is then flagged overdrawn).</param>
    /// <param name="idempotencyKey">Optional <c>Idempotency-Key</c> of the admin request: a replay returns the first transaction and moves nothing.</param>
    public EconomyAdminResult AdminAdjust(string actor, WalletId wallet, long amount, string reason, bool force = false, string? idempotencyKey = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(actor);
        var key = idempotencyKey is null ? null : "admin:" + idempotencyKey;
        var hash = PayloadHasher.Hash("AdminAdjust", wallet.ToString(), amount, reason, force);
        if (key is not null && _ledger.FindReplay(0, key, hash) is { } replay)
        {
            return replay.Ok
                ? new EconomyAdminResult(EconomyAdminError.None, null, replay.TxId is { } id ? _ledger.FindTransaction(id) : null, _ledger.Find(wallet))
                : EconomyAdminResult.Fail(EconomyAdminError.RequestIdReuse, replay.Detail);
        }

        if (amount == 0 || amount == long.MinValue || Math.Abs(amount) > EconomyLedger.MaxAmount)
        {
            return EconomyAdminResult.Fail(EconomyAdminError.InvalidAmount, "use a non-zero amount of at most 10^15");
        }

        if (wallet.Kind is WalletKind.Escrow or WalletKind.World)
        {
            return EconomyAdminResult.Fail(EconomyAdminError.NotAdjustable, "escrow and world wallets cannot be adjusted by hand");
        }

        if (!WalletExists(wallet))
        {
            return EconomyAdminResult.Fail(EconomyAdminError.UnknownWallet, wallet.ToString());
        }

        PostEntry[] entries = amount > 0
            ? [new(WalletId.World, -amount), new(wallet, amount)]
            : [new(wallet, amount), new(WalletId.World, -amount)];
        var outcome = _ledger.Post(new PostRequest
        {
            Kind = TxKind.AdminAdjust,
            Actor = actor,
            RequestId = key,
            RequestType = key is null ? null : "AdminAdjust",
            PayloadHash = key is null ? null : hash,
            Entries = entries,
            RefType = "Admin",
            Note = reason,
            Flags = PostOptions.BypassWalletFreeze | (force ? PostOptions.AllowOverdraw : PostOptions.None),
        });
        if (!outcome.Ok)
        {
            return FromRejected(outcome);
        }

        Finish(outcome, LedgerReason.AdminAdjust, string.Empty, null);
        PublishAdmin(actor, "economy.adjust", "wallet:" + wallet, reason, new Dictionary<string, string?>
        {
            ["amount"] = amount.ToString(CultureInfo.InvariantCulture),
            ["tx"] = outcome.TxId,
            ["force"] = force ? "true" : null,
        });
        return new EconomyAdminResult(EconomyAdminError.None, null, _ledger.FindTransaction(outcome.TxId!), _ledger.Find(wallet));
    }

    /// <summary>Freezes or unfreezes one wallet. A frozen wallet rejects player requests (it can neither send nor receive them) but still books game deltas.</summary>
    public EconomyAdminResult AdminSetFrozen(string actor, WalletId wallet, bool frozen, string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(actor);
        if (wallet.Kind is WalletKind.Escrow or WalletKind.World)
        {
            return EconomyAdminResult.Fail(EconomyAdminError.NotAdjustable, "escrow and world wallets cannot be frozen");
        }

        if (!WalletExists(wallet))
        {
            return EconomyAdminResult.Fail(EconomyAdminError.UnknownWallet, wallet.ToString());
        }

        if (!_ledger.SetWalletFrozen(wallet, frozen, reason))
        {
            return EconomyAdminResult.Fail(EconomyAdminError.StoreFailure, "the wallet could not be saved");
        }

        PublishAdmin(actor, frozen ? "economy.wallet.freeze" : "economy.wallet.unfreeze", "wallet:" + wallet, reason, null);
        return new EconomyAdminResult(EconomyAdminError.None, null, null, _ledger.Find(wallet));
    }

    /// <summary>
    /// Reverses a committed transaction with a <see cref="TxKind.Reversal"/> that negates its entries and links both ways. At most once.
    /// Refused when it would take a wallet below zero, unless <paramref name="force"/> is set (player and shared wallets then go overdrawn).
    /// A trade settlement is reversed in credits only (back to the payer), and the trade is flagged <see cref="TradeRecord.Reversed"/>.
    /// </summary>
    public EconomyAdminResult AdminReverse(string actor, string txId, string reason, bool force = false, string? idempotencyKey = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(actor);
        ArgumentException.ThrowIfNullOrEmpty(txId);
        var key = idempotencyKey is null ? null : "admin:" + idempotencyKey;
        var hash = PayloadHasher.Hash("AdminReverse", txId, reason, force);
        if (key is not null && _ledger.FindReplay(0, key, hash) is { } replay)
        {
            return replay.Ok
                ? new EconomyAdminResult(EconomyAdminError.None, null, replay.TxId is { } id ? _ledger.FindTransaction(id) : null)
                : EconomyAdminResult.Fail(EconomyAdminError.RequestIdReuse, replay.Detail);
        }

        var original = _ledger.FindTransaction(txId);
        if (original is null)
        {
            return EconomyAdminResult.Fail(EconomyAdminError.UnknownTransaction, txId);
        }

        return ReverseCore(actor, original, reason, force, allowLoanDisbursement: false, key, hash);
    }

    private EconomyAdminResult ReverseCore(
        string actor, LedgerTransaction original, string reason, bool force, bool allowLoanDisbursement, string? key = null, byte[]? hash = null)
    {
        if (original.ReversedBy is not null)
        {
            return EconomyAdminResult.Fail(EconomyAdminError.AlreadyReversed, "reversed by " + original.ReversedBy);
        }

        var refusal = original.Kind switch
        {
            TxKind.Reversal => "a reversal cannot be reversed",
            TxKind.ModeMigration or TxKind.TeamMove => "migrations follow the credit layout and cannot be reversed",
            TxKind.LoanEscrow or TxKind.LoanRefund or TxKind.TradeEscrow or TxKind.TradeRefund => "escrow movements are internal to a loan or trade: cancel or resolve it instead",
            TxKind.LoanDisburse when !allowLoanDisbursement => "cancel the loan with reverseDisbursement instead",
            _ => null,
        };
        if (refusal is not null)
        {
            return EconomyAdminResult.Fail(EconomyAdminError.NotReversible, refusal);
        }

        // Negate every entry. The escrow side of a loan disbursement or trade settlement goes back to whoever funded the escrow.
        var sums = new SortedDictionary<WalletId, long>(Comparer<WalletId>.Create(CompareWallets));
        TradeRecord? trade = null;
        foreach (var entry in original.Entries)
        {
            var wallet = entry.Wallet;
            if (wallet.Kind == WalletKind.Escrow)
            {
                if (original.Kind == TxKind.LoanDisburse && original.RefId is { } loanId && FindLoan(loanId) is { } loan)
                {
                    wallet = EffectiveWallet(loan.LenderId);
                }
                else if (original.Kind == TxKind.TradeSettle && original.RefId is { } tradeId && _trades.TryGetValue(tradeId, out trade))
                {
                    wallet = EffectiveWallet(PayerOf(trade));
                }
                else
                {
                    return EconomyAdminResult.Fail(EconomyAdminError.NotReversible, "the escrow of this transaction is no longer known");
                }
            }

            sums[wallet] = sums.GetValueOrDefault(wallet) - entry.Amount;
        }

        var entries = sums.Where(kv => kv.Value != 0).Select(kv => new PostEntry(kv.Key, kv.Value)).ToList();
        if (entries.Count < 2)
        {
            return EconomyAdminResult.Fail(EconomyAdminError.NotReversible, "nothing would move: both sides are one wallet");
        }

        var outcome = _ledger.Post(new PostRequest
        {
            Kind = TxKind.Reversal,
            Actor = actor,
            RequestId = key,
            RequestType = key is null ? null : "AdminReverse",
            PayloadHash = key is null ? null : hash,
            Entries = entries,
            RefType = original.RefType,
            RefId = original.RefId,
            Reverses = original.Id,
            Note = reason,
            Flags = PostOptions.BypassWalletFreeze | (force ? PostOptions.AllowOverdraw : PostOptions.None),
        });
        if (!outcome.Ok)
        {
            return FromRejected(outcome);
        }

        Finish(outcome, LedgerReason.AdminAdjust, string.Empty, null);
        if (trade is not null)
        {
            trade.Reversed = true;
            trade.UpdatedAt = _time.GetUtcNow();
            Persist(trade);
            RaiseTradeChanged(trade, trade.State);
        }

        PublishAdmin(actor, "economy.reverse", "tx:" + original.Id, reason, new Dictionary<string, string?>
        {
            ["reversal"] = outcome.TxId,
            ["kind"] = original.Kind.ToString(),
            ["force"] = force ? "true" : null,
        });
        return new EconomyAdminResult(EconomyAdminError.None, null, _ledger.FindTransaction(outcome.TxId!));
    }

    /// <summary>
    /// Admin cancel of a loan, optionally taking the disbursed principal back from the borrower first. When the reversal is refused
    /// (<see cref="EconomyAdminError.WouldOverdraw"/>), nothing changes and the loan stays as it was.
    /// </summary>
    public EconomyAdminResult AdminCancelLoan(string actor, long loanId, string reason, bool reverseDisbursement, bool force)
    {
        var loan = FindLoan(loanId);
        if (loan is null)
        {
            return EconomyAdminResult.Fail(EconomyAdminError.UnknownLoan, LoanText.Id(loanId));
        }

        if (!loan.IsOpen)
        {
            return EconomyAdminResult.Fail(EconomyAdminError.WrongState, loan.State.ToString());
        }

        if (reverseDisbursement && loan.IsRunning)
        {
            var found = _ledger.QueryTransactions(new LedgerQuery(Kind: TxKind.LoanDisburse, RefType: "Loan", RefId: loanId, Limit: 1));
            var disbursement = found.Count > 0 ? found[0] : null;
            if (disbursement is not null)
            {
                var reversed = ReverseCore(actor, disbursement, "cancel of loan " + LoanText.Id(loanId) + ": " + reason, force, allowLoanDisbursement: true);
                if (!reversed.Ok)
                {
                    return reversed;
                }
            }
        }

        var result = AdminCancelLoan(actor, loanId, reason);
        return result.Ok
            ? new EconomyAdminResult(EconomyAdminError.None, null, null, null, result.Loan)
            : EconomyAdminResult.Fail(result.Reason == EconomyReject.EconomyFrozen ? EconomyAdminError.EconomyFrozen : EconomyAdminError.WrongState, result.Detail);
    }

    /// <summary>The figures of the overview, computed from the cached wallets, loans and trades.</summary>
    public EconomyTotals Totals()
    {
        long escrow = 0;
        var frozen = 0;
        foreach (var wallet in _ledger.Wallets)
        {
            if (wallet.Id.Kind == WalletKind.Escrow)
            {
                escrow += wallet.Balance;
            }

            if (wallet.Frozen)
            {
                frozen++;
            }
        }

        var open = _ledger.Loans.Where(l => l.IsOpen).ToList();
        return new EconomyTotals(
            -_ledger.BalanceOf(WalletId.World),
            escrow,
            open.Count,
            open.Count(l => l.State == LoanState.Overdue),
            open.Where(l => l.IsRunning).Sum(l => l.Outstanding),
            _trades.Values.Count(t => t.IsOpen),
            _trades.Values.Count(t => t.State == TradeState.InDoubt),
            frozen);
    }

    private bool WalletExists(WalletId wallet) =>
        _ledger.Find(wallet) is not null
        || wallet.Kind switch
        {
            WalletKind.Player => IsKnown((int)wallet.OwnerId),
            WalletKind.TeamShared or WalletKind.TeamPool => TeamIds.Contains((int)wallet.OwnerId),
            _ => false,
        };

    private static EconomyAdminResult FromRejected(PostOutcome outcome) => outcome.Reason switch
    {
        PostReject.InsufficientFunds => EconomyAdminResult.Fail(EconomyAdminError.WouldOverdraw, outcome.Detail),
        PostReject.EconomyFrozen or PostReject.WalletFrozen => EconomyAdminResult.Fail(EconomyAdminError.EconomyFrozen, outcome.Detail),
        PostReject.AmountInvalid or PostReject.InvalidEntries or PostReject.Unbalanced => EconomyAdminResult.Fail(EconomyAdminError.InvalidAmount, outcome.Detail),
        _ => EconomyAdminResult.Fail(EconomyAdminError.StoreFailure, outcome.Detail),
    };

    private void PublishAdmin(string actor, string action, string target, string reason, Dictionary<string, string?>? data)
    {
        var all = new Dictionary<string, string?>(StringComparer.Ordinal) { ["reason"] = reason };
        if (data is not null)
        {
            foreach (var (key, value) in data)
            {
                all[key] = value;
            }
        }

        _events?.Publish(new AdminActionTaken(_time.GetUtcNow(), _ledger.SessionId, actor, action, target, all, null));
    }
}
