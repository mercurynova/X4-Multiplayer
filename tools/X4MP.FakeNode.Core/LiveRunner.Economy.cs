using System.Globalization;
using X4MP.Proto;

namespace X4MP.FakeNode;

public static partial class LiveRunner
{
    /// <summary>
    /// The M1-F4 result: what the clients asked of the economy and how the server answered (<c>economy:</c>), whether a replayed, reused or raced key
    /// ever had a second effect (<c>dupes:</c>), the <c>CreditDelta</c> traffic (<c>income:</c>), whether every node's model of its wallets matched what
    /// the server sent (<c>reconciliation:</c>), and, with <c>--admin-url</c>, the server's own books (<c>invariants:</c>). Returns the number of problems
    /// found (they count as errors: the exit code).
    /// </summary>
    private static async Task<long> WriteEconomySummaryAsync(CliOptions o, List<LiveNodeStats> stats, SynchronizedWriter lines)
    {
        var clients = stats.Where(s => s.Economist is not null).ToList();
        var modelled = stats.Where(s => s.Reconciler is { Updates: > 0 } && (s.Economist is not null || s.Role == Role.Authority)).ToList();
        if (!o.EconomyActive && o.AdminUrl is null)
            return 0;

        long errors = 0;
        if (clients.Count > 0)
        {
            var economists = clients.Select(s => s.Economist!).ToList();
            var sent = economists.SelectMany(e => e.SentByKind).GroupBy(k => k.Key).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Sum(k => k.Value)}");
            var rejects = economists.SelectMany(e => e.RejectReasons).GroupBy(k => k.Key).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Sum(k => k.Value)}");
            await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
                $"economy({o.EffectiveEconomy.ToString().ToLowerInvariant()}): clients={clients.Count} requests={economists.Sum(e => e.RequestsSent)} ok={economists.Sum(e => e.RequestsOk)} " +
                $"rejected={economists.Sum(e => e.RequestsRejected)} skipped={economists.Sum(e => e.Skipped)} sent=[{string.Join(",", sent)}] rejects=[{string.Join(",", rejects)}] " +
                $"loans(offers-sent={economists.Sum(e => e.LoansOffered)} accept-requests={economists.Sum(e => e.LoanAccepts)} decline-requests={economists.Sum(e => e.LoanDeclines)} " +
                $"repay-requests={economists.Sum(e => e.LoanRepayments)} borrowed-open-at-end={economists.Sum(e => e.OpenBorrowed)} overdue-at-end={economists.Sum(e => e.OverdueBorrowed)})")).ConfigureAwait(false);
        }

        if (o.DupeAttack && clients.Count > 0)
        {
            var e = clients.Select(s => s.Economist!).ToList();
            long duplicateEffects = e.Sum(x => x.DuplicateEffects);
            errors += duplicateEffects;
            await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
                $"dupes: replays={e.Sum(x => x.ReplaysSent)}(idempotent={e.Sum(x => x.ReplaysIdempotent)} rate-limited={e.Sum(x => x.ReplaysRateLimited)} other={e.Sum(x => x.ReplaysOther)}) " +
                $"reuses={e.Sum(x => x.ReusesSent)}(rejected={e.Sum(x => x.ReusesRejected)} rate-limited={e.Sum(x => x.ReusesRateLimited)} other={e.Sum(x => x.ReusesOther)}) " +
                $"races={e.Sum(x => x.RacesSent)}(copies={e.Sum(x => x.RaceCopiesSent)}) overdraw-pairs={e.Sum(x => x.OverdrawPairs)}(both-ok={e.Sum(x => x.OverdrawBothOk)}) " +
                $"delta-resends={stats.Sum(s => s.Income?.Resends ?? 0)} duplicate-effects={duplicateEffects}")).ConfigureAwait(false);
        }

        var withIncome = stats.Where(s => s.Income is not null).ToList();
        if (withIncome.Count > 0)
        {
            var authority = withIncome.Where(s => s.Role == Role.Authority).ToList();
            var own = withIncome.Where(s => s.Role != Role.Authority).ToList();
            await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
                $"income: authority-deltas={authority.Sum(s => s.Income!.DeltasSent)}(income={authority.Sum(s => s.Income!.IncomeTotal)} spend={authority.Sum(s => s.Income!.SpendTotal)} resends={authority.Sum(s => s.Income!.Resends)} " +
                $"acked-seq={string.Join("/", authority.Select(s => s.Reconciler?.LastAck ?? 0))}) client-deltas={own.Sum(s => s.Income!.DeltasSent)}(resends={own.Sum(s => s.Income!.Resends)} " +
                $"unacked={own.Sum(s => s.Reconciler?.UnackedDeltas ?? 0)})")).ConfigureAwait(false);
        }

        if (modelled.Count > 0)
        {
            var models = modelled.Select(s => s.Reconciler!).ToList();
            long drift = models.Sum(m => m.Drifts);
            errors += drift;
            await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
                $"reconciliation: nodes={models.Count} updates={models.Sum(m => m.Updates)} steps-checked={models.Sum(m => m.StepsChecked)} results-checked={models.Sum(m => m.ResultsChecked)} " +
                $"unverifiable={models.Sum(m => m.Unverifiable)} stale={models.Sum(m => m.Stale)} unacked={models.Sum(m => m.UnackedDeltas)} drift={drift}")).ConfigureAwait(false);
            foreach (var s in modelled.Where(s => s.Reconciler!.Drifts > 0))
            {
                foreach (var note in s.Reconciler!.Notes.Take(3))
                    await lines.WriteAsync($"[{s.Name}]   drift: {note}").ConfigureAwait(false);
            }
        }

        if (o.AdminUrl is not null)
            errors += await WriteInvariantsAsync(o, stats, modelled, lines).ConfigureAwait(false);
        return errors;
    }

    /// <summary>The server's own books over the admin API: the auditor, a ledger scan for twice-booked requests and deltas, wallet drift, loans and trades.</summary>
    private static async Task<long> WriteInvariantsAsync(CliOptions o, List<LiveNodeStats> stats, List<LiveNodeStats> modelled, SynchronizedWriter lines)
    {
        if (o.AdminPassword is null)
        {
            await lines.WriteAsync("invariants: --admin-url needs --admin-password").ConfigureAwait(false);
            return 1;
        }

        long errors = 0;
        try
        {
            using var probe = await EconomyAdminProbe.SignInAsync(o.AdminUrl!, o.AdminUser, o.AdminPassword, CancellationToken.None).ConfigureAwait(false);
            var audit = await probe.AuditAsync(CancellationToken.None).ConfigureAwait(false);
            var scan = await probe.ScanLedgerAsync(CancellationToken.None).ConfigureAwait(false);
            var wallets = await probe.WalletsAsync(CancellationToken.None).ConfigureAwait(false);
            var loans = await probe.LoansAsync(CancellationToken.None).ConfigureAwait(false);
            var trades = await probe.TradesAsync(CancellationToken.None).ConfigureAwait(false);

            errors += audit.Violations.Count + (audit.EconomyFrozen ? 1 : 0);
            long ledgerSum = wallets.Sum(w => w.Balance);
            bool hasWorld = wallets.Any(w => w.Kind == "World");
            if (hasWorld && ledgerSum != 0)
                errors++;
            errors += scan.DuplicateRequests.Count;

            // every node's CreditDeltas: what the ledger booked for it is a prefix of what it sent
            int deltaChecked = 0;
            int deltaBroken = 0;
            foreach (var s in stats.Where(s => s.Income is { DeltasSent: > 0 } && s.Reconciler is not null))
            {
                deltaChecked++;
                long booked = scan.DeltaBooked.GetValueOrDefault($"player:{s.PlayerId}");
                if (!s.Income!.IsBookedPrefix(booked, s.Reconciler!.LastAck, s.Income.LastSeq))
                {
                    deltaBroken++;
                    await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
                        $"[{s.Name}]   delta books: the ledger booked {booked} for player:{s.PlayerId}, which is not the sum of any prefix of the {s.Income.LastSeq} deltas it sent (acked {s.Reconciler.LastAck})")).ConfigureAwait(false);
                }
            }

            errors += deltaBroken;

            // every node's model against the server's wallets
            var tally = new Dictionary<WalletCheck, int>();
            foreach (var s in modelled)
            {
                foreach (var (kind, owner, _, _) in s.Reconciler!.Snapshot())
                {
                    var row = wallets.FirstOrDefault(w => EconomyAdminProbe.WireKind(w.Kind) == kind && w.OwnerId == owner);
                    if (row is null)
                        continue;
                    var verdict = s.Reconciler.Compare(kind, owner, row.Balance, row.Version);
                    tally[verdict] = tally.GetValueOrDefault(verdict) + 1;
                    if (verdict == WalletCheck.Drift)
                    {
                        errors++;
                        await lines.WriteAsync($"[{s.Name}]   wallet {kind}:{owner} at version {row.Version}: the server holds {row.Balance}, the node's model {s.Reconciler.Wallet(kind, owner)?.Balance}").ConfigureAwait(false);
                    }
                }
            }

            var loanStates = loans.GroupBy(l => l.State).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}");
            int overdue = loans.Count(l => l.Overdue || l.State == "Overdue");
            var tradeStates = trades.GroupBy(t => t.State).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}");
            var inDoubt = trades.Where(t => t.State == "InDoubt").Select(t => t.Id).Order().ToList();
            if (o.LoanDefault && stats.Sum(s => s.Economist?.LoanAccepts ?? 0) > 0 && overdue == 0)
            {
                errors++;
                await lines.WriteAsync("invariants: --loan-default accepted loans but none is overdue").ConfigureAwait(false);
            }

            await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
                $"invariants: auditor={(audit.Ok ? "ok" : "VIOLATED")} violations={audit.Violations.Count} frozen={audit.EconomyFrozen} ledger-transactions={scan.Transactions} " +
                $"ledger-sum={(hasWorld ? ledgerSum.ToString(CultureInfo.InvariantCulture) : "n/a")} duplicate-requests={scan.DuplicateRequests.Count} delta-books={deltaChecked - deltaBroken}/{deltaChecked} " +
                $"wallets(match={tally.GetValueOrDefault(WalletCheck.Match)} behind={tally.GetValueOrDefault(WalletCheck.Behind)} ahead={tally.GetValueOrDefault(WalletCheck.Ahead)} " +
                $"drift={tally.GetValueOrDefault(WalletCheck.Drift)}) loans=[{string.Join(",", loanStates)}] overdue-loans={overdue} trades=[{string.Join(",", tradeStates)}] " +
                $"in-doubt=[{string.Join(",", inDoubt)}]")).ConfigureAwait(false);
            foreach (var violation in audit.Violations.Take(5))
                await lines.WriteAsync($"invariants:   violation: {violation}").ConfigureAwait(false);
            foreach (var duplicate in scan.DuplicateRequests.Take(5))
                await lines.WriteAsync($"invariants:   duplicate: {duplicate}").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            await lines.WriteAsync($"invariants: the admin audit failed: {ex.Message}").ConfigureAwait(false);
            errors++;
        }

        return errors;
    }
}
