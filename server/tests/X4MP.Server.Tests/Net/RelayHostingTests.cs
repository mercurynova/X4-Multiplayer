using System.Net;
using System.Net.Sockets;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using X4MP.Core.Relay;
using X4MP.Core.Session;
using X4MP.Core.Settings;
using X4MP.Persistence;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Server.Hosting;

namespace X4MP.Server.Tests.Net;

/// <summary>The relay wired into the full server by its one registration line: DI, settings, and persistence into SQLite.</summary>
[Collection("net")]
public sealed class RelayHostingTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-relay-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private int _tcpPort;

    public async Task InitializeAsync()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        _tcpPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        var http = new TcpListener(IPAddress.Loopback, 0);
        http.Start();
        int httpPort = ((IPEndPoint)http.LocalEndpoint).Port;
        http.Stop();
        string[] args =
        [
            "--data-dir", _dir, "--port", httpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"--X4MP:Net:NodeTcpEndpoint=127.0.0.1:{_tcpPort}", "--X4MP:Net:MaxConnectionsPerIp=20",
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

    private async Task<Peer> JoinAsync(string name, Role roles = Role.Client)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(IPAddress.Loopback, _tcpPort);
        var peer = await Peer.JoinAsync(tcp.GetStream(), name, roles);
        var actor = _app.Services.GetRequiredService<SessionActor>();
        await peer.BringInGameAsync(async condition =>
        {
            long until = Environment.TickCount64 + 5000;
            while (!condition(await actor.GetSnapshotAsync()))
            {
                Assert.True(Environment.TickCount64 < until, "condition not met in time");
                await Task.Delay(10);
            }
        });
        return peer;
    }

    private static async Task<T> EventuallyAsync<T>(Func<T> read, Func<T, bool> ready)
    {
        for (int i = 0; i < 200; i++)
        {
            var value = read();
            if (ready(value))
            {
                return value;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("not reached");
    }

    [Fact]
    public async Task TheRelayIsRegisteredBehindTheMirrorAndTheInterestManagerWithItsSettings()
    {
        var modules = _app.Services.GetServices<ISessionModule>().Select(m => m.GetType().Name).ToList();
        int mirror = modules.IndexOf("WorldMirror");
        int interest = modules.IndexOf("InterestManager");
        int relay = modules.IndexOf("RelayModule");

        Assert.True(mirror >= 0 && interest > mirror && relay > interest, string.Join(",", modules));
        Assert.Same(_app.Services.GetRequiredService<RelayModule>(), _app.Services.GetRequiredService<IChatControl>());
        Assert.IsType<SqliteChatStore>(_app.Services.GetRequiredService<IChatStore>());
        var registry = _app.Services.GetRequiredService<SettingsRegistry>();
        foreach (string key in new[] { "Relay.PlayerStateRelayHz", "Relay.IntentTimeoutMs", "Relay.ChatBurst", "Net.InboundQueueFramesPerNode", "Net.InboundOverflowLimitPerMinute" })
        {
            Assert.True(registry.TryGet(key, out _), key);
        }
    }

    [Fact]
    public async Task ChatMuteAndGameEventsArePersistedAndIntentsGoOnlyToTheAuthority()
    {
        await using var authority = await JoinAsync("Boss", Role.Authority | Role.Client);
        await using var a = await JoinAsync("Alice");
        await using var b = await JoinAsync("Bob");
        await using var c = await JoinAsync("Cleo");
        var control = _app.Services.GetRequiredService<IChatControl>();

        await a.SendAsync(MsgType.ChatSend, bld => ChatSend.Pack(bld, new ChatSendT { Channel = ChatChannel.All, Text = "hello all" }));
        await b.WaitForAsync(p => p.PlayerChats(), m => m.Text == "hello all", "chat to Bob");
        await c.WaitForAsync(p => p.PlayerChats(), m => m.Text == "hello all", "chat to Cleo");
        await control.MuteAsync(a.PlayerId, actor: "jack", reason: "test");
        await a.SendAsync(MsgType.ChatSend, bld => ChatSend.Pack(bld, new ChatSendT { Channel = ChatChannel.All, Text = "muted line" }));
        await control.SendAsync("jack", "welcome");
        await c.WaitForAsync(p => p.PlayerChats(), m => m.Text == "welcome" && m.Channel == ChatChannel.Admin, "admin message");

        // with the interest manager behind it, a kill claim on something Alice does not hold is refused at the server
        await a.SendAsync(MsgType.Intent, bld => Intent.Pack(bld, new IntentT
        {
            RequestKey = new Id128T { Lo = 9, Hi = 1 },
            RequestId = 9,
            Body = IntentBodyUnion.FromKillClaim(new KillClaimT { Target = 4711, Killer = 1 }),
        }));
        var refused = await a.WaitForAsync(p => p.Results(), r => r.RequestKey.Lo == 9, "the refusal");
        Assert.Equal(RejectReason.NotInInterest, refused.Reason);
        Assert.Empty(authority.Intents());

        // other intents go to the authority and to nobody else
        await a.SendAsync(MsgType.Intent, bld => Intent.Pack(bld, new IntentT
        {
            RequestKey = new Id128T { Lo = 10, Hi = 1 },
            RequestId = 10,
            Body = IntentBodyUnion.FromStationBuildRequest(new StationBuildRequestT { Macro = "station_gen_factory_01_macro", Sector = 1 }), // a team member may build (an AssetRename of an unknown entity is now refused by the permission gate)
        }));
        await authority.WaitForAsync(p => p.Intents(), i => i.RequestKey.Lo == 10, "the intent at the authority");
        await Task.Delay(100);
        Assert.Empty(b.Intents());
        Assert.Empty(c.Intents());

        await authority.SendAsync(MsgType.GameEvent, bld => GameEvent.Pack(bld, new GameEventT
        {
            Sector = 0,
            Body = GameEventBodyUnion.FromPlayerDiedEvent(new PlayerDiedEventT { PlayerId = (ushort)a.PlayerId, Killer = 3 }),
        }));
        await b.WaitForAsync(p => p.GameEvents(), e => e.Body.Type == GameEventBody.PlayerDiedEvent, "the event");

        string db = Path.Combine(_dir, "x4mp.db");
        var chat = await EventuallyAsync(
            () => Query<(string Channel, string Text, long? From, string? Admin)>(db, "SELECT channel, text, from_player_id, from_admin FROM chat_messages ORDER BY id"),
            rows => rows.Count >= 2);
        Assert.Equal(("All", "hello all", (long?)a.PlayerId, (string?)null), chat[0]);
        Assert.Equal(("Admin", "welcome", (long?)null, (string?)"jack"), chat[1]);
        Assert.DoesNotContain(chat, r => r.Text == "muted line");

        var events = await EventuallyAsync(
            () => Query<(string Type, long? Player)>(db, "SELECT type, player_id FROM session_events WHERE type IN ('GameEventOccurred', 'ChatPosted', 'AdminActionTaken')"),
            rows => rows.Any(r => r.Type == "GameEventOccurred") && rows.Any(r => r.Type == "AdminActionTaken"));
        Assert.Contains(events, r => r.Type == "GameEventOccurred" && r.Player == a.PlayerId);
        Assert.Equal(1, Scalar<long>(db, $"SELECT is_muted FROM players WHERE id = {a.PlayerId}"));
    }

    private static List<T> Query<T>(string db, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
        return [.. connection.Query<T>(sql)];
    }

    private static T Scalar<T>(string db, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
        return connection.ExecuteScalar<T>(sql)!;
    }
}
