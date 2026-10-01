using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using X4MP.Core.Replication;
using X4MP.FakeNode;
using X4MP.Server.Hosting;
using Xunit.Abstractions;

namespace X4MP.Server.Tests.Net;

/// <summary>The whole stack with the UDP Realtime lane on real sockets: FakeNode with <c>--udp</c> (and injected datagram loss) against the real host.</summary>
[Collection("net")]
public sealed partial class UdpLiveTests(ITestOutputHelper output)
{
    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static int FreeUdpPort()
    {
        using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-udp-" + Guid.NewGuid().ToString("N"));
        private readonly WebApplication _app;

        public Host()
        {
            TcpPort = FreeTcpPort();
            string[] args =
            [
                "--data-dir", _dir, "--port", FreeTcpPort().ToString(CultureInfo.InvariantCulture),
                $"--X4MP:Net:NodeTcpEndpoint=127.0.0.1:{TcpPort}", $"--X4MP:Net:UdpPort={FreeUdpPort()}",
                "--X4MP:Net:MaxConnectionsPerIp=64", "--X4MP:Net:MaxPlayers=16",
            ];
            var cli = CliArguments.Parse(args);
            _app = ServerHost.Build(cli.Remaining, cli, isService: false);
        }

        public int TcpPort { get; }

        public ReplicationModule Replication => (ReplicationModule)_app.Services.GetService(typeof(ReplicationModule))!;

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

    private async Task<(int Exit, string Text)> SwarmAsync(Host host, string extra, int seconds)
    {
        var args = new List<string> { "swarm", "--clients", "3", "--with-authority", "--verify", "--behavior", "explore", "--duration", seconds.ToString(CultureInfo.InvariantCulture) };
        args.AddRange(extra.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var options = CliParser.Parse(args).Options! with { Port = host.TcpPort };
        var text = new StringWriter();
        int exit = await LiveRunner.RunAsync(options, text, new LiveRunOptions { ConnectStagger = TimeSpan.FromMilliseconds(30) }, CancellationToken.None);
        output.WriteLine(text.ToString());
        return (exit, text.ToString());
    }

    [GeneratedRegex(@"^verify: clients=(?<clients>\d+) frames=(?<frames>\d+) entries=(?<entries>\d+) checked=(?<checked>\d+) .*?position-errors=(?<pos>\d+) errors=(?<errors>\d+)", RegexOptions.Multiline)]
    private static partial Regex VerifyLine();

    [Fact]
    public async Task VerifyingClientsOverUdpWithFivePercentLossSeeZeroErrors()
    {
        await using var host = new Host();
        await host.StartAsync();
        var (exit, text) = await SwarmAsync(host, "--udp --loss 5", 14);

        var v = VerifyLine().Match(text);
        Assert.True(v.Success);
        Assert.Equal(0, exit);
        Assert.Equal("0", v.Groups["errors"].Value);
        Assert.Equal("0", v.Groups["pos"].Value);
        Assert.True(long.Parse(v.Groups["checked"].Value, CultureInfo.InvariantCulture) > 500);
        Assert.Contains("udp: nodes=4 bound=4", text);
        Assert.True(host.Replication.Stats.FramesAcked > 50, "acks never drove the baselines");
    }

    [Fact]
    public async Task WhenNoDatagramEverArrivesTheNodesFallBackToTcpWithinThreeSecondsAndVerifyPasses()
    {
        await using var host = new Host();
        await host.StartAsync();
        var (exit, text) = await SwarmAsync(host, "--udp --loss 100", 14);

        var v = VerifyLine().Match(text);
        Assert.True(v.Success);
        Assert.Equal(0, exit);
        Assert.Equal("0", v.Groups["errors"].Value);
        Assert.True(long.Parse(v.Groups["checked"].Value, CultureInfo.InvariantCulture) > 500, "nothing was replicated over TCP");
        Assert.Contains("fallback=4", text);
        Assert.Equal(0, host.Replication.Stats.FramesAcked);
    }

    [Fact]
    public async Task WithTheUdpLaneOffTheNodesStayOnTcp()
    {
        await using var host = new Host();
        await host.StartAsync();
        var (exit, text) = await SwarmAsync(host, "", 8); // no --udp: the node never asks for the capability
        Assert.Equal(0, exit);
        Assert.Equal(0, host.Replication.Stats.FramesAcked);
    }
}
