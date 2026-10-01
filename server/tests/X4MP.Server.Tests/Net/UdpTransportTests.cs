using System.Net;
using X4MP.Core.Net;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;
using X4MP.Transport;

namespace X4MP.Server.Tests.Net;

/// <summary>The UDP Realtime lane on real loopback sockets: binding, acks, rebinding and the drop rules (protocol.md 3.3).</summary>
[Collection("net")]
public sealed class UdpTransportTests
{
    private const uint ConnId = 77;
    private const ulong Token = 0xA1B2C3D4E5F60718UL;

    private sealed class Rig : IAsyncDisposable
    {
        private readonly InProcListener _listener = new(new NetOptions());

        public UdpRealtimeServer Server { get; } = new(IPAddress.Loopback, 0);

        public INodeConnection Connection { get; private set; } = null!;

        public async Task StartAsync()
        {
            Assert.True(Server.Start());
            _listener.Connect();
            await foreach (var c in _listener.AcceptAsync(CancellationToken.None))
            {
                Connection = c;
                break;
            }

            Server.Register(Connection, ConnId, Token);
        }

        public UdpRealtimeClient NewClient(ulong token = Token) => new("127.0.0.1", Server.Port, ConnId, token);

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await Server.DisposeAsync();
            await _listener.DisposeAsync();
        }
    }

    private static byte[] PingPayload(uint seq) => MessageEncoder.EncodePayload(b => Ping.Pack(b, new PingT { Seq = seq, SendTimeUs = 1 }), 32);

    private static async Task<bool> WaitAsync(Func<bool> condition, int ms = 3000)
    {
        var until = Environment.TickCount64 + ms;
        while (!condition())
        {
            if (Environment.TickCount64 > until)
                return false;
            await Task.Delay(5);
        }

        return true;
    }

    [Fact]
    public async Task AValidHelloBindsAndInboundDatagramsReachTheReaderLikeTcpFrames()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        await using var client = rig.NewClient();
        Assert.True(await client.BindAsync());
        Assert.Equal(1, rig.Server.Binds);

        Assert.True(client.TrySend(MsgType.Ping, PingPayload(5)));
        using var cts = new CancellationTokenSource(3000);
        var inbound = await rig.Connection.ReadAsync(cts.Token);
        Assert.NotNull(inbound);
        Assert.Equal(MsgType.Ping, inbound.Value.Type);
        Assert.Equal(5u, MessageRegistry.Default.Decode<Ping>(inbound.Value.Frame).Seq);
    }

    [Fact]
    public async Task AWrongTokenNeverBinds()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        await using var client = rig.NewClient(token: 1234);
        Assert.False(await client.BindAsync(TimeSpan.FromMilliseconds(600)));
        Assert.Equal(0, rig.Server.Binds);
        Assert.True(rig.Server.BadHello >= 1);
        Assert.False(client.TrySend(MsgType.Ping, PingPayload(1))); // not bound: the caller uses TCP
    }

    [Fact]
    public async Task OutboundFramesGoAsDatagramsAndTheAckFiresTheDeliveryObserverOncePerFrame()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        await using var client = rig.NewClient();
        var received = new List<Frame>();
        client.FrameReceived = f => { lock (received) received.Add(f); };
        var delivered = new List<long>();
        rig.Connection.SetFlushObserver(f => { lock (delivered) delivered.Add(f.DeliveryToken); });
        Assert.True(await client.BindAsync());
        Assert.True(rig.Connection.RealtimeOverDatagram);

        for (long token = 1; token <= 3; token++)
        {
            var frame = OutboundFrame.Create(MsgType.Replication, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            frame.DeliveryToken = token;
            Assert.Equal(SendResult.Queued, rig.Connection.TrySend(frame));
            frame.Release();
        }

        Assert.True(await WaitAsync(() => { lock (received) return received.Count == 3; }));
        Assert.All(received, f => Assert.Equal(MsgType.Replication, f.Type));
        // the client has nothing to send, so its acks come in an ack-only datagram after 50 ms
        Assert.True(await WaitAsync(() => { lock (delivered) return delivered.Count == 3; }));
        Assert.Equal([1L, 2L, 3L], delivered.Order().ToArray());
        await Task.Delay(300);
        lock (delivered)
            Assert.Equal(3, delivered.Count); // each frame confirmed once
        Assert.Equal(0, rig.Connection.QueuedBytes(Lane.Realtime)); // nothing went through the TCP queue
    }

    [Fact]
    public async Task AFrameThatCannotTravelOverUdpFallsBackToTheTcpLane()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        await using var client = rig.NewClient();
        Assert.True(await client.BindAsync());
        long before = rig.Server.DatagramsOut;
        var big = OutboundFrame.Create(MsgType.Replication, new byte[1500]); // over the 1200 byte datagram budget
        Assert.Equal(SendResult.Queued, rig.Connection.TrySend(big));
        big.Release();
        Assert.Equal(before, rig.Server.DatagramsOut); // no datagram: it went to the TCP queue
        Assert.True(await WaitAsync(() => rig.Connection.Stats.BytesSentOn(Lane.Realtime) >= 1500));
    }

    [Fact]
    public async Task AValidHelloFromANewEndpointRebindsAndTheOldEndpointIsDropped()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        await using var oldPath = rig.NewClient();
        Assert.True(await oldPath.BindAsync());

        await using var moved = rig.NewClient(); // a new socket = a new source port, like a NAT rebinding
        Assert.NotEqual(oldPath.LocalEndPoint, moved.LocalEndPoint);
        Assert.True(await moved.BindAsync());
        Assert.Equal(1, rig.Server.Rebinds);

        long unboundBefore = rig.Server.UnboundEndpoint;
        var oldReceived = oldPath.DatagramsReceived;
        Assert.True(oldPath.TrySend(MsgType.Ping, PingPayload(1)));
        Assert.True(await WaitAsync(() => rig.Server.UnboundEndpoint > unboundBefore));
        await Task.Delay(200);
        Assert.Equal(oldReceived, oldPath.DatagramsReceived); // no ack-only datagram, nothing for the old endpoint

        long newReceived = moved.DatagramsReceived;
        Assert.True(moved.TrySend(MsgType.Ping, PingPayload(2)));
        using var cts = new CancellationTokenSource(3000);
        var inbound = await rig.Connection.ReadAsync(cts.Token);
        Assert.Equal(2u, MessageRegistry.Default.Decode<Ping>(inbound!.Value.Frame).Seq);
        Assert.True(await WaitAsync(() => moved.DatagramsReceived > newReceived)); // the server keeps talking to the new endpoint
    }

    [Fact]
    public async Task TheSameClientRebindingToANewSocketKeepsWorking()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        await using var client = rig.NewClient();
        Assert.True(await client.BindAsync());
        var firstPort = client.LocalEndPoint!.Port;
        Assert.True(await client.RebindAsync());
        Assert.NotEqual(firstPort, client.LocalEndPoint!.Port);
        Assert.Equal(1, rig.Server.Rebinds);

        var got = new List<Frame>();
        client.FrameReceived = f => { lock (got) got.Add(f); };
        var frame = OutboundFrame.Create(MsgType.Replication, new byte[] { 9, 9, 9, 9 });
        rig.Connection.TrySend(frame);
        frame.Release();
        Assert.True(await WaitAsync(() => { lock (got) return got.Count == 1; }));
    }

    [Fact]
    public async Task AckBitsConfirmA32DatagramWindowOnceAndDuplicateOrOldDatagramsAreIgnored()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var delivered = new List<long>();
        rig.Connection.SetFlushObserver(f => { lock (delivered) delivered.Add(f.DeliveryToken); });

        using var raw = new System.Net.Sockets.UdpClient();
        var server = new IPEndPoint(IPAddress.Loopback, rig.Server.Port);
        byte[] Datagram(uint seq, uint ack, uint ackBits, MsgType? type = null, byte[]? payload = null)
        {
            var b = new DatagramCodec.Builder(new DatagramHeader((byte)ProtocolConstants.ProtocolMajor, ConnId, seq, ack, ackBits));
            if (type is { } t)
                b.TryAdd(t, payload!);
            return b.Build();
        }

        uint clientSeq = 1;
        await raw.SendAsync(Datagram(clientSeq++, 0, 0, MsgType.UdpHello, MessageEncoder.EncodePayload(b => UdpHello.Pack(b, new UdpHelloT { ConnId = ConnId, UdpToken = Token }), 32)), server);
        var ackReply = await raw.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1u, DatagramCodec.ReadHeader(ackReply.Buffer).Seq); // the server's first datagram is the hello ack

        for (long token = 1; token <= 40; token++)
        {
            var frame = OutboundFrame.Create(MsgType.Replication, new byte[] { 1, 2, 3, 4 });
            frame.DeliveryToken = token;
            rig.Connection.TrySend(frame);
            frame.Release();
        }

        for (int i = 0; i < 40; i++)
            await raw.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3)); // server datagrams 2..41 carry tokens 1..40

        // ack = 41 with every ack_bit set confirms 41 and the 32 before it (40..9) = tokens 40..8; datagrams 2..8 (tokens 1..7) stay unconfirmed
        await raw.SendAsync(Datagram(clientSeq, 41, uint.MaxValue), server);
        Assert.True(await WaitAsync(() => { lock (delivered) return delivered.Count == 33; }));
        lock (delivered)
            Assert.Equal(Enumerable.Range(8, 33).Select(i => (long)i), delivered.Order());

        // the same header again (a duplicate sequence number) and an old one (older than highest - 64) confirm nothing more and are counted
        long duplicates = rig.Server.Duplicates;
        await raw.SendAsync(Datagram(clientSeq, 41, uint.MaxValue), server);
        clientSeq += 100;
        await raw.SendAsync(Datagram(clientSeq, 41, uint.MaxValue), server);
        Assert.True(await WaitAsync(() => rig.Server.Duplicates > duplicates));
        await raw.SendAsync(Datagram(clientSeq - 90, 41, uint.MaxValue), server); // 90 behind the highest
        Assert.True(await WaitAsync(() => rig.Server.Duplicates >= duplicates + 2));
        await Task.Delay(100);
        lock (delivered)
            Assert.Equal(33, delivered.Count);
    }

    [Fact]
    public async Task DatagramsForUnknownConnectionsAndDuplicatesAreDropped()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        using var stranger = new System.Net.Sockets.UdpClient();
        var header = new byte[DatagramCodec.HeaderSize];
        DatagramCodec.WriteHeader(header, new DatagramHeader((byte)ProtocolConstants.ProtocolMajor, 999, 1, 0, 0));
        await stranger.SendAsync(header, new IPEndPoint(IPAddress.Loopback, rig.Server.Port));
        await stranger.SendAsync(new byte[] { 1, 2, 3 }, new IPEndPoint(IPAddress.Loopback, rig.Server.Port));
        Assert.True(await WaitAsync(() => rig.Server.UnknownConnection >= 1 && rig.Server.Malformed >= 1));
    }
}
