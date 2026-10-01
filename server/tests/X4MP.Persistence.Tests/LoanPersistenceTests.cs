using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Economy;
using X4MP.Proto;
using LoanState = X4MP.Core.Economy.LoanState;
using WalletKind = X4MP.Core.Economy.WalletKind;

namespace X4MP.Persistence.Tests;

/// <summary>Loans on SQLite: they survive a restart (with their escrow and timers) and commit atomically with the ledger (M1-E4).</summary>
public sealed class LoanPersistenceTests : IDisposable
{
    private const long Session = 1;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-loans-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly TestTeams _teams = new();
    private readonly EconomyOptions _options = new() { StartingCredits = 0, CreditMode = CreditMode.PerPlayer, LoanScope = EconomyScope.Anyone };

    public LoanPersistenceTests()
    {
        _factory = new SqliteConnectionFactory(new PersistenceOptions { DataDir = _dir });
        new MigrationRunner(_factory).Migrate();
        using var db = _factory.Open();
        db.Execute("INSERT INTO sessions (id, name, state, settings_json, created_at) VALUES (1, 'test', 'Idle', '{}', '2026-10-01T12:00:00Z')");
        _teams.Set([1, 2], new Dictionary<int, int?> { [1] = 1, [2] = 1, [3] = 2, [4] = 2 });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort temp cleanup
        }
    }

    /// <summary>A "server process": a fresh store, ledger and service over the same database file.</summary>
    private sealed record Node(SqliteEconomyStore Store, EconomyLedger Ledger, EconomyService Service, EconomyAuditor Auditor) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }

    private Node Start()
    {
        var store = new SqliteEconomyStore(_factory, fullSync: false);
        var ledger = new EconomyLedger(Session, store, _time);
        var service = new EconomyService(ledger, store, _teams, () => _options, _time);
        service.Start();
        var auditor = new EconomyAuditor(ledger, store, _time);
        auditor.AddCheck(EconomyService.AuditLoans);
        return new Node(store, ledger, service, auditor);
    }

    private static void Fund(Node node, int player, long amount) =>
        Assert.True(node.Service.BookCreditDelta(player, false, new CreditDeltaT { Amount = amount, Seq = 0 }).Booked);

    [Fact]
    public void LoansEscrowAndTimersSurviveARestart()
    {
        List<LoanRecord> before;
        long offered;
        long active;
        using (var node = Start())
        {
            foreach (var p in new[] { 1, 2, 3, 4 })
            {
                node.Service.EnsurePlayer(p);
            }

            Fund(node, 1, 10_000);
            Fund(node, 3, 10_000);
            offered = node.Service.OfferLoan(1, "o1", 2, 1000, 1100, 600, 60, 0, "first").Loan!.Id;
            active = node.Service.OfferLoan(3, "o2", 4, 2000, 2400, 100, 300, 25, "second").Loan!.Id;
            Assert.True(node.Service.RespondLoan(4, "r2", active, true).Ok);
            _time.Advance(TimeSpan.FromSeconds(5));
            Fund(node, 4, 1000); // auto-repay 25%: 250 to the lender
            Assert.True(node.Service.RepayLoan(4, "p2", active, 150).Ok);
            before = [.. node.Service.Loans.OrderBy(l => l.Id)];
            Assert.Equal(2000, node.Service.FindLoan(active)!.Outstanding);
            Assert.True(node.Auditor.RunNow().Ok);
        }

        using var restarted = Start();

        // The same loans, to the last field, and the same escrow.
        Assert.Equal(before, [.. restarted.Service.Loans.OrderBy(l => l.Id)]);
        Assert.Equal(1000, restarted.Ledger.BalanceOf(LoanWallets.Escrow(offered)));
        Assert.Equal(0, restarted.Ledger.BalanceOf(LoanWallets.Escrow(active)));
        Assert.Equal(0, restarted.Ledger.TotalBalance());
        Assert.True(restarted.Auditor.RunNow().Ok);

        // Replays still answer from the stored request, and ids continue.
        var replay = restarted.Service.OfferLoan(1, "o1", 2, 1000, 1100, 600, 60, 0, "first");
        Assert.True(replay.Ok);
        Assert.Equal(offered, replay.Loan!.Id);
        Assert.Equal(2, restarted.Service.Loans.Count); // the replay created nothing
        Assert.Equal(3, restarted.Service.OfferLoan(1, "o3", 2, 10, 10, 0, 300, 0).Loan!.Id); // ids 1 and 2 are taken

        // Auto-repay is still armed.
        Fund(restarted, 4, 400); // 100 more to the lender
        Assert.Equal(1900, restarted.Service.FindLoan(active)!.Outstanding);

        // The timers are re-armed: the offer expires, the accepted loan turns overdue, all from the persisted due times.
        _time.Advance(TimeSpan.FromSeconds(100)); // 105 s after acceptance of #2, 100 s after offering #1
        Assert.Equal(2, restarted.Service.ProcessLoanTimers());
        Assert.Equal(LoanState.Expired, restarted.Service.FindLoan(offered)!.State);
        Assert.Equal(LoanState.Overdue, restarted.Service.FindLoan(active)!.State);
        Assert.Equal(10_000 - 10, restarted.Ledger.BalanceOf(WalletId.Player(1))); // the escrow of #1 came back; offer #3 holds 10
        Assert.True(restarted.Auditor.RunNow().Ok);

        // And once more across a second restart: the states are durable.
        restarted.Dispose();
        using var third = Start();
        Assert.Equal(LoanState.Expired, third.Service.FindLoan(offered)!.State);
        Assert.Equal(LoanState.Overdue, third.Service.FindLoan(active)!.State);
        Assert.Equal(0, third.Service.ProcessLoanTimers());
        Assert.True(third.Auditor.RunNow().Ok);
    }

    [Fact]
    public void AnOfferThatExpiredWhileTheServerWasDownIsRefundedOnTheFirstTimerRun()
    {
        long id;
        using (var node = Start())
        {
            node.Service.EnsurePlayer(1);
            node.Service.EnsurePlayer(2);
            Fund(node, 1, 5000);
            id = node.Service.OfferLoan(1, "o", 2, 3000, 3000, 0, 60, 0).Loan!.Id;
        }

        _time.Advance(TimeSpan.FromHours(2)); // the server was off

        using var restarted = Start();
        Assert.Equal(LoanState.Offered, restarted.Service.FindLoan(id)!.State);
        Assert.Equal(1, restarted.Service.ProcessLoanTimers());

        Assert.Equal(LoanState.Expired, restarted.Service.FindLoan(id)!.State);
        Assert.Equal(5000, restarted.Ledger.BalanceOf(WalletId.Player(1)));
        Assert.Equal(0, restarted.Ledger.BalanceOf(LoanWallets.Escrow(id)));
        Assert.Equal(LoanState.Expired.ToString(), Scalar<string>($"SELECT state FROM loans WHERE id = {id}"));
        Assert.Equal(0L, Scalar<long>($"SELECT balance FROM wallets WHERE kind = 'escrow' AND owner_id = {LoanWallets.Escrow(id).OwnerId}"));
    }

    [Fact]
    public void ALoanRowAndItsLedgerTransactionCommitTogether()
    {
        using var node = Start();
        node.Service.EnsurePlayer(1);
        node.Service.EnsurePlayer(2);
        Fund(node, 1, 5000);

        var id = node.Service.OfferLoan(1, "o", 2, 1000, 1200, 0, 300, 10).Loan!.Id;
        Assert.True(node.Service.RespondLoan(2, "r", id, true).Ok);

        Assert.Equal("Active", Scalar<string>($"SELECT state FROM loans WHERE id = {id}"));
        Assert.Equal(1200L, Scalar<long>($"SELECT outstanding FROM loans WHERE id = {id}"));
        Assert.Equal(2L, Scalar<long>($"SELECT COUNT(*) FROM ledger_tx WHERE ref_type = 'Loan' AND ref_id = {id}")); // escrow + disburse
        Assert.Equal(0L, Scalar<long>($"SELECT balance FROM wallets WHERE kind = 'escrow'"));
    }

    [Fact]
    public void RandomLoanTrafficOnSqliteSurvivesARestartUnchanged()
    {
        var rng = new Random(20261001);
        List<LoanRecord> before;
        List<(WalletId, long)> wallets;
        using (var node = Start())
        {
            for (var p = 1; p <= 4; p++)
            {
                node.Service.EnsurePlayer(p);
            }

            var ids = new List<long>();
            for (var i = 0; i < 1500; i++)
            {
                var a = rng.Next(1, 5);
                var b = rng.Next(1, 5);
                var loan = ids.Count == 0 ? 0 : ids[rng.Next(ids.Count)];
                switch (rng.Next(9))
                {
                    case 0:
                    case 1:
                        Fund(node, a, rng.Next(1, 900));
                        break;
                    case 2:
                    case 3:
                        var principal = rng.Next(1, 500);
                        var offer = node.Service.OfferLoan(a, "k" + i, b, principal, principal + rng.Next(0, 50), rng.Next(0, 3) * 20, rng.Next(0, 3) * 30, rng.Next(0, 4) * 25);
                        if (offer.Ok)
                        {
                            ids.Add(offer.Loan!.Id);
                        }

                        break;
                    case 4:
                        node.Service.RespondLoan(a, "k" + i, loan, rng.Next(3) != 0);
                        break;
                    case 5:
                        node.Service.RepayLoan(a, "k" + i, loan, rng.Next(1, 400));
                        break;
                    case 6:
                        node.Service.ForgiveLoan(a, "k" + i, loan, rng.Next(0, 60));
                        break;
                    case 7:
                        node.Service.WithdrawLoan(a, "k" + i, loan);
                        break;
                    default:
                        _time.Advance(TimeSpan.FromSeconds(rng.Next(1, 45)));
                        node.Service.ProcessLoanTimers();
                        break;
                }
            }

            Assert.True(node.Auditor.RunNow().Ok, string.Join("; ", node.Auditor.LastReport!.Violations));
            before = [.. node.Service.Loans.OrderBy(l => l.Id)];
            wallets = [.. node.Ledger.Wallets.OrderBy(w => w.Id.Kind).ThenBy(w => w.Id.OwnerId).Select(w => (w.Id, w.Balance))];
            Assert.True(before.Count > 20, "expected a fair number of loans, got " + before.Count);
            Assert.Contains(before, l => l.State == LoanState.Repaid || l.State == LoanState.Overdue || l.State == LoanState.Expired);
        }

        using var restarted = Start();
        Assert.Equal(before, [.. restarted.Service.Loans.OrderBy(l => l.Id)]);
        Assert.Equal(wallets, [.. restarted.Ledger.Wallets.OrderBy(w => w.Id.Kind).ThenBy(w => w.Id.OwnerId).Select(w => (w.Id, w.Balance))]);
        Assert.True(restarted.Auditor.RunNow().Ok, string.Join("; ", restarted.Auditor.LastReport!.Violations));
        Assert.All(restarted.Service.Loans.Where(l => !l.IsOpen), l => Assert.Equal(0, restarted.Ledger.BalanceOf(l.EscrowWallet)));
        Assert.All(restarted.Ledger.Wallets.Where(w => w.Id.Kind == WalletKind.Escrow && !restarted.Service.Loans.Any(l => l.State == LoanState.Offered && l.EscrowWallet == w.Id)), w => Assert.Equal(0, w.Balance));
    }

    private T Scalar<T>(string sql)
    {
        using var db = _factory.Open();
        return db.ExecuteScalar<T>(sql)!;
    }
}
