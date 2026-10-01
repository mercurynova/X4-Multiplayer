using System.Security.Cryptography;
using System.Threading.Channels;
using X4MP.Proto;
using X4MP.Protocol.Client;

namespace X4MP.Protocol.Tests;

/// <summary>One end of an in-memory duplex stream (a pair is created by <see cref="CreatePair"/>).</summary>
internal sealed class ChannelStream : Stream
{
    private readonly ChannelReader<byte[]> _in;
    private readonly ChannelWriter<byte[]> _out;
    private byte[] _left = [];
    private int _leftPos;

    private ChannelStream(ChannelReader<byte[]> r, ChannelWriter<byte[]> w)
    {
        _in = r;
        _out = w;
    }

    public static (ChannelStream A, ChannelStream B) CreatePair()
    {
        var ab = Channel.CreateUnbounded<byte[]>();
        var ba = Channel.CreateUnbounded<byte[]>();
        return (new ChannelStream(ba.Reader, ab.Writer), new ChannelStream(ab.Reader, ba.Writer));
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => _out.TryWrite(buffer.AsSpan(offset, count).ToArray());

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_leftPos >= _left.Length)
        {
            if (!await _in.WaitToReadAsync(cancellationToken).ConfigureAwait(false) || !_in.TryRead(out var next))
                return 0;
            _left = next;
            _leftPos = 0;
        }
        int n = Math.Min(buffer.Length, _left.Length - _leftPos);
        _left.AsSpan(_leftPos, n).CopyTo(buffer.Span);
        _leftPos += n;
        return n;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // deliver in 3-byte slices to exercise reassembly
        var data = buffer.ToArray();
        for (int i = 0; i < data.Length; i += 3)
            _out.TryWrite(data.AsSpan(i, Math.Min(3, data.Length - i)).ToArray());
        return ValueTask.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        _out.TryComplete();
        return base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _out.TryComplete();
        base.Dispose(disposing);
    }
}

/// <summary>A tiny server that speaks the handshake of protocol.md 4 over a stream.</summary>
internal sealed class FakeHandshakeServer
{
    private readonly string? _password;
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(32);

    public FakeHandshakeServer(string? password) => _password = password;

    public ClientHelloT? SeenHello { get; private set; }
    public Id128T IssuedToken { get; } = new() { Lo = 0x1111, Hi = 0x2222 };
    public bool SendServerPing { get; init; }
    public ushort ProtocolMajor { get; init; } = ProtocolConstants.ProtocolMajor;

    private static Task Send<T>(Stream s, MsgType type, Func<Google.FlatBuffers.FlatBufferBuilder, Google.FlatBuffers.Offset<T>> pack) where T : struct =>
        s.WriteAsync(MessageEncoder.EncodeFrame(type, pack)).AsTask();

    public async Task RunAsync(Stream stream, CancellationToken ct)
    {
        var hello = new ServerHelloT
        {
            ProtocolMajor = ProtocolMajor, ProtocolMinor = ProtocolConstants.ProtocolMinor, ServerVersion = "test", ServerName = "FakeServer",
            SessionId = new Id128T { Lo = 1, Hi = 2 }, Nonce = [.. _nonce],
            Auth = _password is null ? AuthMethod.None : AuthMethod.SessionPassword, Phase = SessionPhase.Running,
            RequiredGameBuild = "", SupportedGameBuilds = ["900-611726"], RequiredModVersion = "", ExtensionsHash = [],
        };
        await Send(stream, MsgType.ServerHello, b => ServerHello.Pack(b, hello));

        var frame = await FrameCodec.ReadFrameAsync(stream, cancellationToken: ct) ?? throw new IOException("client closed");
        Assert.Equal(MsgType.ClientHello, frame.Type);
        var ch = MessageRegistry.Default.Decode<ClientHello>(frame).UnPack();
        SeenHello = ch;

        if (_password is not null)
        {
            var expected = HandshakeAuth.ComputeProof(_password, _nonce, ch.PlayerKey.ToArray());
            if (!HandshakeAuth.ProofsEqual(expected, ch.AuthProof.ToArray()))
            {
                var d = new DisconnectT { Code = DisconnectCode.AuthFailed, Message = "bad proof", Expected = "" };
                await Send(stream, MsgType.Disconnect, b => Disconnect.Pack(b, d));
                return;
            }
        }

        bool resumed = ch.ResumeToken is { Lo: 0x1111, Hi: 0x2222 };
        var welcome = new WelcomeT
        {
            PlayerId = 3, GrantedRoles = ch.RequestedRoles, NegotiatedCaps = ch.ClientCaps, ResumeToken = IssuedToken, Resumed = resumed,
            ConnId = 9, UdpPort = 0, ServerTimeUs = 1_000_000, HeartbeatIntervalMs = 1000, HeartbeatTimeoutMs = 10_000, ResumeGraceS = 60,
            HttpBaseUrl = "", MaxGhosts = 4000,
        };
        await Send(stream, MsgType.Welcome, b => Welcome.Pack(b, welcome));

        if (SendServerPing)
        {
            var ping = new PingT { Seq = 500, SendTimeUs = 42 };
            await Send(stream, MsgType.Ping, b => Ping.Pack(b, ping));
        }

        while (await FrameCodec.ReadFrameAsync(stream, cancellationToken: ct) is { } f)
        {
            if (f.Type == MsgType.Ping)
            {
                var p = MessageRegistry.Default.Decode<Ping>(f).UnPack();
                var pong = new PongT { Seq = p.Seq, EchoSendTimeUs = p.SendTimeUs, RecvTimeUs = 7, ReplyTimeUs = 8 };
                await Send(stream, MsgType.Pong, b => Pong.Pack(b, pong));
            }
            else if (f.Type == MsgType.Pong)
            {
                Pongs.Add(MessageRegistry.Default.Decode<Pong>(f).UnPack());
            }
            else if (f.Type == MsgType.Disconnect)
            {
                return;
            }
        }
    }

    public List<PongT> Pongs { get; } = [];
}

public sealed class NodeClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static async Task<(TcpNodeClient Client, FakeHandshakeServer Server, Task ServerTask)> Connect(
        string? serverPassword, string? clientPassword, NodeClientOptions? options = null, bool serverPing = false)
    {
        var (a, b) = ChannelStream.CreatePair();
        var server = new FakeHandshakeServer(serverPassword) { SendServerPing = serverPing };
        var serverTask = Task.Run(() => server.RunAsync(b, new CancellationTokenSource(Timeout).Token));
        var opts = (options ?? new NodeClientOptions { PlayerName = "Bob" }) with { Password = clientPassword };
        var client = await TcpNodeClient.ConnectAsync(a, opts, new CancellationTokenSource(Timeout).Token);
        return (client, server, serverTask);
    }

    [Fact]
    public async Task HandshakeSucceedsWithCorrectPassword()
    {
        var (client, server, serverTask) = await Connect("hunter2", "hunter2");
        await using (client)
        {
            Assert.Equal((ushort)3, client.Welcome.PlayerId);
            Assert.Equal(Role.Client, client.Welcome.GrantedRoles);
            Assert.Equal("Bob", server.SeenHello!.PlayerName);
            Assert.Equal(32, server.SeenHello.PlayerKey.Count);
            Assert.Equal(ProtocolConstants.ProtocolMajor, server.SeenHello.ProtocolMajor);
            Assert.Equal("FakeServer", client.ServerHello.ServerName);
            Assert.Equal(32, server.SeenHello.AuthProof.Count);
            Assert.Empty(server.SeenHello.AdminProof);
        }
        await serverTask;
    }

    [Fact]
    public async Task HandshakeWorksWithoutAuth()
    {
        var (client, server, serverTask) = await Connect(null, null);
        await using (client)
            Assert.Empty(server.SeenHello!.AuthProof);
        await serverTask;
    }

    [Fact]
    public async Task WrongPasswordIsRejectedWithAuthFailed()
    {
        var ex = await Assert.ThrowsAsync<HandshakeRejectedException>(() => Connect("hunter2", "wrong"));
        Assert.Equal(DisconnectCode.AuthFailed, ex.Code);
        Assert.Equal("bad proof", ex.ServerMessage);
    }

    [Fact]
    public async Task MissingPasswordIsRejected()
    {
        var ex = await Assert.ThrowsAsync<HandshakeRejectedException>(() => Connect("hunter2", null));
        Assert.Equal(DisconnectCode.AuthFailed, ex.Code);
    }

    [Fact]
    public async Task ProtocolMajorMismatchIsRejectedClientSide()
    {
        var (a, b) = ChannelStream.CreatePair();
        var server = new FakeHandshakeServer(null) { ProtocolMajor = 9 };
        _ = Task.Run(() => server.RunAsync(b, CancellationToken.None));
        var ex = await Assert.ThrowsAsync<HandshakeRejectedException>(() =>
            TcpNodeClient.ConnectAsync(a, new NodeClientOptions(), new CancellationTokenSource(Timeout).Token));
        Assert.Equal(DisconnectCode.ProtocolMismatch, ex.Code);
    }

    [Fact]
    public async Task HandshakeTimesOutWhenServerIsSilent()
    {
        var (a, _) = ChannelStream.CreatePair();
        var ex = await Assert.ThrowsAsync<HandshakeRejectedException>(() =>
            TcpNodeClient.ConnectAsync(a, new NodeClientOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(200) }));
        Assert.Equal(DisconnectCode.HandshakeTimeout, ex.Code);
    }

    [Fact]
    public async Task PingGetsAPongAndServerPingsAreAnswered()
    {
        var (client, server, serverTask) = await Connect(null, null, serverPing: true);
        await using (client)
        {
            var rtt = await client.PingAsync();
            Assert.True(rtt >= TimeSpan.Zero);
            // the server's own Ping (seq 500) was answered with an echo of its send time
            var cts = new CancellationTokenSource(Timeout);
            await Task.Run(async () => { while (server.Pongs.Count == 0) await Task.Delay(10, cts.Token); }, cts.Token);
            Assert.Equal(500u, server.Pongs[0].Seq);
            Assert.Equal(42UL, server.Pongs[0].EchoSendTimeUs);
        }
        await serverTask;
    }

    [Fact]
    public async Task SendPingDoesNotBlockAndItsPongIsReportedWhileTheReceiveLoopRuns()
    {
        var (client, _, serverTask) = await Connect(null, null);
        await using (client)
        {
            var rtts = new List<TimeSpan>();
            client.PongReceived += rtts.Add;
            await client.SendPingAsync();
            await client.SendPingAsync();

            // the receive loop (the only reader) swallows Pongs and raises the event; nothing else arrives
            using var cts = new CancellationTokenSource(Timeout);
            var received = client.ReceiveAsync(cts.Token);
            while (true)
            {
                int count;
                lock (rtts)
                    count = rtts.Count;
                if (count >= 2)
                    break;
                await Task.Delay(10, cts.Token);
            }
            Assert.All(rtts, r => Assert.True(r >= TimeSpan.Zero));
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await received);
        }
        await serverTask;
    }

    [Fact]
    public async Task ResumeTokenIsKeptAndReusedOnReconnect()
    {
        var (client, _, serverTask) = await Connect(null, null);
        Assert.Equal(0x1111UL, client.ResumeToken!.Lo);
        var again = client.ForResume(lastJournalSeq: 17);
        Assert.Equal(client.PlayerKey, again.PlayerKey);
        await client.DisposeAsync();
        await serverTask;

        var (a, b) = ChannelStream.CreatePair();
        var server2 = new FakeHandshakeServer(null);
        var task2 = Task.Run(() => server2.RunAsync(b, new CancellationTokenSource(Timeout).Token));
        await using var second = await TcpNodeClient.ConnectAsync(a, again, new CancellationTokenSource(Timeout).Token);
        Assert.True(second.Welcome.Resumed);
        Assert.Equal(17UL, server2.SeenHello!.LastJournalSeq);
        Assert.Equal(client.PlayerKey, server2.SeenHello.PlayerKey.ToArray());
        await second.DisposeAsync();
        await task2;
    }

    [Fact]
    public void ProofMatchesTheDocumentedConstruction()
    {
        byte[] nonce = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
        byte[] key = [.. Enumerable.Range(100, 32).Select(i => (byte)i)];
        var expected = HMACSHA256.HashData(SHA256.HashData("pw"u8.ToArray()).AsSpan(), nonce.Concat(key).ToArray());
        Assert.Equal(expected, HandshakeAuth.ComputeProof("pw", nonce, key));
        Assert.NotEqual(expected, HandshakeAuth.ComputeProof("pw2", nonce, key));
        Assert.NotEqual(expected, HandshakeAuth.ComputeProof("pw", key, nonce));
    }
}
