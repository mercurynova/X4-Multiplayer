using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using X4MP.Core.Interest;
using X4MP.Core.Metrics;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Core.Settings;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Replication;

/// <summary>Counters of what replication did (diagnostics, tests, the admin API later). Written on the actor thread.</summary>
public sealed class ReplicationStats
{
    public long Ticks { get; internal set; }

    public long FramesSent { get; internal set; }

    public long EntriesSent { get; internal set; }

    public long FullEntriesSent { get; internal set; }

    /// <summary>Full entries sent because the keyframe interval elapsed (a subset of <see cref="FullEntriesSent"/>).</summary>
    public long KeyframesSent { get; internal set; }

    /// <summary>Bytes of Replication frames queued, headers included.</summary>
    public long BytesSent { get; internal set; }

    /// <summary>Frames the Realtime lane refused (above the high watermark or closed): nothing changed for their entities.</summary>
    public long FramesDropped { get; internal set; }

    /// <summary>Client ticks skipped because the previous frame was not confirmed delivered yet.</summary>
    public long InFlightSkips { get; internal set; }

    /// <summary>Client ticks skipped because the Realtime lane was above its low watermark.</summary>
    public long LaneSkips { get; internal set; }

    /// <summary>Frames not confirmed within the timeout (TCP: their entities were marked for a full re-send; UDP: the fields are simply sent again).</summary>
    public long FramesLost { get; internal set; }

    /// <summary>Datagram mode: frames confirmed by an ack.</summary>
    public long FramesAcked { get; internal set; }

    public long Tombstones { get; internal set; }

    public long ChecksumsSent { get; internal set; }

    public long ResyncsHandled { get; internal set; }

    public long ResyncsRefused { get; internal set; }

    public long EpochResets { get; internal set; }
}

/// <summary>
/// Replication (server-design 2.6, protocol.md 10, M1-08): turns the <see cref="WorldMirror"/> into per-client <c>Replication</c> frames.
/// <list type="bullet">
/// <item><b>Which entities.</b> The ghosts the <see cref="InterestManager"/> holds for the client, learned through <see cref="IGhostObserver"/>
/// at the moment a spawn is queued or a despawn is sent. The spawn goes out on the Control lane, which the writer always drains before the
/// Realtime lane, so a state entry can never overtake its spawn; on top of that an entity gets no entry for
/// <c>SpawnHoldTicks</c> ticks (spawn-before-state hold), and its first entry is a full keyframe (the spawn is not trusted as a baseline).</item>
/// <item><b>Priority accumulator.</b> Each tick every changed ghost that is due for its tier rate (Near 20 Hz, Sector 5 Hz, Adjacent and Linger
/// 1 Hz, player ships at least 2 Hz) gets priority = time since last send x rate x tier weight x a distance factor inside Near. The per-client
/// byte budget (<c>BandwidthBudgetKBps</c>, a token bucket) goes to the highest priorities first; what does not fit keeps accumulating.</item>
/// <item><b>Field-mask entries against a baseline.</b> An entry carries only the fields that differ from the client's acked baseline, as absolute
/// values, and TIME (the sample time offset) with any moving field. A baseline moves forward only when the frame that carried the fields was
/// <i>confirmed delivered</i> (TCP: the writer flushed it; UDP: acked), never when it was merely queued, so a dropped frame changes nothing.
/// At most one tick of frames is unconfirmed at a time: a client whose previous frame is still in flight is skipped, which also closes the
/// "changed, then reverted" hole of protocol.md 10.3. A frame unconfirmed after <c>InFlightTimeoutMs</c> counts as lost and its entities are re-sent in full.</item>
/// <item><b>Keyframes.</b> Every ghost gets a full-mask entry at least every 5 s (Near, Sector) or 15 s (Adjacent, Linger).</item>
/// <item><b>Tombstones.</b> A despawned id is tombstoned for <c>TombstoneSeconds</c>: a delivery confirmation of a frame that was in flight when the entity
/// despawned (or was respawned) is ignored, so no baseline is rebuilt from it.</item>
/// <item><b>Desync guard.</b> <c>InterestChecksum{tick, count, xor}</c> over the held ghost set every <c>ChecksumIntervalSeconds</c> on the Control lane
/// (the same lane as the spawns, so the client sees it after every spawn that precedes it). A <c>ResyncRequest</c> clears the baselines of the asked
/// sectors and makes the interest manager deliver them again, rate-limited per client.</item>
/// <item><b>Resume.</b> A node that resumed (<see cref="SessionNode.BaselineEpoch"/> changed) starts from nothing: new baselines, new keyframes.</item>
/// </list>
/// The module ticks itself from a <see cref="TimeProvider"/> timer at <c>TickRateHz</c> (the actor's own 250 ms tick is far too slow) and runs
/// each tick on the actor thread through <see cref="ISessionNodeDriver.Post"/>. It also sets <c>Welcome.max_ghosts</c> from <see cref="InterestOptions.MaxGhosts"/>.
/// <para>
/// UDP seam for M1-09: <see cref="IReplicationTransport"/> carries the frames and the delivery observer. A datagram path stamps
/// <see cref="OutboundFrame.DeliveryToken"/> back to the observer when the ack arrives and may lower <c>MaxFramePayloadBytes</c> to the datagram
/// budget; the baseline rules for UDP (per-field "unacked in flight differs") replace the one-frame-in-flight rule in <c>TickClient</c> only.
/// </para>
/// </summary>
public sealed partial class ReplicationModule : ISessionModule, ISessionActorBound, IGhostObserver, IDisposable
{
    /// <summary>A <c>ResyncRequest</c> names at most this many sectors (the rest of the list is ignored).</summary>
    private const int MaxResyncSectors = 64;

    private readonly WorldMirror _mirror;
    private readonly InterestManager _interest;
    private readonly Func<ReplicationOptions> _options;
    private readonly Func<InterestOptions> _interestOptions;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Dictionary<int, SessionNode> _nodes = [];
    private readonly Dictionary<int, ClientReplication> _clients = [];
    private readonly IReplicationTransport _transport;
    private readonly Action _tickAction;
    private readonly byte[] _scratch = new byte[ReplicationFrame.HeaderBytes + 65536];
    private ISessionNodeDriver? _driver;
    private ITimer? _timer;
    private int _timerRateHz;
    private int _tickQueued;
    private uint _serverTick;

    public ReplicationModule(
        WorldMirror mirror,
        InterestManager interest,
        Func<ReplicationOptions> options,
        Func<InterestOptions> interestOptions,
        TimeProvider? time = null,
        IReplicationTransport? transport = null,
        ILogger<ReplicationModule>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentNullException.ThrowIfNull(interest);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(interestOptions);
        _mirror = mirror;
        _interest = interest;
        _options = options;
        _interestOptions = interestOptions;
        _time = time ?? TimeProvider.System;
        _transport = transport ?? new NodeReplicationTransport(_nodes);
        _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _tickAction = OnTimerTick;
        _interest.AddGhostObserver(this);
    }

    public ReplicationModule(
        WorldMirror mirror,
        InterestManager interest,
        ReplicationOptions options,
        InterestOptions interestOptions,
        TimeProvider? time = null,
        IReplicationTransport? transport = null)
        : this(mirror, interest, () => options, () => interestOptions, time, transport)
    {
    }

    public ReplicationStats Stats { get; } = new();

    /// <summary>
    /// True (default): the module ticks itself from a timer. Tests that call <see cref="Tick"/> by hand switch it off before the first client
    /// so the two do not race.
    /// </summary>
    public bool TimerEnabled { get; set; } = true;

    private ReplicationOptions Opt => _options();

    private long Now => _time.GetTimestamp();

    private long Seconds(double seconds) => (long)(seconds * _time.TimestampFrequency);

    private double ToSeconds(long ticks) => (double)ticks / _time.TimestampFrequency;

    // ------------------------------------------------------------------ queries (tests, GUI)

    /// <summary>Clients replication currently serves.</summary>
    public int ClientCount => _clients.Count;

    /// <summary>Ghosts replication tracks for a client (equals the interest manager's held set once the hooks ran).</summary>
    public int GhostCount(int playerId) => _clients.TryGetValue(playerId, out var c) ? c.Entries.Count : 0;

    /// <summary>True when the id is in the client's tombstone window.</summary>
    public bool IsTombstoned(int playerId, uint netId) =>
        _clients.TryGetValue(playerId, out var c) && c.Tombstones.TryGetValue(netId, out long until) && until > Now;

    /// <summary>The baseline the client is known to hold for an entity (null before a frame carrying it was confirmed, or when it is not a ghost).</summary>
    public Baseline? BaselineOf(int playerId, uint netId)
    {
        if (_clients.TryGetValue(playerId, out var c) && c.Index.TryGetValue(netId, out int index))
        {
            var entry = c.Entries[index];
            return entry.NeedsFull ? null : entry.Base;
        }

        return null;
    }

    /// <summary>Entries sent but not yet confirmed delivered, for a client.</summary>
    public int PendingEntries(int playerId) => _clients.TryGetValue(playerId, out var c) ? c.Pending.Count : 0;

    /// <summary>The <see cref="SessionNode.BaselineEpoch"/> replication state of a client belongs to (-1 when there is none).</summary>
    public int EpochOf(int playerId) => _clients.TryGetValue(playerId, out var c) ? c.Epoch : -1;

    // ------------------------------------------------------------------ module callbacks

    public void Bind(ISessionNodeDriver driver) => _driver = driver;

    public AdmissionVerdict OnNodeAdmitting(SessionNode node, WelcomeT welcome, bool resumed)
    {
        // Follow-up (2) of the M1-06/07 review: the client's ghost budget is the interest manager's.
        welcome.MaxGhosts = (uint)Math.Max(1, _interestOptions().MaxGhosts);
        return AdmissionVerdict.Accept;
    }

    public void OnNodeAttached(SessionNode node, bool resumed)
    {
        _nodes[node.PlayerId] = node;
        if (_clients.TryGetValue(node.PlayerId, out var existing)
            && (existing.Epoch != node.BaselineEpoch || !ReferenceEquals(existing.Connection, node.Connection)))
        {
            // The node resumed (protocol.md 6.6): its baselines are void, a keyframe of everything follows.
            DropClient(node.PlayerId);
            Stats.EpochResets++;
        }
    }

    public void OnNodePhaseChanged(SessionNode node, NodePhase previous, NodePhase current)
    {
        _nodes[node.PlayerId] = node;
        if (previous == NodePhase.InGame && current != NodePhase.InGame)
        {
            DropClient(node.PlayerId);
        }
    }

    public void OnNodeDetached(SessionNode node, DetachReason reason) => DropClient(node.PlayerId);

    public void OnNodeLeft(SessionNode node, string reason)
    {
        DropClient(node.PlayerId);
        _nodes.Remove(node.PlayerId);
    }

    public void OnSessionPhaseChanged(SessionPhase previous, SessionPhase current)
    {
        if (current == SessionPhase.Ended)
        {
            foreach (int id in _clients.Keys.ToArray())
            {
                DropClient(id);
            }
        }
    }

    public bool OnMessage(SessionNode node, InboundFrame frame)
    {
        if (frame.Type != MsgType.ResyncRequest)
        {
            return false;
        }

        var request = MessageRegistry.Default.Decode<ResyncRequest>(frame.Frame);
        int count = Math.Min(request.SectorsLength, MaxResyncSectors);
        var sectors = new List<ushort>(count);
        for (int i = 0; i < count; i++)
        {
            sectors.Add(request.Sectors(i));
        }

        Resync(node.PlayerId, sectors, request.Reason);
        return true;
    }

    // ------------------------------------------------------------------ resync

    /// <summary>
    /// A client says its view is wrong (<c>ResyncRequest</c>): its ghosts in <paramref name="sectors"/> (empty = all) are delivered again and
    /// their baselines start over. Rate-limited per client (<c>ResyncMinIntervalMs</c>). Returns true when a resync started.
    /// </summary>
    public bool Resync(int playerId, IReadOnlyCollection<ushort>? sectors, string? reason = null)
    {
        long now = Now;
        if (!_interest.IsActive(playerId))
        {
            Stats.ResyncsRefused++; // an observer, or a node that is not in game: nothing is delivered to it
            return false;
        }

        _clients.TryGetValue(playerId, out var c);
        if (c is not null && c.LastResyncTs != 0 && now - c.LastResyncTs < Seconds(Opt.ResyncMinIntervalMs / 1000.0))
        {
            Stats.ResyncsRefused++;
            return false;
        }

        if (c is not null)
        {
            c.LastResyncTs = now;
        }

        // The interest manager takes the ghosts out of the held set (we hear GhostRemoval.Resync and drop their baselines) and queues
        // their spawns again, which re-creates the entries as full-keyframe entries through OnGhostAdded.
        _interest.Resync(playerId, sectors);
        Stats.ResyncsHandled++;
        ServerMetrics.RecordReplicationResync();
        if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information))
        {
            LogResync(playerId, sectors is null ? 0 : sectors.Count, reason ?? string.Empty);
        }

        return true;
    }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "replication: player {PlayerId} asked for a resync of {Sectors} sector(s) (0 = all): {Reason}")]
    private partial void LogResync(int playerId, int sectors, string reason);

    // ------------------------------------------------------------------ ghost observer

    private ClientReplication? EnsureClient(int playerId)
    {
        if (_clients.TryGetValue(playerId, out var existing))
        {
            if (existing.Epoch == existing.Node.BaselineEpoch && ReferenceEquals(existing.Connection, existing.Node.Connection))
            {
                return existing;
            }

            // The node resumed (new epoch, new connection) and the interest manager already re-delivers to it: start from nothing.
            DropClient(playerId);
            Stats.EpochResets++;
        }

        if (!_nodes.TryGetValue(playerId, out var node) || !node.IsAttached || node.Connection is not { } connection)
        {
            return null;
        }

        long now = Now;
        var opt = Opt;
        var client = new ClientReplication(playerId, node, now)
        {
            Connection = connection,
            NextChecksumTs = now + Seconds(Math.Max(1, opt.ChecksumIntervalSeconds)),
            Credit = BytesPerSecond(opt) * 0.1,
        };
        _clients[playerId] = client;
        _transport.SetDeliveryObserver(playerId, connection, client.Observer);
        EnsureTimer();
        return client;
    }

    private void DropClient(int playerId)
    {
        if (_clients.Remove(playerId, out var client))
        {
            _transport.SetDeliveryObserver(playerId, client.Connection, null);
        }

        if (_clients.Count == 0)
        {
            StopTimer();
        }
    }

    public void OnGhostAdded(int playerId, MirrorEntity entity)
    {
        var c = EnsureClient(playerId);
        if (c is null)
        {
            return;
        }

        long now = Now;
        var opt = Opt;
        c.Tombstones.Remove(entity.NetId);
        var ghost = new GhostEntry
        {
            NetId = entity.NetId,
            Generation = ++c.NextGeneration,
            NeedsFull = true,
            EligibleTs = now + Seconds(opt.SpawnHoldTicks / (double)Math.Max(1, opt.TickRateHz)),
            LastSentTs = now,
            LastKeyframeTs = now,
        };
        if (c.Index.TryGetValue(entity.NetId, out int index))
        {
            c.Entries[index] = ghost; // a respawn without a despawn in between: a new life
        }
        else
        {
            c.Index[entity.NetId] = c.Entries.Count;
            c.Entries.Add(ghost);
        }
    }

    public void OnGhostRemoved(int playerId, uint netId, GhostRemoval reason)
    {
        if (!_clients.TryGetValue(playerId, out var c) || !c.Index.TryGetValue(netId, out int index))
        {
            return;
        }

        c.RemoveAt(index);
        if (reason == GhostRemoval.Despawned)
        {
            c.Tombstones[netId] = Now + Seconds(Opt.TombstoneSeconds);
            Stats.Tombstones++;
        }
    }

    public void OnGhostsReset(int playerId)
    {
        if (_clients.TryGetValue(playerId, out var c))
        {
            c.Entries.Clear();
            c.Index.Clear();
            c.Pending.Clear();
            c.PendingSinceTs = 0;
            c.Tombstones.Clear();
        }
    }

    // ------------------------------------------------------------------ timer

    private static double BytesPerSecond(ReplicationOptions options) => options.BandwidthBudgetKBps * 1000.0;

    private void EnsureTimer()
    {
        if (_driver is null || !TimerEnabled)
        {
            return; // tests drive Tick() by hand
        }

        int rate = Math.Clamp(Opt.TickRateHz, 1, 1000);
        if (_timer is null)
        {
            var period = TimeSpan.FromSeconds(1.0 / rate);
            _timer = _time.CreateTimer(static state => ((ReplicationModule)state!).QueueTick(), this, period, period);
            _timerRateHz = rate;
        }
        else if (rate != _timerRateHz)
        {
            var period = TimeSpan.FromSeconds(1.0 / rate);
            _timer.Change(period, period);
            _timerRateHz = rate;
        }
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void QueueTick()
    {
        // At most one tick waits in the actor's mailbox, however many timer callbacks fire.
        if (Interlocked.Exchange(ref _tickQueued, 1) == 0 && _driver is { } driver && !driver.Post(_tickAction))
        {
            Volatile.Write(ref _tickQueued, 0);
        }
    }

    private void OnTimerTick()
    {
        Volatile.Write(ref _tickQueued, 0);
        Tick(Now);
    }

    public void Dispose() => StopTimer();

    // ------------------------------------------------------------------ the tick

    /// <summary>
    /// One replication tick at <paramref name="timestamp"/> (a <see cref="TimeProvider.GetTimestamp"/> value): for every client, confirm what
    /// was delivered, then (unless the previous frame is still in flight or the lane is full) pick the entries the byte budget allows and send them.
    /// The actor calls it from the module's timer; tests call it directly.
    /// </summary>
    public void Tick(long timestamp)
    {
        var opt = Opt;
        if (_clients.Count == 0)
        {
            StopTimer(); // the last client left while a tick was queued: do not keep a timer alive for nobody
            return;
        }

        EnsureTimer();
        _serverTick++;
        Stats.Ticks++;
        foreach (var client in _clients.Values)
        {
            TickClient(client, timestamp, opt);
        }
    }

    /// <summary>The actor's own tick (every 250 ms by default): housekeeping only; frames are sent by the module's timer.</summary>
    public void OnTick(long timestamp)
    {
    }
}
