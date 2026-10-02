using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using X4MP.Core.Economy;
using X4MP.Core.Session;
using X4MP.FakeNode;
using X4MP.Server.Hosting;
using Xunit.Abstractions;

namespace X4MP.Server.Tests.Net;

/// <summary>
/// M1-F4 over real sockets: the real server host, the fake authority booking <c>CreditDelta</c>s (with resends) and clients running
/// <c>--economy heavy --dupe-attack</c>. Nothing may take effect twice (the clients' answers and the ledger agree), every node's model of its wallets
/// matches what the server sent, the ledger sums to zero and the auditor is content. <c>--loan-default</c> leaves overdue loans.
/// </summary>
[Collection("net")]
public sealed class EconomyBehaviourLiveTests(ITestOutputHelper output)
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-econ-" + Guid.NewGuid().ToString("N"));
        private readonly WebApplication _app;

        public Host()
        {
            TcpPort = FreePort();
            string[] args =
            [
                "--data-dir", _dir, "--port", FreePort().ToString(CultureInfo.InvariantCulture),
                $"--X4MP:Net:NodeTcpEndpoint=127.0.0.1:{TcpPort}", "--X4MP:Net:MaxConnectionsPerIp=64", "--X4MP:Net:MaxPlayers=16",
                "--X4MP:Economy:CreditMode=PerPlayer", "--X4MP:Economy:DonateScope=Anyone", "--X4MP:Economy:LoanScope=Anyone",
                "--X4MP:Economy:TradeScope=Anyone", "--X4MP:Economy:TradeRequiresProximity=false",
                "--X4MP:Economy:TradeExecuteTimeoutSeconds=2", "--X4MP:Economy:TradeQueryIntervalSeconds=1",
            ];
            var cli = CliArguments.Parse(args);
            _app = ServerHost.Build(cli.Remaining, cli, isService: false);
        }

        public int TcpPort { get; }

        public EconomyModule Economy => (EconomyModule)_app.Services.GetService(typeof(EconomyModule))!;

        public SessionActor Actor => (SessionActor)_app.Services.GetService(typeof(SessionActor))!;

        public Task StartAsync() => _app.StartAsync();

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            X4MP.Persistence.SqliteConnectionFactory.ClearPool(_dir);
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task<(int Exit, string Text, IReadOnlyList<LiveNodeStats> Stats)> SwarmAsync(Host host, params string[] extra)
    {
        var options = CliParser.Parse(["swarm", "--with-authority", .. extra]).Options! with { Port = host.TcpPort };
        var text = new StringWriter();
        IReadOnlyList<LiveNodeStats> stats = [];
        int exit = await LiveRunner.RunAsync(
            options, text,
            new LiveRunOptions { ReportInterval = TimeSpan.FromSeconds(10), ConnectStagger = TimeSpan.FromMilliseconds(30), OnFinished = s => stats = s },
            CancellationToken.None);
        output.WriteLine(text.ToString());
        return (exit, text.ToString(), stats);
    }

    /// <summary>Waits until no trade is waiting for the authority any more.</summary>
    private static async Task WaitForQuietAsync(Host host)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            if (await host.Actor.CallAsync(() => host.Economy.Service!.Trades.All(t => t.State != X4MP.Proto.TradeState.Transferring)))
            {
                return;
            }

            await Task.Delay(250);
        }
    }

    [Fact]
    public async Task AHeavyEconomyUnderAttackLeavesCleanBooksAndMatchingModels()
    {
        await using var host = new Host();
        await host.StartAsync();

        var (exit, text, stats) = await SwarmAsync(
            host, "--clients", "4", "--economy", "heavy", "--dupe-attack", "--income-rate", "1", "--trade-fail", "10", "--trade-timeout", "10", "--duration", "40");

        Assert.Equal(0, exit);
        await WaitForQuietAsync(host);
        var economists = stats.Where(s => s.Economist is not null).Select(s => s.Economist!).ToList();
        Assert.Equal(4, economists.Count);
        Assert.True(economists.Sum(e => e.RequestsSent) >= 30, text);
        Assert.True(economists.Sum(e => e.RequestsOk) >= 10, text);
        Assert.True(economists.Sum(e => e.ReplaysSent + e.ReusesSent + e.RacesSent) >= 4, text);
        Assert.True(economists.Sum(e => e.ReplaysIdempotent + e.ReusesRejected) > 0, "no replay or reuse got an answer other than the rate limit: " + text);
        Assert.Equal(0, economists.Sum(e => e.DuplicateEffects));

        // every node's model of its wallets matched what the server sent
        foreach (var s in stats.Where(s => s.Reconciler is { Updates: > 0 }))
        {
            Assert.True(s.Reconciler!.Drifts == 0, $"{s.Name}: {string.Join("; ", s.Reconciler.Notes)}");
        }

        var authority = stats.Single(s => s.Role == X4MP.Proto.Role.Authority);
        Assert.True(authority.Income!.Resends > 0, authority.Income.Summary());
        Assert.True(authority.Reconciler!.StepsChecked > 20, authority.Reconciler.Summary());

        // the server's own ledger: no request and no CreditDelta booked twice, Σ = 0, auditor content
        var end = await host.Actor.CallAsync(() =>
        {
            var service = host.Economy.Service!;
            var all = service.Ledger.QueryTransactions(new LedgerQuery(Limit: 100_000));
            var duplicates = all
                .Where(t => t.RequestId is not null && t.RequestId != "starting-credits")
                .GroupBy(t => (t.Actor, t.RequestId, t.Kind))
                .Where(g => g.Count() > 1)
                .Select(g => $"{g.Key.Actor} {g.Key.RequestId} {g.Key.Kind} x{g.Count()}")
                .ToList();
            var booked = all
                .Where(t => t.Kind is TxKind.GameIncome or TxKind.GameSpend && t.RefType == "CreditDelta")
                .GroupBy(t => t.Actor)
                .ToDictionary(g => g.Key, g => g.Sum(t => t.Entries.Where(e => e.Wallet.Kind != X4MP.Core.Economy.WalletKind.World).Sum(e => e.Amount)));
            return (Duplicates: duplicates, Booked: booked, Sum: service.Ledger.TotalBalance(), Audit: host.Economy.Auditor!.RunNow(), Transactions: all.Count);
        });
        Assert.Empty(end.Duplicates);
        Assert.Equal(0, end.Sum);
        Assert.True(end.Audit.Ok, string.Join("; ", end.Audit.Violations));
        foreach (var s in stats.Where(s => s.Income is { DeltasSent: > 0 }))
        {
            long booked = end.Booked.GetValueOrDefault($"player:{s.PlayerId}");
            Assert.True(s.Income!.IsBookedPrefix(booked, s.Reconciler!.LastAck, s.Income.LastSeq), $"{s.Name}: booked {booked}, {s.Income.Summary()}, acked {s.Reconciler.LastAck}");
        }

        Assert.Contains("dupes:", text);
        Assert.Contains("reconciliation:", text);
        output.WriteLine($"ledger transactions: {end.Transactions}");
    }

    [Fact]
    public async Task LoanDefaultLeavesOverdueLoans()
    {
        await using var host = new Host();
        await host.StartAsync();

        var (exit, text, stats) = await SwarmAsync(host, "--clients", "3", "--loan-default", "--duration", "30");

        Assert.Equal(0, exit);
        var economists = stats.Where(s => s.Economist is not null).Select(s => s.Economist!).ToList();
        Assert.True(economists.Sum(e => e.LoanAccepts) > 0, text);
        Assert.Equal(0, economists.Sum(e => e.LoanRepayments)); // nobody repays
        var loans = await host.Actor.CallAsync(() => host.Economy.Service!.Loans.ToList());
        Assert.Contains(loans, l => l.State == X4MP.Core.Economy.LoanState.Overdue);
        Assert.DoesNotContain(loans, l => l.State == X4MP.Core.Economy.LoanState.Repaid);
        Assert.True(economists.Sum(e => e.OverdueBorrowed) > 0, text); // the borrowers were told (LoanStatus Overdue)
        var summary = await host.Actor.CallAsync(() => host.Economy.Auditor!.RunNow());
        Assert.True(summary.Ok, string.Join("; ", summary.Violations));
    }
}
