using System.Globalization;

namespace X4MP.Core.Economy;

/// <summary>Result of one audit pass.</summary>
public sealed record AuditReport(bool Ok, IReadOnlyList<string> Violations, DateTimeOffset At);

/// <summary>
/// Verifies the ledger invariants (server-design 2.14 point 7) and freezes the economy on a violation:
/// every transaction sums to zero, the sum of all balances is zero, the stored balances equal the sums of the ledger
/// entries, the in-memory cache equals the database, and no escrow wallet is negative. Other tasks add checks with
/// <see cref="AddCheck"/> (loans and trades: terminal offers hold zero escrow).
/// </summary>
public sealed class EconomyAuditor
{
    private const int MaxReported = 50;

    private readonly EconomyLedger _ledger;
    private readonly IEconomyStore _store;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan> _interval;
    private readonly List<Func<EconomyLedger, IEnumerable<string>>> _checks = [];
    private long _nextDue;
    private bool _started;

    public EconomyAuditor(EconomyLedger ledger, IEconomyStore store, TimeProvider? time = null, Func<TimeSpan>? interval = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(store);
        _ledger = ledger;
        _store = store;
        _time = time ?? TimeProvider.System;
        _interval = interval ?? (() => TimeSpan.FromSeconds(60));
    }

    public AuditReport? LastReport { get; private set; }

    /// <summary>Adds an extra invariant. The check returns one message per violation.</summary>
    public void AddCheck(Func<EconomyLedger, IEnumerable<string>> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        _checks.Add(check);
    }

    /// <summary>Runs the audit when its interval has elapsed (the first call runs at once). Call from the actor tick.</summary>
    public void Tick(long timestamp)
    {
        if (_started && timestamp < _nextDue)
        {
            return;
        }

        _started = true;
        _nextDue = timestamp + (long)(_interval().TotalSeconds * _time.TimestampFrequency);
        RunNow();
    }

    /// <summary>Audits immediately. A violation freezes the economy and raises a critical alert.</summary>
    public AuditReport RunNow()
    {
        var violations = new List<string>();
        void Add(string message)
        {
            if (violations.Count < MaxReported)
            {
                violations.Add(message);
            }
        }

        LedgerAuditData data;
        try
        {
            data = _store.ReadAuditData(_ledger.SessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            data = new LedgerAuditData([], new Dictionary<WalletId, long>(), new Dictionary<WalletId, long>());
            Add("audit could not read the ledger: " + ex.Message);
        }

        foreach (var tx in data.UnbalancedTransactions)
        {
            Add($"transaction {tx} does not sum to zero");
        }

        long entrySum = 0;
        foreach (var sum in data.EntrySums.Values)
        {
            entrySum = unchecked(entrySum + sum);
        }

        if (entrySum != 0)
        {
            Add($"sum of all ledger entries is {entrySum.ToString(CultureInfo.InvariantCulture)}, expected 0");
        }

        foreach (var id in data.EntrySums.Keys.Union(data.StoredBalances.Keys))
        {
            var fromEntries = data.EntrySums.GetValueOrDefault(id);
            var stored = data.StoredBalances.GetValueOrDefault(id);
            if (fromEntries != stored)
            {
                Add($"wallet {id}: stored balance {stored.ToString(CultureInfo.InvariantCulture)} but ledger entries sum to {fromEntries.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        long cacheSum = 0;
        foreach (var wallet in _ledger.Wallets)
        {
            cacheSum = unchecked(cacheSum + wallet.Balance);
            var stored = data.StoredBalances.GetValueOrDefault(wallet.Id);
            if (stored != wallet.Balance)
            {
                Add($"wallet {wallet.Id}: cached balance {wallet.Balance.ToString(CultureInfo.InvariantCulture)} differs from the database ({stored.ToString(CultureInfo.InvariantCulture)})");
            }

            if (wallet.Id.Kind is WalletKind.Escrow or WalletKind.TeamPool && wallet.Balance < 0)
            {
                Add($"wallet {wallet.Id} is negative ({wallet.Balance.ToString(CultureInfo.InvariantCulture)})");
            }
        }

        if (cacheSum != 0)
        {
            Add($"sum of all wallet balances is {cacheSum.ToString(CultureInfo.InvariantCulture)}, expected 0");
        }

        foreach (var check in _checks)
        {
            foreach (var message in check(_ledger))
            {
                Add(message);
            }
        }

        var report = new AuditReport(violations.Count == 0, violations, _time.GetUtcNow());
        LastReport = report;
        if (!report.Ok && !_ledger.IsFrozen)
        {
            _ledger.Freeze($"ledger audit failed: {violations[0]}", violations);
        }

        return report;
    }
}
