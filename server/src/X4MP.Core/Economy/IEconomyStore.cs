namespace X4MP.Core.Economy;

/// <summary>
/// Persistence seam of the economy (the <c>wallets</c>, <c>ledger_*</c>, <c>economy_requests</c> tables). Unlike the
/// session store it is <b>synchronous and durable</b>: <see cref="Commit"/> returns only after the data is committed to
/// disk, because a credit operation is acknowledged to a player right after it. Implementations use their own
/// connection, never the batched write-behind writer. Called on the actor thread only.
/// </summary>
public interface IEconomyStore
{
    /// <summary>Wallets, the per-node delta sequences and the applied layout of a session.</summary>
    EconomyLoad Load(long sessionId);

    /// <summary>
    /// Persists one operation atomically: the transaction and its entries, the wallet rows it changed, the idempotency
    /// record, the delta sequence and the layout. All or nothing; throws if it could not be made durable.
    /// </summary>
    void Commit(EconomyCommit commit);

    /// <summary>The stored result of an earlier request, or null.</summary>
    EconomyRequestRecord? FindRequest(long sessionId, int playerId, string requestId);

    /// <summary>Total credits the actor moved with transactions of <paramref name="kind"/> since <paramref name="since"/> (positive entries).</summary>
    long SumInflow(long sessionId, TxKind kind, string actor, DateTimeOffset since);

    /// <summary>Recomputes the ledger invariants from the persisted rows.</summary>
    LedgerAuditData ReadAuditData(long sessionId);

    /// <summary>One committed transaction with its entries and its reversal link, or null.</summary>
    LedgerTransaction? GetTransaction(long sessionId, string txId);

    /// <summary>Committed transactions matching the filter, newest first (by id), at most <see cref="LedgerQuery.Limit"/>.</summary>
    IReadOnlyList<LedgerTransaction> QueryTransactions(long sessionId, LedgerQuery query);

    /// <summary>
    /// The ledger's monotonic sequence number after its newest committed transaction (0 = empty). Every transaction gets the next
    /// number (<see cref="LedgerTransaction.Seq"/>) when it is committed; numbers are never reused and never depend on a clock.
    /// A checkpoint records this value as its ledger mark (ADR-053).
    /// </summary>
    long HighWaterSeq(long sessionId);

    /// <summary>Every committed transaction whose sequence is greater than <paramref name="seq"/>, oldest first, with <see cref="LedgerTransaction.Seq"/> set.</summary>
    IReadOnlyList<LedgerTransaction> TransactionsAfter(long sessionId, long seq);

    /// <summary>Every wallet balance as it was after the transaction with sequence <paramref name="seq"/> (the sum of all entries up to it; wallets that did not exist yet are absent).</summary>
    IReadOnlyDictionary<WalletId, long> BalancesAt(long sessionId, long seq);
}
