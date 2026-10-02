using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using X4MP.FakeNode;
using X4MP.Server.Hosting;
using Xunit.Abstractions;

namespace X4MP.Server.Tests.Net;

/// <summary>A new run of authority and clients against a session whose authority left (it had stored a checkpoint): the join must complete again.</summary>
[Collection("net")]
public sealed class AuthorityTakeoverLiveTests(ITestOutputHelper output)
{
    private static int FreePort() => TestPorts.FreeTcp();

    private sealed class Host : IAsyncDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-takeover-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task ASecondRunAfterTheAuthorityLeftJoinsAgain()
    {
        await using var host = new Host();
        await host.StartAsync();

        for (int run = 1; run <= 3; run++)
        {
            var options = CliParser.Parse(["swarm", "--clients", "2", "--with-authority", "--duration", "6"]).Options! with { Port = host.TcpPort };
            var text = new StringWriter();
            int exit = await LiveRunner.RunAsync(options, text, new LiveRunOptions { ConnectStagger = TimeSpan.FromMilliseconds(30), PhaseTimeout = TimeSpan.FromSeconds(8), StopWhen = s => LiveStop.Everyone(s, 3) }, CancellationToken.None);
            output.WriteLine(text.ToString());
            Assert.Equal(0, exit);
            Assert.Contains("ingame=3", text.ToString().Split(Environment.NewLine).Last(l => l.StartsWith("summary:", StringComparison.Ordinal)));
        }
    }
}
