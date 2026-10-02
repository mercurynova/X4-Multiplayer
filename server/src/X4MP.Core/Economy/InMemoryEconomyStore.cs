namespace X4MP.Core.Economy;

/// <summary>
/// An <see cref="IEconomyStore"/> that keeps everything in memory (running without persistence, and tests of the
/// logic above the store). Nothing survives the process. The <c>Tamper*</c> and <see cref="FailCommits"/> members exist
/// so tests can corrupt the ledger and simulate a failing disk.
/// </summary>
public sealed class InMemoryEconomyStore : IEconomyStore
{
    private readonly Dictionary<(long Session, WalletId Id), WalletState> _wallets = [];
    private readonly List<LedgerTransaction> _transactions = [];
    private readonly Dictionary<(long Session, int Player, string Request), EconomyRequestRecord> _requests = [];
    private readonly Dictionary<(long Session, int Player), ulong> _deltaSeqs = [];
    private readonly Dictionary<long, EconomyLayout> _layouts = [];
    private readonly Dictionary<(long Session, long Id), LoanRecord> _loans = [];

    /// <summary>When true, <see cref="Commit"/> throws (simulates a full or failing disk).</summary>
    public bool FailCommits { get; set; }

    public IReadOnlyList<LedgerTransaction> Transactions => _transactions;

    public EconomyLoad Load(long sessionId) => new(
        _wallets.Where(kv => kv.Key.Session == sessionId).Select(kv => kv.Value.Clone()).ToList(),
        _deltaSeqs.Where(kv => kv.Key.Session == sessionId).ToDictionary(kv => kv.Key.Player, kv => kv.Value),
        _layouts.GetValueOrDefault(sessionId) ?? new EconomyLayout(null, new Dictionary<int, int?>()),
        _loans.Where(kv => kv.Key.Session == sessionId).Select(kv => kv.Value).OrderBy(l => l.Id).ToList());

    public void Commit(EconomyCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (FailCommits)
        {
            throw new IOException("simulated commit failure");
        }

        if (commit.Transaction is { } tx)
        {
            if (tx.Reverses is { } original)
            {
                var index = _transactions.FindIndex(t => t.Id == original);
                if (index < 0 || _transactions[index].ReversedBy is not null)
                {
                    throw new InvalidOperationException("the transaction to reverse is unknown or already reversed");
                }

                _transactions[index] = _transactions[index] with { ReversedBy = tx.Id };
            }

            _transactions.Add(tx);
        }

        foreach (var wallet in commit.Wallets)
        {
            _wallets[(commit.SessionId, wallet.Id)] = wallet.Clone();
        }

        if (commit.Request is { } request)
        {
            _requests[(request.SessionId, request.PlayerId, request.RequestId)] = request;
        }

        if (commit.DeltaSeq is { } delta)
        {
            _deltaSeqs[(commit.SessionId, delta.PlayerId)] = delta.Seq;
        }

        if (commit.Layout is { } layout)
        {
            _layouts[commit.SessionId] = layout;
        }

        foreach (var loan in commit.Loans ?? [])
        {
            _loans[(commit.SessionId, loan.Id)] = loan;
        }
    }

    public EconomyRequestRecord? FindRequest(long sessionId, int playerId, string requestId) =>
        _requests.GetValueOrDefault((sessionId, playerId, requestId));

    public long SumInflow(long sessionId, TxKind kind, string actor, DateTimeOffset since) =>
        _transactions
            .Where(t => t.SessionId == sessionId && t.Kind == kind && t.Actor == actor && t.At >= since)
            .Sum(t => t.Entries.Where(e => e.Amount > 0).Sum(e => e.Amount));

    public LedgerAuditData ReadAuditData(long sessionId)
    {
        var unbalanced = _transactions
            .Where(t => t.SessionId == sessionId && (t.Entries.Count < 2 || t.Entries.Sum(e => e.Amount) != 0))
            .Select(t => t.Id)
            .ToList();
        var sums = new Dictionary<WalletId, long>();
        foreach (var entry in _transactions.Where(t => t.SessionId == sessionId).SelectMany(t => t.Entries))
        {
            sums[entry.Wallet] = sums.GetValueOrDefault(entry.Wallet) + entry.Amount;
        }

        var balances = _wallets.Where(kv => kv.Key.Session == sessionId).ToDictionary(kv => kv.Key.Id, kv => kv.Value.Balance);
        return new LedgerAuditData(unbalanced, sums, balances);
    }

    public LedgerTransaction? GetTransaction(long sessionId, string txId) =>
        _transactions.FirstOrDefault(t => t.SessionId == sessionId && t.Id == txId);

    public IReadOnlyList<LedgerTransaction> QueryTransactions(long sessionId, LedgerQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var rows = _transactions.Where(t => t.SessionId == sessionId);
        if (query.Wallet is { } wallet)
        {
            rows = rows.Where(t => t.Entries.Any(e => e.Wallet == wallet));
        }

        if (query.Kind is { } kind)
        {
            rows = rows.Where(t => t.Kind == kind);
        }

        if (query.Actor is { } actor)
        {
            rows = rows.Where(t => t.Actor == actor);
        }

        if (query.RefType is { } refType)
        {
            rows = rows.Where(t => t.RefType == refType);
        }

        if (query.RefId is { } refId)
        {
            rows = rows.Where(t => t.RefId == refId);
        }

        if (query.Since is { } since)
        {
            rows = rows.Where(t => t.At >= since);
        }

        if (query.Until is { } until)
        {
            rows = rows.Where(t => t.At < until);
        }

        if (query.Before is { } before)
        {
            rows = rows.Where(t => string.CompareOrdinal(t.Id, before) < 0);
        }

        if (query.After is { } after)
        {
            rows = rows.Where(t => string.CompareOrdinal(t.Id, after) > 0);
        }

        var ordered = query.Ascending ? rows.OrderBy(t => t.Id, StringComparer.Ordinal) : rows.OrderByDescending(t => t.Id, StringComparer.Ordinal);
        return [.. ordered.Take(Math.Max(1, query.Limit))];
    }

    /// <summary>Test hook: overwrite a stored wallet balance without a ledger entry.</summary>
    public void TamperBalance(long sessionId, WalletId id, long balance)
    {
        var wallet = _wallets[(sessionId, id)];
        wallet.Balance = balance;
    }
}
