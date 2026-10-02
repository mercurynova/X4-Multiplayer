using System.Globalization;
using X4MP.Proto;

namespace X4MP.FakeNode;

/// <summary>
/// The client-side model of the server's ledger as one node sees it (M1-F4, server-design 2.14, "reconciliation"): it follows every
/// <c>WalletUpdate</c> and <c>EconomyResult</c> the node gets, remembers the <c>CreditDelta{seq}</c>s the node sent, and checks that what the server
/// reports adds up. No I/O; feed it the decoded messages.
/// <para>
/// The checks (a wallet's <c>version</c> counts its committed changes, so two updates with consecutive versions are one ledger transaction):
/// </para>
/// <list type="bullet">
/// <item><b>Ack.</b> <c>acked_delta_seq</c> never goes backwards and never passes the highest <c>seq</c> this node sent. When it advances, the wallet
/// change of that same update equals the amount of the delta(s) it acknowledges (a duplicate <c>seq</c> must leave the balance alone).</item>
/// <item><b>Conservation</b> (a node that sees every wallet, the authority). A transfer, pool movement, donation, loan or trade posting moves credits
/// between wallets: the changes of one update sum to zero.</item>
/// <item><b>Results.</b> The balances of an <c>EconomyResult</c> equal the balance the updates reported for the same wallet version (a replayed request
/// returns the stored result, so a second effect would show as a version the updates never reported or a different balance).</item>
/// <item><b>Signs.</b> A player or shared wallet never goes negative except through game spending.</item>
/// </list>
/// A version gap (a lost or reordered update, a reconnect) makes a step unverifiable: it is counted, not reported as drift.
/// </summary>
public sealed class EconomyReconciler(int playerId, bool seesAllWallets)
{
    private const int HistoryLimit = 128;
    private const int NoteLimit = 12;

    private readonly object _gate = new();
    private readonly Dictionary<(WalletKind Kind, ushort Owner), Track> _wallets = [];
    private readonly Dictionary<ulong, long> _deltas = [];
    private readonly List<string> _notes = [];
    private ulong _lastAck;
    private ulong _maxSeq;
    private EffectiveCreditMode _mode = EffectiveCreditMode.PerPlayer;

    private sealed class Track
    {
        public long Balance;
        public ulong Version;
        public readonly Dictionary<ulong, long> History = [];
    }

    public int PlayerId => playerId;

    public long Updates { get; private set; }

    /// <summary>Steps checked against an expectation (consecutive versions with something to compare).</summary>
    public long StepsChecked { get; private set; }

    /// <summary>Steps that could not be checked because a version was missing.</summary>
    public long Unverifiable { get; private set; }

    public long Stale { get; private set; }

    /// <summary>Escrow balances left out (their wire id is not unique).</summary>
    public long EscrowSkipped { get; private set; }

    public long ResultsChecked { get; private set; }

    /// <summary>Everything that did not add up (the first few are in <see cref="Notes"/>).</summary>
    public long Drifts { get; private set; }

    public IReadOnlyList<string> Notes
    {
        get
        {
            lock (_gate)
                return [.. _notes];
        }
    }

    /// <summary>Highest <c>CreditDelta.seq</c> the server has acknowledged.</summary>
    public ulong LastAck
    {
        get
        {
            lock (_gate)
                return _lastAck;
        }
    }

    public ulong MaxSeqSent
    {
        get
        {
            lock (_gate)
                return _maxSeq;
        }
    }

    /// <summary>Deltas this node sent that the server has not acknowledged yet.</summary>
    public int UnackedDeltas
    {
        get
        {
            lock (_gate)
                return _deltas.Keys.Count(s => s > _lastAck);
        }
    }

    public EffectiveCreditMode Mode
    {
        get
        {
            lock (_gate)
                return _mode;
        }
    }

    /// <summary>Records a delta this node sent (the model needs its amount to explain the ack).</summary>
    public void NoteDeltaSent(ulong seq, long amount)
    {
        lock (_gate)
        {
            _deltas.TryAdd(seq, amount);
            if (seq > _maxSeq)
                _maxSeq = seq;
        }
    }

    /// <summary>The balance and version of a wallet as the updates last reported it.</summary>
    public (long Balance, ulong Version)? Wallet(WalletKind kind, ushort owner)
    {
        lock (_gate)
            return _wallets.TryGetValue((kind, owner), out var t) ? (t.Balance, t.Version) : null;
    }

    /// <summary>The wallet the node's game money lives in (its own, or its team's shared wallet in Shared mode) and its balance; null before the first update.</summary>
    public (WalletKind Kind, ushort Owner, long Balance, ulong Version)? EffectiveWallet()
    {
        lock (_gate)
        {
            if (_mode == EffectiveCreditMode.Shared)
            {
                foreach (var ((kind, owner), t) in _wallets)
                {
                    if (kind == WalletKind.TeamShared)
                        return (kind, owner, t.Balance, t.Version);
                }
            }

            return _wallets.TryGetValue((WalletKind.Player, (ushort)playerId), out var own)
                ? (WalletKind.Player, (ushort)playerId, own.Balance, own.Version)
                : null;
        }
    }

    /// <summary>Every wallet this node knows: kind, owner, balance, version.</summary>
    public IReadOnlyList<(WalletKind Kind, ushort Owner, long Balance, ulong Version)> Snapshot()
    {
        lock (_gate)
            return [.. _wallets.Select(w => (w.Key.Kind, w.Key.Owner, w.Value.Balance, w.Value.Version))];
    }

    private void Drift(string note)
    {
        Drifts++;
        if (_notes.Count < NoteLimit)
            _notes.Add(note);
    }

    private static bool Conserves(LedgerReason reason) => reason is LedgerReason.Transfer or LedgerReason.PoolDeposit or LedgerReason.PoolWithdraw
        or LedgerReason.Donation or LedgerReason.LoanPrincipal or LedgerReason.LoanRepayment or LedgerReason.TradeEscrow or LedgerReason.TradeSettle
        or LedgerReason.TradeRollback;

    /// <summary>Processes one <c>WalletUpdate</c>.</summary>
    public void OnWalletUpdate(WalletUpdateT update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            Updates++;
            _mode = update.EffectiveMode;
            ulong ack = update.AckedDeltaSeq;
            if (ack < _lastAck)
                Drift(string.Create(CultureInfo.InvariantCulture, $"acked_delta_seq went backwards: {_lastAck} -> {ack}"));
            else if (ack > _maxSeq)
                Drift(string.Create(CultureInfo.InvariantCulture, $"acked_delta_seq {ack} is beyond the highest seq sent ({_maxSeq})"));

            bool contiguous = true;
            long sum = 0;
            foreach (var b in update.Balances ?? [])
            {
                if (b.Wallet.Kind == WalletKind.Escrow)
                {
                    // The wire clamps an escrow's owner (the trade or loan number) to 16 bits, so two escrows can share one id: they cannot be followed.
                    EscrowSkipped++;
                    contiguous = false;
                    continue;
                }

                var key = (b.Wallet.Kind, b.Wallet.OwnerId);
                if (!_wallets.TryGetValue(key, out var t))
                {
                    t = new Track();
                    _wallets[key] = t;
                    if (b.Version == 1)
                    {
                        sum += b.Balance; // a new wallet starts at 0
                    }
                    else
                    {
                        contiguous = false; // first sight of a wallet that has history: a baseline, not a step
                    }
                }
                else if (b.Version <= t.Version)
                {
                    Stale++;
                    contiguous = false;
                    if (b.Version == t.Version && b.Balance != t.Balance)
                        Drift(string.Create(CultureInfo.InvariantCulture, $"{Name(key)} v{b.Version}: balance {b.Balance} here, {t.Balance} before"));
                    continue;
                }
                else if (b.Version == t.Version + 1)
                {
                    sum += b.Balance - t.Balance;
                }
                else
                {
                    Unverifiable++;
                    contiguous = false;
                }

                if (b.Balance < 0 && key.Kind is WalletKind.Player or WalletKind.TeamShared && update.Reason != LedgerReason.GameSpend)
                    Drift(string.Create(CultureInfo.InvariantCulture, $"{Name(key)} v{b.Version} is negative ({b.Balance}) after {update.Reason}"));
                Remember(t, b.Version, b.Balance);
            }

            ulong previousAck = _lastAck;
            if (ack > _lastAck)
                _lastAck = ack;
            if (ack > previousAck)
            {
                CheckAck(previousAck, ack, contiguous, sum);
            }
            else if (seesAllWallets && contiguous && Conserves(update.Reason) && update.Balances is { Count: > 0 })
            {
                StepsChecked++;
                if (sum != 0)
                    Drift(string.Create(CultureInfo.InvariantCulture, $"{update.Reason} does not conserve credits: the wallets changed by {sum} in all"));
            }
        }
    }

    private void CheckAck(ulong from, ulong to, bool contiguous, long sum)
    {
        long expected = 0;
        for (ulong seq = from + 1; seq <= to; seq++)
        {
            if (!_deltas.TryGetValue(seq, out long amount))
            {
                Unverifiable++; // a seq this model never saw (sent before a reconnect)
                return;
            }

            expected += amount;
        }

        if (!contiguous)
        {
            Unverifiable++;
            return;
        }

        StepsChecked++;
        if (sum != expected)
        {
            Drift(string.Create(CultureInfo.InvariantCulture,
                $"CreditDelta seq {from + 1}..{to} should have changed the wallets by {expected}, they changed by {sum}"));
        }
    }

    private static void Remember(Track t, ulong version, long balance)
    {
        t.Balance = balance;
        t.Version = version;
        t.History[version] = balance;
        if (t.History.Count > HistoryLimit)
            t.History.Remove(t.History.Keys.Min());
    }

    /// <summary>Compares the balances of an <c>EconomyResult</c> with what the updates reported for the same wallet versions.</summary>
    public void OnResult(EconomyResultT result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            foreach (var b in result.Balances ?? [])
            {
                if (b.Wallet.Kind == WalletKind.Escrow)
                    continue;
                if (!_wallets.TryGetValue((b.Wallet.Kind, b.Wallet.OwnerId), out var t) || !t.History.TryGetValue(b.Version, out long known))
                    continue; // no update for that version (yet, or any more): nothing to compare
                ResultsChecked++;
                if (known != b.Balance)
                {
                    Drift(string.Create(CultureInfo.InvariantCulture,
                        $"{Name((b.Wallet.Kind, b.Wallet.OwnerId))} v{b.Version}: the result says {b.Balance}, the update said {known}"));
                }
            }
        }
    }

    /// <summary>
    /// Compares a wallet reading of the server (the admin API: balance and version) with this node's model. A server that is ahead (a newer version)
    /// is a node that has not caught up, not drift; the same version with another balance is drift.
    /// </summary>
    public WalletCheck Compare(WalletKind kind, ushort owner, long serverBalance, ulong serverVersion)
    {
        lock (_gate)
        {
            if (!_wallets.TryGetValue((kind, owner), out var t))
                return WalletCheck.Unknown;
            if (serverVersion > t.Version)
                return WalletCheck.Behind;
            if (serverVersion < t.Version)
                return WalletCheck.Ahead;
            return serverBalance == t.Balance ? WalletCheck.Match : WalletCheck.Drift;
        }
    }

    private static string Name((WalletKind Kind, ushort Owner) key) => $"{key.Kind}:{key.Owner}";

    public string Summary() => string.Create(CultureInfo.InvariantCulture,
        $"updates={Updates} steps-checked={StepsChecked} results-checked={ResultsChecked} unverifiable={Unverifiable} stale={Stale} unacked={UnackedDeltas} drift={Drifts}");
}

/// <summary>The outcome of comparing one server wallet reading with a node's model.</summary>
public enum WalletCheck
{
    Unknown,
    Match,

    /// <summary>The server has committed changes this node has not seen yet.</summary>
    Behind,

    /// <summary>The node is ahead of the reading (the reading was taken earlier).</summary>
    Ahead,

    /// <summary>Same version, different balance.</summary>
    Drift,
}
