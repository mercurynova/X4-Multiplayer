using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using X4MP.Core.Events;
using X4MP.Core.Saves;
using X4MP.Proto;

namespace X4MP.Core.Economy;

/// <summary>What a rollback of the ledger to a checkpoint's mark did (ADR-053).</summary>
/// <param name="Performed">False when nothing was done; <see cref="Skipped"/> says why.</param>
/// <param name="Skipped">Unknown mark, frozen economy, unreadable snapshot...</param>
/// <param name="Reversed">Transactions undone (one compensating transaction each, or 1 for the aggregate fallback).</param>
/// <param name="Kept">Transactions after the mark that survive: admin adjustments, admin reversals of older transactions, starting credits of late joiners, the save-money seed.</param>
/// <param name="Cancelled">Transactions after the mark that cancelled each other already and were left alone.</param>
/// <param name="FromSeq">The ledger's sequence number before the rollback.</param>
/// <param name="ToSeq">The ledger's sequence number after it.</param>
/// <param name="Aggregated">The per-transaction order would have broken a wallet rule (pool or escrow below zero): one net transaction was posted instead.</param>
/// <param name="Drift">Wallets whose balance after the reversal differs from the mark plus the kept transactions (empty when sound).</param>
/// <param name="NewMark">The mark the checkpoint carries from now on (the restored state), to be stored with it.</param>
public sealed record LedgerRollbackResult(
    bool Performed,
    string? Skipped,
    int Reversed,
    int Kept,
    int Cancelled,
    long FromSeq,
    long ToSeq,
    bool Aggregated,
    IReadOnlyList<string> Drift,
    LedgerMark? NewMark)
{
    public static LedgerRollbackResult Skip(string why, long seq = 0) =>
        new(false, why, 0, 0, 0, seq, seq, false, [], null);
}

/// <summary>
/// The ledger follows the checkpoint (ADR-053, F13b). When the authority loads an older checkpoint (F13 resume of an Ended session, the M3-21
/// authority-crash reload) the world goes back to the checkpoint, and so must the credits: everything game-tied after the checkpoint's ledger
/// mark is undone by compensating transactions (the ledger stays append-only), the loans, trades and credit layout return to their state at the
/// mark, and what an admin did by hand survives.
/// </summary>
public sealed partial class EconomyService
{
    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private bool _suppressLoanNotices;

    /// <summary>The state a rollback cannot derive from the ledger entries, as stored with a checkpoint's mark.</summary>
    internal sealed class LedgerSnapshot
    {
        public string? AppliedMode { get; set; }

        public Dictionary<int, int?> PlayerTeams { get; set; } = [];

        public List<LoanRecord> Loans { get; set; } = [];

        public List<TradeRecord> Trades { get; set; } = [];
    }

    /// <summary>The ledger's high-water mark and the loans, trades and credit layout as they are now: what a checkpoint that begins now records.</summary>
    public LedgerMark CaptureMark()
    {
        var snapshot = new LedgerSnapshot
        {
            AppliedMode = _ledger.Layout.AppliedMode,
            PlayerTeams = new Dictionary<int, int?>(_ledger.Layout.PlayerTeams),
            Loans = [.. _ledger.Loans.OrderBy(l => l.Id)],
            Trades = [.. _trades.Values.OrderBy(t => t.Id)],
        };
        return new LedgerMark(_store.HighWaterSeq(_ledger.SessionId), JsonSerializer.Serialize(snapshot, SnapshotJson));
    }

    /// <summary>
    /// Rolls the ledger back to <paramref name="mark"/>: see the class summary. Safe to repeat (a second run finds nothing after the new mark).
    /// A null mark (unknown: the checkpoint predates ADR-053 or was stored without an economy) reverses nothing: the ledger is never guessed at.
    /// </summary>
    /// <param name="label">How the checkpoint is named in notes and the audit log (its id).</param>
    public LedgerRollbackResult RollBackToMark(LedgerMark? mark, string label, string reason, string actor = "system")
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(reason);
        var now = _time.GetUtcNow();
        var session = _ledger.SessionId;
        var high = _store.HighWaterSeq(session);
        if (mark is null)
        {
            _events?.Publish(new AlertRaised(now, session, AlertSeverity.Warning, "economy_rollback_unknown_mark",
                $"Checkpoint {label} has no ledger mark (stored before the ledger followed checkpoints): wallets, loans and trades were NOT rolled back."));
            return LedgerRollbackResult.Skip("the checkpoint has no ledger mark", high);
        }

        if (_ledger.IsFrozen)
        {
            Fail(now, session, label, "the economy is frozen");
            return LedgerRollbackResult.Skip("the economy is frozen", high);
        }

        LedgerSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<LedgerSnapshot>(mark.Snapshot, SnapshotJson);
        }
        catch (JsonException)
        {
            snapshot = null;
        }

        if (snapshot is null)
        {
            Fail(now, session, label, "the stored snapshot is unreadable");
            return LedgerRollbackResult.Skip("the stored snapshot is unreadable", high);
        }

        if (mark.Seq > high)
        {
            Fail(now, session, label, "the mark is ahead of the ledger");
            return LedgerRollbackResult.Skip("the mark is ahead of the ledger", high);
        }

        var after = _store.TransactionsAfter(session, mark.Seq);
        var (undo, kept, cancelled) = Classify(after);
        var note = $"rollback to checkpoint {label}: {reason}";

        // 1. Undo, newest first, one compensating transaction each (every wallet then walks back through states it really had).
        var ordered = undo.OrderByDescending(t => t.Seq).ToList();
        var aggregate = !ReplayStaysValid(ordered);
        var reversed = 0;
        if (aggregate)
        {
            var net = new SortedDictionary<WalletId, long>(Comparer<WalletId>.Create(CompareWallets));
            foreach (var entry in ordered.SelectMany(t => t.Entries))
            {
                net[entry.Wallet] = net.GetValueOrDefault(entry.Wallet) - entry.Amount;
            }

            var entries = net.Where(kv => kv.Value != 0).Select(kv => new PostEntry(kv.Key, kv.Value)).ToList();
            if (entries.Count >= 2 && !PostCompensation(entries, null, mark.Seq, $"{note} (net of {ordered.Count} transactions)", actor))
            {
                return Failed(now, session, label, "the net reversal was refused", high);
            }

            reversed = entries.Count >= 2 ? 1 : 0;
        }
        else
        {
            foreach (var tx in ordered)
            {
                var entries = tx.Entries.Select(e => new PostEntry(e.Wallet, -e.Amount)).ToList();
                var link = tx.ReversedBy is null ? tx.Id : null;
                if (!PostCompensation(entries, link, mark.Seq, $"{note}; undoes {tx.Kind} {tx.Id}", actor))
                {
                    return Failed(now, session, label, $"reversing {tx.Id} was refused", high);
                }

                reversed++;
            }
        }

        var drift = CheckBalances(mark.Seq, kept);

        // 2. The credit layout returns to the mark (players who joined later stay known: their starting credits are kept).
        RestoreLayout(snapshot, actor);

        // 3. Loans and trades return to their state at the mark.
        var loansRestored = RestoreLoans(snapshot.Loans, label, actor);
        var tradesRestored = RestoreTrades(snapshot.Trades, label);

        // 4. A layout change an admin made after the mark (a mode switch, a team move) is applied again on the restored balances.
        Reconcile(confirm: true, actor: "system");

        var newMark = CaptureMark();
        var toSeq = newMark.Seq;
        var data = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["reason"] = reason,
            ["checkpoint"] = label,
            ["markSeq"] = mark.Seq.ToString(CultureInfo.InvariantCulture),
            ["fromSeq"] = high.ToString(CultureInfo.InvariantCulture),
            ["toSeq"] = toSeq.ToString(CultureInfo.InvariantCulture),
            ["reversed"] = reversed.ToString(CultureInfo.InvariantCulture),
            ["kept"] = string.Join(',', kept.Select(t => t.Id)),
            ["aggregated"] = aggregate ? "true" : null,
            ["loansRestored"] = loansRestored.ToString(CultureInfo.InvariantCulture),
            ["tradesRestored"] = tradesRestored.ToString(CultureInfo.InvariantCulture),
            ["drift"] = drift.Count == 0 ? null : string.Join("; ", drift),
        };
        _events?.Publish(new AdminActionTaken(now, session, actor, "economy.rollback", "checkpoint:" + label, data, null));
        if (drift.Count > 0)
        {
            _events?.Publish(new AlertRaised(now, session, AlertSeverity.Critical, "economy_rollback_drift",
                $"After the rollback to checkpoint {label} {drift.Count} wallet(s) differ from the mark: {string.Join("; ", drift.Take(5))}"));
        }

        return new LedgerRollbackResult(true, null, reversed, kept.Count, cancelled, high, toSeq, aggregate, drift, newMark);
    }

    /// <summary>
    /// Sorts the transactions after the mark: <c>undo</c> are rolled back; <c>kept</c> stay (an admin did them by hand, or they belong to a
    /// player who joined after the mark); <c>cancelled</c> pairs (a transaction and the reversal that undid it, both after the mark) are left
    /// alone because they net to zero.
    /// </summary>
    private (List<LedgerTransaction> Undo, List<LedgerTransaction> Kept, int Cancelled) Classify(IReadOnlyList<LedgerTransaction> after)
    {
        var byId = after.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var paired = new HashSet<string>(StringComparer.Ordinal);
        var pairs = 0;
        foreach (var tx in after)
        {
            if (!paired.Contains(tx.Id) && tx.ReversedBy is { } by && byId.ContainsKey(by) && !paired.Contains(by))
            {
                paired.Add(tx.Id);
                paired.Add(by);
                pairs++;
            }
        }

        var undo = new List<LedgerTransaction>();
        var kept = new List<LedgerTransaction>();
        foreach (var tx in after)
        {
            if (paired.Contains(tx.Id))
            {
                continue;
            }

            (IsKept(tx, byId) ? kept : undo).Add(tx);
        }

        return (undo, kept, pairs);
    }

    /// <summary>Admin credit adjustments, admin reversals of transactions from before the mark, the starting credits of a player who first joined after the mark, and the save-money seed.</summary>
    private bool IsKept(LedgerTransaction tx, Dictionary<string, LedgerTransaction> after)
    {
        switch (tx.Kind)
        {
            case TxKind.AdminAdjust:
            case TxKind.StartingCredits:
            case TxKind.SaveMoney:
                return true;
            case TxKind.Reversal when tx.Actor.StartsWith("admin:", StringComparison.Ordinal) && tx.Reverses is { } original && !after.ContainsKey(original):
                var target = _ledger.FindTransaction(original);
                return target is not null && target.Kind is not (TxKind.LoanEscrow or TxKind.LoanDisburse or TxKind.LoanRepay or TxKind.LoanAutoRepay or TxKind.LoanRefund
                    or TxKind.TradeEscrow or TxKind.TradeSettle or TxKind.TradeRefund or TxKind.ModeMigration or TxKind.TeamMove or TxKind.Reversal);
            default:
                return false;
        }
    }

    /// <summary>True when undoing the transactions one by one, newest first, never takes a pool or escrow wallet below zero.</summary>
    private bool ReplayStaysValid(List<LedgerTransaction> ordered)
    {
        var balances = _ledger.Wallets.ToDictionary(w => w.Id, w => w.Balance);
        foreach (var tx in ordered)
        {
            foreach (var entry in tx.Entries)
            {
                var next = balances.GetValueOrDefault(entry.Wallet) - entry.Amount;
                balances[entry.Wallet] = next;
            }

            foreach (var entry in tx.Entries)
            {
                if (entry.Wallet.Kind is WalletKind.TeamPool or WalletKind.Escrow && balances[entry.Wallet] < 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool PostCompensation(IReadOnlyList<PostEntry> entries, string? reverses, long markSeq, string note, string actor)
    {
        var outcome = _ledger.Post(new PostRequest
        {
            Kind = TxKind.Reversal,
            Actor = actor,
            Entries = entries,
            RefType = "Rollback",
            RefId = markSeq,
            Reverses = reverses,
            Note = note,
            Flags = PostOptions.AllowOverdraw | PostOptions.BypassWalletFreeze,
        });
        return outcome.Ok;
    }

    /// <summary>Compares every wallet with the balance it had at the mark plus the transactions that were kept.</summary>
    private List<string> CheckBalances(long markSeq, List<LedgerTransaction> kept)
    {
        var expected = new Dictionary<WalletId, long>(_store.BalancesAt(_ledger.SessionId, markSeq));
        foreach (var entry in kept.SelectMany(t => t.Entries))
        {
            expected[entry.Wallet] = expected.GetValueOrDefault(entry.Wallet) + entry.Amount;
        }

        var drift = new List<string>();
        foreach (var wallet in expected.Keys.Union(_ledger.Wallets.Select(w => w.Id)).OrderBy(w => w.Kind).ThenBy(w => w.OwnerId))
        {
            var want = expected.GetValueOrDefault(wallet);
            var have = _ledger.BalanceOf(wallet);
            if (want != have)
            {
                drift.Add($"{wallet}: {have.ToString(CultureInfo.InvariantCulture)} instead of {want.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        return drift;
    }

    private void RestoreLayout(LedgerSnapshot snapshot, string actor)
    {
        var teams = new Dictionary<int, int?>(snapshot.PlayerTeams);
        foreach (var (player, team) in _ledger.Layout.PlayerTeams)
        {
            teams.TryAdd(player, team); // joined after the mark: still registered (their starting credits were kept), so a rejoin does not credit them again
        }

        var mode = snapshot.AppliedMode ?? _ledger.Layout.AppliedMode;
        var current = _ledger.Layout;
        if (mode == current.AppliedMode && teams.Count == current.PlayerTeams.Count && teams.All(kv => current.PlayerTeams.TryGetValue(kv.Key, out var t) && t == kv.Value))
        {
            return;
        }

        _ledger.Post(new PostRequest { Kind = TxKind.Reversal, Actor = actor, Entries = [], Layout = new EconomyLayout(mode, teams) });
    }

    private int RestoreLoans(IReadOnlyList<LoanRecord> snapshot, string label, string actor)
    {
        var atMark = snapshot.ToDictionary(l => l.Id);
        var changed = new List<LoanRecord>();
        var closeReason = "rolled back to checkpoint " + label;
        foreach (var loan in _ledger.Loans.OrderBy(l => l.Id))
        {
            if (atMark.TryGetValue(loan.Id, out var old))
            {
                if (old != loan)
                {
                    changed.Add(old);
                }
            }
            else if (!(loan.State == LoanState.Cancelled && loan.CloseReason?.StartsWith("rolled back to checkpoint", StringComparison.Ordinal) == true))
            {
                changed.Add(loan with { State = LoanState.Cancelled, Outstanding = 0, ClosedAt = Now, CloseReason = closeReason });
            }
        }

        if (changed.Count == 0)
        {
            return 0;
        }

        _suppressLoanNotices = true;
        try
        {
            _ledger.Post(new PostRequest { Kind = TxKind.Reversal, Actor = actor, Entries = [], Loans = changed });
        }
        finally
        {
            _suppressLoanNotices = false;
        }

        return changed.Count;
    }

    private int RestoreTrades(IReadOnlyList<TradeRecord> snapshot, string label)
    {
        var atMark = snapshot.ToDictionary(t => t.Id);
        var count = 0;
        var detail = "rolled back to checkpoint " + label;
        foreach (var live in _trades.Values.OrderBy(t => t.Id).ToList())
        {
            if (!atMark.TryGetValue(live.Id, out var old))
            {
                // Created after the mark: it never happened in the restored timeline.
                if (live.State is not (TradeState.RolledBack or TradeState.Cancelled or TradeState.Expired or TradeState.Rejected))
                {
                    live.ResolvedBy = "system";
                    Transition(live, TradeState.RolledBack, EconomyReject.None, "system", detail);
                    count++;
                }

                continue;
            }

            if (live.ToJson() == old.ToJson())
            {
                continue;
            }

            var previous = live.State;
            Unlock(live);
            _trades[old.Id] = old;
            if (old.IsOpen)
            {
                LockAll(old);
            }

            _ = Persist(old); // a lock conflict cannot happen here: the locks follow the restored set
            IndexRequests(old);
            PublishTrade(old, previous, old.State, EconomyReject.None, "system", detail);
            RaiseTradeChanged(old, previous);
            count++;
        }

        // The authority that reloads has lost the transfer order, and the money of the escrow is back where it was: end such a trade by refunding it.
        foreach (var trade in _trades.Values.Where(t => t.IsOpen && _ledger.BalanceOf(t.EscrowWallet) > 0).OrderBy(t => t.Id).ToList())
        {
            RefundAfterRollback(trade, detail);
            count++;
        }

        return count;
    }

    /// <summary>Like the normal rollback of a trade, with a request key of its own (the key of the first refund may be used already).</summary>
    private void RefundAfterRollback(TradeRecord trade, string detail)
    {
        var escrow = _ledger.BalanceOf(trade.EscrowWallet);
        var payer = PayerOf(trade);
        var outcome = _ledger.Post(new PostRequest
        {
            Kind = TxKind.TradeRefund,
            Actor = "system",
            RefType = "trade",
            RefId = trade.Id,
            Entries = [new(trade.EscrowWallet, -escrow), new(EffectiveWallet(payer), escrow)],
            Note = detail,
            Flags = PostOptions.BypassWalletFreeze,
        });
        if (!outcome.Ok)
        {
            Park(trade, EconomyReject.EconomyFrozen, "system", "the ledger refused the refund after a rollback: " + outcome.Detail);
            return;
        }

        Transition(trade, TradeState.RolledBack, EconomyReject.None, "system", detail);
    }

    private void Fail(DateTimeOffset now, long session, string label, string why) =>
        _events?.Publish(new AlertRaised(now, session, AlertSeverity.Critical, "economy_rollback_failed",
            $"The ledger could not follow the rollback to checkpoint {label}: {why}. Wallets may not match the restored world."));

    private LedgerRollbackResult Failed(DateTimeOffset now, long session, string label, string why, long from)
    {
        Fail(now, session, label, why);
        return LedgerRollbackResult.Skip(why, from);
    }
}
