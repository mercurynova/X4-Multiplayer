using System.Globalization;
using X4MP.Core.Events;
using X4MP.Proto;

namespace X4MP.Core.Economy;

/// <summary>
/// Loans (server-design 2.14 "Loan", roadmap M1-E4). The principal sits in <c>Escrow(loan)</c> from the offer until the
/// borrower answers; every state change is one ledger posting that also carries the new loan row, so money and loan state
/// are committed together or not at all. v1 only tracks overdue loans (ADR-040): no automatic collection or punishment.
/// </summary>
public sealed partial class EconomyService
{
    private const int MaxOfferTtlSeconds = 7 * 24 * 3600;

    /// <summary>The current time of the service's clock (the loan timers compare against it).</summary>
    internal DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Every loan of the session, open and closed.</summary>
    public IReadOnlyCollection<LoanRecord> Loans => _ledger.Loans;

    public LoanRecord? FindLoan(long id) => _ledger.FindLoan(id);

    /// <summary>Raised after every committed loan change (the module turns it into <c>LoanStatus</c> frames and notices).</summary>
    public Action<LoanChange>? LoanChanged { get; set; }

    /// <summary>Open loans (offered, active, overdue) a player is part of, as lender or borrower.</summary>
    public int OpenLoanCount(int playerId) => _ledger.Loans.Count(l => l.IsOpen && l.InvolvesPlayer(playerId));

    private void InitLoans()
    {
        IncomeSplitter = new LoanIncomeSplitter(this);
        _ledger.LoanCommitted += OnLoanCommitted;
    }

    private long NextLoanId() => _ledger.Loans.Count == 0 ? 1 : _ledger.Loans.Max(l => l.Id) + 1;

    // ------------------------------------------------------------------ offer

    /// <summary>
    /// <c>LoanOffer</c>: moves <paramref name="principal"/> from the lender's effective wallet into the loan's escrow and
    /// creates the loan as Offered. <paramref name="repayTotal"/> is principal plus the flat interest.
    /// </summary>
    public LoanResult OfferLoan(
        int lenderId, string requestKey, int borrowerId, long principal, long repayTotal, int dueInSeconds, int offerTtlSeconds, int autoRepayPercent, string? memo = null)
    {
        var note = memo is { Length: > MaxMemoLength } ? memo[..MaxMemoLength] : memo;
        var hash = PayloadHasher.Hash("LoanOffer", borrowerId, principal, repayTotal, dueInSeconds, offerTtlSeconds, autoRepayPercent, memo ?? string.Empty);

        if (IsStoredRequest(lenderId, requestKey))
        {
            var replay = Finish(_ledger.Post(Build(TxKind.LoanEscrow, "LoanOffer", lenderId, requestKey, hash, [], note)), LedgerReason.LoanPrincipal, requestKey, null);
            var existing = _ledger.Loans.FirstOrDefault(l => l.LenderId == lenderId && l.OfferRequestKey == requestKey);
            return LoanResult.From(replay, replay.Ok ? existing : null);
        }

        var options = _options();
        if (_ledger.IsFrozen)
        {
            return LoanResult.Rejected(EconomyReject.EconomyFrozen, _ledger.FreezeReason);
        }

        if (principal <= 0 || repayTotal < principal || autoRepayPercent is < 0 or > 100 || dueInSeconds < 0 || repayTotal > EconomyLedger.MaxAmount)
        {
            return LoanResult.Rejected(EconomyReject.AmountInvalid);
        }

        if (principal > options.MaxLoanPrincipal)
        {
            return LoanResult.Rejected(EconomyReject.OverMaxAmount, options.MaxLoanPrincipal.ToString(CultureInfo.InvariantCulture));
        }

        var extra = (Int128)(repayTotal - principal);
        if (extra * 10_000 > (Int128)principal * options.MaxLoanInterestBp)
        {
            return LoanResult.Rejected(EconomyReject.AmountInvalid, $"interest above the limit of {options.MaxLoanInterestBp.ToString(CultureInfo.InvariantCulture)} basis points");
        }

        if (!IsKnown(lenderId) || !IsKnown(borrowerId))
        {
            return LoanResult.Rejected(EconomyReject.UnknownPlayer, IsKnown(lenderId) ? "unknown borrower" : null);
        }

        if (lenderId == borrowerId)
        {
            return LoanResult.Rejected(EconomyReject.NotParty, "lender and borrower are the same player");
        }

        var scope = LoanScopeVerdict(options, lenderId, borrowerId);
        if (scope is not null)
        {
            return LoanResult.Rejected(scope.Reason, scope.Detail);
        }

        var from = EffectiveWallet(lenderId);
        var to = EffectiveWallet(borrowerId);
        if (from == to)
        {
            return LoanResult.Rejected(EconomyReject.NotApplicableInSharedMode, "both players use the same wallet");
        }

        if (from.Kind == WalletKind.TeamShared && options.SharedWalletSpend == SharedWalletSpendPolicy.LeaderOnly
            && AppliedTeamOf(lenderId) is { } team && _leaderOf(team) != lenderId)
        {
            return LoanResult.Rejected(EconomyReject.NotParty, "only the team leader may spend the shared wallet");
        }

        var outgoing = CheckOutgoing(lenderId);
        if (outgoing is not null)
        {
            return LoanResult.Rejected(outgoing.Reason, outgoing.Detail);
        }

        if (_ledger.Find(to) is { Frozen: true })
        {
            return LoanResult.Rejected(EconomyReject.EconomyFrozen, "borrower wallet frozen");
        }

        if (OpenLoanCount(lenderId) >= options.MaxOpenLoansPerPlayer || OpenLoanCount(borrowerId) >= options.MaxOpenLoansPerPlayer)
        {
            return LoanResult.Rejected(EconomyReject.TooManyOpen, $"at most {options.MaxOpenLoansPerPlayer.ToString(CultureInfo.InvariantCulture)} open loans per player");
        }

        if (_ledger.BalanceOf(from) < principal)
        {
            return LoanResult.Rejected(EconomyReject.InsufficientFunds);
        }

        var now = Now;
        var ttl = offerTtlSeconds <= 0 ? options.OfferDefaultTtlMinutes * 60 : Math.Min(offerTtlSeconds, MaxOfferTtlSeconds);
        var loan = new LoanRecord(
            NextLoanId(), lenderId, borrowerId, principal, repayTotal, 0, 0, 0, (int)(extra * 10_000 / principal), autoRepayPercent, dueInSeconds, null,
            LoanState.Offered, now, now + TimeSpan.FromSeconds(ttl), null, null, null, note, requestKey);
        var outcome = _ledger.Post(new PostRequest
        {
            Kind = TxKind.LoanEscrow,
            Actor = Actor(lenderId),
            PlayerId = lenderId,
            RequestId = requestKey,
            RequestType = "LoanOffer",
            PayloadHash = hash,
            Entries = [new(from, -principal), new(loan.EscrowWallet, principal)],
            RefType = "Loan",
            RefId = loan.Id,
            Note = note,
            Loans = [loan],
        });
        var result = Finish(outcome, LedgerReason.LoanPrincipal, requestKey, null);
        if (outcome.Ok && !outcome.Replayed)
        {
            PublishCompleted("LoanOffer", lenderId, borrowerId, principal, outcome, requestKey);
        }

        return LoanResult.From(result, outcome.Ok ? loan : null, principal);
    }

    // ------------------------------------------------------------------ accept / decline

    /// <summary><c>LoanRespond</c> (borrower): accept (escrow to the borrower) or decline (escrow back to the lender).</summary>
    public LoanResult RespondLoan(int playerId, string requestKey, long loanId, bool accept)
    {
        var hash = PayloadHasher.Hash("LoanRespond", loanId, accept);
        if (IsStoredRequest(playerId, requestKey))
        {
            var replay = Finish(_ledger.Post(Build(TxKind.LoanDisburse, "LoanRespond", playerId, requestKey, hash, [], null)), LedgerReason.LoanPrincipal, requestKey, null);
            return LoanResult.From(replay, FindLoan(loanId));
        }

        var loan = FindLoan(loanId);
        if (loan is null)
        {
            return LoanResult.Rejected(EconomyReject.UnknownLoan);
        }

        if (loan.BorrowerId != playerId)
        {
            return LoanResult.Rejected(EconomyReject.NotParty, "only the borrower may answer the offer", loan);
        }

        if (loan.State != LoanState.Offered)
        {
            return LoanResult.Rejected(EconomyReject.WrongState, loan.State.ToString(), loan);
        }

        if (_ledger.IsFrozen)
        {
            return LoanResult.Rejected(EconomyReject.EconomyFrozen, _ledger.FreezeReason, loan);
        }

        if (Now >= loan.OfferExpiresAt)
        {
            var expired = CloseOffer(loan, LoanState.Expired, "offer expired", "system", null);
            return LoanResult.Rejected(EconomyReject.WrongState, "offer expired", expired.Loan ?? loan);
        }

        if (!accept)
        {
            return CloseOffer(loan, LoanState.Declined, "declined by the borrower", Actor(playerId), (playerId, requestKey, "LoanRespond", hash), "LoanDecline");
        }

        var options = _options();
        var scope = LoanScopeVerdict(options, loan.LenderId, loan.BorrowerId);
        if (scope is not null)
        {
            // The relation (or the setting) changed since the offer: the offer dies and the lender gets the escrow back.
            var cancelled = CloseOffer(loan, LoanState.Cancelled, "the loan scope no longer allows this loan", "system", null);
            return LoanResult.Rejected(scope.Reason, scope.Detail, cancelled.Loan ?? loan);
        }

        var lenderWallet = EffectiveWallet(loan.LenderId);
        var borrowerWallet = EffectiveWallet(loan.BorrowerId);
        if (lenderWallet == borrowerWallet)
        {
            return LoanResult.Rejected(EconomyReject.NotApplicableInSharedMode, "both players use the same wallet", loan);
        }

        if (_ledger.Find(borrowerWallet) is { Frozen: true })
        {
            return LoanResult.Rejected(EconomyReject.EconomyFrozen, "wallet frozen", loan);
        }

        var now = Now;
        var active = loan with
        {
            State = LoanState.Active,
            Outstanding = loan.RepayTotal,
            AcceptedAt = now,
            DueAt = loan.DueInSeconds > 0 ? now + TimeSpan.FromSeconds(loan.DueInSeconds) : null,
        };
        var outcome = _ledger.Post(new PostRequest
        {
            Kind = TxKind.LoanDisburse,
            Actor = Actor(playerId),
            PlayerId = playerId,
            RequestId = requestKey,
            RequestType = "LoanRespond",
            PayloadHash = hash,
            Entries = [new(loan.EscrowWallet, -loan.Principal), new(borrowerWallet, loan.Principal)],
            RefType = "Loan",
            RefId = loan.Id,
            Flags = PostOptions.BypassWalletFreeze,
            Loans = [active],
        });
        var result = Finish(outcome, LedgerReason.LoanPrincipal, requestKey, null);
        if (outcome.Ok && !outcome.Replayed)
        {
            PublishCompleted("LoanAccept", loan.LenderId, playerId, loan.Principal, outcome, requestKey);
        }

        return LoanResult.From(result, outcome.Ok ? active : loan, loan.Principal);
    }

    // ------------------------------------------------------------------ repay

    /// <summary><c>LoanRepay</c> (borrower): partial repayments allowed; the amount is capped at the outstanding balance.</summary>
    public LoanResult RepayLoan(int playerId, string requestKey, long loanId, long amount)
    {
        var hash = PayloadHasher.Hash("LoanRepay", loanId, amount);
        if (IsStoredRequest(playerId, requestKey))
        {
            var replay = Finish(_ledger.Post(Build(TxKind.LoanRepay, "LoanRepay", playerId, requestKey, hash, [], null)), LedgerReason.LoanRepayment, requestKey, null);
            return LoanResult.From(replay, FindLoan(loanId));
        }

        var loan = FindLoan(loanId);
        if (loan is null)
        {
            return LoanResult.Rejected(EconomyReject.UnknownLoan);
        }

        if (loan.BorrowerId != playerId)
        {
            return LoanResult.Rejected(EconomyReject.NotParty, "only the borrower repays a loan", loan);
        }

        if (!loan.IsRunning)
        {
            return LoanResult.Rejected(EconomyReject.WrongState, loan.State.ToString(), loan);
        }

        if (amount <= 0)
        {
            return LoanResult.Rejected(EconomyReject.AmountInvalid, null, loan);
        }

        var pay = Math.Min(amount, loan.Outstanding);
        var outgoing = CheckOutgoing(playerId);
        if (outgoing is not null)
        {
            return LoanResult.Rejected(outgoing.Reason, outgoing.Detail, loan);
        }

        var from = EffectiveWallet(loan.BorrowerId);
        var to = EffectiveWallet(loan.LenderId);
        if (from == to)
        {
            return LoanResult.Rejected(EconomyReject.NotApplicableInSharedMode, "both players use the same wallet", loan);
        }

        if (_ledger.Find(to) is { Frozen: true })
        {
            return LoanResult.Rejected(EconomyReject.EconomyFrozen, "lender wallet frozen", loan);
        }

        if (_ledger.BalanceOf(from) < pay)
        {
            return LoanResult.Rejected(EconomyReject.InsufficientFunds, null, loan);
        }

        var left = loan.Outstanding - pay;
        var updated = loan with
        {
            Outstanding = left,
            Repaid = loan.Repaid + pay,
            State = left == 0 ? LoanState.Repaid : loan.State,
            ClosedAt = left == 0 ? Now : null,
            CloseReason = left == 0 ? "repaid" : null,
        };
        var outcome = _ledger.Post(new PostRequest
        {
            Kind = TxKind.LoanRepay,
            Actor = Actor(playerId),
            PlayerId = playerId,
            RequestId = requestKey,
            RequestType = "LoanRepay",
            PayloadHash = hash,
            Entries = [new(from, -pay), new(to, pay)],
            RefType = "Loan",
            RefId = loan.Id,
            Loans = [updated],
        });
        var result = Finish(outcome, LedgerReason.LoanRepayment, requestKey, null);
        if (outcome.Ok && !outcome.Replayed)
        {
            PublishCompleted("LoanRepay", playerId, loan.LenderId, pay, outcome, requestKey);
        }

        return LoanResult.From(result, outcome.Ok ? updated : loan, pay);
    }

    // ------------------------------------------------------------------ lender: forgive, withdraw

    /// <summary><c>LoanForgive</c> (lender): writes off <paramref name="amount"/> of what is owed (0 = everything).</summary>
    public LoanResult ForgiveLoan(int playerId, string requestKey, long loanId, long amount)
    {
        var hash = PayloadHasher.Hash("LoanForgive", loanId, amount);
        if (IsStoredRequest(playerId, requestKey))
        {
            var replay = Finish(_ledger.Post(Build(TxKind.LoanRepay, "LoanForgive", playerId, requestKey, hash, [], null)), LedgerReason.LoanForgiven, requestKey, null);
            return LoanResult.From(replay, FindLoan(loanId));
        }

        var loan = FindLoan(loanId);
        if (loan is null)
        {
            return LoanResult.Rejected(EconomyReject.UnknownLoan);
        }

        if (loan.LenderId != playerId)
        {
            return LoanResult.Rejected(EconomyReject.NotParty, "only the lender may forgive a loan", loan);
        }

        if (!loan.IsRunning)
        {
            return LoanResult.Rejected(EconomyReject.WrongState, loan.State.ToString(), loan);
        }

        if (amount < 0)
        {
            return LoanResult.Rejected(EconomyReject.AmountInvalid, null, loan);
        }

        var written = amount == 0 || amount >= loan.Outstanding ? loan.Outstanding : amount;
        var updated = WriteOff(loan, written, "forgiven by the lender");
        var outcome = _ledger.Post(new PostRequest
        {
            Kind = TxKind.LoanRepay,
            Actor = Actor(playerId),
            PlayerId = playerId,
            RequestId = requestKey,
            RequestType = "LoanForgive",
            PayloadHash = hash,
            Entries = [],
            RefType = "Loan",
            RefId = loan.Id,
            Loans = [updated],
        });
        var result = Finish(outcome, LedgerReason.LoanForgiven, requestKey, null);
        if (outcome.Ok && !outcome.Replayed)
        {
            PublishCompleted("LoanForgive", playerId, loan.BorrowerId, written, outcome, requestKey);
        }

        return LoanResult.From(result, outcome.Ok ? updated : loan, written);
    }

    /// <summary><c>LoanCancel</c> (lender): withdraws an offer that is still Offered; the escrow goes back to the lender.</summary>
    public LoanResult WithdrawLoan(int playerId, string requestKey, long loanId)
    {
        var hash = PayloadHasher.Hash("LoanCancel", loanId);
        if (IsStoredRequest(playerId, requestKey))
        {
            var replay = Finish(_ledger.Post(Build(TxKind.LoanRefund, "LoanCancel", playerId, requestKey, hash, [], null)), LedgerReason.LoanPrincipal, requestKey, null);
            return LoanResult.From(replay, FindLoan(loanId));
        }

        var loan = FindLoan(loanId);
        if (loan is null)
        {
            return LoanResult.Rejected(EconomyReject.UnknownLoan);
        }

        if (loan.LenderId != playerId)
        {
            return LoanResult.Rejected(EconomyReject.NotParty, "only the lender may withdraw an offer", loan);
        }

        if (loan.State != LoanState.Offered)
        {
            return LoanResult.Rejected(EconomyReject.WrongState, loan.State.ToString(), loan);
        }

        if (_ledger.IsFrozen)
        {
            return LoanResult.Rejected(EconomyReject.EconomyFrozen, _ledger.FreezeReason, loan);
        }

        return CloseOffer(loan, LoanState.Withdrawn, "withdrawn by the lender", Actor(playerId), (playerId, requestKey, "LoanCancel", hash), "LoanWithdraw");
    }

    // ------------------------------------------------------------------ admin

    /// <summary>Admin forgive: the whole outstanding balance is written off and the loan closes as Forgiven. Audited.</summary>
    public LoanResult AdminForgiveLoan(string actor, long loanId, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(actor);
        var loan = FindLoan(loanId);
        if (loan is null)
        {
            return LoanResult.Rejected(EconomyReject.UnknownLoan);
        }

        if (!loan.IsRunning)
        {
            return LoanResult.Rejected(EconomyReject.WrongState, loan.State.ToString(), loan);
        }

        var updated = WriteOff(loan, loan.Outstanding, reason is null ? "forgiven by an admin" : "forgiven by an admin: " + reason);
        var outcome = _ledger.Post(new PostRequest
        {
            Kind = TxKind.LoanRepay,
            Actor = actor,
            Entries = [],
            RefType = "Loan",
            RefId = loan.Id,
            Note = reason,
            Loans = [updated],
        });
        var result = Finish(outcome, LedgerReason.LoanForgiven, string.Empty, null);
        if (outcome.Ok)
        {
            PublishAdminAction(actor, "economy.loan.forgive", loan, reason);
        }

        return LoanResult.From(result, outcome.Ok ? updated : loan, loan.Outstanding);
    }

    /// <summary>
    /// Admin cancel: an Offered loan refunds its escrow; an Active or Overdue loan closes with no further obligation
    /// (the disbursement is not reversed; reversing it is a separate admin ledger action). Audited.
    /// </summary>
    public LoanResult AdminCancelLoan(string actor, long loanId, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(actor);
        var loan = FindLoan(loanId);
        if (loan is null)
        {
            return LoanResult.Rejected(EconomyReject.UnknownLoan);
        }

        if (!loan.IsOpen)
        {
            return LoanResult.Rejected(EconomyReject.WrongState, loan.State.ToString(), loan);
        }

        var text = reason is null ? "cancelled by an admin" : "cancelled by an admin: " + reason;
        LoanResult result;
        if (loan.State == LoanState.Offered)
        {
            if (_ledger.IsFrozen)
            {
                return LoanResult.Rejected(EconomyReject.EconomyFrozen, _ledger.FreezeReason, loan);
            }

            result = CloseOffer(loan, LoanState.Cancelled, text, actor, null);
        }
        else
        {
            var closed = loan with { State = LoanState.Cancelled, Outstanding = 0, ClosedAt = Now, CloseReason = text };
            var outcome = _ledger.Post(new PostRequest
            {
                Kind = TxKind.LoanRepay,
                Actor = actor,
                Entries = [],
                RefType = "Loan",
                RefId = loan.Id,
                Note = reason,
                Loans = [closed],
            });
            result = LoanResult.From(Finish(outcome, LedgerReason.LoanForgiven, string.Empty, null), outcome.Ok ? closed : loan);
        }

        if (result.Ok)
        {
            PublishAdminAction(actor, "economy.loan.cancel", loan, reason);
        }

        return result;
    }

    // ------------------------------------------------------------------ timers

    /// <summary>
    /// Expires offers whose time to live has passed (escrow refunded) and flags loans past their due time as Overdue.
    /// Call every second or so from the actor tick; it keeps no timers of its own, so it works unchanged after a restart.
    /// A frozen economy postpones everything to the next call. Returns the number of loans changed.
    /// </summary>
    public int ProcessLoanTimers()
    {
        var now = Now;
        var changed = 0;
        foreach (var loan in _ledger.Loans.Where(l => l.IsOpen).OrderBy(l => l.Id).ToList())
        {
            if (loan.State == LoanState.Offered && now >= loan.OfferExpiresAt)
            {
                changed += CloseOffer(loan, LoanState.Expired, "offer expired", "system", null).Ok ? 1 : 0;
            }
            else if (loan.State == LoanState.Active && loan.Outstanding > 0 && loan.DueAt is { } due && now >= due)
            {
                var overdue = loan with { State = LoanState.Overdue };
                var outcome = _ledger.Post(new PostRequest
                {
                    Kind = TxKind.LoanRepay,
                    Actor = "system",
                    Entries = [],
                    RefType = "Loan",
                    RefId = loan.Id,
                    Note = "overdue",
                    Loans = [overdue],
                });
                changed += outcome.Ok ? 1 : 0;
            }
        }

        return changed;
    }

    // ------------------------------------------------------------------ auditor

    /// <summary>Loan invariants for <see cref="EconomyAuditor.AddCheck"/>: escrow equals the principal while Offered and is zero after, balances stay within bounds.</summary>
    public static IEnumerable<string> AuditLoans(EconomyLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var known = new HashSet<WalletId>();
        foreach (var loan in ledger.Loans)
        {
            var escrow = ledger.BalanceOf(loan.EscrowWallet);
            known.Add(loan.EscrowWallet);
            var expected = loan.State == LoanState.Offered ? loan.Principal : 0;
            if (escrow != expected)
            {
                yield return $"loan {LoanText.Id(loan.Id)} ({loan.State}): escrow holds {escrow.ToString(CultureInfo.InvariantCulture)}, expected {expected.ToString(CultureInfo.InvariantCulture)}";
            }

            if (loan.Outstanding < 0 || loan.Outstanding > loan.RepayTotal)
            {
                yield return $"loan {LoanText.Id(loan.Id)}: outstanding {loan.Outstanding.ToString(CultureInfo.InvariantCulture)} is outside 0..{loan.RepayTotal.ToString(CultureInfo.InvariantCulture)}";
            }

            if (!loan.IsRunning && loan.State != LoanState.Offered && loan.Outstanding != 0)
            {
                yield return $"loan {LoanText.Id(loan.Id)} is {loan.State} but still owes {loan.Outstanding.ToString(CultureInfo.InvariantCulture)}";
            }
        }

        foreach (var wallet in ledger.Wallets)
        {
            if (wallet.Id.Kind == WalletKind.Escrow && wallet.Id.OwnerId is >= LoanWallets.EscrowOffset and < 2 * LoanWallets.EscrowOffset
                && wallet.Balance != 0 && !known.Contains(wallet.Id))
            {
                yield return $"escrow wallet {wallet.Id} belongs to no loan but holds {wallet.Balance.ToString(CultureInfo.InvariantCulture)}";
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    private static LoanRecord WriteOff(LoanRecord loan, long written, string reason)
    {
        var left = loan.Outstanding - written;
        return loan with
        {
            Outstanding = left,
            Forgiven = loan.Forgiven + written,
            State = left == 0 ? LoanState.Forgiven : loan.State,
            ClosedAt = left == 0 ? null : loan.ClosedAt,
            CloseReason = left == 0 ? reason : loan.CloseReason,
        };
    }

    /// <summary>Closes an Offered loan and returns the escrow to the lender (decline, withdraw, expiry, cancel). Refunds ignore wallet freezes.</summary>
    private LoanResult CloseOffer(
        LoanRecord loan, LoanState state, string reason, string actor, (int Player, string Key, string Type, byte[] Hash)? request, string? completedType = null)
    {
        var closed = loan with { State = state, ClosedAt = Now, CloseReason = reason };
        var outcome = _ledger.Post(new PostRequest
        {
            Kind = TxKind.LoanRefund,
            Actor = actor,
            PlayerId = request?.Player ?? 0,
            RequestId = request?.Key,
            RequestType = request?.Type,
            PayloadHash = request?.Hash,
            Entries = [new(loan.EscrowWallet, -loan.Principal), new(EffectiveWallet(loan.LenderId), loan.Principal)],
            RefType = "Loan",
            RefId = loan.Id,
            Note = reason,
            Flags = PostOptions.BypassWalletFreeze,
            Loans = [closed],
        });
        var result = Finish(outcome, LedgerReason.LoanPrincipal, request?.Key ?? string.Empty, null);
        if (outcome.Ok && !outcome.Replayed && completedType is not null && request is { } r)
        {
            PublishCompleted(completedType, r.Player, r.Player == loan.LenderId ? loan.BorrowerId : loan.LenderId, loan.Principal, outcome, r.Key);
        }

        return LoanResult.From(result, outcome.Ok ? closed : loan, loan.Principal);
    }

    private EconomyActionResult? LoanScopeVerdict(EconomyOptions options, int lenderId, int borrowerId)
    {
        var teamA = LiveTeamOf(lenderId);
        var teamB = LiveTeamOf(borrowerId);
        var same = teamA is not null && teamA == teamB;
        var allied = same || (teamA is { } a && teamB is { } b && _teams?.RelationBetween(a, b) == X4MP.Core.Teams.TeamRelation.Allied);
        return options.LoanScope switch
        {
            EconomyScope.Off => EconomyActionResult.Rejected(EconomyReject.ScopeDisabled),
            EconomyScope.Teammates => same ? null : EconomyActionResult.Rejected(EconomyReject.ScopeDenied, "borrower is not a teammate"),
            EconomyScope.Allied => allied ? null : EconomyActionResult.Rejected(EconomyReject.ScopeDenied, "borrower's team is not allied"),
            _ => null,
        };
    }

    private void OnLoanCommitted(LoanRecord loan, LoanRecord? previous)
    {
        var now = Now;
        if (previous is null || previous.State != loan.State)
        {
            _events?.Publish(new LoanStateChanged(now, _ledger.SessionId, loan.Id, loan.LenderId, loan.BorrowerId, previous?.State, loan.State,
                loan.Principal, loan.Outstanding, loan.CloseReason));
        }

        LoanChanged?.Invoke(new LoanChange(loan, previous, NoticesFor(loan, previous)));
    }

    private static IReadOnlyList<(int Player, string Text)> NoticesFor(LoanRecord loan, LoanRecord? previous)
    {
        var id = LoanText.Id(loan.Id);
        if (previous is null)
        {
            return [(loan.BorrowerId, $"Loan {id} offered: you receive {LoanText.Credits(loan.Principal)} and repay {LoanText.Credits(loan.RepayTotal)}.")];
        }

        if (previous.State == loan.State)
        {
            return [];
        }

        return loan.State switch
        {
            LoanState.Active => [(loan.LenderId, $"Loan {id} was accepted: {LoanText.Credits(loan.Principal)} paid out.")],
            LoanState.Overdue => [(loan.LenderId, $"Loan {id} is overdue ({LoanText.Credits(loan.Outstanding)} outstanding)."), (loan.BorrowerId, $"Loan {id} is overdue: {LoanText.Credits(loan.Outstanding)} outstanding.")],
            LoanState.Repaid => [(loan.LenderId, $"Loan {id} is repaid in full."), (loan.BorrowerId, $"Loan {id} is repaid in full.")],
            LoanState.Declined => [(loan.LenderId, $"Loan {id} was declined; {LoanText.Credits(loan.Principal)} returned to you.")],
            LoanState.Withdrawn => [(loan.BorrowerId, $"Loan offer {id} was withdrawn.")],
            LoanState.Expired => [(loan.LenderId, $"Loan offer {id} expired; {LoanText.Credits(loan.Principal)} returned to you."), (loan.BorrowerId, $"Loan offer {id} expired.")],
            LoanState.Forgiven => [(loan.BorrowerId, $"Loan {id} was forgiven.")],
            LoanState.Cancelled => [(loan.LenderId, $"Loan {id} was cancelled."), (loan.BorrowerId, $"Loan {id} was cancelled.")],
            _ => [],
        };
    }

    private void PublishAdminAction(string actor, string action, LoanRecord loan, string? reason) =>
        _events?.Publish(new AdminActionTaken(Now, _ledger.SessionId, actor, action, "loan:" + loan.Id.ToString(CultureInfo.InvariantCulture),
            new Dictionary<string, string?>
            {
                ["lender"] = loan.LenderId.ToString(CultureInfo.InvariantCulture),
                ["borrower"] = loan.BorrowerId.ToString(CultureInfo.InvariantCulture),
                ["state"] = loan.State.ToString(),
                ["outstanding"] = loan.Outstanding.ToString(CultureInfo.InvariantCulture),
                ["reason"] = reason,
            }, null));
}
