using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using X4MP.Core.Net;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Transport;

/// <summary>
/// The server end of the UDP Realtime lane (protocol.md 3.3, server-design 2.2). One socket serves every node:
/// <list type="bullet">
/// <item>The gateway <see cref="Register"/>s each admitted connection with its <c>conn_id</c> and <c>udp_token</c> (the numbers of <c>Welcome</c>).</item>
/// <item>A datagram with a known <c>conn_id</c> that carries a valid <c>UdpHello</c> binds the connection to the datagram's source endpoint (a
/// <see cref="UdpPeer"/> is attached to it as its <see cref="IDatagramPath"/>) and is answered with <c>UdpHelloAck</c>. A valid hello from a
/// different endpoint re-binds (NAT rebinding). Datagrams from any other endpoint are dropped and counted.</item>
/// <item>Bound datagrams are checked against the sequence window (duplicates and datagrams older than 64 are dropped), their <c>ack/ack_bits</c> confirm
/// outbound frames (the connection's delivery observer is called per frame, which is what drives replication's baselines), and their sub-messages
/// reach the session through <see cref="PipeNodeConnection.EnqueueDatagramFrame"/> exactly like TCP frames.</item>
/// <item>A node with nothing to send for 50 ms that has unacknowledged inbound datagrams gets an ack-only datagram (a bare 24 byte header).</item>
/// </list>
/// </summary>
public sealed partial class UdpRealtimeServer : IUdpRealtimeHost, IAsyncDisposable
{
    private const int AckOnlyAfterMs = 50;
    private const long InFlightRetainMs = 1000;

    private readonly IPAddress _bindAddress;
    private readonly int _requestedPort;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<uint, Registration> _registrations = new();
    private readonly CancellationTokenSource _cts = new();
    private Socket? _socket;
    private Task _loop = Task.CompletedTask;
    private ITimer? _timer;
    private long _datagramsIn;
    private long _datagramsOut;
    private long _bytesIn;
    private long _bytesOut;
    private long _malformed;
    private long _unknownConnection;
    private long _unboundEndpoint;
    private long _badHello;
    private long _duplicates;
    private long _disallowed;
    private long _inboundOverflow;
    private long _binds;
    private long _rebinds;
    private long _sendErrors;
    private int _disposed;

    /// <param name="bindAddress">Local address (the node TCP endpoint's address).</param>
    /// <param name="port">UDP port; 0 picks a free one (tests).</param>
    public UdpRealtimeServer(IPAddress bindAddress, int port, TimeProvider? time = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        _bindAddress = bindAddress;
        _requestedPort = port;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>The bound port; 0 before <see cref="Start"/> and when binding failed (the lane is then off).</summary>
    public int Port { get; private set; }

    public long DatagramsIn => Interlocked.Read(ref _datagramsIn);

    public long DatagramsOut => Interlocked.Read(ref _datagramsOut);

    public long BytesIn => Interlocked.Read(ref _bytesIn);

    public long BytesOut => Interlocked.Read(ref _bytesOut);

    /// <summary>Datagrams that were no valid datagram (header or sub-message structure).</summary>
    public long Malformed => Interlocked.Read(ref _malformed);

    /// <summary>Datagrams with a <c>conn_id</c> nobody registered.</summary>
    public long UnknownConnection => Interlocked.Read(ref _unknownConnection);

    /// <summary>Datagrams from an endpoint the connection is not bound to (and that carried no valid hello).</summary>
    public long UnboundEndpoint => Interlocked.Read(ref _unboundEndpoint);

    /// <summary><c>UdpHello</c> with a wrong token, a mismatching conn_id or an unreadable payload.</summary>
    public long BadHello => Interlocked.Read(ref _badHello);

    /// <summary>Datagrams refused by the sequence window.</summary>
    public long Duplicates => Interlocked.Read(ref _duplicates);

    /// <summary>Sub-messages of a type that may not travel over UDP.</summary>
    public long Disallowed => Interlocked.Read(ref _disallowed);

    /// <summary>Frames dropped because the connection's inbound backlog was full.</summary>
    public long InboundOverflow => Interlocked.Read(ref _inboundOverflow);

    /// <summary>First-time bindings.</summary>
    public long Binds => Interlocked.Read(ref _binds);

    /// <summary>Bindings moved to a new source endpoint.</summary>
    public long Rebinds => Interlocked.Read(ref _rebinds);

    public long SendErrors => Interlocked.Read(ref _sendErrors);

    /// <summary>Registered connections (bound or not).</summary>
    public int RegisteredConnections => _registrations.Count;

    /// <summary>Registered connections that are bound to an endpoint now.</summary>
    public int BoundConnections => _registrations.Values.Count(r => r.Peer is not null);

    /// <summary>Binds the socket and starts receiving. False (and the lane stays off) when the port cannot be bound.</summary>
    public bool Start()
    {
        if (_socket is not null)
        {
            return true;
        }

        try
        {
            var socket = new Socket(_bindAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.ReceiveBufferSize = 1 << 20;
                socket.SendBufferSize = 1 << 20;
                socket.Blocking = false;
                if (OperatingSystem.IsWindows())
                {
                    socket.IOControl((IOControlCode)(-1744830452), [0, 0, 0, 0], null); // SIO_UDP_CONNRESET
                }
            }
            catch (Exception ex) when (ex is SocketException or NotSupportedException or PlatformNotSupportedException)
            {
                // tuning is best effort
            }

            socket.Bind(new IPEndPoint(_bindAddress, _requestedPort));
            _socket = socket;
            Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        }
        catch (SocketException ex)
        {
            LogBindFailed(_bindAddress.ToString(), _requestedPort, ex.Message);
            return false;
        }

        _loop = Task.Run(ReceiveLoopAsync, CancellationToken.None);
        _timer = _time.CreateTimer(static state => ((UdpRealtimeServer)state!).OnTimer(), this, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25));
        return true;
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "UDP realtime lane off: could not bind {Address}:{Port} ({Reason})")]
    private partial void LogBindFailed(string address, int port, string reason);

    // ------------------------------------------------------------------ registry (IUdpRealtimeHost)

    public void Register(INodeConnection connection, uint connId, ulong token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var registration = new Registration(connection, connId, token);
        if (_registrations.TryGetValue(connId, out var previous) && !ReferenceEquals(previous.Connection, connection))
        {
            Unregister(previous.Connection);
        }

        _registrations[connId] = registration;
    }

    public void Unregister(INodeConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        foreach (var (id, registration) in _registrations)
        {
            if (!ReferenceEquals(registration.Connection, connection))
            {
                continue;
            }

            _registrations.TryRemove(new KeyValuePair<uint, Registration>(id, registration));
            UdpPeer? peer;
            lock (registration)
            {
                peer = registration.Peer;
                registration.Peer = null;
            }

            if (peer is not null)
            {
                connection.DetachDatagramPath(peer);
                peer.Dispose();
            }
        }
    }

    // ------------------------------------------------------------------ receive

    private async Task ReceiveLoopAsync()
    {
        var socket = _socket!;
        var buffer = new byte[2048];
        var from = new SocketAddress(socket.AddressFamily);
        var subs = new List<(MsgType Type, int Offset, int Length)>(8);
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await socket.ReceiveFromAsync(buffer.AsMemory(), SocketFlags.None, from, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(5, CancellationToken.None).ConfigureAwait(false); // e.g. a transient ICMP error: keep serving
                continue;
            }

            Interlocked.Increment(ref _datagramsIn);
            Interlocked.Add(ref _bytesIn, n);
            try
            {
                Handle(buffer.AsSpan(0, n), from, subs);
            }
            catch (Exception ex) when (ex is ProtocolViolation or ArgumentException or InvalidOperationException)
            {
                Interlocked.Increment(ref _malformed);
            }
        }
    }

    private void Handle(ReadOnlySpan<byte> datagram, SocketAddress from, List<(MsgType Type, int Offset, int Length)> subs)
    {
        var header = DatagramCodec.ReadHeader(datagram);
        if (!_registrations.TryGetValue(header.ConnId, out var registration))
        {
            Interlocked.Increment(ref _unknownConnection);
            return;
        }

        subs.Clear();
        DatagramCodec.ReadSubMessages(datagram, subs);

        var peer = registration.Peer;
        bool bound = peer is not null && peer.IsEndpoint(from);
        bool helloAnswered = false;
        foreach (var (type, offset, length) in subs)
        {
            if (type != MsgType.UdpHello)
            {
                continue;
            }

            if (!IsValidHello(datagram.Slice(offset, length), registration))
            {
                Interlocked.Increment(ref _badHello);
                continue;
            }

            peer = Bind(registration, from);
            bound = true;
            if (!helloAnswered)
            {
                helloAnswered = true;
                peer.SendHelloAck(registration.HelloAck);
            }
        }

        if (!bound || peer is null)
        {
            Interlocked.Increment(ref _unboundEndpoint);
            return;
        }

        peer.ProcessAcks(header);
        if (!peer.AcceptSequence(header.Seq))
        {
            Interlocked.Increment(ref _duplicates);
            return;
        }

        bool carriesData = false;
        foreach (var (type, offset, length) in subs)
        {
            if (type is MsgType.UdpHello or MsgType.UdpHelloAck)
            {
                continue;
            }

            if (!DatagramCodec.IsAllowedOnUdp(type))
            {
                Interlocked.Increment(ref _disallowed);
                continue;
            }

            carriesData = true;
            var frame = new Frame(type, FrameOptions.None, MessageRegistry.Default.GetDescriptor(type).Lane, datagram.Slice(offset, length).ToArray());
            if (registration.Connection is PipeNodeConnection pipe && !pipe.EnqueueDatagramFrame(new InboundFrame(frame, _time.GetTimestamp())))
            {
                Interlocked.Increment(ref _inboundOverflow);
            }
        }

        if (carriesData)
        {
            peer.MarkAckPending();
        }
    }

    private static bool IsValidHello(ReadOnlySpan<byte> payload, Registration registration)
    {
        try
        {
            var frame = new Frame(MsgType.UdpHello, FrameOptions.None, Lane.Realtime, payload.ToArray());
            var hello = MessageRegistry.Default.Decode<UdpHello>(frame);
            Span<byte> expected = stackalloc byte[8];
            Span<byte> actual = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(expected, registration.Token);
            BinaryPrimitives.WriteUInt64LittleEndian(actual, hello.UdpToken);
            return hello.ConnId == registration.ConnId && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (ProtocolViolation)
        {
            return false;
        }
    }

    private UdpPeer Bind(Registration registration, SocketAddress from)
    {
        UdpPeer peer;
        bool created = false;
        bool moved = false;
        lock (registration)
        {
            if (registration.Peer is null)
            {
                registration.Peer = peer = new UdpPeer(this, registration.ConnId, Clone(from), _time);
                created = true;
            }
            else
            {
                peer = registration.Peer;
                if (!peer.IsEndpoint(from))
                {
                    peer.Rebind(Clone(from));
                    moved = true;
                }
            }
        }

        if (created)
        {
            Interlocked.Increment(ref _binds);
            registration.Connection.AttachDatagramPath(peer);
        }
        else if (moved)
        {
            Interlocked.Increment(ref _rebinds);
        }

        return peer;
    }

    private static SocketAddress Clone(SocketAddress source)
    {
        var copy = new SocketAddress(source.Family, source.Size);
        source.Buffer.Span.CopyTo(copy.Buffer.Span);
        return copy;
    }

    internal bool SendTo(ReadOnlySpan<byte> data, SocketAddress to)
    {
        var socket = _socket;
        if (socket is null)
        {
            return false;
        }

        try
        {
            socket.SendTo(data, SocketFlags.None, to);
            Interlocked.Increment(ref _datagramsOut);
            Interlocked.Add(ref _bytesOut, data.Length);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            Interlocked.Increment(ref _sendErrors);
            return false;
        }
    }

    private void OnTimer()
    {
        foreach (var registration in _registrations.Values)
        {
            registration.Peer?.OnTimer();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        _socket?.Dispose();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // shutting down
        }

        foreach (var registration in _registrations.Values)
        {
            Unregister(registration.Connection);
        }

        _cts.Dispose();
    }

    private sealed class Registration
    {
        public Registration(INodeConnection connection, uint connId, ulong token)
        {
            Connection = connection;
            ConnId = connId;
            Token = token;
            HelloAck = MessageEncoder.EncodePayload(b => UdpHelloAck.Pack(b, new UdpHelloAckT { ConnId = connId }), 16);
        }

        public INodeConnection Connection { get; }

        public uint ConnId { get; }

        public ulong Token { get; }

        public byte[] HelloAck { get; }

        public UdpPeer? Peer { get; set; }
    }

    /// <summary>One bound node as a datagram path: sends Realtime frames as datagrams, tracks what the node acknowledged.</summary>
    internal sealed class UdpPeer : IDatagramPath, IDisposable
    {
        private const int SlotCount = 256;

        private struct Slot
        {
            public uint Seq;
            public OutboundFrame? Frame;
            public long SentTs;
        }

        private readonly UdpRealtimeServer _server;
        private readonly uint _connId;
        private readonly TimeProvider _time;
        private readonly object _gate = new();
        private readonly Slot[] _slots = new Slot[SlotCount];
        private readonly byte[] _tx = new byte[DatagramCodec.MaxDatagramBytes];
        private readonly OutboundFrame?[] _ackScratch = new OutboundFrame?[33]; // receive thread only
        private readonly DatagramReceiveWindow _rx = new();
        private SocketAddress _endpoint;
        private uint _seq;
        private long _lastSendTs;
        private long _lastSweepTs;
        private bool _ackPending;
        private bool _disposed;
        private volatile Action<OutboundFrame>? _observer;

        public UdpPeer(UdpRealtimeServer server, uint connId, SocketAddress endpoint, TimeProvider time)
        {
            _server = server;
            _connId = connId;
            _endpoint = endpoint;
            _time = time;
            _lastSendTs = time.GetTimestamp();
            _lastSweepTs = _lastSendTs;
        }

        public void SetDeliveryObserver(Action<OutboundFrame>? observer) => _observer = observer;

        public bool IsEndpoint(SocketAddress other)
        {
            lock (_gate)
            {
                return _endpoint.Equals(other);
            }
        }

        public void Rebind(SocketAddress endpoint)
        {
            lock (_gate)
            {
                _endpoint = endpoint;
                _rx.Reset(); // the node may restart its numbering from a new socket
                _ackPending = false;
            }
        }

        public bool AcceptSequence(uint seq)
        {
            lock (_gate)
            {
                return _rx.Accept(seq);
            }
        }

        public void MarkAckPending()
        {
            lock (_gate)
            {
                _ackPending = true;
            }
        }

        /// <summary>Sends the frame as one datagram. False (use TCP) when the type may not travel over UDP, it does not fit, or the socket refused it.</summary>
        public bool TrySend(OutboundFrame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            if (!DatagramCodec.IsAllowedOnUdp(frame.MessageType))
            {
                return false;
            }

            var payload = frame.Bytes.Span[FrameCodec.HeaderSize..];
            int size = DatagramCodec.SubMessageSize(payload.Length);
            if (payload.Length == 0 || payload.Length > ushort.MaxValue || DatagramCodec.HeaderSize + size > DatagramCodec.MaxDatagramBytes)
            {
                return false;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                uint seq = ++_seq;
                int length = WriteHeader(seq);
                length = WriteSubMessage(length, frame.MessageType, payload);
                if (!_server.SendTo(_tx.AsSpan(0, length), _endpoint))
                {
                    return false;
                }

                _lastSendTs = _time.GetTimestamp();
                ref var slot = ref _slots[seq % SlotCount];
                slot.Frame?.Release(); // an unacknowledged datagram that fell out of the window: counts as lost
                slot.Seq = seq;
                slot.SentTs = _lastSendTs;
                slot.Frame = frame.DeliveryToken != 0 ? frame.AddRef() : null;
                return true;
            }
        }

        public void SendHelloAck(byte[] payload)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                int length = WriteHeader(++_seq);
                length = WriteSubMessage(length, MsgType.UdpHelloAck, payload);
                if (_server.SendTo(_tx.AsSpan(0, length), _endpoint))
                {
                    _lastSendTs = _time.GetTimestamp();
                }
            }
        }

        /// <summary>Calls the delivery observer for every outbound frame the header of an inbound datagram acknowledges (once each).</summary>
        public void ProcessAcks(in DatagramHeader header)
        {
            int count = 0;
            lock (_gate)
            {
                if (_disposed || unchecked(_seq - header.Ack) > int.MaxValue)
                {
                    return; // an ack for a datagram we never sent
                }

                for (int k = 0; k <= 32; k++)
                {
                    if (k > 0 && (header.AckBits & (1u << (k - 1))) == 0)
                    {
                        continue;
                    }

                    uint seq = unchecked(header.Ack - (uint)k);
                    ref var slot = ref _slots[seq % SlotCount];
                    if (slot.Frame is not null && slot.Seq == seq)
                    {
                        _ackScratch[count++] = slot.Frame;
                        slot.Frame = null;
                    }
                }
            }

            var observer = _observer;
            for (int i = 0; i < count; i++)
            {
                var frame = _ackScratch[i]!;
                _ackScratch[i] = null;
                try
                {
                    observer?.Invoke(frame);
                }
                finally
                {
                    frame.Release();
                }
            }
        }

        /// <summary>Ack-only datagram after 50 ms of silence, and release of the frames of datagrams unacknowledged for a second.</summary>
        public void OnTimer()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                long now = _time.GetTimestamp();
                if (_ackPending && _time.GetElapsedTime(_lastSendTs, now).TotalMilliseconds >= AckOnlyAfterMs)
                {
                    int length = WriteHeader(++_seq);
                    if (_server.SendTo(_tx.AsSpan(0, length), _endpoint))
                    {
                        _lastSendTs = now;
                    }
                }

                if (_time.GetElapsedTime(_lastSweepTs, now).TotalMilliseconds >= 250)
                {
                    _lastSweepTs = now;
                    for (int i = 0; i < _slots.Length; i++)
                    {
                        ref var slot = ref _slots[i];
                        if (slot.Frame is not null && _time.GetElapsedTime(slot.SentTs, now).TotalMilliseconds > InFlightRetainMs)
                        {
                            slot.Frame.Release(); // lost: replication retires the entry by its own timeout
                            slot.Frame = null;
                        }
                    }
                }
            }
        }

        // Caller holds _gate. Writes the header (with the current ack state) and clears the pending ack.
        private int WriteHeader(uint seq)
        {
            DatagramCodec.WriteHeader(_tx, new DatagramHeader((byte)ProtocolConstants.ProtocolMajor, _connId, seq, _rx.Ack, _rx.AckBits));
            _ackPending = false;
            return DatagramCodec.HeaderSize;
        }

        private int WriteSubMessage(int at, MsgType type, ReadOnlySpan<byte> payload)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_tx.AsSpan(at), (ushort)type);
            BinaryPrimitives.WriteUInt16LittleEndian(_tx.AsSpan(at + 2), (ushort)payload.Length);
            payload.CopyTo(_tx.AsSpan(at + DatagramCodec.SubMessageHeaderSize));
            int size = DatagramCodec.SubMessageSize(payload.Length);
            int padStart = at + DatagramCodec.SubMessageHeaderSize + payload.Length;
            _tx.AsSpan(padStart, at + size - padStart).Clear();
            return at + size;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                for (int i = 0; i < _slots.Length; i++)
                {
                    _slots[i].Frame?.Release();
                    _slots[i].Frame = null;
                }
            }
        }
    }
}
