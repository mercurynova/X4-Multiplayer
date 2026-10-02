using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using X4MP.Core.Economy;
using X4MP.Core.Session;
using X4MP.Core.World;
using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Server.Hosting;
using Xunit.Abstractions;

namespace X4MP.Server.Tests.Net;

/// <summary>
/// M1-E5 over real sockets: the real server host, a fake authority that fails and withholds confirms (<c>--trade-fail</c>,
/// <c>--trade-timeout</c>) and trading clients (<c>--trade</c>). Every failed trade is refunded and unlocked, every timeout settles through the
/// <c>TradeQuery</c> or is left InDoubt and resolvable, the ledger sums to zero and nothing stays locked.
/// </summary>
[Collection("net")]
public sealed class TradeLiveTests(ITestOutputHelper output)
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
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-trade-" + Guid.NewGuid().ToString("N"));
        private readonly WebApplication _app;

        public Host()
        {
            TcpPort = FreePort();
            string[] args =
            [
                "--data-dir", _dir, "--port", FreePort().ToString(CultureInfo.InvariantCulture),
                $"--X4MP:Net:NodeTcpEndpoint=127.0.0.1:{TcpPort}", "--X4MP:Net:MaxConnectionsPerIp=64", "--X4MP:Net:MaxPlayers=16",
                "--X4MP:Economy:CreditMode=PerPlayer", "--X4MP:Economy:TradeRequiresProximity=false",
                "--X4MP:Economy:TradeExecuteTimeoutSeconds=2", "--X4MP:Economy:TradeQueryIntervalSeconds=1",
            ];
            var cli = CliArguments.Parse(args);
            _app = ServerHost.Build(cli.Remaining, cli, isService: false);
        }

        public int TcpPort { get; }

        public EconomyModule Economy => (EconomyModule)_app.Services.GetService(typeof(EconomyModule))!;

        public SessionActor Actor => (SessionActor)_app.Services.GetService(typeof(SessionActor))!;

        public WorldMirror Mirror => (WorldMirror)_app.Services.GetService(typeof(WorldMirror))!;

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

    private sealed record Run(int Exit, string Text, IReadOnlyList<LiveNodeStats> Stats);

    private async Task<Run> SwarmAsync(Host host, string failPercent, string timeoutPercent, int seconds, int clients = 3, Func<IReadOnlyList<LiveNodeStats>, bool>? stopWhen = null)
    {
        var options = CliParser.Parse(
        [
            "swarm", "--clients", clients.ToString(CultureInfo.InvariantCulture), "--with-authority", "--trade",
            "--trade-fail", failPercent, "--trade-timeout", timeoutPercent, "--duration", seconds.ToString(CultureInfo.InvariantCulture),
        ]).Options! with { Port = host.TcpPort };
        var text = new StringWriter();
        IReadOnlyList<LiveNodeStats> stats = [];
        int exit = await LiveRunner.RunAsync(
            options, text,
            new LiveRunOptions { ReportInterval = TimeSpan.FromSeconds(10), ConnectStagger = TimeSpan.FromMilliseconds(30), OnFinished = s => stats = s, StopWhen = stopWhen },
            CancellationToken.None);
        output.WriteLine(text.ToString());
        return new Run(exit, text.ToString(), stats);
    }

    /// <summary>Waits until no trade is waiting for the authority any more (the timeline is a few seconds with the settings above).</summary>
    private static async Task WaitForQuietAsync(Host host)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            if (await host.Actor.CallAsync(() => host.Economy.Service!.Trades.All(t => t.State != TradeState.Transferring)))
            {
                return;
            }

            await Task.Delay(250);
        }
    }

    [Fact]
    public async Task FailuresAreRefundedTimeoutsSettleOrGoInDoubtAndTheLedgerEndsAtZero()
    {
        await using var host = new Host();
        await host.StartAsync();

        var run = await SwarmAsync(host, "20", "45", seconds: 50, stopWhen: s => s.FirstOrDefault(n => n.Role == Role.Authority)?.TradeAuthority is { OrdersReceived: >= 8, Applied: >= 2, Failed: >= 1, Withheld: >= 1, Silent: >= 1 });

        Assert.Equal(0, run.Exit);
        await WaitForQuietAsync(host);
        var authority = run.Stats.Single(s => s.TradeAuthority is not null && s.Role == Role.Authority).TradeAuthority!;
        Assert.True(authority.OrdersReceived >= 8, $"only {authority.OrdersReceived} orders reached the authority");
        Assert.True(authority.Failed >= 1 && authority.Withheld >= 1 && authority.Silent >= 1, authority.Summary());

        var report = await host.Actor.CallAsync(() =>
        {
            var service = host.Economy.Service!;
            var trades = service.Trades.OrderBy(t => t.Id).ToList();
            var problems = new List<string>();
            foreach (var trade in trades)
            {
                if (!authority.Outcomes.TryGetValue((ulong)trade.Id, out var truth))
                {
                    // never ordered: it ended before the transfer started
                    if (trade.State is TradeState.Transferring or TradeState.Completed or TradeState.InDoubt)
                    {
                        problems.Add($"trade {trade.Id} is {trade.State} but the authority never got an order");
                    }

                    continue;
                }

                switch (trade.State)
                {
                    case TradeState.Completed when !truth.Applied:
                        problems.Add($"trade {trade.Id} completed but the authority did not apply it");
                        break;
                    case TradeState.RolledBack when truth.Applied:
                        problems.Add($"trade {trade.Id} was refunded but the authority applied the transfer");
                        break;
                    case TradeState.InDoubt when !truth.Silent:
                        problems.Add($"trade {trade.Id} is InDoubt although the authority answers queries");
                        break;
                    case TradeState.Transferring:
                        problems.Add($"trade {trade.Id} is still transferring");
                        break;
                }
            }

            return (Trades: trades.Select(t => (t.Id, t.State)).ToList(), Problems: problems);
        });
        Assert.Empty(report.Problems);
        Assert.Contains(report.Trades, t => t.State == TradeState.Completed);
        Assert.Contains(report.Trades, t => t.State == TradeState.RolledBack);
        output.WriteLine("server trades: " + string.Join(", ", report.Trades.GroupBy(t => t.State).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}")));

        // Resolve whatever is InDoubt the way the authority's ground truth says (what an admin would do after looking at the game).
        var resolved = await host.Actor.CallAsync(() =>
        {
            var service = host.Economy.Service!;
            var count = 0;
            foreach (var trade in service.Trades.Where(t => t.State == TradeState.InDoubt).ToList())
            {
                Assert.True(service.AdminResolveTrade(trade.Id, authority.Outcomes[(ulong)trade.Id].Applied, "admin:test").Ok);
                count++;
            }

            return count;
        });
        output.WriteLine($"in doubt and resolved: {resolved}");

        var end = await host.Actor.CallAsync(() =>
        {
            var service = host.Economy.Service!;
            var audit = host.Economy.Auditor!.RunNow();
            return (
                Sum: service.Ledger.TotalBalance(),
                Open: service.Trades.Count(t => t.IsOpen),
                Locks: service.Trades.SelectMany(t => t.IsOpen ? t.LockedAssets : []).Count(),
                Escrow: service.Ledger.Wallets.Where(w => w.Id.Kind == X4MP.Core.Economy.WalletKind.Escrow).Sum(w => Math.Abs(w.Balance)),
                Audit: audit,
                Frozen: service.Ledger.IsFrozen,
                Owners: CheckOwners(host.Mirror, service));
        });
        Assert.Equal(0, end.Sum);
        Assert.Equal(0, end.Open);
        Assert.Equal(0, end.Locks);
        Assert.Equal(0, end.Escrow);
        Assert.True(end.Audit.Ok, string.Join("; ", end.Audit.Violations));
        Assert.False(end.Frozen);
        Assert.Empty(end.Owners);
        Assert.Contains("trade:", run.Text);
    }

    /// <summary>The mirror shows the buyer as the owner of every ship whose latest trade completed.</summary>
    private static List<string> CheckOwners(WorldMirror mirror, EconomyService service)
    {
        var problems = new List<string>();
        var latest = new Dictionary<uint, TradeRecord>();
        foreach (var trade in service.Trades.Where(t => t.State == TradeState.Completed).OrderBy(t => t.Id))
        {
            foreach (var ship in trade.InitiatorGives.Concat(trade.CounterpartyGives).Where(i => i.Kind == TradeItemKind.Ship))
            {
                latest[ship.Asset] = trade;
            }
        }

        foreach (var (asset, trade) in latest)
        {
            var buyer = trade.Counterparty; // the fake traders always sell: the counterparty receives the ship
            if (!mirror.TryGet(asset, out var entity) || entity.OwnerPlayer != buyer)
            {
                problems.Add($"ship {asset}: mirror owner is {(mirror.TryGet(asset, out var e) ? e.OwnerPlayer : -1)}, trade {trade.Id} gave it to {buyer}");
            }
        }

        return problems;
    }
}
