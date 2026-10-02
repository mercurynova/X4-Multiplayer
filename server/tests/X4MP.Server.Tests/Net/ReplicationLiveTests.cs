using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using X4MP.Core.Replication;
using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Server.Hosting;
using Xunit.Abstractions;

namespace X4MP.Server.Tests.Net;

/// <summary>
/// The whole stack on real sockets: the real server host (Kestrel, SQLite, session actor, mirror, interest, replication) and FakeNode's
/// authority and verifying clients over TCP, in real time. The virtual-time tests in X4MP.Core.Tests run the same pipeline for minutes of game
/// time in seconds; these prove it over the wire, including the settings plumbing.
/// </summary>
[Collection("net")]
public sealed partial class ReplicationLiveTests(ITestOutputHelper output)
{
    private static int FreePort() => TestPorts.FreeTcp();

    private sealed class Host : IAsyncDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-repl-" + Guid.NewGuid().ToString("N"));
        private WebApplication _app;

        private readonly string[] _settings;

        public Host(params string[] settings)
        {
            _settings = settings;
            _app = Build();
        }

        private WebApplication Build()
        {
            TcpPort = FreePort();
            string[] args =
            [
                "--data-dir", _dir, "--port", FreePort().ToString(CultureInfo.InvariantCulture),
                $"--X4MP:Net:NodeTcpEndpoint=127.0.0.1:{TcpPort}", "--X4MP:Net:MaxConnectionsPerIp=64", "--X4MP:Net:MaxPlayers=16",
                .. _settings,
            ];
            var cli = CliArguments.Parse(args);
            return ServerHost.Build(cli.Remaining, cli, isService: false);
        }

        public int TcpPort { get; private set; }

        public ReplicationModule Replication => _app.Services.GetService(typeof(ReplicationModule)) as ReplicationModule
            ?? throw new InvalidOperationException("the host did not register replication");

        public Task StartAsync() => TestPorts.StartWithRetryAsync(() => _app.StartAsync(), async () =>
        {
            await _app.DisposeAsync();
            _app = Build();
        });

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

    private async Task<(int Exit, string Text)> SwarmAsync(Host host, int clients, int seconds, Action<FakeClientSession>? onSession = null, string behavior = "wander", Func<IReadOnlyList<LiveNodeStats>, bool>? stopWhen = null)
    {
        var options = CliParser.Parse(
            ["swarm", "--clients", clients.ToString(CultureInfo.InvariantCulture), "--with-authority", "--verify", "--behavior", behavior, "--duration", seconds.ToString(CultureInfo.InvariantCulture)]).Options!
            with { Port = host.TcpPort };
        var text = new StringWriter();
        int exit = await LiveRunner.RunAsync(
            options,
            text,
            new LiveRunOptions { ReportInterval = TimeSpan.FromSeconds(5), ConnectStagger = TimeSpan.FromMilliseconds(30), OnClientSession = onSession, StopWhen = stopWhen },
            CancellationToken.None);
        output.WriteLine(text.ToString());
        return (exit, text.ToString());
    }

    private static Match Verify(string text) =>
        VerifyLine().Match(text);

    [GeneratedRegex(@"^verify: clients=(?<clients>\d+) frames=(?<frames>\d+) entries=(?<entries>\d+) checked=(?<checked>\d+) ghosts=(?<ghosts>\d+) .*?checksums-ok=(?<ok>\d+)/(?<all>\d+) resyncs=(?<resyncs>\d+) position-errors=(?<pos>\d+) errors=(?<errors>\d+)", RegexOptions.Multiline)]
    private static partial Regex VerifyLine();

    [Fact]
    public async Task VerifyingClientsOverRealSocketsSeeZeroErrors()
    {
        await using var host = new Host();
        await host.StartAsync();

        var (exit, text) = await SwarmAsync(host, clients: 3, seconds: 14, behavior: "explore", stopWhen: s => LiveStop.Verified(s, 3, 1500) && s.Sum(x => x.Session?.Ghosts ?? 0) > 0);

        var v = Verify(text);
        Assert.True(v.Success, "no verify line in the output");
        Assert.Equal(0, exit);
        Assert.Equal("3", v.Groups["clients"].Value);
        Assert.True(long.Parse(v.Groups["checked"].Value, CultureInfo.InvariantCulture) > 1000);
        Assert.True(long.Parse(v.Groups["ghosts"].Value, CultureInfo.InvariantCulture) > 0);
        Assert.Equal("0", v.Groups["errors"].Value);
        Assert.Equal("0", v.Groups["pos"].Value);
        Assert.Equal(0, host.Replication.Stats.FramesDropped);
        Assert.Contains("summary: nodes=4 joined=4 errors=0", text);
    }

    [Fact]
    public async Task AtThirtyTwoKilobytesPerSecondTheStreamStaysWithinBudgetAndVerifies()
    {
        await using var host = new Host("--X4MP:Replication:BandwidthBudgetKBps=32");
        await host.StartAsync();

        var (exit, text) = await SwarmAsync(host, clients: 2, seconds: 8);

        Assert.Equal(0, exit);
        Assert.Equal("0", Verify(text).Groups["errors"].Value);
        var stats = host.Replication.Stats;
        double perClientPerSecond = stats.BytesSent / 2.0 / 7.0; // about 7 s of streaming after the join pipeline
        output.WriteLine($"32 KB/s budget: {stats.BytesSent} B in {stats.FramesSent} frames => {perClientPerSecond / 1000:F1} KB/s per client");
        Assert.True(stats.FramesSent > 100);
        Assert.True(perClientPerSecond <= 32_000 * 1.15, $"{perClientPerSecond:F0} B/s per client");
    }

    [Fact]
    public async Task AChecksumMismatchTriggersAResyncOverTheWireAndTheClientHealsWithoutErrors()
    {
        await using var host = new Host("--X4MP:Replication:ChecksumIntervalSeconds=1");
        await host.StartAsync();

        int sessions = 0;
        var (exit, text) = await SwarmAsync(host, clients: 2, seconds: 12, stopWhen: s => s.Where(x => x.Role == Role.Client).Sum(x => x.Session?.ChecksumsOk ?? 0) >= 10 && s.Sum(x => x.Session?.ResyncsRequested ?? 0) >= 1, onSession: session =>
        {
            // the first client's view of itself is off by one ghost at its next checksum
            if (Interlocked.Increment(ref sessions) == 1)
            {
                session.ChecksumCountSkew = 1;
            }
        });

        var v = Verify(text);
        Assert.Equal(0, exit);
        Assert.Equal("0", v.Groups["errors"].Value);
        Assert.Equal("1", v.Groups["resyncs"].Value);
        Assert.Equal(1, host.Replication.Stats.ResyncsHandled);
        Assert.True(long.Parse(v.Groups["ok"].Value, CultureInfo.InvariantCulture) >= 10);   // every other checksum matched, the mismatching client matched again after the resync
    }
}
