using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using X4MP.Core.Relay;
using X4MP.FakeNode;
using X4MP.Server.Hosting;
using Xunit.Abstractions;

namespace X4MP.Server.Tests.Net;

/// <summary>
/// M1-T4 over real sockets: the real server host and FakeNode's <c>--commander</c> clients (swarm with an authority). Foreign orders are
/// rejected and never reach the authority; own and teammate orders are forwarded or rejected by the asset policy.
/// </summary>
[Collection("net")]
public sealed partial class AssetPermissionLiveTests(ITestOutputHelper output)
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
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-perm-" + Guid.NewGuid().ToString("N"));
        private readonly WebApplication _app;

        public Host(params string[] settings)
        {
            TcpPort = FreePort();
            string[] args =
            [
                "--data-dir", _dir, "--port", FreePort().ToString(CultureInfo.InvariantCulture),
                $"--X4MP:Net:NodeTcpEndpoint=127.0.0.1:{TcpPort}", "--X4MP:Net:MaxConnectionsPerIp=64", "--X4MP:Net:MaxPlayers=16",
                .. settings,
            ];
            var cli = CliArguments.Parse(args);
            _app = ServerHost.Build(cli.Remaining, cli, isService: false);
        }

        public int TcpPort { get; }

        public RelayModule Relay => (RelayModule)_app.Services.GetService(typeof(RelayModule))!;

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
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record Outcome(int Exit, long Sent, long Accepted, long Rejected, long Forwarded, string Text);

    private async Task<Outcome> RunAsync(Host host, string commander, int clients = 3, int seconds = 14)
    {
        var options = CliParser.Parse(
            ["swarm", "--clients", clients.ToString(CultureInfo.InvariantCulture), "--with-authority", "--commander", commander, "--duration", seconds.ToString(CultureInfo.InvariantCulture)]).Options!
            with { Port = host.TcpPort };
        var text = new StringWriter();
        int exit = await LiveRunner.RunAsync(
            options, text, new LiveRunOptions { ReportInterval = TimeSpan.FromSeconds(5), ConnectStagger = TimeSpan.FromMilliseconds(30) }, CancellationToken.None);
        output.WriteLine(text.ToString());
        var m = CommanderLine().Match(text.ToString());
        Assert.True(m.Success, "no commander summary line");
        return new Outcome(exit, Num(m, "sent"), Num(m, "accepted"), Num(m, "rejected"), Num(m, "forwarded"), text.ToString());
    }

    private static long Num(Match m, string group) => long.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^commander\(\w+\): orders-sent=(?<sent>\d+) accepted=(?<accepted>\d+) rejected=(?<rejected>\d+) forwarded-to-authority=(?<forwarded>\d+)", RegexOptions.Multiline)]
    private static partial Regex CommanderLine();

    [Fact]
    public async Task ForeignOrdersAreAllRejectedAndNoneReachesTheAuthority()
    {
        await using var host = new Host();
        await host.StartAsync();

        var r = await RunAsync(host, "foreign");

        Assert.Equal(0, r.Exit);
        Assert.True(r.Sent >= 10, $"only {r.Sent} orders were sent");
        Assert.True(r.Rejected >= r.Sent - 3, $"{r.Rejected} of {r.Sent} rejected"); // the last ones may still be in flight at the end
        Assert.Equal(0, r.Accepted);
        Assert.Equal(0, r.Forwarded);       // the fake authority's own count of intents it received
        Assert.Equal(0, host.Relay.Stats.IntentsForwarded);
        Assert.Equal(r.Sent, host.Relay.Stats.IntentsPermissionDenied); // the server refused every one
        Assert.Contains("NotYourAsset=", r.Text);
    }

    [Fact]
    public async Task OwnTeamOrdersAreForwardedAndAnswered()
    {
        await using var host = new Host();
        await host.StartAsync();

        var r = await RunAsync(host, "own");

        Assert.Equal(0, r.Exit);
        Assert.True(r.Sent >= 10, $"only {r.Sent} orders were sent");
        Assert.Equal(0, r.Rejected);
        Assert.Equal(r.Forwarded, host.Relay.Stats.IntentsForwarded);
        Assert.True(r.Accepted >= r.Sent - 3, $"{r.Accepted} of {r.Sent} answered");
        Assert.Equal(0, host.Relay.Stats.IntentsPermissionDenied);
    }

    [Fact]
    public async Task TeammateOrdersFollowTheAssetPolicy()
    {
        await using (var shared = new Host())
        {
            await shared.StartAsync();
            var r = await RunAsync(shared, "shared");
            Assert.True(r.Sent >= 10);
            Assert.Equal(0, r.Rejected);
            Assert.Equal(r.Forwarded, shared.Relay.Stats.IntentsForwarded);
        }

        await using var ownerOnly = new Host("--X4MP:Teams:AssetPolicy=OwnerOnly");
        await ownerOnly.StartAsync();
        var owner = await RunAsync(ownerOnly, "shared");

        Assert.True(owner.Sent >= 10);
        Assert.True(owner.Rejected >= owner.Sent - 3);
        Assert.Equal(0, owner.Forwarded);
        Assert.Equal(0, ownerOnly.Relay.Stats.IntentsForwarded);
    }
}
