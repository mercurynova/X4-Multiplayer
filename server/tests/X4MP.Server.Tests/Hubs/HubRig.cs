using System.Globalization;
using Microsoft.AspNetCore.SignalR.Client;
using X4MP.FakeNode;
using X4MP.Server.Auth;
using X4MP.Server.Hubs;
using X4MP.Server.Tests.Saves;

namespace X4MP.Server.Tests.Hubs;

/// <summary>The messages a hub connection received, by method name, with waiting helpers.</summary>
internal sealed class HubRecorder
{
    private readonly object _gate = new();
    private readonly List<(string Method, object? Payload)> _received = [];

    /// <summary>Hooks every server-to-client call of <c>IAdminClient</c> on the connection.</summary>
    public static HubRecorder HookAll(HubConnection connection)
    {
        var recorder = new HubRecorder();
        connection.On<X4MP.Server.Api.DashboardSnapshotDto>("Dashboard", p => recorder.Add("Dashboard", p));
        connection.On<X4MP.Server.Api.PlayerLiveDto>("PlayerChanged", p => recorder.Add("PlayerChanged", p));
        connection.On<long>("PlayerRemoved", p => recorder.Add("PlayerRemoved", p));
        connection.On<X4MP.Server.Api.SessionSummaryDto>("SessionChanged", p => recorder.Add("SessionChanged", p));
        connection.On<X4MP.Server.Api.GalaxyFrameDto>("GalaxyFrame", p => recorder.Add("GalaxyFrame", p));
        connection.On<X4MP.Server.Api.SectorFrameDto>("SectorFrame", p => recorder.Add("SectorFrame", p));
        connection.On<List<X4MP.Server.Api.LogEntryDto>>("LogBatch", p => recorder.Add("LogBatch", p));
        connection.On<List<X4MP.Server.Api.ConnectionStatsDto>>("Diagnostics", p => recorder.Add("Diagnostics", p));
        connection.On<X4MP.Server.Api.ChatMessageDto>("Chat", p => recorder.Add("Chat", p));
        connection.On<X4MP.Server.Api.TransferProgressDto>("SaveTransfer", p => recorder.Add("SaveTransfer", p));
        connection.On<X4MP.Server.Api.AlertDto>("Alert", p => recorder.Add("Alert", p));
        connection.On<X4MP.Server.Api.SettingsDto>("SettingsChanged", p => recorder.Add("SettingsChanged", p));
        connection.On<X4MP.Server.Api.PermissionDeniedDto>("PermissionDenied", p => recorder.Add("PermissionDenied", p));
        connection.On<X4MP.Server.Api.WalletDto>("WalletChanged", p => recorder.Add("WalletChanged", p));
        connection.On<X4MP.Server.Api.LedgerTxDto>("LedgerPosted", p => recorder.Add("LedgerPosted", p));
        connection.On<X4MP.Server.Api.LoanDto>("LoanChanged", p => recorder.Add("LoanChanged", p));
        connection.On<X4MP.Server.Api.TradeOfferDto>("TradeChanged", p => recorder.Add("TradeChanged", p));
        connection.On<X4MP.Server.Api.EconomyEventDto>("EconomyEvent", p => recorder.Add("EconomyEvent", p));
        connection.On<X4MP.Server.Api.EconomySummaryDto>("EconomySummary", p => recorder.Add("EconomySummary", p));
        connection.On<X4MP.Server.Api.AlertDto>("EconomyAlert", p => recorder.Add("EconomyAlert", p));
        connection.On<X4MP.Server.Api.TeamDto>("TeamUpserted", p => recorder.Add("TeamUpserted", p));
        connection.On<long>("TeamDeleted", p => recorder.Add("TeamDeleted", p));
        connection.On<X4MP.Server.Api.TeamMemberDto>("TeamMemberChanged", p => recorder.Add("TeamMemberChanged", p));
        connection.On<X4MP.Server.Api.TeamRelationsDto>("TeamRelationsChanged", p => recorder.Add("TeamRelationsChanged", p));
        connection.On<X4MP.Server.Api.TeamPolicyDto>("TeamPolicyChanged", p => recorder.Add("TeamPolicyChanged", p));
        connection.On<X4MP.Server.Api.TeamsStateDto>("TeamsReset", p => recorder.Add("TeamsReset", p));
        connection.On<X4MP.Server.Api.TeamMemberDto>("PlayerAwaitingTeam", p => recorder.Add("PlayerAwaitingTeam", p));
        return recorder;
    }

    private void Add(string method, object? payload)
    {
        lock (_gate)
        {
            _received.Add((method, payload));
        }
    }

    public List<T> All<T>(string method)
    {
        lock (_gate)
        {
            return [.. _received.Where(r => r.Method == method).Select(r => (T)r.Payload!)];
        }
    }

    public int Count(string method)
    {
        lock (_gate)
        {
            return _received.Count(r => r.Method == method);
        }
    }

    /// <summary>Waits until a message of <paramref name="method"/> that satisfies <paramref name="predicate"/> arrived, and returns it.</summary>
    public async Task<T> WaitAsync<T>(string method, Func<T, bool>? predicate = null, int timeoutMs = 20_000)
        where T : class
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var match = All<T>(method).FirstOrDefault(p => predicate?.Invoke(p) ?? true);
            if (match is not null)
            {
                return match;
            }

            Assert.True(Environment.TickCount64 < until, $"no '{method}' push matched in {timeoutMs} ms (received {Count(method)} of that kind)");
            await Task.Delay(10);
        }
    }

    /// <summary>Waits until a push of a value type (for example the player id of <c>PlayerRemoved</c>) arrived.</summary>
    public async Task WaitForValueAsync<T>(string method, T expected, int timeoutMs = 20_000)
        where T : struct
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (!All<T>(method).Contains(expected))
        {
            Assert.True(Environment.TickCount64 < until, $"no '{method}' push with {expected} in {timeoutMs} ms");
            await Task.Delay(10);
        }
    }
}

/// <summary>A full server (fast hub intervals) with helpers to open hub connections and to start a fake authority.</summary>
internal sealed class HubRig : IAsyncDisposable
{
    private readonly List<HubConnection> _connections = [];

    private HubRig(SaveServer server) => Server = server;

    public SaveServer Server { get; }

    public AuthorityRig? Authority { get; private set; }

    public AdminBroadcaster Broadcaster => Server.Service<AdminBroadcaster>();

    public AdminSubscriptions Subscriptions => Server.Service<AdminSubscriptions>();

    public static async Task<HubRig> StartAsync(params string[] extra)
    {
        string[] settings =
        [
            "--X4MP:Saves:AutosaveMinutes=0",
            "--X4MP:AdminHub:DashboardIntervalMs=100",
            "--X4MP:AdminHub:GalaxyIntervalMs=100",
            "--X4MP:AdminHub:SectorIntervalMs=50",
            "--X4MP:AdminHub:DiagnosticsIntervalMs=100",
            "--X4MP:AdminHub:LogBatchIntervalMs=50",
            "--X4MP:AdminHub:TransferIntervalMs=20",
            .. extra,
        ];
        return new HubRig(await SaveServer.StartAsync(settings));
    }

    public string Url => $"http://127.0.0.1:{Server.HttpPort}{X4MP.Server.Api.AdminHubMethods.Route}";

    /// <summary>A hub connection (not started). <paramref name="role"/> null means no credentials.</summary>
    public HubConnection Create(string? role = AdminRoles.Admin, string? token = null)
    {
        string? bearer = token ?? (role is null ? null : Server.AdminToken(role));
        var connection = new HubConnectionBuilder()
            .WithUrl(Url, o =>
            {
                if (bearer is not null)
                {
                    o.AccessTokenProvider = () => Task.FromResult<string?>(bearer);
                }
            })
            .Build();
        _connections.Add(connection);
        return connection;
    }

    /// <summary>A started connection with its recorder.</summary>
    public async Task<(HubConnection Connection, HubRecorder Recorder)> ConnectAsync(string role = AdminRoles.Admin)
    {
        var connection = Create(role);
        var recorder = HubRecorder.HookAll(connection);
        await connection.StartAsync();
        return (connection, recorder);
    }

    public async Task<AuthorityRig> StartAuthorityAsync(int megabytes = 1, int sectors = 20)
    {
        var options = new FakeAuthoritySaveOptions
        {
            SaveBytes = megabytes * 1024L * 1024,
            Directory = Path.Combine(Server.Dir, "fake-authority"),
        };
        Authority = await AuthorityRig.StartAsync(Server, options, sectors: sectors);
        await Server.WaitForAsync(s => s.Phase == X4MP.Proto.SessionPhase.Running, 60_000, "first checkpoint");
        return Authority;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException)
            {
                // already closed
            }
        }

        if (Authority is not null)
        {
            await Authority.DisposeAsync();
        }

        await Server.DisposeAsync();
    }

    public static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);
}
