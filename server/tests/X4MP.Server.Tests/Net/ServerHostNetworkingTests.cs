using System.Net;
using System.Net.Sockets;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using X4MP.Persistence;
using X4MP.Proto;
using X4MP.Server.Hosting;

namespace X4MP.Server.Tests.Net;

/// <summary>The full server (Kestrel on real ports, SQLite, gateway) driven over a real TCP socket.</summary>
[Collection("net")]
public sealed class ServerHostNetworkingTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-host-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private int _tcpPort;
    private int _httpPort;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async Task InitializeAsync()
    {
        _tcpPort = FreePort();
        _httpPort = FreePort();
        string[] args =
        [
            "--data-dir", _dir, "--port", _httpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"--X4MP:Net:NodeTcpEndpoint=127.0.0.1:{_tcpPort}", "--X4MP:Net:JoinPassword=hunter2",
        ];
        var cli = CliArguments.Parse(args);
        _app = ServerHost.Build(cli.Remaining, cli, isService: false);
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
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
            // Serilog keeps the log open until process exit
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task<ClientHandle> ConnectAsync()
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, _tcpPort);
        return new ClientHandle(client.GetStream(), new Owner(client));
    }

    private sealed class Owner(TcpClient c) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            c.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task NodeJoinsOverTcpAndIsPersistedWhileTheAdminPortKeepsServing()
    {
        await using var client = await ConnectAsync();
        var alice = new TestNode("Alice") { JoinPassword = "hunter2" };
        var (server, reply) = await alice.JoinAsync(client);
        Assert.Equal(AuthMethod.SessionPassword, server.Auth);
        var welcome = TestNode.AsWelcome(reply);
        Assert.Equal(1, welcome.PlayerId);

        using var db = new SqliteConnection($"Data Source={Path.Combine(_dir, "x4mp.db")}");
        Assert.Equal("Alice", db.ExecuteScalar<string>("SELECT name FROM players WHERE id = 1"));
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(alice.Key), db.ExecuteScalar<byte[]>("SELECT key_hash FROM players WHERE id = 1"));

        using var http = new HttpClient();
        var health = await http.GetAsync($"http://127.0.0.1:{_httpPort}/healthz");
        Assert.True(health.IsSuccessStatusCode);
    }

    [Fact]
    public async Task WrongPasswordOverTcpIsRejected()
    {
        await using var client = await ConnectAsync();
        var (_, reply) = await new TestNode("Mallory") { JoinPassword = "guess" }.JoinAsync(client);
        Assert.Equal(DisconnectCode.AuthFailed, TestNode.AsDisconnect(reply).Code);
    }
}
