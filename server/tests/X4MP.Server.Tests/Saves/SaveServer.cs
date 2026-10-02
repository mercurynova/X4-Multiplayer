using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using X4MP.Core.Saves;
using X4MP.Core.Session;
using X4MP.Core.World;
using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;
using X4MP.Server.Auth;
using X4MP.Server.Hosting;

namespace X4MP.Server.Tests.Saves;

/// <summary>Size of the fake saves the transfer tests move: small by default, 200 MB when <c>X4MP_LONG_TESTS=1</c>.</summary>
public static class SaveTestSize
{
    public const string LongFlag = "X4MP_LONG_TESTS";

    public static bool IsLong => Environment.GetEnvironmentVariable(LongFlag) == "1";

    /// <summary>The CI size (MB): enough chunks to exercise the window and the resume.</summary>
    public const int CiMegabytes = 12;

    public const int LongMegabytes = 200;
}

/// <summary>A <c>[Fact]</c> that is skipped unless <c>X4MP_LONG_TESTS=1</c> (the 200 MB acceptance runs).</summary>
public sealed class LongFactAttribute : FactAttribute
{
    public LongFactAttribute()
    {
        if (!SaveTestSize.IsLong)
        {
            Skip = $"long test: set {SaveTestSize.LongFlag}=1 to run it";
        }
    }
}

/// <summary>
/// The full server (Kestrel, SQLite, session actor with the team, economy, world, relay and save modules) on free ports with a
/// temp data directory, plus helpers to connect fake nodes. Settings go in as command-line overrides.
/// </summary>
public sealed class SaveServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _nodeCounter;

    private SaveServer(WebApplication app, string dir, int tcpPort, int httpPort)
    {
        _app = app;
        Dir = dir;
        TcpPort = tcpPort;
        HttpPort = httpPort;
    }

    public string Dir { get; }

    public int TcpPort { get; }

    public int HttpPort { get; }

    public SessionActor Actor => _app.Services.GetRequiredService<SessionActor>();

    public SaveService Saves => _app.Services.GetRequiredService<SaveService>();

    public WorldMirror World => _app.Services.GetRequiredService<WorldMirror>();

    public T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public string ClientDir(string name) => Path.Combine(Dir, "fake-clients", name);

    public static async Task<SaveServer> StartAsync(params string[] settings)
    {
        string dir = Path.Combine(Path.GetTempPath(), "x4mp-saves-" + Guid.NewGuid().ToString("N"));
        int tcp = FreePort();
        int http = FreePort();
        var args = new List<string>
        {
            "--data-dir", dir,
            "--port", http.ToString(CultureInfo.InvariantCulture),
            $"--X4MP:Net:NodeTcpEndpoint=127.0.0.1:{tcp}",
            "--X4MP:Net:MaxConnectionsPerIp=64",
            "--X4MP:Net:MaxPlayers=32",
            "--X4MP:Session:TickIntervalMs=50",
        };
        args.AddRange(settings);
        var cli = CliArguments.Parse([.. args]);
        var app = ServerHost.Build(cli.Remaining, cli, isService: false);
        await app.StartAsync();
        return new SaveServer(app, dir, tcp, http);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Connects a fake node (a stable key per name, so the same name resumes as the same player).</summary>
    public Task<TcpNodeClient> ConnectAsync(string name, Role role, Id128T? resumeToken = null, ulong lastJournalSeq = 0, ulong caps = 0) =>
        TcpNodeClient.ConnectAsync("127.0.0.1", TcpPort, new NodeClientOptions
        {
            PlayerName = name,
            PlayerKey = LiveRunner.DeriveKey(42, name),
            RequestedRoles = role,
            ResumeToken = resumeToken,
            LastJournalSeq = lastJournalSeq,
            ClientCaps = caps,
        });

    public string NextName(string prefix) => prefix + (++_nodeCounter).ToString("00", CultureInfo.InvariantCulture);

    /// <summary>An admin API token (Admin role) for the HTTP endpoints.</summary>
    public string AdminToken(string role = AdminRoles.Admin) => _app.Services.GetRequiredService<AdminStore>().CreateToken("test-" + role, role);

    public HttpClient Http(string? bearer = null)
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{HttpPort}"), Timeout = TimeSpan.FromMinutes(5) };
        if (bearer is not null)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        return client;
    }

    public async Task<SessionSnapshot> WaitForAsync(Func<SessionSnapshot, bool> condition, int timeoutMs = 20000, string? what = null)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var snapshot = await Actor.GetSnapshotAsync();
            if (condition(snapshot))
            {
                return snapshot;
            }

            Assert.True(Environment.TickCount64 < until, "condition not met in time: " + (what ?? string.Empty) + " phase=" + snapshot.Phase + " nodes=" +
                string.Join(",", snapshot.Nodes.Select(n => n.Name + ":" + n.Phase)));
            await Task.Delay(25);
        }
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 20000, string? what = null)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < until, "condition not met in time: " + what);
            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        X4MP.Persistence.SqliteConnectionFactory.ClearPool(Dir);
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException)
        {
            // Serilog keeps the log open until process exit
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

