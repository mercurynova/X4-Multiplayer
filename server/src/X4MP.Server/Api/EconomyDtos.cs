namespace X4MP.Server.Api;

// DTOs of the economy admin API and the hub's economy topic (server-design 2.14, 4.4 to 4.6, task M1-E6). Same conventions as AdminDtos:
// strings for enums, timestamps as ISO-8601. Credits are whole numbers.

/// <summary>
/// One wallet. <c>Kind</c> is <c>Player</c>, <c>TeamShared</c>, <c>TeamPool</c>, <c>Escrow</c> or <c>World</c>; <c>OwnerId</c> is the player or team id
/// (the loan or trade reference for an escrow, 0 for the world). <c>Version</c> counts the wallet's committed changes. <c>Overdrawn</c> means the ledger
/// balance is negative (the game spent more than the wallet held).
/// </summary>
[TsContract]
public sealed record WalletDto(
    string Kind, long OwnerId, string OwnerName, long Balance, bool Frozen, string? FrozenReason, bool Overdrawn, long Version);

/// <summary>One line of a transaction: the signed amount and the wallet balance after it.</summary>
[TsContract]
public sealed record LedgerEntryDto(string WalletKind, long WalletOwnerId, string WalletName, long Amount, long BalanceAfter);

/// <summary>
/// A committed ledger transaction. <c>Actor</c> is <c>player:id</c>, <c>authority</c>, <c>admin:name</c> or <c>system</c>. <c>Reverses</c> and
/// <c>ReversedBy</c> link a reversal and the transaction it undid (both ways).
/// </summary>
[TsContract]
public sealed record LedgerTxDto(
    string Id, DateTimeOffset At, string Kind, string Actor, string? RequestId, string? RefType, long? RefId, string? Reverses,
    string? ReversedBy, string? Note, List<LedgerEntryDto> Entries);

/// <summary>A wallet with its latest transactions and the open loans and trades of its owner (for a player wallet; empty for the others).</summary>
[TsContract]
public sealed record WalletDetailDto(WalletDto Wallet, List<LedgerTxDto> Recent, List<LoanDto> OpenLoans, List<TradeOfferDto> OpenTrades);

/// <summary>
/// A loan. <c>State</c> is Offered, Active, Overdue, Repaid, Declined, Withdrawn, Expired, Forgiven or Cancelled; <c>InterestBp</c> is the flat
/// interest in basis points of the principal; <c>RepayTotal</c> is what the borrower owes in all; <c>Outstanding</c> what is still owed.
/// </summary>
[TsContract]
public sealed record LoanDto(
    long Id, long LenderId, string Lender, long BorrowerId, string Borrower, long Principal, int InterestBp, long RepayTotal, long Outstanding,
    long Repaid, long Forgiven, int AutoRepayPercent, int DueInSeconds, DateTimeOffset? DueAt, string State, bool Overdue, DateTimeOffset CreatedAt,
    DateTimeOffset OfferExpiresAt, DateTimeOffset? AcceptedAt, DateTimeOffset? ClosedAt, string? CloseReason, string? Memo);

/// <summary>A loan with the ledger transactions that moved its money and the events of its life.</summary>
[TsContract]
public sealed record LoanDetailDto(LoanDto Loan, List<LedgerTxDto> Transactions, List<EconomyEventDto> Events);

/// <summary>One item of a trade side. <c>Kind</c> is Credits, Ware, Ship or Station. <c>WareRef</c> is the ware's string-table index, <c>Asset</c> the ship or the source container.</summary>
[TsContract]
public sealed record TradeItemDto(string Kind, long Amount, long WareRef, long Asset);

/// <summary>
/// A trade. <c>State</c> is Proposed, Countered, Accepted, Escrowed, Transferring, InDoubt, Completed, RolledBack, Cancelled, Expired or Rejected.
/// <c>Reversed</c> is set after an admin reversed the credits of a completed trade. <c>EscrowAmount</c> is what the escrow wallet holds now.
/// </summary>
[TsContract]
public sealed record TradeOfferDto(
    long Id, long InitiatorId, string Initiator, long CounterpartyId, string Counterparty, List<TradeItemDto> InitiatorGives,
    List<TradeItemDto> CounterpartyGives, string State, long Version, DateTimeOffset ExpiresAt, int QueryAttempts, long EscrowAmount,
    string? Reason, string? Detail, string? ResolvedBy, bool Reversed, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? Memo);

/// <summary>A trade with the ledger transactions that moved its credits and the events of its life.</summary>
[TsContract]
public sealed record TradeDetailDto(TradeOfferDto Trade, List<LedgerTxDto> Transactions, List<EconomyEventDto> Events);

/// <summary>An entry of the economy event log: a loan or trade state change, an applied migration, a freeze or an admin action.</summary>
[TsContract]
public sealed record EconomyEventDto(
    long Id, DateTimeOffset At, string Type, string Actor, string? RefType, long? RefId, string? FromState, string? ToState, string? Reason);

/// <summary>Result of an invariant check. <c>EconomyFrozen</c> is true while the economy is frozen after a breach until an admin acknowledges it.</summary>
[TsContract]
public sealed record AuditorReportDto(DateTimeOffset At, bool Ok, bool EconomyFrozen, List<string> Violations);

/// <summary>The overview: totals, counts and the state of the auditor. <c>MoneySupply</c> is the negated balance of the World wallet.</summary>
[TsContract]
public sealed record EconomySummaryDto(
    string CreditMode, string EffectiveCreditMode, string AppliedCreditMode, bool MigrationPending, long MoneySupply, long InEscrow, int OpenLoans,
    int OverdueLoans, long OutstandingDebt, int OpenTrades, int InDoubtTrades, int FrozenWallets, bool EconomyFrozen, string? FreezeReason,
    AuditorReportDto LastAudit);

/// <summary>The economy settings (<c>Economy.*</c>). Scopes are Off, Teammates, Allied or Anyone; <c>CreditMode</c> is Auto, PerPlayer or Shared.</summary>
[TsContract]
public sealed record EconomyPolicyDto(
    string CreditMode, string EffectiveCreditMode, long StartingCredits, bool TeamPoolEnabled, string PoolWithdrawPolicy,
    long PoolWithdrawDailyLimitPerPlayer, string SharedWalletSpend, string DonateScope, bool AllowAlliedTransfers, string LoanScope,
    string TradeScope, bool TradeShipsEnabled, int MaxOpenTradesPerPlayer, bool TradeRequiresProximity, int TradeExecuteTimeoutSeconds,
    int TradeQueryIntervalSeconds, int MaxOpenLoansPerPlayer, long MaxLoanPrincipal, int MaxLoanInterestBp, int OfferDefaultTtlMinutes,
    long MaxSingleTransfer, int AuditIntervalSeconds);

/// <summary>
/// Body of <c>PATCH /api/v1/economy/policy</c>: only the fields that are present change. <c>Confirm</c> approves a credit-mode change on a live session
/// after the migration preview; <c>Reason</c> goes into the audit row.
/// </summary>
[TsContract]
public sealed record EconomyPolicyPatch(
    string? CreditMode, long? StartingCredits, bool? TeamPoolEnabled, string? PoolWithdrawPolicy, long? PoolWithdrawDailyLimitPerPlayer,
    string? SharedWalletSpend, string? DonateScope, bool? AllowAlliedTransfers, string? LoanScope, string? TradeScope, bool? TradeShipsEnabled,
    int? MaxOpenTradesPerPlayer, bool? TradeRequiresProximity, int? TradeExecuteTimeoutSeconds, int? TradeQueryIntervalSeconds,
    int? MaxOpenLoansPerPlayer, long? MaxLoanPrincipal, int? MaxLoanInterestBp, int? OfferDefaultTtlMinutes, long? MaxSingleTransfer,
    int? AuditIntervalSeconds, bool? Confirm, string? Reason);

/// <summary>A wallet's balance before and after a migration.</summary>
[TsContract]
public sealed record WalletPreviewDto(string Kind, long OwnerId, string OwnerName, long Before, long After);

/// <summary>What a credit-mode switch or team move would do to the balances.</summary>
[TsContract]
public sealed record MigrationPreviewDto(
    bool Needed, bool RequiresConfirm, string From, string To, string Kind, List<WalletPreviewDto> Changes, long TotalMoved, List<string> TeamMoves);

/// <summary>The 409 body of <c>PATCH /api/v1/economy/policy</c> when a credit-mode change on a live session was not confirmed: a problem plus the preview.</summary>
[TsContract]
public sealed record MigrationConfirmProblem(
    string Type, string Title, int Status, string Code, string? Detail, MigrationPreviewDto MigrationPreview);

/// <summary>Body of <c>POST /api/v1/economy/wallets/{kind}/{ownerId}/adjust</c>: a signed amount (positive credits, negative debits). <c>Force</c> lets a debit overdraw the wallet.</summary>
[TsContract]
public sealed record AdjustWalletRequest(long? Amount, string? Reason, bool? Force);

/// <summary>Body of <c>POST /api/v1/economy/wallets/{kind}/{ownerId}/freeze</c>.</summary>
[TsContract]
public sealed record FreezeWalletRequest(bool? Frozen, string? Reason);

/// <summary>Body of <c>POST /api/v1/economy/transactions/{id}/reverse</c>. <c>ReturnAsset</c> (also sending the assets of a trade back) is not available yet and is refused.</summary>
[TsContract]
public sealed record ReverseTransactionRequest(string? Reason, bool? Force, bool? ReturnAsset);

/// <summary>Body of <c>POST /api/v1/economy/loans/{id}/forgive</c>.</summary>
[TsContract]
public sealed record ForgiveLoanRequest(string? Reason);

/// <summary>Body of <c>POST /api/v1/economy/loans/{id}/cancel</c>. <c>ReverseDisbursement</c> takes the principal back from the borrower; <c>Force</c> lets that overdraw them.</summary>
[TsContract]
public sealed record CancelLoanRequest(string? Reason, bool? ReverseDisbursement, bool? Force);

/// <summary>Body of <c>POST /api/v1/economy/trades/{id}/resolve</c>. <c>Outcome</c> is <c>complete</c> (the authority did apply the transfer) or <c>refund</c>.</summary>
[TsContract]
public sealed record ResolveTradeRequest(string? Outcome, string? Reason);

/// <summary>Body of <c>POST /api/v1/economy/trades/{id}/cancel</c>.</summary>
[TsContract]
public sealed record CancelTradeRequest(string? Reason);

/// <summary>Body of <c>POST /api/v1/economy/audit</c>. <c>Acknowledge</c> lifts the freeze of an earlier breach before the check runs.</summary>
[TsContract]
public sealed record AuditEconomyRequest(bool? Acknowledge);
