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

    /// <summary>When true, <see cref="Commit"/> throws (simulates a full or failing disk).</summary>
    public bool FailCommits { get; set; }

    public IReadOnlyList<LedgerTransaction> Transactions => _transactions;

    public EconomyLoad Load(long sessionId) => new(
        _wallets.Where(kv => kv.Key.Session == sessionId).Select(kv => kv.Value.Clone()).ToList(),
        _deltaSeqs.Where(kv => kv.Key.Session == sessionId).ToDictionary(kv => kv.Key.Player, kv => kv.Value),
        _layouts.GetValueOrDefault(sessionId) ?? new EconomyLayout(null, new Dictionary<int, int?>()));

    public void Commit(EconomyCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (FailCommits)
        {
            throw new IOException("simulated commit failure");
        }

        if (commit.Transaction is { } tx)
        {
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

    /// <summary>Test hook: overwrite a stored wallet balance without a ledger entry.</summary>
    public void TamperBalance(long sessionId, WalletId id, long balance)
    {
        var wallet = _wallets[(sessionId, id)];
        wallet.Balance = balance;
    }
}
