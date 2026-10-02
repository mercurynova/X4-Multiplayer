using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Server.Hosting;
using Xunit.Abstractions;

namespace X4MP.Server.Tests.Net;

/// <summary>M1-F2: FakeNode's failure injection against the real host on real sockets (resume, reload, latency, a slow reader, a fuzzer, inspect).</summary>
[Collection("net")]
public sealed partial class FailureInjectionLiveTests(ITestOutputHelper output)
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
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-f2-" + Guid.NewGuid().ToString("N"));
        private readonly WebApplication _app;

        public Host()
        {
            TcpPort = FreePort();
            string[] args =
            [
                "--data-dir", _dir, "--port", FreePort().ToString(CultureInfo.InvariantCulture),
                $"--X4MP:Net:NodeTcpEndpoint=127.0.0.1:{TcpPort}", $"--X4MP:Net:UdpPort={FreePort()}",
                "--X4MP:Net:MaxConnectionsPerIp=64", "--X4MP:Net:MaxPlayers=16",
            ];
            var cli = CliArguments.Parse(args);
            _app = ServerHost.Build(cli.Remaining, cli, isService: false);
        }

        public int TcpPort { get; }

        public Task StartAsync() => _app.StartAsync();

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task<(int Exit, string Text)> RunAsync(Host host, IEnumerable<string> args, LiveRunOptions? run = null)
    {
        var options = CliParser.Parse([.. args]).Options! with { Port = host.TcpPort };
        var text = new StringWriter();
        int exit = await LiveRunner.RunAsync(options, text, run ?? new LiveRunOptions { ConnectStagger = TimeSpan.FromMilliseconds(30) }, CancellationToken.None);
        output.WriteLine(text.ToString());
        return (exit, text.ToString());
    }

    private Task<(int Exit, string Text)> SwarmAsync(Host host, string extra, int seconds, Func<IReadOnlyList<LiveNodeStats>, bool>? stopWhen = null) =>
        RunAsync(host, new[] { "swarm", "--clients", "3", "--with-authority", "--verify", "--behavior", "explore", "--duration", seconds.ToString(CultureInfo.InvariantCulture) }
            .Concat(extra.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            new LiveRunOptions { ConnectStagger = TimeSpan.FromMilliseconds(30), StopWhen = stopWhen });

    [GeneratedRegex(@"^verify: clients=(?<clients>\d+) .*?position-errors=(?<pos>\d+) errors=(?<errors>\d+)", RegexOptions.Multiline)]
    private static partial Regex VerifyLine();

    [GeneratedRegex(@"^resume: clients=(?<clients>\d+) disconnects=(?<disconnects>\d+) reloads=(?<reloads>\d+) refused=(?<refused>\d+) keyframes-after-resume=(?<keyframes>\d+) missing=(?<missing>\d+)", RegexOptions.Multiline)]
    private static partial Regex ResumeLine();

    [Fact]
    public async Task StoppingTheHostClosesIdleNodeConnectionsPromptly()
    {
        var host = new Host();
        await host.StartAsync();
        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, host.TcpPort);
        var stream = idle.GetStream();
        var hello = new byte[64];
        Assert.True(await stream.ReadAsync(hello) > 0, "no ServerHello"); // the connection is registered with the gateway; the peer now stays silent

        var stopwatch = Stopwatch.StartNew();
        await host.DisposeAsync();
        stopwatch.Stop();

        // Before the fix Kestrel's stop waited for the silent peer (handshake / 10 s heartbeat timeout); now it says goodbye at once (+ the 2 s drain).
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8), $"host stop took {stopwatch.Elapsed.TotalSeconds:F1} s");
    }

    [Fact]
    public async Task ClientsResumeAfterDisconnectsAndReloadsAndVerifyStaysClean()
    {
        await using var host = new Host();
        await host.StartAsync();
        var (exit, text) = await SwarmAsync(host, "--disconnect-every 3 --reload-every 4", 18,
            s => LiveStop.Clients(s).Count(c => c.Connected && c.InGame) == 3
                 && s.Sum(c => c.Disconnects) >= 3 && s.Sum(c => c.Reloads) >= 3 && s.Sum(c => c.ResumesWithKeyframe) >= 6
                 && LiveStop.Clients(s).All(c => c.Session is { KeyframesSinceResume: > 0 }));

        var resume = ResumeLine().Match(text);
        Assert.True(resume.Success, "no resume summary line");
        Assert.True(int.Parse(resume.Groups["disconnects"].Value, CultureInfo.InvariantCulture) >= 3, "too few reconnects");
        Assert.True(int.Parse(resume.Groups["reloads"].Value, CultureInfo.InvariantCulture) >= 3, "too few reloads");
        Assert.Equal("0", resume.Groups["refused"].Value);
        Assert.Equal("0", resume.Groups["missing"].Value);
        Assert.True(int.Parse(resume.Groups["keyframes"].Value, CultureInfo.InvariantCulture) >= 3, "no keyframes after the resumes");
        Assert.Contains("resumed=True", text, StringComparison.Ordinal);
        var verify = VerifyLine().Match(text);
        Assert.True(verify.Success);
        Assert.Equal("0", verify.Groups["errors"].Value);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task LatencyOnTcpAndUdpStillVerifiesClean()
    {
        await using var host = new Host();
        await host.StartAsync();
        var (exit, text) = await SwarmAsync(host, "--udp --latency 40 --jitter 10", 14);

        Assert.Contains("latency: +40 ms", text, StringComparison.Ordinal);
        Assert.Contains("udp: nodes=4 bound=4", text, StringComparison.Ordinal);
        var verify = VerifyLine().Match(text);
        Assert.True(verify.Success);
        Assert.Equal("0", verify.Groups["errors"].Value);
        Assert.Equal(0, exit);
        Assert.Matches(@"rtt avg=(\d{2,3})\.\d+ms", text); // the round trip grew by about 2 x 40 ms
    }

    [Fact]
    public async Task ASlowReaderDoesNotDisturbTheOtherClients()
    {
        await using var host = new Host();
        await host.StartAsync();
        var (exit, text) = await SwarmAsync(host, "--slow-reader pause=1/60", 14,
            s => s.Where(n => n.Impairment is not { Slow: not null } && n.Role == Role.Client).Count(n => n.InGame && n.Session is { Verifier.EntriesChecked: > 500 }) >= 2
                 && s.Any(n => n.Impairment is { Slow: not null } imp && imp.PausedMilliseconds >= 3000));

        Assert.Contains("slow reader on:", text, StringComparison.Ordinal);
        Assert.Contains("slow-reader: slow-clients=1", text, StringComparison.Ordinal);
        Assert.Contains("other-clients=2 with-errors=0", text, StringComparison.Ordinal);
        var verify = VerifyLine().Match(text);
        Assert.True(verify.Success);
        Assert.Equal("2", verify.Groups["clients"].Value); // the slow reader is left out of the verification
        Assert.Equal("0", verify.Groups["errors"].Value);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task AFuzzerIsCountedClosedAndBannedWhileTheSwarmCarriesOn()
    {
        await using var host = new Host();
        await host.StartAsync();
        IReadOnlyList<LiveNodeStats>? live = null;
        bool fuzzDone = false;
        var swarm = SwarmAsync(host, "", 30, s =>
        {
            live = s;
            return Volatile.Read(ref fuzzDone) && LiveStop.Verified(s, 3, 500); // the swarm carries on until the fuzzer is through, and verifies afterwards
        });
        var joined = Stopwatch.StartNew();
        while (!(live is { } l && LiveStop.Verified(l, 3, 300))) // let the swarm join and replicate first
        {
            Assert.True(joined.Elapsed < TimeSpan.FromSeconds(20), "the swarm never got going");
            await Task.Delay(25);
        }

        var (fuzzExit, fuzzText) = await RunAsync(host, ["fuzz", "--duration", "5", "--clients", "2", "--seed", "5", "--local-ip", "127.0.0.2", "--rotate-ip"]);
        Volatile.Write(ref fuzzDone, true);
        var (swarmExit, swarmText) = await swarm;

        Assert.Contains("server-alive=yes", fuzzText, StringComparison.Ordinal);
        Assert.Equal(0, fuzzExit);
        Assert.Matches(@"frames-sent=[1-9]", fuzzText);
        Assert.Matches(@"closed-by-server=\[[^\]]*(MalformedMessage|UnexpectedMessage|TooManyViolations)", fuzzText);
        var verify = VerifyLine().Match(swarmText);
        Assert.True(verify.Success);
        Assert.Equal("0", verify.Groups["errors"].Value);
        Assert.Equal(0, swarmExit);
    }

    [Fact]
    public async Task InspectPrintsTheDecodedFramesTheServerSends()
    {
        await using var host = new Host();
        await host.StartAsync();
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var (exit, text) = await RunAsync(host, ["inspect", "--no-join", "--duration", "3", "--filter", "SessionState,RosterUpdate,SessionSettings"],
            new LiveRunOptions
            {
                OnInspectLine = lines.Enqueue,
                StopWhen = _ => lines.Any(l => l.Contains("SessionState{", StringComparison.Ordinal)) && lines.Any(l => l.Contains("RosterUpdate{", StringComparison.Ordinal)),
            });

        Assert.Equal(0, exit);
        Assert.Contains(lines, l => l.Contains("SessionState{", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("RosterUpdate{", StringComparison.Ordinal));
        Assert.Contains("inspect: frames-received=", text, StringComparison.Ordinal);
    }
}
