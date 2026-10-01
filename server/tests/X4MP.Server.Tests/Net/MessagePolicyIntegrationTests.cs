using System.Net;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Server.Tests.Net;

[Collection("net")]
public class MessagePolicyIntegrationTests
{
    public static TheoryData<string> Kinds => ["inproc", "tcp"];

    private static byte[] Frame(MsgType type, Lane? lane = null, int payload = 16)
    {
        var catalogLane = MessageRegistry.Default.TryGetDescriptor(type, out var d) ? d.Lane : Lane.Control;
        return FrameCodec.Encode(type, lane ?? catalogLane, new byte[payload]);
    }

    /// <summary>Round-trips a Ping: proves every earlier frame was read (and policed) in order.</summary>
    private static async Task BarrierAsync(ClientHandle client, uint seq = 9999)
    {
        await client.SendAsync(MsgType.Ping, TestFrames.Ping(seq));
        Assert.Equal(seq, (await client.ReadAsync<Pong>(MsgType.Pong)).Seq);
    }

    private static async Task<(NetHarness Net, ClientHandle Client, AdmittedNode Node)> JoinAsync(
        string kind, Role roles = Role.Client, TimeProvider? time = null, NetOptions? options = null, IPAddress? ip = null,
        string name = "Alice", Action<TestNode>? configure = null, InMemoryNodeStore? store = null)
    {
        var net = await NetHarness.CreateAsync(kind, options, time, withGateway: true, store: store);
        var client = await net.ConnectAsync(ip);
        var node = new TestNode(name) { Roles = roles };
        configure?.Invoke(node);
        var (_, reply) = await node.JoinAsync(client);
        TestNode.AsWelcome(reply);
        return (net, client, net.Gateway!.AdmittedNodes.Single());
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ClientSendingWorldUpdateIsCountedAndClosedPastTheThreshold(string kind)
    {
        var (net, client, node) = await JoinAsync(kind);
        await using var _ = net;
        await using var __ = client;

        for (int i = 0; i < 20; i++)
        {
            await client.SendRawAsync(Frame(MsgType.WorldUpdate));
        }

        await BarrierAsync(client);
        Assert.Equal(20, node.Reader.ViolationCount);
        Assert.Equal(20, node.Connection.Stats.Violations);
        Assert.False(node.Connection.Closed.IsCancellationRequested); // 20 per minute is still tolerated

        await client.SendRawAsync(Frame(MsgType.WorldUpdate)); // the 21st
        var d = TestNode.AsDisconnect(await client.ReadAsync());
        Assert.Equal(DisconnectCode.TooManyViolations, d.Code);
        Assert.Null(await client.ReadAsync());
        await node.Connection.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ThresholdTripTemporarilyBansTheAddress()
    {
        var store = new InMemoryNodeStore();
        var time = new FakeTimeProvider();
        var ip = IPAddress.Parse("10.4.4.4");
        var (net, client, node) = await JoinAsync("inproc", time: time, ip: ip, store: store);
        await using var _ = net;
        await using var __ = client;

        for (int i = 0; i < 21; i++)
        {
            await client.SendRawAsync(Frame(MsgType.WorldUpdate));
        }

        Assert.Equal(DisconnectCode.TooManyViolations, TestNode.AsDisconnect(await client.ReadAsync()).Code);
        await node.Connection.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        await using var again = await net.ConnectAsync(ip);
        Assert.Equal(DisconnectCode.Banned, TestNode.AsDisconnect(await again.ReadAsync()).Code); // refused before the handshake

        await using var other = await net.ConnectAsync(IPAddress.Parse("10.4.4.5"));
        await other.ReadAsync<ServerHello>(MsgType.ServerHello); // other addresses are fine

        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1)); // ban expired
        await using var later = await net.ConnectAsync(ip);
        await later.ReadAsync<ServerHello>(MsgType.ServerHello);
    }

    [Fact]
    public async Task ViolationsOlderThanAMinuteDoNotCount()
    {
        var time = new FakeTimeProvider();
        var (net, client, node) = await JoinAsync("inproc", time: time);
        await using var _ = net;
        await using var __ = client;

        for (int i = 0; i < 20; i++)
        {
            await client.SendRawAsync(Frame(MsgType.WorldUpdate));
        }

        await BarrierAsync(client, 1);
        time.Advance(TimeSpan.FromSeconds(61));
        for (int i = 0; i < 20; i++)
        {
            await client.SendRawAsync(Frame(MsgType.WorldUpdate));
        }

        await BarrierAsync(client, 2);
        Assert.Equal(20, node.Reader.ViolationCount);
        Assert.False(node.Connection.Closed.IsCancellationRequested);
    }

    [Fact]
    public async Task AuthorityMaySendWorldUpdateWithoutViolations()
    {
        var (net, client, node) = await JoinAsync("inproc", Role.Authority | Role.Client);
        await using var _ = net;
        await using var __ = client;

        for (int i = 0; i < 40; i++)
        {
            await client.SendRawAsync(Frame(MsgType.WorldUpdate));
            await client.SendRawAsync(Frame(MsgType.EntitySpawn));
        }

        await BarrierAsync(client);
        Assert.Equal(0, node.Reader.ViolationCount);
    }

    [Fact]
    public async Task ServerOnlyMessagesLaneMismatchAndPhaseErrorsAllCount()
    {
        var (net, client, node) = await JoinAsync("inproc");
        await using var _ = net;
        await using var __ = client;

        await client.SendRawAsync(Frame(MsgType.Welcome));                    // server-only
        await client.SendRawAsync(Frame(MsgType.Replication));                // server-only
        await client.SendRawAsync(Frame(MsgType.ChatSend, Lane.Realtime));    // lane differs from the catalog
        await client.SendRawAsync(Frame(MsgType.PlayerState));                // client message, but the node is only Admitted
        await client.SendRawAsync(Frame(MsgType.AdminCommand));               // no Admin flag
        await client.SendRawAsync(Frame(MsgType.DamageReport));               // reserved
        await BarrierAsync(client);
        Assert.Equal(6, node.Reader.ViolationCount);

        node.Phase = NodePhase.InGame; // the session layer advances the phase
        await client.SendRawAsync(Frame(MsgType.PlayerState));
        await client.SendRawAsync(Frame(MsgType.ChatSend));
        await BarrierAsync(client, 2);
        Assert.Equal(6, node.Reader.ViolationCount); // both fine now
    }

    [Fact]
    public async Task AdminFlagUnlocksAdminCommand()
    {
        var (net, client, node) = await JoinAsync("inproc", options: new NetOptions { AdminPassword = "root" }, configure: n => n.AdminPassword = "root");
        await using var _ = net;
        await using var __ = client;
        await client.SendRawAsync(Frame(MsgType.AdminCommand));
        await BarrierAsync(client);
        Assert.Equal(0, node.Reader.ViolationCount);
    }

    [Fact]
    public async Task UnknownTypesAreViolationsUnlessThePeerIsNewer()
    {
        var unknown = FrameCodec.Encode((MsgType)0x7F01, Lane.Control, new byte[16]);

        var (net, client, node) = await JoinAsync("inproc");
        await using (net)
        await using (client)
        {
            await client.SendRawAsync(unknown);
            await BarrierAsync(client);
            Assert.Equal(1, node.Reader.ViolationCount);
        }

        var (net2, client2, node2) = await JoinAsync("inproc", configure: n => n.ProtocolMinor = 5);
        await using (net2)
        await using (client2)
        {
            for (int i = 0; i < 30; i++)
            {
                await client2.SendRawAsync(unknown);
            }

            await BarrierAsync(client2);
            Assert.Equal(0, node2.Reader.ViolationCount); // skipped and logged, never closed
            Assert.False(node2.Connection.Closed.IsCancellationRequested);
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ViolationBeforeTheHandshakeCompletesClosesImmediately(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind, withGateway: true);
        await using var client = await net.ConnectAsync();
        await client.ReadAsync<ServerHello>(MsgType.ServerHello);
        await client.SendRawAsync(Frame(MsgType.WorldUpdate));
        Assert.Equal(DisconnectCode.UnexpectedMessage, TestNode.AsDisconnect(await client.ReadAsync()).Code);
        Assert.Null(await client.ReadAsync());
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ReservedFlagBitsCloseWithMalformedMessage(string kind)
    {
        var (net, client, node) = await JoinAsync(kind);
        await using var _ = net;
        await using var __ = client;
        var bad = Frame(MsgType.ChatSend);
        bad[6] = 0x80;
        await client.SendRawAsync(bad);
        Assert.Equal(DisconnectCode.MalformedMessage, TestNode.AsDisconnect(await client.ReadAsync()).Code);
        await node.Connection.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ---- robustness ----

    [Fact]
    public async Task FuzzedConnectionsNeverTakeTheServerDown()
    {
        var rng = new Random(20261001);
        await using var net = await NetHarness.CreateAsync("inproc", new NetOptions { MaxConnectionsPerIp = 1000, HandshakesPerSecond = 100_000 }, withGateway: true);

        // 1. raw garbage before and during the handshake
        for (int i = 0; i < 150; i++)
        {
            await using var c = await net.ConnectAsync(IPAddress.Parse($"10.20.{i / 250}.{i % 250 + 1}"));
            var junk = new byte[rng.Next(1, 600)];
            rng.NextBytes(junk);
            await c.SendRawAsync(junk);
        }

        // 2. well-formed frames with random types, lanes, flags and payloads after a valid join
        for (int round = 0; round < 40; round++)
        {
            await using var c = await net.ConnectAsync(IPAddress.Parse($"10.30.0.{round + 1}"));
            var (_, reply) = await new TestNode("Fuzzer" + round) { Roles = (Role)rng.Next(1, 8) }.JoinAsync(c);
            if (reply!.Value.Type != MsgType.Welcome)
            {
                continue;
            }

            for (int i = 0; i < 60; i++)
            {
                var payload = new byte[rng.Next(1, 200)];
                rng.NextBytes(payload);
                var header = new byte[8];
                FrameCodec.WriteHeader(header, (uint)payload.Length, (MsgType)rng.Next(0, 0x0900), (FrameOptions)(rng.Next(0, 4) == 0 ? rng.Next(0, 256) : 0), (Lane)rng.Next(0, 4));
                await c.SendRawAsync([.. header, .. payload]);
            }
        }

        // The gateway is alive and still admits an honest node.
        await using var honest = await net.ConnectAsync(IPAddress.Parse("10.40.0.1"));
        var (_, welcome) = await new TestNode("Honest").JoinAsync(honest);
        TestNode.AsWelcome(welcome);
        await honest.SendAsync(MsgType.Ping, TestFrames.Ping(5));
        Assert.Equal(5u, (await honest.ReadAsync<Pong>(MsgType.Pong)).Seq);
    }
}
