using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using X4MP.Proto;

namespace X4MP.Protocol.Client;

/// <summary>
/// The node side of the UDP Realtime lane (protocol.md 3.3), shared by FakeNode and tests. After <c>Welcome</c> it binds with
/// <c>UdpHello{conn_id, udp_token}</c> (repeated every 250 ms until <c>UdpHelloAck</c>, given up after 3 s: the caller then stays on TCP), then
/// sends and receives datagrams with <c>seq/ack/ack_bits</c>, answers a stretch of silence with an ack-only datagram after 50 ms, and hands every
/// received sub-message (as a <see cref="Frame"/>) to <see cref="FrameReceived"/> on the receive thread.
/// <para>
/// <see cref="LossRate"/> drops that share of datagrams in both directions before they are processed or sent (failure injection:
/// FakeNode <c>--loss</c>). <see cref="RebindAsync"/> switches to a new local socket, the way a NAT rebinding looks to the server.
/// </para>
/// </summary>
public sealed class UdpRealtimeClient : IAsyncDisposable
{
    /// <summary>How often the hello is repeated until it is acknowledged.</summary>
    public static readonly TimeSpan HelloInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How long binding is tried before the lane is given up (the Realtime lane then runs over TCP).</summary>
    public static readonly TimeSpan BindTimeout = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan AckOnlyAfter = TimeSpan.FromMilliseconds(50);

    private readonly string _host;
    private readonly int _port;
    private readonly uint _connId;
    private readonly ulong _token;
    private readonly object _sendGate = new();
    private readonly object _rxGate = new();
    private readonly DatagramReceiveWindow _rx = new();
    private readonly byte[] _tx = new byte[DatagramCodec.MaxDatagramBytes];
    private readonly Random _random;
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Timer _ackTimer;
    private Socket? _socket;
    private TaskCompletionSource _acked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint _seq;
    private volatile bool _bound;
    private volatile bool _ackPending;
    private long _lastSendMs;
    private long _sent;
    private long _received;
    private long _simulatedDrops;
    private long _malformed;
    private int _disposed;

    public UdpRealtimeClient(string host, int port, uint connId, ulong token, double lossRate = 0, int? seed = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        _host = host;
        _port = port;
        _connId = connId;
        _token = token;
        LossRate = lossRate;
        _random = seed is { } s ? new Random(s) : new Random();
        _ackTimer = new Timer(static state => ((UdpRealtimeClient)state!).OnAckTimer(), this, 25, 25);
    }

    /// <summary>Called on the receive thread for every sub-message except <c>UdpHelloAck</c>. Set before <see cref="BindAsync"/>.</summary>
    public Action<Frame>? FrameReceived { get; set; }

    /// <summary>Share (0..1) of datagrams dropped in each direction (failure injection; set before binding, or any time).</summary>
    public double LossRate { get; set; }

    /// <summary>Failure injection: delay added to every datagram in each direction (0 = none).</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>Failure injection: each datagram's delay varies by up to this much either way (so datagrams can reorder).</summary>
    public TimeSpan Jitter { get; set; }

    /// <summary>True once the server acknowledged the hello; the lane is usable.</summary>
    public bool Bound => _bound;

    /// <summary>The local endpoint (changes with <see cref="RebindAsync"/>).</summary>
    public IPEndPoint? LocalEndPoint => _socket?.LocalEndPoint as IPEndPoint;

    public long DatagramsSent => Interlocked.Read(ref _sent);

    public long DatagramsReceived => Interlocked.Read(ref _received);

    /// <summary>Datagrams dropped by <see cref="LossRate"/> (both directions).</summary>
    public long SimulatedDrops => Interlocked.Read(ref _simulatedDrops);

    /// <summary>Datagrams that were not valid datagrams or were refused by the sequence window.</summary>
    public long Malformed => Interlocked.Read(ref _malformed);

    /// <summary>The loss the received sequence numbers show, in percent (what <c>NodeStats.udp_rx_loss_pct</c> reports).</summary>
    public float RxLossPercent
    {
        get
        {
            lock (_rxGate)
            {
                return _rx.LossPercent;
            }
        }
    }

    /// <summary>
    /// Binds: sends <c>UdpHello</c> every 250 ms until the ack arrives. Returns true when bound, false after <paramref name="timeout"/>
    /// (default 3 s) or when the server could not be reached; the caller then keeps the Realtime lane on TCP.
    /// </summary>
    public async Task<bool> BindAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        try
        {
            OpenSocket();
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return false;
        }

        return await HelloLoopAsync(timeout ?? BindTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Switches to a new local socket (a new source port) and binds again with a fresh hello: what the server sees after a NAT rebinding. The
    /// sequence numbering continues.
    /// </summary>
    public async Task<bool> RebindAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        _bound = false;
        Interlocked.Exchange(ref _acked, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        OpenSocket();
        return await HelloLoopAsync(timeout ?? BindTimeout, ct).ConfigureAwait(false);
    }

    private async Task<bool> HelloLoopAsync(TimeSpan timeout, CancellationToken ct)
    {
        var hello = MessageEncoder.EncodePayload(b => UdpHello.Pack(b, new UdpHelloT { ConnId = _connId, UdpToken = _token }), 32);
        var started = Stopwatch.StartNew();
        var ack = Volatile.Read(ref _acked).Task;
        while (started.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            SendDatagram(MsgType.UdpHello, hello);
            var wait = Task.WhenAny(ack, Task.Delay(HelloInterval, ct));
            await wait.ConfigureAwait(false);
            if (ack.IsCompleted)
            {
                return true;
            }
        }

        return false;
    }

    private void OpenSocket()
    {
        var addresses = Dns.GetHostAddresses(_host);
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                socket.IOControl((IOControlCode)(-1744830452), [0, 0, 0, 0], null); // SIO_UDP_CONNRESET: an ICMP port-unreachable must not break receives
            }
        }
        catch (Exception ex) when (ex is SocketException or NotSupportedException or PlatformNotSupportedException)
        {
            // best effort
        }

        socket.Connect(new IPEndPoint(address, _port));
        Socket? old;
        lock (_sendGate)
        {
            old = _socket;
            _socket = socket;
        }

        old?.Dispose();
        _ = Task.Run(() => ReceiveLoopAsync(socket));
    }

    private bool Lose()
    {
        double rate = LossRate;
        if (rate <= 0)
        {
            return false;
        }

        bool drop;
        lock (_random)
        {
            drop = _random.NextDouble() < rate;
        }

        if (drop)
        {
            Interlocked.Increment(ref _simulatedDrops);
        }

        return drop;
    }

    private async Task ReceiveLoopAsync(Socket socket)
    {
        var buffer = new byte[2048];
        var subs = new List<(MsgType Type, int Offset, int Length)>(8);
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await socket.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                if (!ReferenceEquals(socket, _socket))
                {
                    return;
                }

                await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            if (!ReferenceEquals(socket, _socket) || Lose())
            {
                continue;
            }

            if (NextDelay() is { } delay)
            {
                var copy = buffer.AsSpan(0, n).ToArray();
                _ = DelayedAsync(delay, () => HandleGuarded(copy, []));
                continue;
            }

            HandleGuarded(buffer.AsSpan(0, n), subs);
        }
    }

    private void HandleGuarded(ReadOnlySpan<byte> datagram, List<(MsgType Type, int Offset, int Length)> subs)
    {
        try
        {
            Handle(datagram, subs);
        }
        catch (ProtocolViolation)
        {
            Interlocked.Increment(ref _malformed);
        }
    }

    /// <summary>The delay for the next datagram (latency plus or minus jitter), or null when no delay is injected.</summary>
    private TimeSpan? NextDelay()
    {
        var latency = Latency;
        if (latency <= TimeSpan.Zero && Jitter <= TimeSpan.Zero)
        {
            return null;
        }

        double ms = latency.TotalMilliseconds;
        if (Jitter > TimeSpan.Zero)
        {
            lock (_random)
            {
                ms += (_random.NextDouble() * 2 - 1) * Jitter.TotalMilliseconds;
            }
        }

        return TimeSpan.FromMilliseconds(Math.Max(0, ms));
    }

    private async Task DelayedAsync(TimeSpan delay, Action action)
    {
        try
        {
            await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
            action();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // the lane was disposed while the datagram was in flight
        }
    }

    private void Handle(ReadOnlySpan<byte> datagram, List<(MsgType Type, int Offset, int Length)> subs)
    {
        var header = DatagramCodec.ReadHeader(datagram);
        if (header.ConnId != _connId)
        {
            Interlocked.Increment(ref _malformed);
            return;
        }

        subs.Clear();
        DatagramCodec.ReadSubMessages(datagram, subs);
        lock (_rxGate)
        {
            if (!_rx.Accept(header.Seq))
            {
                Interlocked.Increment(ref _malformed);
                return;
            }
        }

        Interlocked.Increment(ref _received);
        if (subs.Count > 0)
        {
            _ackPending = true;
        }

        foreach (var (type, offset, length) in subs)
        {
            if (type == MsgType.UdpHelloAck)
            {
                _bound = true;
                Volatile.Read(ref _acked).TrySetResult();
                continue;
            }

            if (!DatagramCodec.IsAllowedOnUdp(type))
            {
                Interlocked.Increment(ref _malformed);
                continue;
            }

            var payload = datagram.Slice(offset, length).ToArray();
            FrameReceived?.Invoke(new Frame(type, FrameOptions.None, MessageRegistry.Default.GetDescriptor(type).Lane, payload));
        }
    }

    /// <summary>
    /// Sends one message as a datagram. False when the lane is not bound, the type may not travel over UDP, or the message does not fit a
    /// datagram (the caller sends it over TCP then). A datagram dropped by <see cref="LossRate"/> still counts as sent.
    /// </summary>
    public bool TrySend(MsgType type, ReadOnlySpan<byte> payload)
    {
        if (!_bound || !DatagramCodec.IsAllowedOnUdp(type) || payload.Length == 0 || DatagramCodec.HeaderSize + DatagramCodec.SubMessageSize(payload.Length) > DatagramCodec.MaxDatagramBytes)
        {
            return false;
        }

        return SendDatagram(type, payload);
    }

    private bool SendDatagram(MsgType type, ReadOnlySpan<byte> payload)
    {
        lock (_sendGate)
        {
            if (_socket is not { } socket || Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            int length = BuildHeader();
            if (payload.Length > 0)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(_tx.AsSpan(length), (ushort)type);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(_tx.AsSpan(length + 2), (ushort)payload.Length);
                payload.CopyTo(_tx.AsSpan(length + DatagramCodec.SubMessageHeaderSize));
                int size = DatagramCodec.SubMessageSize(payload.Length);
                _tx.AsSpan(length + DatagramCodec.SubMessageHeaderSize + payload.Length, size - DatagramCodec.SubMessageHeaderSize - payload.Length).Clear();
                length += size;
            }

            return Transmit(socket, length);
        }
    }

    // Caller holds _sendGate.
    private int BuildHeader()
    {
        uint ack;
        uint ackBits;
        lock (_rxGate)
        {
            ack = _rx.Ack;
            ackBits = _rx.AckBits;
        }

        DatagramCodec.WriteHeader(_tx, new DatagramHeader((byte)ProtocolConstants.ProtocolMajor, _connId, ++_seq, ack, ackBits));
        _ackPending = false;
        return DatagramCodec.HeaderSize;
    }

    private bool Transmit(Socket socket, int length)
    {
        Volatile.Write(ref _lastSendMs, _clock.ElapsedMilliseconds);
        Interlocked.Increment(ref _sent);
        if (Lose())
        {
            return true;
        }

        if (NextDelay() is { } delay)
        {
            var copy = _tx.AsSpan(0, length).ToArray();
            _ = DelayedAsync(delay, () => socket.Send(copy));
            return true;
        }

        try
        {
            socket.Send(_tx.AsSpan(0, length));
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    private void OnAckTimer()
    {
        if (!_bound || !_ackPending || _clock.ElapsedMilliseconds - Volatile.Read(ref _lastSendMs) < AckOnlyAfter.TotalMilliseconds)
        {
            return;
        }

        lock (_sendGate)
        {
            if (_socket is not { } socket || Volatile.Read(ref _disposed) != 0 || !_ackPending)
            {
                return;
            }

            Transmit(socket, BuildHeader()); // a bare header: only the acks
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _ackTimer.DisposeAsync().ConfigureAwait(false);
        await _cts.CancelAsync().ConfigureAwait(false);
        lock (_sendGate)
        {
            _socket?.Dispose();
            _socket = null;
        }

        _cts.Dispose();
    }
}
