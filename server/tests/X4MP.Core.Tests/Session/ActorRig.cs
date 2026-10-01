using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using Google.FlatBuffers;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Events;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Tests.Session;

/// <summary>A frame the server queued for a node, decoded lazily.</summary>
public sealed record SentFrame(MsgType Type, byte[] Payload)
{
    public T Decode<T>() where T : struct, IFlatbufferObject =>
        MessageRegistry.Default.Decode<T>(new Frame(Type, FrameOptions.None, Lane.Control, Payload));
}

/// <summary>An in-memory node connection the test drives by hand.</summary>
public sealed class FakeConnection(TimeProvider time) : INodeConnection
{
    private readonly Channel<InboundFrame> _inbound = Channel.CreateUnbounded<InboundFrame>();
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<SentFrame> _sent = [];
    private readonly object _gate = new();

    public ConnectionId Id { get; } = ConnectionId.Next();

    public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Parse("10.0.0.7"), 50000);

    public ConnectionStats Stats { get; } = new();

    public CancellationToken Closed => _closed.Token;

    public Task Completion => _completion.Task;

    public int MaxInboundFrameBytes { get; set; } = 1 << 20;

    public bool CanAcceptRealtime => true;

    public bool ControlOverSoftCap => false;

    public DisconnectCode? CloseCode { get; private set; }

    public string? CloseDetail { get; private set; }

    public bool IsClosed => CloseCode is not null;

    public IReadOnlyList<SentFrame> Sent
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    public List<SentFrame> SentOf(MsgType type) => [.. Sent.Where(f => f.Type == type)];

    public async ValueTask<InboundFrame?> ReadAsync(CancellationToken ct)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public SendResult TrySend(OutboundFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (IsClosed)
        {
            return SendResult.Closed;
        }

        lock (_gate)
        {
            _sent.Add(new SentFrame(frame.MessageType, frame.Bytes[FrameCodec.HeaderSize..].ToArray()));
        }

        return SendResult.Queued;
    }

    public long QueuedBytes(X4MP.Protocol.Lane lane) => 0;

    public void AttachDatagramPath(IDatagramPath path)
    {
    }

    public void Close(DisconnectCode reason, string? detail = null, string? expected = null, uint retryAfterMs = 0)
    {
        if (CloseCode is not null)
        {
            return;
        }

        CloseCode = reason;
        CloseDetail = detail;
        _closed.Cancel();
        _inbound.Writer.TryComplete();
        _completion.TrySetResult();
    }

    /// <summary>The peer's socket vanished (no Disconnect frame).</summary>
    public void Drop()
    {
        _inbound.Writer.TryComplete();
        _completion.TrySetResult();
    }

    /// <summary>The node sends a frame; the receive timestamp is the fake clock now.</summary>
    public void Push(MsgType type, FlatBufferBuilder finished)
    {
        var payload = finished.SizedByteArray();
        var lane = MessageRegistry.Default.GetDescriptor(type).Lane;
        _inbound.Writer.TryWrite(new InboundFrame(new Frame(type, FrameOptions.None, lane, payload), time.GetTimestamp()));
    }

    /// <summary>The node sends an already encoded payload (built with a <c>MessageEncoder</c> or a kit).</summary>
    public void PushPayload(MsgType type, byte[] payload)
    {
        var lane = MessageRegistry.Default.GetDescriptor(type).Lane;
        _inbound.Writer.TryWrite(new InboundFrame(new Frame(type, FrameOptions.None, lane, payload), time.GetTimestamp()));
    }

    /// <summary>Frames pushed and not yet read by the actor's reader loop.</summary>
    public int PendingInbound => _inbound.Reader.Count;

    public ValueTask DisposeAsync()
    {
        Close(DisconnectCode.ClientQuit);
        return ValueTask.CompletedTask;
    }
}

public sealed class RecordingStore : ISessionStore
{
    private long _next;
    private readonly object _gate = new();

    public List<(long Session, string Name)> Sessions { get; } = [];

    public List<(long Session, SessionPhase Phase, int Authority, string? Reason)> Phases { get; } = [];

    public List<(long Session, int Player, Role Roles)> Joins { get; } = [];

    public List<(long Session, int Player, string Reason)> Leaves { get; } = [];

    public ValueTask<long> BeginSessionAsync(string name, Guid sessionGuid, DateTimeOffset at, CancellationToken ct)
    {
        lock (_gate)
        {
            long id = ++_next;
            Sessions.Add((id, name));
            return ValueTask.FromResult(id);
        }
    }

    public void RecordPhase(long sessionId, SessionPhase phase, int authorityPlayerId, DateTimeOffset at, string? reason)
    {
        lock (_gate)
        {
            Phases.Add((sessionId, phase, authorityPlayerId, reason));
        }
    }

    public void PlayerJoined(long sessionId, int playerId, Role roles, DateTimeOffset at)
    {
        lock (_gate)
        {
            Joins.Add((sessionId, playerId, roles));
        }
    }

    public void PlayerLeft(long sessionId, int playerId, DateTimeOffset at, string reason)
    {
        lock (_gate)
        {
            Leaves.Add((sessionId, playerId, reason));
        }
    }
}

public sealed class RecordingEvents : IEventPublisher
{
    private readonly object _gate = new();
    private readonly List<DomainEvent> _events = [];

    public IReadOnlyList<DomainEvent> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public IReadOnlyList<T> OfType<T>() where T : DomainEvent => [.. All.OfType<T>()];

    public void Publish(DomainEvent domainEvent)
    {
        lock (_gate)
        {
            _events.Add(domainEvent);
        }
    }
}

/// <summary>Records every callback in order, on the actor thread.</summary>
public sealed class RecordingModule : ISessionModule
{
    public List<string> Log { get; } = [];

    public List<(int Player, bool Resumed, int Epoch)> Attached { get; } = [];

    public List<InboundFrame> Messages { get; } = [];

    public ManualResetEventSlim? TickGate { get; set; }

    public bool ConsumeMessages { get; set; }

    public void OnNodeAttached(SessionNode node, bool resumed)
    {
        Attached.Add((node.PlayerId, resumed, node.BaselineEpoch));
        Log.Add($"attached:{node.PlayerId}:{resumed}");
    }

    public void OnNodePhaseChanged(SessionNode node, NodePhase previous, NodePhase current) =>
        Log.Add($"node:{node.PlayerId}:{previous}->{current}");

    public void OnNodeDetached(SessionNode node, DetachReason reason) => Log.Add($"detached:{node.PlayerId}:{reason}");

    public void OnNodeLeft(SessionNode node, string reason) => Log.Add($"left:{node.PlayerId}:{reason}");

    public void OnSessionPhaseChanged(SessionPhase previous, SessionPhase current) => Log.Add($"session:{previous}->{current}");

    public bool OnMessage(SessionNode node, InboundFrame frame)
    {
        Messages.Add(frame);
        return ConsumeMessages;
    }

    public ManualResetEventSlim? TickEntered { get; set; }

    public void OnTick(long timestamp)
    {
        TickEntered?.Set();
        TickGate?.Wait(TimeSpan.FromSeconds(30));
    }
}

/// <summary>A node that joined through the actor.</summary>
public sealed class JoinedNode
{
    public required AdmissionVerdict Verdict { get; init; }

    public required AdmittedNode Node { get; init; }

    public required FakeConnection Connection { get; init; }

    public Task? ReadLoop { get; init; }

    public bool Accepted => Verdict.Accepted;

    public int PlayerId => Node.PlayerId;

    public WelcomeT Welcome => Node.Welcome;
}

/// <summary>The actor wired to a fake clock, fake connections and recording collaborators.</summary>
public sealed class ActorRig : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, (int Id, byte[] Key)> _identities = [];
    private readonly Task _loop;

    public ActorRig(SessionActorOptions? options = null, NetOptions? net = null, params ISessionModule[] modules)
        : this(null, options, net, modules)
    {
    }

    /// <param name="time">The fake clock, when a module needs the same one (otherwise the rig makes its own).</param>
    public ActorRig(FakeTimeProvider? time, SessionActorOptions? options, NetOptions? net, ISessionModule[] modules)
    {
        Net = net ?? new NetOptions();
        Options = options ?? new SessionActorOptions();
        // Long heartbeat by default so tests can jump the clock without keeping nodes alive; the heartbeat test overrides it.
        if (options is null)
        {
            Options.HeartbeatTimeoutMs = 24 * 3600 * 1000;
        }

        Time = time ?? new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        Gateway = GatewayState.FromOptions(Net);
        Store = new RecordingStore();
        Events = new RecordingEvents();
        Actor = new SessionActor(Options, Net, Gateway, Time, Store, Events, modules);
        _loop = Actor.RunAsync(_stop.Token);
    }

    public FakeTimeProvider Time { get; }

    public GatewayState Gateway { get; }

    public NetOptions Net { get; }

    public SessionActorOptions Options { get; }

    public RecordingStore Store { get; }

    public RecordingEvents Events { get; }

    public SessionActor Actor { get; }

    /// <summary>Advances the clock and waits until the actor has processed the resulting tick.</summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        Time.Advance(by);
        await Actor.FlushAsync();
    }

    public Task AdvanceSecondsAsync(double seconds) => AdvanceAsync(TimeSpan.FromSeconds(seconds));

    public Task<SessionSnapshot> SnapshotAsync() => Actor.GetSnapshotAsync();

    public async Task<NodeSnapshot> NodeAsync(int playerId) => (await SnapshotAsync()).Nodes.Single(n => n.PlayerId == playerId);

    /// <summary>
    /// Runs the gateway's part of a join (a <c>ClientHello</c> already checked, a <c>Welcome</c> drafted) and
    /// hands the node to the actor exactly as <see cref="NodeGateway"/> does.
    /// </summary>
    public async Task<JoinedNode> JoinAsync(
        string name, Role roles = Role.Client, Id128T? resumeToken = null, ulong lastJournalSeq = 0,
        string? modBuild = null)
    {
        if (!_identities.TryGetValue(name, out var identity))
        {
            identity = (_identities.Count + 1, RandomNumberGenerator.GetBytes(32));
            _identities[name] = identity;
        }

        var connection = new FakeConnection(Time);
        var reader = new NodeFrameReader(connection, Net, Time);
        Span<byte> random = stackalloc byte[16];
        RandomNumberGenerator.Fill(random);
        var hello = new ClientHelloT
        {
            ProtocolMajor = ProtocolConstants.ProtocolMajor,
            ProtocolMinor = ProtocolConstants.ProtocolMinor,
            ModVersion = "0.1.0",
            ModBuild = modBuild ?? "build",
            GameBuild = "900-611726",
            ExtensionsHash = [.. new byte[32]],
            Extensions = ["x4mp@0.1.0"],
            PlayerKey = [.. identity.Key],
            PlayerName = name,
            RequestedRoles = roles,
            ResumeToken = resumeToken ?? new Id128T(),
            LastJournalSeq = lastJournalSeq,
        };
        var node = new AdmittedNode
        {
            Connection = connection,
            Reader = reader,
            PlayerId = identity.Id,
            Name = name,
            KeyHash = SHA256.HashData(identity.Key),
            Roles = roles,
            Hello = hello,
            NegotiatedMinor = ProtocolConstants.ProtocolMinor,
            NegotiatedCaps = 0,
            Nonce = RandomNumberGenerator.GetBytes(32),
            RemoteAddress = IPAddress.Parse("10.0.0.7"),
            Welcome = new WelcomeT
            {
                PlayerId = (ushort)identity.Id,
                GrantedRoles = roles,
                ResumeToken = new Id128T { Lo = BitConverter.ToUInt64(random[..8]), Hi = BitConverter.ToUInt64(random[8..]) },
                ConnId = (uint)connection.Id.Value,
                ResumeGraceS = (ushort)Net.ResumeGraceSeconds,
            },
        };

        var verdict = await Actor.BeforeWelcomeAsync(node, CancellationToken.None);
        if (!verdict.Accepted)
        {
            connection.Close(verdict.Code, verdict.Message);
            return new JoinedNode { Verdict = verdict, Node = node, Connection = connection };
        }

        // What NodeGateway does between BeforeWelcome and OnAdmitted.
        reader.Roles = roles;
        node.Phase = NodePhase.Admitted;
        var readLoop = Actor.OnAdmittedAsync(node, _stop.Token);
        await Actor.FlushAsync();
        return new JoinedNode { Verdict = verdict, Node = node, Connection = connection, ReadLoop = readLoop };
    }

    public Task<JoinedNode> JoinAuthorityAsync(string name = "Boss") => JoinAsync(name, Role.Authority | Role.Client);

    /// <summary>Same player, new connection, presenting the previous Welcome's resume token.</summary>
    public Task<JoinedNode> ResumeAsync(string name, JoinedNode previous, Role? roles = null) =>
        JoinAsync(name, roles ?? previous.Node.Roles, previous.Welcome.ResumeToken);

    /// <summary>The node sends a frame; completes once the actor has processed it.</summary>
    public async Task SendAsync(JoinedNode node, MsgType type, FlatBufferBuilder finished)
    {
        long before = Actor.FramesReceived;
        node.Connection.Push(type, finished);
        var until = Environment.TickCount64 + 5000;
        while (Actor.FramesReceived == before)
        {
            Assert.True(Environment.TickCount64 < until, $"the actor never saw the {type} frame");
            await Task.Delay(1);
        }

        await Actor.FlushAsync();
    }

    public Task LoadStatusAsync(JoinedNode node, NodePhase phase) =>
        SendAsync(node, MsgType.LoadStatus, Frames.LoadStatus(phase));

    public Task ReadyAsync(JoinedNode node) => SendAsync(node, MsgType.NodeReady, Frames.NodeReady());

    public Task DisconnectAsync(JoinedNode node, DisconnectCode code) =>
        SendAsync(node, MsgType.Disconnect, Frames.Disconnect(code));

    /// <summary>Walks a client through the join pipeline to <see cref="NodePhase.InGame"/>.</summary>
    public async Task BringInGameAsync(JoinedNode node)
    {
        foreach (var phase in new[] { NodePhase.SyncingSave, NodePhase.Verifying, NodePhase.Loading, NodePhase.Matching, NodePhase.CatchingUp })
        {
            await LoadStatusAsync(node, phase);
        }

        await ReadyAsync(node);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }
}

/// <summary>Builders for the frames a node sends.</summary>
public static class Frames
{
    public static FlatBufferBuilder LoadStatus(NodePhase phase)
    {
        var fbb = new FlatBufferBuilder(64);
        fbb.Finish(X4MP.Proto.LoadStatus.CreateLoadStatus(fbb, phase, 0f, 0, default, DisconnectCode.None).Value);
        return fbb;
    }

    public static FlatBufferBuilder NodeReady()
    {
        var fbb = new FlatBufferBuilder(64);
        fbb.Finish(X4MP.Proto.NodeReady.CreateNodeReady(fbb, 42).Value);
        return fbb;
    }

    public static FlatBufferBuilder Disconnect(DisconnectCode code)
    {
        var fbb = new FlatBufferBuilder(64);
        fbb.Finish(X4MP.Proto.Disconnect.CreateDisconnect(fbb, code).Value);
        return fbb;
    }

    public static FlatBufferBuilder Ping(uint seq, ulong sendUs)
    {
        var fbb = new FlatBufferBuilder(64);
        fbb.Finish(X4MP.Proto.Ping.CreatePing(fbb, seq, sendUs).Value);
        return fbb;
    }

    public static FlatBufferBuilder Pong(uint seq, ulong echoSendUs, ulong recvUs, ulong replyUs)
    {
        var fbb = new FlatBufferBuilder(64);
        fbb.Finish(X4MP.Proto.Pong.CreatePong(fbb, seq, echoSendUs, recvUs, replyUs).Value);
        return fbb;
    }

    public static FlatBufferBuilder NodeStats(float fps, float rttMs, long clockOffsetUs, uint ghosts)
    {
        var fbb = new FlatBufferBuilder(128);
        var t = new NodeStatsT { Fps = fps, FrameMsP95 = 18.5f, Ghosts = ghosts, RttMs = rttMs, ClockOffsetUs = clockOffsetUs, MemoryMb = 2048, UdpActive = false };
        fbb.Finish(X4MP.Proto.NodeStats.Pack(fbb, t).Value);
        return fbb;
    }
}
