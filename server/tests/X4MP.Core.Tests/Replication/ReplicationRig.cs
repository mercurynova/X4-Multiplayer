using Google.FlatBuffers;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Interest;
using X4MP.Core.Net;
using X4MP.Core.Replication;
using X4MP.Core.Session;
using X4MP.Core.Settings;
using X4MP.Core.Tests.Session;
using X4MP.Core.World;
using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Protocol;
using static X4MP.Core.Tests.World.WorldKit;
using ReplicationMsg = X4MP.Proto.Replication;

namespace X4MP.Core.Tests.Replication;

/// <summary>
/// An in-memory network between the server modules and the fake clients: one in-order pipe per client in which Control frames always go
/// before Realtime frames (exactly what the writer loop does), with a hook to drop or refuse realtime frames and a switch for the delivery
/// confirmations (the writer's flush callback). It is the interest manager's and replication's transport at the same time.
/// </summary>
public sealed class SimNet : IInterestTransport, IReplicationTransport
{
    private sealed class Pipe
    {
        public List<(MsgType Type, byte[] Payload)> Control { get; } = [];

        public List<(byte[] Payload, long Token)> Realtime { get; } = [];

        public Action<OutboundFrame>? Observer { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, Pipe> _pipes = [];
    private readonly List<(Action<OutboundFrame> Observer, long Token)> _unconfirmed = [];

    /// <summary>Return anything but Queued to refuse a realtime frame (the lane dropped it).</summary>
    public Func<int, OutboundFrame, SendResult>? RealtimePolicy { get; set; }

    public bool CanAccept { get; set; } = true;

    /// <summary>True: the clients' Realtime lane is UDP (acks one by one, several frames in flight).</summary>
    public bool Datagram { get; set; }

    public bool UsesDatagram(int playerId) => Datagram;

    /// <summary>Return true to make a realtime frame vanish on the network: never delivered to the client, never acknowledged (by its delivery token).</summary>
    public Func<long, bool>? Lose { get; set; }

    /// <summary>Delivery tokens of the frames handed to the client whose confirmation is still withheld.</summary>
    public IReadOnlyList<long> UnconfirmedTokens
    {
        get
        {
            lock (_gate)
            {
                return [.. _unconfirmed.Select(u => u.Token)];
            }
        }
    }

    public bool OverSoftCap { get; set; }

    public bool AuthorityReady { get; set; } = true;

    public List<(MsgType Type, byte[] Payload)> ToAuthority { get; } = [];

    /// <summary>Every realtime frame that was accepted (client, payload), for decoding in tests.</summary>
    public List<(int Player, byte[] Payload)> RealtimeLog { get; } = [];

    public List<(int Player, MsgType Type, byte[] Payload)> ControlLog { get; } = [];

    private Pipe PipeOf(int playerId)
    {
        if (!_pipes.TryGetValue(playerId, out var pipe))
        {
            _pipes[playerId] = pipe = new Pipe();
        }

        return pipe;
    }

    public bool IsOverSoftCap(int playerId) => OverSoftCap;

    public SendResult Send(int playerId, OutboundFrame frame) => Control(playerId, frame);

    public SendResult SendControl(int playerId, OutboundFrame frame) => Control(playerId, frame);

    private SendResult Control(int playerId, OutboundFrame frame)
    {
        lock (_gate)
        {
            byte[] payload = frame.Bytes[FrameCodec.HeaderSize..].ToArray();
            PipeOf(playerId).Control.Add((frame.MessageType, payload));
            ControlLog.Add((playerId, frame.MessageType, payload));
        }

        return SendResult.Queued;
    }

    public SendResult SendToAuthority(OutboundFrame frame)
    {
        lock (_gate)
        {
            ToAuthority.Add((frame.MessageType, frame.Bytes[FrameCodec.HeaderSize..].ToArray()));
        }

        return SendResult.Queued;
    }

    public bool CanAcceptRealtime(int playerId) => CanAccept;

    public SendResult SendRealtime(int playerId, OutboundFrame frame)
    {
        lock (_gate)
        {
            var result = RealtimePolicy?.Invoke(playerId, frame) ?? SendResult.Queued;
            if (result is SendResult.Queued or SendResult.Coalesced)
            {
                byte[] payload = frame.Bytes[FrameCodec.HeaderSize..].ToArray();
                PipeOf(playerId).Realtime.Add((payload, frame.DeliveryToken));
                RealtimeLog.Add((playerId, payload));
            }

            return result;
        }
    }

    public void SetDeliveryObserver(int playerId, INodeConnection? connection, Action<OutboundFrame>? observer)
    {
        lock (_gate)
        {
            PipeOf(playerId).Observer = observer;
        }
    }

    /// <summary>
    /// The writer loop, one flush: hands every queued frame to <paramref name="deliver"/> in wire order (Control first), and afterwards,
    /// when <paramref name="confirm"/> is true, calls the delivery observer for each realtime frame (the flush callback).
    /// </summary>
    public void Pump(Action<int, Frame> deliver, bool confirm = true)
    {
        List<(int Player, Frame Frame)> frames = [];
        List<(Action<OutboundFrame> Observer, long Token)> confirmations = [];
        lock (_gate)
        {
            foreach (var (player, pipe) in _pipes)
            {
                foreach (var (type, payload) in pipe.Control)
                {
                    frames.Add((player, new Frame(type, FrameOptions.None, Lane.Control, payload)));
                }

                foreach (var (payload, token) in pipe.Realtime)
                {
                    if (Lose?.Invoke(token) == true)
                    {
                        continue;
                    }

                    frames.Add((player, new Frame(MsgType.Replication, FrameOptions.None, Lane.Realtime, payload)));
                    if (pipe.Observer is { } observer)
                    {
                        (confirm ? confirmations : _unconfirmed).Add((observer, token));
                    }
                }

                pipe.Control.Clear();
                pipe.Realtime.Clear();
            }
        }

        foreach (var (player, frame) in frames)
        {
            deliver(player, frame);
        }

        foreach (var (observer, token) in confirmations)
        {
            Confirm(observer, token);
        }
    }

    private static void Confirm(Action<OutboundFrame> observer, long token)
    {
        var dummy = OutboundFrame.Create(MsgType.Replication, Lane.Realtime, new byte[] { 1 });
        dummy.DeliveryToken = token;
        observer(dummy);
        dummy.Release();
    }

    /// <summary>Confirms only the withheld frames whose token matches (a partial ack); the others stay withheld.</summary>
    public void ConfirmWhere(Func<long, bool> which)
    {
        List<(Action<OutboundFrame> Observer, long Token)> batch;
        lock (_gate)
        {
            batch = [.. _unconfirmed.Where(u => which(u.Token))];
            _unconfirmed.RemoveAll(u => which(u.Token));
        }

        foreach (var (observer, token) in batch)
        {
            Confirm(observer, token);
        }
    }

    /// <summary>Delivers the flush callbacks of frames that were handed to the client while confirmations were off.</summary>
    public void ConfirmAll()
    {
        List<(Action<OutboundFrame> Observer, long Token)> batch;
        lock (_gate)
        {
            batch = [.. _unconfirmed];
            _unconfirmed.Clear();
        }

        foreach (var (observer, token) in batch)
        {
            Confirm(observer, token);
        }
    }

    /// <summary>Confirms the realtime frames still queued without delivering them (a flush callback with nobody reading).</summary>
    public int Realtimes(int playerId)
    {
        lock (_gate)
        {
            return PipeOf(playerId).Realtime.Count;
        }
    }
}

/// <summary>What a client would hold of one ghost, rebuilt by merging the spawn state and every entry (omitted fields keep their value).</summary>
public sealed class ClientGhostState
{
    public ushort Sector { get; set; }

    public int Px { get; set; }

    public int Py { get; set; }

    public int Pz { get; set; }

    public short Yaw { get; set; }

    public short Pitch { get; set; }

    public short Roll { get; set; }

    public short Vx { get; set; }

    public short Vy { get; set; }

    public short Vz { get; set; }

    public ushort Flags { get; set; }

    public byte Hull { get; set; }

    public byte Shield { get; set; }

    public int Entries { get; set; }

    public int FullEntries { get; set; }

    public List<double> FullEntryTimes { get; } = [];
}

/// <summary>A fake client the rig runs: a player ship and the receiving half (<see cref="FakeClientSession"/>).</summary>
public sealed class RigClient(JoinedNode node, FakeClientSession session, bool verify = true)
{
    public bool Verify { get; } = verify;

    public JoinedNode Node { get; } = node;

    public FakeClientSession Session { get; } = session;

    public int PlayerId => Node.PlayerId;

    public FakePlayer? Player { get; set; }

    public uint StateSeq { get; set; }

    /// <summary>Replication entries decoded per entity, for rate measurements (net_id to entry count).</summary>
    public Dictionary<uint, int> EntryCounts { get; } = [];

    /// <summary>The merged state of every ghost the client holds.</summary>
    public Dictionary<uint, ClientGhostState> States { get; } = [];

    public void ApplySpawn(EntitySpawn spawn)
    {
        for (int i = 0; i < spawn.EntitiesLength; i++)
        {
            if (spawn.Entities(i) is not { } r || FakeClientSession.IsPersistent(r.Kind))
            {
                continue;
            }

            var st = r.State!.Value;
            States[r.NetId] = new ClientGhostState
            {
                Sector = st.Sector, Px = st.Px, Py = st.Py, Pz = st.Pz, Yaw = st.Yaw, Pitch = st.Pitch, Roll = st.Roll,
                Vx = st.Vx, Vy = st.Vy, Vz = st.Vz, Flags = st.Flags, Hull = r.Hull, Shield = r.Shield,
            };
        }
    }

    public void ApplyDespawn(EntityDespawn despawn)
    {
        for (int i = 0; i < despawn.EntriesLength; i++)
        {
            States.Remove(despawn.Entries(i)!.Value.NetId);
        }
    }

    public void ApplyEntry(in ReplicationEntry e, double atSeconds)
    {
        if (!States.TryGetValue(e.NetId, out var st))
        {
            return;
        }

        var m = e.Mask;
        st.Entries++;
        if ((m & ReplicationMask.Sector) != 0) st.Sector = e.Sector;
        if ((m & ReplicationMask.Pos) != 0) { st.Px = e.PosX; st.Py = e.PosY; st.Pz = e.PosZ; }
        if ((m & ReplicationMask.Rot) != 0) { st.Yaw = e.Yaw; st.Pitch = e.Pitch; st.Roll = e.Roll; }
        if ((m & ReplicationMask.Vel) != 0) { st.Vx = e.VelX; st.Vy = e.VelY; st.Vz = e.VelZ; }
        if ((m & ReplicationMask.Flags) != 0) st.Flags = e.StateFlags;
        if ((m & ReplicationMask.Status) != 0) { st.Hull = e.Hull; st.Shield = e.Shield; }
        if ((m & ReplicationMathMask) == ReplicationMathMask)
        {
            st.FullEntries++;
            st.FullEntryTimes.Add(atSeconds);
        }
    }

    private const ReplicationMask ReplicationMathMask = X4MP.Core.Replication.ReplicationMath.FullMask;
}

/// <summary>
/// The whole server side of replication against FakeNode's deterministic galaxy, driven in virtual time: a real <see cref="SessionActor"/> with the
/// mirror, interest manager and replication module attached over fake connections, FakeNode's authority answering the CaptureSets,
/// and <see cref="FakeClientSession"/> clients verifying what arrives against ground truth.
/// </summary>
public sealed class ReplicationRig : IAsyncDisposable
{
    public const double StepSeconds = 0.05;

    private int _captureSetsDelivered;
    private long _tick;

    private ReplicationRig()
    {
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    public ReplicationOptions ReplicationOptions { get; } = new();

    public InterestOptions InterestOptions { get; } = new();

    public SimNet Net { get; } = new();

    public WorldMirror Mirror { get; private set; } = null!;

    public InterestManager Interest { get; private set; } = null!;

    public ReplicationModule Replication { get; private set; } = null!;

    public ActorRig Actor { get; private set; } = null!;

    public FakeGalaxy Galaxy { get; private set; } = null!;

    public FakeWorld World { get; private set; } = null!;

    public FakeAuthority Authority { get; private set; } = null!;

    public JoinedNode Boss { get; private set; } = null!;

    public List<RigClient> Clients { get; } = [];

    /// <summary>False for synthetic worlds: the test spawns entities in the mirror itself and completes sectors by hand.</summary>
    public bool UseFakeAuthority { get; private set; } = true;

    /// <summary>True: the fake authority stops streaming (the mirror becomes static, so convergence can be checked).</summary>
    public bool AuthorityPaused { get; set; }

    /// <summary>Debugging: net_ids whose spawns, despawns and entries are written to <see cref="TraceLog"/>.</summary>
    public HashSet<uint> TraceNetIds { get; } = [];

    public List<string> TraceLog { get; } = [];

    public long Tick => _tick;

    public double GameTime => _tick * StepSeconds;

    public static async Task<ReplicationRig> CreateAsync(
        Action<ReplicationOptions>? replication = null,
        Action<InterestOptions>? interest = null,
        FakeGalaxy? galaxy = null,
        bool fakeAuthority = true,
        int lineSectors = 6,
        IReplicationTransport? replicationTransport = null,
        bool replicationTimer = true)
    {
        var rig = new ReplicationRig { UseFakeAuthority = fakeAuthority };
        replication?.Invoke(rig.ReplicationOptions);
        interest?.Invoke(rig.InterestOptions);
        rig.Galaxy = galaxy ?? FakeGalaxy.Generate(42);
        rig.World = new FakeWorld(rig.Galaxy);
        rig.Authority = new FakeAuthority(new FakeWorld(rig.Galaxy), new FakeAuthorityOptions());
        rig.Mirror = new WorldMirror(rig.Time, initialCapacity: 16384);
        rig.Interest = new InterestManager(rig.Mirror, () => rig.InterestOptions, rig.Time, rig.Net);
        rig.Replication = new ReplicationModule(
            rig.Mirror, rig.Interest, () => rig.ReplicationOptions, () => rig.InterestOptions, rig.Time, replicationTransport ?? rig.Net)
        {
            TimerEnabled = replicationTimer,
        };
        rig.Actor = new ActorRig(rig.Time, null, null, [rig.Mirror, rig.Interest, rig.Replication]);
        rig.Boss = await rig.Actor.JoinAuthorityAsync();
        await rig.Actor.BringInGameAsync(rig.Boss);
        await rig.OnActorAsync(() =>
        {
            if (fakeAuthority)
            {
                foreach (var message in rig.Authority.StartupMessages())
                {
                    rig.Feed(message);
                }
            }
            else
            {
                var sha = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();
                rig.Mirror.HandleForTest(new InboundFrame(AsFrame(MsgType.GalaxyMetadata, LineGalaxy(sha, lineSectors)), 0), authority: true, 99);
            }
        });

        return rig;
    }

    public SessionActor ActorLoop => Actor.Actor;

    /// <summary>Runs <paramref name="work"/> on the actor thread (the modules are not thread-safe).</summary>
    public Task OnActorAsync(Action work) => Actor.Actor.CallAsync(() =>
    {
        work();
        return true;
    });

    public Task<T> OnActorAsync<T>(Func<T> work) => Actor.Actor.CallAsync(work);

    /// <summary>Joins a client through the actor and brings it in game; its player ship is placed with <see cref="Place"/>.</summary>
    public async Task<RigClient> AddClientAsync(string name, bool verify = true)
    {
        var node = await Actor.JoinAsync(name);
        await Actor.BringInGameAsync(node);
        var client = new RigClient(node, new FakeClientSession(new FakeWorld(Galaxy), verify, () => Time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0), verify);
        Clients.Add(client);
        return client;
    }

    /// <summary>The authority's message reaches the server the way the actor would hand it over (on the actor thread).</summary>
    private void Feed(OutMessage message)
    {
        var frame = new InboundFrame(AsFrame(message.Type, message.Payload), 0);
        if (message.Type == MsgType.SectorComplete)
        {
            Interest.HandleSectorComplete(Decode<SectorComplete>(MsgType.SectorComplete, message.Payload));
        }
        else
        {
            Mirror.HandleFrame(frame, 99, senderIsAuthority: true);
        }
    }

    /// <summary>Synthetic worlds: the authority completes every sector of the last capture set.</summary>
    public Task CompleteCapturedAsync() => OnActorAsync(() =>
    {
        foreach (var rate in Interest.LastCaptureSectors.ToArray())
        {
            Interest.HandleSectorComplete(Decode<SectorComplete>(
                MsgType.SectorComplete,
                MessageEncoder.EncodePayload(b => SectorComplete.Pack(b, new SectorCompleteT { Sector = rate.Sector, Epoch = Interest.CaptureEpoch }), 32)));
        }
    });

    /// <summary>The client's socket drops and it resumes with its token: same player, new connection, new baseline epoch, a fresh fake client.</summary>
    public async Task<RigClient> ResumeClientAsync(RigClient client, bool dropFirst = true)
    {
        if (dropFirst)
        {
            client.Node.Connection.Drop();
            var until = Environment.TickCount64 + 2000;
            while (await OnActorAsync(() => Interest.IsActive(client.PlayerId)))
            {
                Assert.True(Environment.TickCount64 < until, "the node never detached");
                await Task.Delay(5);
            }
        }

        var back = await Actor.ResumeAsync(client.Node.Node.Name, client.Node);
        await Actor.Actor.FlushAsync();
        var fresh = new RigClient(back, new FakeClientSession(new FakeWorld(Galaxy), client.Verify, () => Time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0), client.Verify) { Player = client.Player, StateSeq = client.StateSeq };
        Clients[Clients.IndexOf(client)] = fresh;
        return fresh;
    }

    public Task PlaceAsync(RigClient client, ushort sector, int px = 0, int py = 0, int pz = 0) =>
        OnActorAsync(() => Mirror.ApplyPlayerState(client.PlayerId, Decode<PlayerState>(
            MsgType.PlayerState, PlayerStatePayload(++client.StateSeq, 0, sector, px, py, pz))));

    /// <summary>
    /// One 50 ms step in virtual time: the clock moves (the actor's and the replication timer fire), the authority answers new capture sets and
    /// streams, fake players report their position, and the network delivers what was queued to the fake clients.
    /// </summary>
    public async Task StepAsync(bool confirm = true)
    {
        _tick++;
        await OnActorAsync(() =>
        {
            foreach (var client in Clients)
            {
                if (client.Player is { } player)
                {
                    var s = player.Step(_tick);
                    Mirror.ApplyPlayerState(client.PlayerId, Decode<PlayerState>(
                        MsgType.PlayerState, MessageEncoder.EncodePayload(b => PlayerState.Pack(b, s), 128)));
                }
            }
        });

        Time.Advance(TimeSpan.FromSeconds(StepSeconds));
        await Actor.Actor.FlushAsync();

        await OnActorAsync(() =>
        {
            while (_captureSetsDelivered < Net.ToAuthority.Count)
            {
                var sent = Net.ToAuthority[_captureSetsDelivered++];
                if (sent.Type == MsgType.CaptureSet && UseFakeAuthority)
                {
                    Authority.OnCaptureSet(Decode<CaptureSet>(MsgType.CaptureSet, sent.Payload).UnPack(), _tick);
                }
            }

            if (UseFakeAuthority && !AuthorityPaused)
            {
                foreach (var message in Authority.Tick(_tick))
                {
                    Feed(message);
                }
            }
        });

        await PumpAsync(confirm);
    }

    /// <summary>Delivers everything queued to the fake clients (and confirms the realtime frames, like the writer's flush); their answers go back through the actor.</summary>
    public async Task PumpAsync(bool confirm = true)
    {
        List<(RigClient Client, OutMessage Message)> replies = [];
        await OnActorAsync(() => Net.Pump(
            (player, frame) =>
            {
                var client = Clients.FirstOrDefault(c => c.PlayerId == player);
                if (client is null)
                {
                    return;
                }

                if (frame.Type == MsgType.Replication)
                {
                    var message = MessageRegistry.Default.Decode<ReplicationMsg>(frame);
                    foreach (var entry in ReplicationCodec.Decode(message.GetEntriesArray(), message.EntryCount))
                    {
                        if (TraceNetIds.Contains(entry.NetId))
                        {
                            TraceLog.Add($"t{_tick} p{player} entry {entry.NetId} mask {entry.Mask} sector {entry.Sector} pos {entry.PosX} time {entry.TimeMs} tick {message.ServerTick}");
                        }

                        client.EntryCounts[entry.NetId] = client.EntryCounts.GetValueOrDefault(entry.NetId) + 1;
                        client.ApplyEntry(entry, Time.GetTimestamp() / (double)Time.TimestampFrequency);
                    }
                }
                else if (frame.Type == MsgType.EntitySpawn)
                {
                    var spawn = MessageRegistry.Default.Decode<EntitySpawn>(frame);
                    for (int i = 0; i < spawn.EntitiesLength; i++)
                    {
                        if (TraceNetIds.Contains(spawn.Entities(i)!.Value.NetId))
                        {
                            TraceLog.Add($"t{_tick} p{player} SPAWN {spawn.Entities(i)!.Value.NetId} sector {spawn.Entities(i)!.Value.State!.Value.Sector}");
                        }
                    }

                    client.ApplySpawn(spawn);
                }
                else if (frame.Type == MsgType.EntityDespawn)
                {
                    var despawn = MessageRegistry.Default.Decode<EntityDespawn>(frame);
                    for (int i = 0; i < despawn.EntriesLength; i++)
                    {
                        if (TraceNetIds.Contains(despawn.Entries(i)!.Value.NetId))
                        {
                            TraceLog.Add($"t{_tick} p{player} DESPAWN {despawn.Entries(i)!.Value.NetId} {despawn.Entries(i)!.Value.Reason}");
                        }
                    }

                    client.ApplyDespawn(despawn);
                }

                foreach (var reply in client.Session.Handle(frame))
                {
                    replies.Add((client, reply));
                }
            },
            confirm));

        foreach (var (client, reply) in replies)
        {
            await Actor.SendAsync(client.Node, reply.Type, Raw(reply));
        }
    }

    private static FlatBufferBuilder Raw(OutMessage message)
    {
        // Re-pack the decoded ResyncRequest the client built (the actor takes builders).
        var request = MessageRegistry.Default.Decode<ResyncRequest>(AsFrame(message.Type, message.Payload)).UnPack();
        var fbb = new FlatBufferBuilder(128);
        fbb.Finish(ResyncRequest.Pack(fbb, request).Value);
        return fbb;
    }

    /// <summary>
    /// The mirror is static (the authority stopped): every ghost the server believes the client holds must equal the mirror's state in every wire
    /// field after the client merged all spawns and entries. Returns the ghosts that differ (empty = converged).
    /// </summary>
    public Task<List<string>> DivergenceAsync(RigClient client) => OnActorAsync(() =>
    {
        var bad = new List<string>();
        foreach (uint id in Interest.HeldBy(client.PlayerId))
        {
            if (!Mirror.TryGet(id, out var e) || e.ControllerPlayer == client.PlayerId)
            {
                continue;
            }

            if (!client.States.TryGetValue(id, out var s))
            {
                bad.Add($"{id}: missing on the client");
            }
            else if (s.Sector != e.Sector || s.Px != e.Px || s.Py != e.Py || s.Pz != e.Pz || s.Yaw != e.Yaw || s.Pitch != e.Pitch || s.Roll != e.Roll
                || s.Vx != e.Vx || s.Vy != e.Vy || s.Vz != e.Vz || s.Flags != e.Flags || s.Hull != e.Hull || s.Shield != e.Shield)
            {
                bad.Add($"{id}: client ({s.Sector},{s.Px},{s.Py},{s.Pz},v{s.Vx}) mirror ({e.Sector},{e.Px},{e.Py},{e.Pz},v{e.Vx})");
            }
        }

        foreach (uint id in client.States.Keys)
        {
            if (!Interest.IsHeld(client.PlayerId, id))
            {
                bad.Add($"{id}: the client holds a ghost the server does not");
            }
        }

        return bad;
    });

    public async Task RunAsync(int steps, bool confirm = true)
    {
        for (int i = 0; i < steps; i++)
        {
            await StepAsync(confirm);
        }
    }

    public Task RunSecondsAsync(double seconds, bool confirm = true) => RunAsync((int)Math.Round(seconds / StepSeconds), confirm);

    public ValueTask DisposeAsync() => Actor.DisposeAsync();
}
