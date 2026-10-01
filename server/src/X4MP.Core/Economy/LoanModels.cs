using System.Globalization;
using X4MP.Core.Events;
using X4MP.Proto;

namespace X4MP.Core.Economy;

/// <summary>Lifecycle of a loan (server-design 2.14). The wire <c>LoanState</c> has no Withdrawn: it maps to Cancelled.</summary>
public enum LoanState : byte
{
    Offered,
    Active,
    Overdue,
    Repaid,
    Declined,
    Withdrawn,
    Expired,
    Forgiven,
    Cancelled,
}

/// <summary>One loan, as persisted (<c>loans</c> table). Immutable: every change is a new record posted through the ledger.</summary>
/// <param name="Principal">Credits escrowed at offer time and handed to the borrower on accept.</param>
/// <param name="RepayTotal">Principal plus the flat interest agreed up front; the borrower owes this much.</param>
/// <param name="Outstanding">What is still owed (0 until accepted).</param>
/// <param name="Repaid">Credits repaid so far (manual and auto-repay).</param>
/// <param name="Forgiven">Credits written off by the lender or an admin.</param>
/// <param name="InterestBasisPoints">Flat interest on the principal, for display (derived from <see cref="RepayTotal"/>).</param>
/// <param name="DueInSeconds">Real-time seconds after acceptance until due; 0 = no due date.</param>
/// <param name="DueAt">Set at acceptance when <see cref="DueInSeconds"/> is not 0.</param>
/// <param name="OfferRequestKey">The lender's request key of the offer (replays find the loan through it).</param>
public sealed record LoanRecord(
    long Id,
    int LenderId,
    int BorrowerId,
    long Principal,
    long RepayTotal,
    long Outstanding,
    long Repaid,
    long Forgiven,
    int InterestBasisPoints,
    int AutoRepayPercent,
    int DueInSeconds,
    DateTimeOffset? DueAt,
    LoanState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset OfferExpiresAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? ClosedAt,
    string? CloseReason,
    string? Memo,
    string OfferRequestKey)
{
    /// <summary>Offered, Active or Overdue: counts toward the open-loan limit and may still change.</summary>
    public bool IsOpen => State is LoanState.Offered or LoanState.Active or LoanState.Overdue;

    /// <summary>Accepted and not closed (Active or Overdue): money is owed.</summary>
    public bool IsRunning => State is LoanState.Active or LoanState.Overdue;

    public bool InvolvesPlayer(int playerId) => LenderId == playerId || BorrowerId == playerId;

    /// <summary>Escrow wallet that holds the principal while the loan is Offered.</summary>
    public WalletId EscrowWallet => LoanWallets.Escrow(Id);
}

/// <summary>Wallet naming of loans. Escrow owner ids share one namespace with trades, so loans live above <see cref="EscrowOffset"/>.</summary>
public static class LoanWallets
{
    /// <summary>Added to a loan id to form the owner id of its escrow wallet (keeps loan escrows apart from trade escrows).</summary>
    public const long EscrowOffset = 1L << 40;

    public static WalletId Escrow(long loanId) => WalletId.Escrow(EscrowOffset + loanId);
}

/// <summary>Result of a loan action: the wire reason (None = success), the posting, and the loan after the action.</summary>
/// <param name="Moved">Credits moved by the action (the capped amount of a repayment).</param>
public sealed record LoanResult(EconomyReject Reason, PostOutcome? Outcome, string? Detail, LoanRecord? Loan, long Moved = 0)
{
    public bool Ok => Reason == EconomyReject.None;

    public static LoanResult Rejected(EconomyReject reason, string? detail = null, LoanRecord? loan = null) => new(reason, null, detail, loan);

    public static LoanResult From(EconomyActionResult result, LoanRecord? loan, long moved = 0) =>
        new(result.Reason, result.Outcome, result.Detail, loan, moved);

    public EconomyActionResult ToAction() => new(Reason, Outcome, Detail);
}

/// <summary>A loan changed state (offered, accepted, overdue, closed...). Persisted into the event log.</summary>
public sealed record LoanStateChanged(
    DateTimeOffset At, long? Session, long LoanId, int Lender, int Borrower, LoanState? From, LoanState To, long Principal, long Outstanding, string? Reason)
    : DomainEvent(At, Session), IPlayerScoped
{
    long? IPlayerScoped.PlayerId => Lender;
}

/// <summary>What the module needs to tell the two parties: the loan after the change, the one before (null = new) and the notices (player, text) to show.</summary>
public sealed record LoanChange(LoanRecord Loan, LoanRecord? Previous, IReadOnlyList<(int Player, string Text)> Notices);

/// <summary>Auto-repay: diverts a share of booked game income of a borrower to the lender, inside the income transaction.</summary>
internal sealed class LoanIncomeSplitter(EconomyService service) : IIncomeSplitter
{
    private List<LoanRecord>? _pending;

    public IReadOnlyList<(WalletId To, long Amount)> Split(int? playerId, WalletId target, long income)
    {
        _pending = null;
        if (playerId is not { } borrower || income <= 0)
        {
            return [];
        }

        var shares = new SortedDictionary<WalletId, long>(Comparer<WalletId>.Create((a, b) =>
        {
            var kind = a.Kind.CompareTo(b.Kind);
            return kind != 0 ? kind : a.OwnerId.CompareTo(b.OwnerId);
        }));
        var remainingIncome = income;
        foreach (var loan in service.Loans.Where(l => l.IsRunning && l.BorrowerId == borrower && l.AutoRepayPercent > 0 && l.Outstanding > 0).OrderBy(l => l.Id))
        {
            var lenderWallet = service.EffectiveWallet(loan.LenderId);
            if (lenderWallet == target)
            {
                continue; // same wallet (Shared mode, one team): nothing to move
            }

            var share = Math.Min(Math.Min(income * loan.AutoRepayPercent / 100, loan.Outstanding), remainingIncome);
            if (share <= 0)
            {
                continue;
            }

            remainingIncome -= share;
            shares[lenderWallet] = shares.GetValueOrDefault(lenderWallet) + share;
            var left = loan.Outstanding - share;
            (_pending ??= []).Add(loan with
            {
                Outstanding = left,
                Repaid = loan.Repaid + share,
                State = left == 0 ? LoanState.Repaid : loan.State,
                ClosedAt = left == 0 ? service.Now : loan.ClosedAt,
                CloseReason = left == 0 ? "auto-repaid" : loan.CloseReason,
            });
        }

        return [.. shares.Select(kv => (kv.Key, kv.Value))];
    }

    public IReadOnlyList<LoanRecord>? TakeChanges()
    {
        var pending = _pending;
        _pending = null;
        return pending;
    }
}

internal static class LoanText
{
    public static string Id(long id) => "#" + id.ToString(CultureInfo.InvariantCulture);

    public static string Credits(long amount) => amount.ToString("N0", CultureInfo.InvariantCulture) + " Cr";
}
