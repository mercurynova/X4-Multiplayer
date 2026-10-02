using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using Google.FlatBuffers;
using X4MP.Proto;

namespace X4MP.Protocol.Client;

/// <summary>
/// Node-side connection to the server (shared by FakeNode and tests; server-design 6): TCP framing, the
/// <c>ServerHello</c> / <c>ClientHello</c> (HMAC proof) / <c>Welcome</c> handshake, Ping/Pong, and a resume
/// token placeholder. It works over any duplex <see cref="Stream"/>, so tests use an in-memory pipe.
/// <para>
/// Reads are single-consumer: call <see cref="ReceiveAsync"/> from one loop. Pings from the server are answered
/// inside <see cref="ReceiveAsync"/> and never surfaced. Writes are serialised and may come from any thread.
/// </para>
/// </summary>
public sealed class TcpNodeClient : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly NodeClientOptions _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Queue<Frame> _pending = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TcpClient? _tcp;
    private uint _pingSeq;
    private readonly Dictionary<uint, ulong> _pingsInFlight = [];

    private TcpNodeClient(Stream stream, NodeClientOptions options, TcpClient? tcp)
    {
        _stream = stream;
        _options = options;
        _tcp = tcp;
        PlayerKey = options.PlayerKey ?? RandomNumberGenerator.GetBytes(HandshakeAuth.PlayerKeyLength);
        if (PlayerKey.Length != HandshakeAuth.PlayerKeyLength)
            throw new ArgumentException("PlayerKey must be 32 bytes.", nameof(options));
    }

    public byte[] PlayerKey { get; }
    public ServerHelloT ServerHello { get; private set; } = new();
    public WelcomeT Welcome { get; private set; } = new();

    /// <summary>Resume token from the last <c>Welcome</c> (placeholder: the server decides what it means).</summary>
    public Id128T? ResumeToken => Welcome.ResumeToken;

    /// <summary>
    /// Raised from <see cref="ReceiveAsync"/> (on the reading thread) with the round trip of a ping sent by <see cref="SendPingAsync"/> when
    /// its Pong arrives. Lets a node that has a receive loop of its own measure RTT without <see cref="PingAsync"/> taking over the reading.
    /// </summary>
    public event Action<TimeSpan>? PongReceived;

    /// <summary>Local monotonic clock in microseconds (what Ping/Pong carry).</summary>
    public ulong NowUs => (ulong)(_clock.Elapsed.TotalMilliseconds * 1000.0);

    /// <summary>Options for reconnecting as the same node: same key, the previous resume token.</summary>
    public NodeClientOptions ForResume(ulong lastJournalSeq = 0) =>
        _options with { PlayerKey = PlayerKey, ResumeToken = Welcome.ResumeToken, LastJournalSeq = lastJournalSeq };

    /// <summary>Opens a TCP connection and runs the handshake.</summary>
    public static async Task<TcpNodeClient> ConnectAsync(string host, int port, NodeClientOptions options, CancellationToken ct = default)
    {
        var tcp = options.LocalAddress is { } local
            ? new TcpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Parse(local), 0)) { NoDelay = true }
            : new TcpClient { NoDelay = true };
        if (options.ReceiveBufferBytes > 0)
            tcp.ReceiveBufferSize = options.ReceiveBufferBytes;
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
            Stream stream = tcp.GetStream();
            if (options.StreamWrapper is { } wrap)
                stream = wrap(stream);
            var client = new TcpNodeClient(stream, options, tcp);
            await client.HandshakeAsync(ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Runs the handshake over an existing duplex stream (tests, in-proc).</summary>
    public static async Task<TcpNodeClient> ConnectAsync(Stream duplex, NodeClientOptions options, CancellationToken ct = default)
    {
        var client = new TcpNodeClient(duplex, options, null);
        await client.HandshakeAsync(ct).ConfigureAwait(false);
        return client;
    }

    private async Task HandshakeAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.HandshakeTimeout);
        try
        {
            // 1. ServerHello (or an immediate Disconnect).
            var first = await ReadFrameAsync(cts.Token).ConfigureAwait(false)
                ?? throw new HandshakeRejectedException(DisconnectCode.None, "connection closed before ServerHello");
            ThrowIfDisconnect(first);
            if (first.Type != MsgType.ServerHello)
                throw new ProtocolViolation(ViolationCode.MalformedPayload, $"expected ServerHello, got {first.Type}");
            var hello = MessageRegistry.Default.Decode<ServerHello>(first).UnPack();
            ServerHello = hello;
            if (hello.ProtocolMajor != ProtocolConstants.ProtocolMajor)
                throw new HandshakeRejectedException(DisconnectCode.ProtocolMismatch,
                    $"server speaks {hello.ProtocolMajor}.{hello.ProtocolMinor}", $"{ProtocolConstants.ProtocolMajor}.x");

            // 2. ClientHello with the HMAC proof(s).
            var nonce = hello.Nonce?.ToArray() ?? [];
            var clientHello = BuildClientHello(hello.Auth, nonce);
            await SendAsync(MsgType.ClientHello, b => ClientHello.Pack(b, clientHello), cts.Token).ConfigureAwait(false);

            // 3. Welcome or Disconnect.
            var reply = await ReadFrameAsync(cts.Token).ConfigureAwait(false)
                ?? throw new HandshakeRejectedException(DisconnectCode.None, "connection closed after ClientHello");
            ThrowIfDisconnect(reply);
            if (reply.Type != MsgType.Welcome)
                throw new ProtocolViolation(ViolationCode.MalformedPayload, $"expected Welcome, got {reply.Type}");
            Welcome = MessageRegistry.Default.Decode<Welcome>(reply).UnPack();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HandshakeRejectedException(DisconnectCode.HandshakeTimeout, "client-side handshake timeout");
        }
    }

    private ClientHelloT BuildClientHello(AuthMethod auth, byte[] nonce)
    {
        var hello = new ClientHelloT
        {
            ProtocolMajor = ProtocolConstants.ProtocolMajor,
            ProtocolMinor = ProtocolConstants.ProtocolMinor,
            ModVersion = _options.ModVersion,
            ModBuild = _options.ModBuild,
            GameVersion = _options.GameVersion,
            GameBuild = _options.GameBuild,
            X4nativeVersion = _options.X4NativeVersion,
            Platform = _options.Platform,
            ExtensionsHash = [],
            Extensions = [],
            PlayerKey = [.. PlayerKey],
            PlayerName = _options.PlayerName,
            RequestedRoles = _options.RequestedRoles,
            ClientCaps = _options.ClientCaps,
            AuthProof = [],
            AdminProof = [],
            ResumeToken = _options.ResumeToken ?? new Id128T(),
            LastJournalSeq = _options.LastJournalSeq,
            LoadedSaveSha256 = [],
            CachedSaves = [],
        };
        if (auth == AuthMethod.SessionPassword && _options.Password is not null)
            hello.AuthProof = [.. HandshakeAuth.ComputeProof(_options.Password, nonce, PlayerKey)];
        if (_options.AdminPassword is not null)
            hello.AdminProof = [.. HandshakeAuth.ComputeProof(_options.AdminPassword, nonce, PlayerKey)];
        return hello;
    }

    private static void ThrowIfDisconnect(Frame frame)
    {
        if (frame.Type != MsgType.Disconnect)
            return;
        var d = MessageRegistry.Default.Decode<Disconnect>(frame).UnPack();
        throw new HandshakeRejectedException(d.Code, d.Message ?? "", d.Expected ?? "", d.RetryAfterMs);
    }

    // ---- send ----

    /// <summary>Sends an already encoded frame (see <see cref="MessageEncoder.EncodeFrame{T}"/>).</summary>
    public async Task SendRawFrameAsync(byte[] frame, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Encodes and sends one message.</summary>
    public Task SendAsync<T>(MsgType type, Func<FlatBufferBuilder, Offset<T>> pack, CancellationToken ct = default) where T : struct =>
        SendRawFrameAsync(MessageEncoder.EncodeFrame(type, pack), ct);

    /// <summary>Sends a pre-built payload with the catalog lane.</summary>
    public Task SendPayloadAsync(MsgType type, byte[] payload, CancellationToken ct = default) =>
        SendRawFrameAsync(FrameCodec.Encode(type, payload, _options.MaxFrameBytes), ct);

    // ---- receive ----

    private async ValueTask<Frame?> ReadFrameAsync(CancellationToken ct) =>
        await FrameCodec.ReadFrameAsync(_stream, _options.MaxFrameBytes, ct).ConfigureAwait(false);

    /// <summary>
    /// Next frame from the server, or null on a clean close. Server <c>Ping</c>s are answered with <c>Pong</c>
    /// and swallowed; <c>Pong</c>s are only consumed by <see cref="PingAsync"/>.
    /// </summary>
    public async Task<Frame?> ReceiveAsync(CancellationToken ct = default)
    {
        while (true)
        {
            Frame? f = _pending.Count > 0 ? _pending.Dequeue() : await ReadFrameAsync(ct).ConfigureAwait(false);
            if (f is null)
                return null;
            if (f.Value.Type == MsgType.Ping)
            {
                await AnswerPingAsync(f.Value, ct).ConfigureAwait(false);
                continue;
            }
            if (f.Value.Type == MsgType.Pong)
            {
                var pong = MessageRegistry.Default.Decode<Pong>(f.Value).UnPack();
                ulong sent;
                bool known;
                lock (_pingsInFlight)
                    known = _pingsInFlight.Remove(pong.Seq, out sent);
                if (known)
                    PongReceived?.Invoke(TimeSpan.FromMicroseconds(NowUs - sent));
                continue;
            }
            return f;
        }
    }

    private async Task AnswerPingAsync(Frame f, CancellationToken ct)
    {
        ulong recv = NowUs;
        var ping = MessageRegistry.Default.Decode<Ping>(f).UnPack();
        var pong = new PongT { Seq = ping.Seq, EchoSendTimeUs = ping.SendTimeUs, RecvTimeUs = recv, ReplyTimeUs = NowUs };
        await SendAsync(MsgType.Pong, b => Pong.Pack(b, pong), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a Ping and waits for its Pong, returning the round trip. Other frames that arrive meanwhile are
    /// queued for <see cref="ReceiveAsync"/> (server Pings are still answered).
    /// </summary>
    public async Task<TimeSpan> PingAsync(CancellationToken ct = default)
    {
        uint seq = ++_pingSeq;
        ulong sent = NowUs;
        var ping = new PingT { Seq = seq, SendTimeUs = sent };
        await SendAsync(MsgType.Ping, b => Ping.Pack(b, ping), ct).ConfigureAwait(false);
        while (true)
        {
            var f = (await ReadFrameAsync(ct).ConfigureAwait(false))
                ?? throw new IOException("connection closed while waiting for Pong");
            switch (f.Type)
            {
                case MsgType.Pong:
                    var pong = MessageRegistry.Default.Decode<Pong>(f).UnPack();
                    if (pong.Seq == seq)
                        return TimeSpan.FromMicroseconds(NowUs - sent);
                    break;
                case MsgType.Ping:
                    await AnswerPingAsync(f, ct).ConfigureAwait(false);
                    break;
                default:
                    _pending.Enqueue(f);
                    break;
            }
        }
    }

    /// <summary>
    /// Sends a Ping without waiting: its Pong is consumed by <see cref="ReceiveAsync"/> and reported through <see cref="PongReceived"/>.
    /// Do not mix with <see cref="PingAsync"/> on the same client.
    /// </summary>
    public Task SendPingAsync(CancellationToken ct = default)
    {
        uint seq;
        ulong sent = NowUs;
        lock (_pingsInFlight)
        {
            seq = ++_pingSeq;
            _pingsInFlight[seq] = sent;
            if (_pingsInFlight.Count > 64)
                _pingsInFlight.Remove(seq - 64); // an unanswered ping that is long gone
        }
        var ping = new PingT { Seq = seq, SendTimeUs = sent };
        return SendAsync(MsgType.Ping, b => Ping.Pack(b, ping), ct);
    }

    /// <summary>
    /// Drops the connection without a <c>Disconnect</c>, like a crash or a pulled cable: the server keeps the slot for the resume
    /// grace instead of treating it as a quit. Reconnect with <see cref="ForResume"/>.
    /// </summary>
    public void Abort()
    {
        try
        {
            _stream.Dispose();
            _tcp?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // already gone
        }
    }

    /// <summary>Sends <c>Disconnect(ClientQuit)</c> (best effort) and closes the stream.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            var d = new DisconnectT { Code = DisconnectCode.ClientQuit, Message = "", Expected = "" };
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await SendAsync(MsgType.Disconnect, b => Disconnect.Pack(b, d), cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            // already gone
        }
        await _stream.DisposeAsync().ConfigureAwait(false);
        _tcp?.Dispose();
        _writeLock.Dispose();
    }
}
