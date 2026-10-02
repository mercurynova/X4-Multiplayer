using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Economy;
using X4MP.Core.Teams;
using X4MP.Proto;

namespace X4MP.Persistence.Tests;

/// <summary>Minimal team directory for the persistence-level economy tests.</summary>
internal sealed class TestTeams : ITeamDirectory
{
    private Dictionary<int, int?> _teamOf = [];
    private List<int> _teams = [];

    public IReadOnlyList<X4MP.Core.Teams.TeamInfo> Teams => [.. _teams.Order().Select(t => new X4MP.Core.Teams.TeamInfo(t, "T" + t, t))];

    public event Action<TeamDirectoryChanged>? Changed;

    public int? TeamOf(int playerId) => _teamOf.GetValueOrDefault(playerId);

    public IReadOnlyList<int> MembersOf(int teamId) => [.. _teamOf.Where(kv => kv.Value == teamId).Select(kv => kv.Key)];

    public X4MP.Core.Teams.TeamRelation RelationBetween(int teamA, int teamB) =>
        teamA == teamB ? X4MP.Core.Teams.TeamRelation.Allied : X4MP.Core.Teams.TeamRelation.Neutral;

    public void Set(IEnumerable<int> teams, Dictionary<int, int?> members)
    {
        _teams = [.. teams];
        _teamOf = members;
        Changed?.Invoke(new TeamDirectoryChanged(1));
    }
}

/// <summary>The credit-mode layer on SQLite: random game traffic, pool use, mode switches and team moves.</summary>
public sealed class EconomyServiceFuzzTests : IDisposable
{
    private const long Session = 1;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-economy-fuzz-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;

    public EconomyServiceFuzzTests()
    {
        _factory = new SqliteConnectionFactory(new PersistenceOptions { DataDir = _dir });
        new MigrationRunner(_factory).Migrate();
        using var db = _factory.Open();
        db.Execute("INSERT INTO sessions (id, name, state, settings_json, created_at) VALUES (1, 'test', 'Idle', '{}', '2026-10-01T12:00:00Z')");
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

    private T Scalar<T>(string sql)
    {
        using var db = _factory.Open();
        return db.ExecuteScalar<T>(sql)!;
    }

    [Fact]
    public void RandomGameTrafficPoolUseAndModeChurnKeepTheSumAtZero()
    {
        using var store = new SqliteEconomyStore(_factory, fullSync: false);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var options = new EconomyOptions { StartingCredits = 1000, PoolWithdrawDailyLimitPerPlayer = 5_000 };
        var teams = new TestTeams();
        var ledger = new EconomyLedger(Session, store, time);
        var service = new EconomyService(ledger, store, teams, () => options, time);
        teams.Changed += _ => service.Reconcile(confirm: false, actor: "system");
        service.Start();
        var auditor = new EconomyAuditor(ledger, store, time);

        const int players = 6;
        var rng = new Random(20261001);
        var seqs = new ulong[players + 1];
        void Regroup(int teamCount)
        {
            var members = new Dictionary<int, int?>();
            for (var p = 1; p <= players; p++)
            {
                members[p] = ((p - 1) % teamCount) + 1;
            }

            teams.Set(Enumerable.Range(1, teamCount), members);
        }

        Regroup(2);
        for (var p = 1; p <= players; p++)
        {
            service.EnsurePlayer(p);
        }

        var migrations = 0;
        for (var op = 0; op < 100_000; op++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            var p = rng.Next(1, players + 1);
            var roll = rng.Next(100);
            if (roll < 30)
            {
                service.BookCreditDelta(p, false, new CreditDeltaT { Amount = rng.Next(1, 5_000), Seq = ++seqs[p] });
            }
            else if (roll < 50)
            {
                service.BookCreditDelta(p, false, new CreditDeltaT { Amount = -rng.Next(1, 8_000), Seq = ++seqs[p] }); // may overdraw
            }
            else if (roll < 56)
            {
                service.BookCreditDelta(p, false, new CreditDeltaT { Amount = 10, Seq = seqs[p] }); // duplicate seq
            }
            else if (roll < 66)
            {
                service.PoolDeposit(p, "d" + op, rng.Next(1, 3_000));
            }
            else if (roll < 76)
            {
                service.PoolWithdraw(p, "w" + op, rng.Next(1, 3_000));
            }
            else if (roll < 86)
            {
                var q = rng.Next(1, players + 1);
                var amount = rng.Next(1, 2_000);
                var from = service.EffectiveWallet(p);
                var to = service.EffectiveWallet(q);
                if (q != p && from != to)
                {
                    ledger.Post(new PostRequest
                    {
                        Kind = TxKind.Donate, Actor = $"player:{p}", PlayerId = p,
                        Entries = [new(from, -amount), new(to, amount)],
                    });
                }
            }
            else if (roll < 94)
            {
                service.BookCreditDelta(9, true, new CreditDeltaT { TeamId = (ushort)rng.Next(1, 4), Amount = rng.Next(1, 4_000), Seq = ++seqs[0] });
            }

            if (op % 3_500 == 1_750)
            {
                // Mode and team churn (nothing is live, so it applies at once).
                if (rng.Next(2) == 0)
                {
                    Regroup(rng.Next(1, 4));
                }
                else
                {
                    options.CreditMode = (CreditMode)rng.Next(0, 3);
                    service.Reconcile(confirm: false, actor: "system");
                }

                migrations++;
            }

            if (op % 10_000 == 9_999)
            {
                Assert.Equal(0, ledger.TotalBalance());
                Assert.True(auditor.RunNow().Ok, string.Join("; ", auditor.LastReport!.Violations));
            }
        }

        Assert.True(migrations > 20);
        Assert.Equal(0, ledger.TotalBalance());
        Assert.False(ledger.IsFrozen);
        Assert.Equal(0, Scalar<long>("SELECT COALESCE(SUM(amount), 0) FROM ledger_entries"));
        Assert.Equal(0, Scalar<long>("SELECT COALESCE(SUM(balance), 0) FROM wallets"));
        Assert.True(Scalar<long>("SELECT COUNT(*) FROM ledger_tx") > 50_000);
        Assert.True(Scalar<long>("SELECT COUNT(*) FROM ledger_tx WHERE kind = 'ModeMigration'") > 0);
        Assert.Equal(0, Scalar<long>(
            "SELECT COUNT(*) FROM (SELECT tx_id FROM ledger_entries GROUP BY tx_id HAVING SUM(amount) <> 0 OR COUNT(*) < 2)"));
        Assert.True(auditor.RunNow().Ok);

        // A restart sees the same layout and balances.
        var reloaded = new EconomyLedger(Session, store, time);
        var again = new EconomyService(reloaded, store, teams, () => options, time);
        again.Start();
        Assert.Equal(service.AppliedMode, again.AppliedMode);
        Assert.All(ledger.Wallets, w => Assert.Equal(w.Balance, reloaded.BalanceOf(w.Id)));
    }
}
