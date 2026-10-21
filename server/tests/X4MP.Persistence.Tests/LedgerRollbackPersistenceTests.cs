using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Economy;
using X4MP.Core.Saves;
using X4MP.Core.World;
using X4MP.Proto;
using LoanState = X4MP.Core.Economy.LoanState;
using WalletKind = X4MP.Core.Economy.WalletKind;

namespace X4MP.Persistence.Tests;

/// <summary>ADR-053 (F13b) on SQLite: the sequence number, the mark stored with a checkpoint (migration 0009) and a rollback that survives a restart.</summary>
public sealed partial class LedgerRollbackPersistenceTests : IDisposable
{
    private const long Session = 1;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-ledgermark-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly TestTeams _teams = new();
    private readonly EconomyOptions _options = new() { StartingCredits = 0, CreditMode = CreditMode.PerPlayer, LoanScope = EconomyScope.Anyone };

    public LedgerRollbackPersistenceTests()
    {
        _factory = new SqliteConnectionFactory(new PersistenceOptions { DataDir = _dir });
        new MigrationRunner(_factory).Migrate();
        using var db = _factory.Open();
        db.Execute("INSERT INTO sessions (id, name, state, settings_json, created_at) VALUES (1, 'test', 'Idle', '{}', '2026-10-01T12:00:00Z')");
        _teams.Set([1], new Dictionary<int, int?> { [1] = 1, [2] = 1 });
    }

    public void Dispose()
    {
        X4MP.Persistence.SqliteConnectionFactory.ClearPool(_dir);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort temp cleanup
        }
    }

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
        service.EnsurePlayer(1);
        service.EnsurePlayer(2);
        var auditor = new EconomyAuditor(ledger, store, _time);
        auditor.AddCheck(EconomyService.AuditLoans);
        return new Node(store, ledger, service, auditor);
    }

    private static void Delta(Node node, int player, long amount) =>
        Assert.True(node.Service.BookCreditDelta(player, false, new CreditDeltaT { Amount = amount }).Booked);

    [Fact]
    public void TheSequenceNumberIsMonotonicAcrossARestartAndIndependentOfTheClock()
    {
        long afterTwo;
        using (var node = Start())
        {
            Delta(node, 1, 100);
            Delta(node, 1, 200);
            afterTwo = node.Store.HighWaterSeq(Session);
            Assert.True(afterTwo >= 2);
        }

        using var again = Start();
        Assert.Equal(afterTwo, again.Store.HighWaterSeq(Session));
        Delta(again, 1, 5); // same fake instant as every other posting: ties in time do not matter
        var after = again.Store.TransactionsAfter(Session, afterTwo);
        Assert.Single(after);
        Assert.Equal(afterTwo + 1, after[0].Seq);
        Assert.Equal(300, again.Store.BalancesAt(Session, afterTwo)[WalletId.Player(1)]);
        Assert.Equal(305, again.Ledger.BalanceOf(WalletId.Player(1)));
    }

    [Fact]
    public void ARollbackAfterARestartRestoresWalletsLoansAndLeavesTheAuditorContent()
    {
        LedgerMark mark;
        long loanId;
        using (var node = Start())
        {
            Delta(node, 1, 50_000);
            mark = node.Service.CaptureMark();
            Delta(node, 1, -30_000);
            var offer = node.Service.OfferLoan(1, "o1", 2, 5_000, 5_500, 3600, 300, 0, "m");
            Assert.True(offer.Ok, offer.Reason.ToString());
            loanId = offer.Loan!.Id;
            Assert.True(node.Service.RespondLoan(2, "a1", loanId, true).Ok);
        }

        // The server restarts; the authority then loads the checkpoint.
        using var restarted = Start();
        var result = restarted.Service.RollBackToMark(mark, "cp", "authority reloads the checkpoint");

        Assert.True(result.Performed, result.Skipped);
        Assert.Equal(50_000, restarted.Ledger.BalanceOf(WalletId.Player(1)));
        Assert.Equal(0, restarted.Ledger.BalanceOf(WalletId.Player(2)));
        Assert.Equal(LoanState.Cancelled, restarted.Service.FindLoan(loanId)!.State);
        var report = restarted.Auditor.RunNow();
        Assert.True(report.Ok, string.Join("; ", report.Violations));
        Assert.Equal(0, restarted.Ledger.TotalBalance());
        Assert.Empty(result.Drift);

        // The reversals are rows of their own: nothing was deleted, and they say why.
        using var db = _factory.Open();
        Assert.True(db.ExecuteScalar<long>("SELECT COUNT(*) FROM ledger_tx WHERE ref_type = 'Rollback' AND note LIKE 'rollback to checkpoint cp: authority reloads the checkpoint%'") >= 3);
        using var third = Start();
        Assert.Equal(50_000, third.Ledger.BalanceOf(WalletId.Player(1))); // and it all persisted
        Assert.Equal(LoanState.Cancelled, third.Service.FindLoan(loanId)!.State);
    }

    [Fact]
    public async Task TheMarkIsStoredWithTheCheckpointAndAnOldCheckpointHasNone()
    {
        await using var writer = new PersistenceWriter(_factory, new PersistenceOptions { DataDir = _dir });
        var catalog = new SqliteSaveCatalog(_factory, writer);
        string sha = new('a', 64);
        string manifest = new('c', 64);
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        catalog.AddSave(new SaveRecord(sha, 10, "n", "authority", "A", at, SaveMeta.Empty, true));
        var withMark = new CheckpointId(1, 2);
        var old = new CheckpointId(3, 4);
        catalog.AddCheckpoint(Session, new CheckpointRecord(withMark, sha, manifest, 5, 1, 0, 0, true, at, new LedgerMark(42, "{\"loans\":[]}")));
        catalog.AddCheckpoint(Session, new CheckpointRecord(old, sha, manifest, 5, 1, 0, 0, true, at)); // stored without a mark (before ADR-053)
        await writer.FlushAsync();

        Assert.Equal(new LedgerMark(42, "{\"loans\":[]}"), catalog.FindLedgerMark(withMark));
        Assert.Null(catalog.FindLedgerMark(old));
        Assert.Null(catalog.FindLedgerMark(new CheckpointId(9, 9)));

        catalog.UpdateLedgerMark(withMark, new LedgerMark(77, "{}"));
        await writer.FlushAsync();
        Assert.Equal(new LedgerMark(77, "{}"), catalog.FindLedgerMark(withMark));
    }

    [Fact]
    public void MigrationNineAddsTheColumnsAndLeavesExistingCheckpointRowsWithoutAMark()
    {
        // A database as it was at schema version 8, with one stored checkpoint.
        string dir = Path.Combine(Path.GetTempPath(), "x4mp-mig9-" + Guid.NewGuid().ToString("N"));
        try
        {
            var factory = new SqliteConnectionFactory(new PersistenceOptions { DataDir = dir });
            var all = EmbeddedMigrations();
            Assert.Equal(9, all.Count);
            new MigrationRunner(factory, all.Where(m => m.Version <= 8)).Migrate();
            using (var db = factory.Open())
            {
                db.Execute("INSERT INTO sessions (id, name, state, settings_json, created_at) VALUES (1, 's', 'Idle', '{}', '2026-10-01T12:00:00Z')");
                db.Execute("INSERT INTO checkpoints (session_id, save_sha256, journal_seq, ghosts_cleaned, created_at, checkpoint_id) VALUES (1, 'x', 1, 1, '2026-10-01T12:00:00Z', '00000000000000020000000000000001')");
            }

            Assert.Equal(9, new MigrationRunner(factory, all).Migrate());

            using var after = factory.Open();
            Assert.Null(after.ExecuteScalar<long?>("SELECT ledger_seq FROM checkpoints"));
            Assert.Null(after.ExecuteScalar<string?>("SELECT ledger_snapshot FROM checkpoints"));
            var catalog = new SqliteSaveCatalog(factory, new PersistenceWriter(factory, new PersistenceOptions { DataDir = dir }));
            Assert.Null(catalog.FindLedgerMark(new CheckpointId(1, 2)));
        }
        finally
        {
            X4MP.Persistence.SqliteConnectionFactory.ClearPool(dir);
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    private static List<Migration> EmbeddedMigrations()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var list = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = ResourceName().Match(resource);
            if (!match.Success)
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            list.Add(new Migration(int.Parse(match.Groups["version"].Value, System.Globalization.CultureInfo.InvariantCulture), match.Groups["name"].Value, reader.ReadToEnd()));
        }

        return [.. list.OrderBy(m => m.Version)];
    }

    [GeneratedRegex(@"Migrations\.(?<version>\d{4})_(?<name>.+)\.sql$")]
    private static partial Regex ResourceName();
}
