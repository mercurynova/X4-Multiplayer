using X4MP.Core.Events;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;

namespace X4MP.Server.Tests.Net;

/// <summary>
/// The real gateway, the real <see cref="SessionActor"/> and the real <see cref="TcpNodeClient"/>, over the
/// in-process transport and over real TCP: join, ping, stats, ClientReload, resume, quit.
/// </summary>
[Collection("net")]
public class SessionActorIntegrationTests
{
    public static TheoryData<string> Kinds => ["inproc", "tcp"];

    private static NetOptions Fast() => new() { AuthFailureDelayMs = 0, HandshakeTimeoutSeconds = 2 };

    private static async Task<TcpNodeClient> JoinAsync(NetHarness net, NodeClientOptions options)
    {
        var handle = await net.ConnectAsync();
        try
        {
            return await TcpNodeClient.ConnectAsync(handle.Stream, options);
        }
        catch
        {
            await handle.DisposeAsync();
            throw;
        }
    }

    private static Task SendReloadAsync(TcpNodeClient client) =>
        client.SendAsync(MsgType.Disconnect, b => Disconnect.Pack(b, new DisconnectT { Code = DisconnectCode.ClientReload, Message = "", Expected = "" }));

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task JoinPingStatsReloadResumeAndQuit(string kind)
    {
        await using var fixture = new ActorFixture(Fast());
        await using var net = await NetHarness.CreateAsync(kind, fixture.Net, withGateway: true, handler: fixture.Handler);
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

        // --- join ---
        var client = await JoinAsync(net, new NodeClientOptions { PlayerName = "Alice", PlayerKey = key });
        Assert.False(client.Welcome.Resumed);
        Assert.Equal(1000, client.Welcome.HeartbeatIntervalMs);
        var token = client.ResumeToken!;
        int playerId = client.Welcome.PlayerId;

        // --- ping: our Ping gets a Pong, and answering the server's first Ping gives it an RTT and an offset ---
        using var cts = new CancellationTokenSource(10_000);
        Assert.True(await client.PingAsync(cts.Token) < TimeSpan.FromSeconds(2));
        var snap = await fixture.WaitForAsync(s => s.Nodes.Any(n => n.ClockSamples >= 1));
        var node = Assert.Single(snap.Nodes);
        Assert.Equal(playerId, node.PlayerId);
        Assert.InRange(node.RttMs, 0, 2000);
        Assert.Equal(NodePhase.Admitted, node.Phase);

        // The server told us about the session and the roster after Welcome.
        var seen = new List<MsgType>();
        while (seen.Count < 2)
        {
            var frame = await client.ReceiveAsync(cts.Token);
            seen.Add(frame!.Value.Type);
        }

        Assert.Equal([MsgType.SessionState, MsgType.RosterUpdate], seen);

        // --- NodeStats ingest ---
        await client.SendAsync(MsgType.NodeStats, b => NodeStats.Pack(b, new NodeStatsT { Fps = 61.5f, RttMs = 3f, Ghosts = 10 }));
        snap = await fixture.WaitForAsync(s => s.Nodes.Any(n => n.Stats is not null));
        Assert.Equal(61.5f, snap.Nodes[0].Stats!.Fps);

        // --- ClientReload: the slot stays, no leave/join ---
        await SendReloadAsync(client);
        var echoed = await ClientReading.UntilDisconnectAsync(client, cts.Token);
        Assert.True(echoed is null or DisconnectCode.ClientReload, $"unexpected {echoed}");

        snap = await fixture.WaitForAsync(s => s.Nodes.Any(n => n.Phase == NodePhase.Detached));
        Assert.Equal(DetachReason.ClientReload, snap.Nodes[0].DetachReason);
        Assert.Empty(fixture.Events.OfType<PlayerLeft>());
        Assert.Single(fixture.Events.OfType<PlayerJoined>());

        // --- resume: same player id, same token, baselines reset, still no join ---
        var resumed = await JoinAsync(net, new NodeClientOptions { PlayerName = "Alice", PlayerKey = key, ResumeToken = token });
        Assert.True(resumed.Welcome.Resumed);
        Assert.Equal(playerId, resumed.Welcome.PlayerId);
        Assert.Equal(token.Lo, resumed.Welcome.ResumeToken.Lo);
        Assert.Equal(token.Hi, resumed.Welcome.ResumeToken.Hi);
        snap = await fixture.WaitForAsync(s => s.Nodes.Any(n => n.Connected));
        Assert.Equal(1, snap.Nodes[0].BaselineEpoch);
        Assert.Single(fixture.Events.OfType<PlayerJoined>());
        Assert.Single(fixture.Events.OfType<PlayerResumed>());
        Assert.Empty(fixture.Events.OfType<PlayerLeft>());
        Assert.True(await resumed.PingAsync(cts.Token) < TimeSpan.FromSeconds(2));

        // --- the abandoned first client object: dispose without effect on the server's view ---
        await client.DisposeAsync();

        // --- quit: slot freed at once ---
        await resumed.DisposeAsync();
        await fixture.WaitForAsync(s => s.Nodes.Count == 0);
        Assert.Single(fixture.Events.OfType<PlayerLeft>());
        var until = Environment.TickCount64 + 5000;
        while (net.Gateway!.AdmittedNodes.Count > 0) // the gateway lets go once the connection is fully closed
        {
            Assert.True(Environment.TickCount64 < until, "the gateway still holds the node");
            await Task.Delay(10);
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ResumeAfterTheGraceIsRefusedWithResumeExpired(string kind)
    {
        var net1 = Fast();
        net1.ResumeGraceSeconds = 1;
        await using var fixture = new ActorFixture(net1);
        await using var net = await NetHarness.CreateAsync(kind, fixture.Net, withGateway: true, handler: fixture.Handler);
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

        var client = await JoinAsync(net, new NodeClientOptions { PlayerName = "Alice", PlayerKey = key });
        var options = client.ForResume();
        await SendReloadAsync(client);
        await fixture.WaitForAsync(s => s.Nodes.Any(n => n.Phase == NodePhase.Detached));
        await client.DisposeAsync();

        await fixture.WaitForAsync(s => s.Nodes.Count == 0, timeoutMs: 10_000); // the 1 s grace runs out on the real clock
        var ex = await Assert.ThrowsAsync<HandshakeRejectedException>(async () => await (await JoinAsync(net, options)).DisposeAsync());
        Assert.Equal(DisconnectCode.ResumeExpired, ex.Code);
        Assert.Single(fixture.Events.OfType<PlayerLeft>());

        // A fresh join with the same key is fine and is a join.
        await using var fresh = await JoinAsync(net, new NodeClientOptions { PlayerName = "Alice", PlayerKey = key });
        Assert.False(fresh.Welcome.Resumed);
        Assert.Equal(2, fixture.Events.OfType<PlayerJoined>().Count);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AuthorityDropAndResumeOverTheWire(string kind)
    {
        await using var fixture = new ActorFixture(Fast());
        await using var net = await NetHarness.CreateAsync(kind, fixture.Net, withGateway: true, handler: fixture.Handler);
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

        var boss = await JoinAsync(net, new NodeClientOptions { PlayerName = "Boss", PlayerKey = key, RequestedRoles = Role.Authority | Role.Client });
        Assert.Equal(120, boss.Welcome.ResumeGraceS);
        var snap = await fixture.Actor.GetSnapshotAsync();
        Assert.Equal(SessionPhase.AuthorityLoading, snap.Phase);
        Assert.True((await fixture.Actor.ApplyAsync(SessionTrigger.CheckpointStored)).Applied);
        Assert.True(net.State!.AuthorityLive);

        var options = boss.ForResume();
        await SendReloadAsync(boss);
        snap = await fixture.WaitForAsync(s => s.Phase == SessionPhase.AuthorityLost);
        Assert.Equal(AuthorityStatus.Lost, snap.Authority.Status);
        Assert.False(net.State.AuthorityLive);

        await using var back = await JoinAsync(net, options);
        Assert.True(back.Welcome.Resumed);
        snap = await fixture.WaitForAsync(s => s.Phase == SessionPhase.Running);
        Assert.Equal(AuthorityStatus.Live, snap.Authority.Status);
        Assert.True(net.State.AuthorityLive);
        await boss.DisposeAsync();
    }
}
