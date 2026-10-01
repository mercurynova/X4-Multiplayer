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
}
