using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Protocol;

namespace X4MP.Core.Replication;

/// <summary>One ghost of one client: what the client is known to hold and when it was last sent something.</summary>
internal struct GhostEntry
{
    public uint NetId;

    /// <summary>Counts the lives of this id for this client (a respawn is a new life): a confirmation of an older life is ignored.</summary>
    public uint Generation;

    /// <summary>The mirror <c>Version</c> the baseline corresponds to.</summary>
    public uint BaseVersion;

    /// <summary>True until a full-mask entry was confirmed delivered: the next entry is a full keyframe (spawn state is not trusted as baseline).</summary>
    public bool NeedsFull;

    /// <summary>No entry before this timestamp (the spawn-before-state hold).</summary>
    public long EligibleTs;

    public long LastSentTs;

    public long LastKeyframeTs;

    public Baseline Base;

    // --- datagram (UDP) mode only: what unconfirmed datagrams carried (protocol.md 10.3, "unacked in flight differs") ---

    /// <summary>The value of each field in <see cref="SentMask"/> as the most recent unconfirmed entry carried it.</summary>
    public Baseline Sent;

    /// <summary>Fields some entry still unconfirmed carried. A field may only be left out of an entry when it equals the baseline and, if it is in this mask, also <see cref="Sent"/>.</summary>
    public ReplicationMask SentMask;

    /// <summary>The mirror version of the newest entry sent (unchanged since then and still in flight: wait for the ack instead of sending again).</summary>
    public uint SentVersion;

    /// <summary>Entries of this ghost sent and neither acknowledged nor timed out.</summary>
    public int InFlight;

    /// <summary>Stamp of the last ack batch that folded an entry of this ghost (the newest entry of a batch wins).</summary>
    public int FoldBatch;
}

/// <summary>An entry that went out in a frame whose delivery is not confirmed yet.</summary>
internal struct PendingEntry
{
    public long FrameSeq;

    public uint Generation;

    public uint Version;

    public bool Full;

    public long PrevSentTs;

    public long PrevKeyframeTs;

    /// <summary>When the frame went out (datagram mode: entries older than the in-flight timeout count as lost).</summary>
    public long SentTs;

    /// <summary>Datagram mode: 0 = still in flight, 1 = acknowledged, 2 = timed out (set while an ack batch is processed).</summary>
    public byte State;

    public ReplicationEntry Entry;
}

/// <summary>Replication state of one client node. Created when its first ghost is added, dropped when it leaves the game. Actor-thread only except <see cref="OnDelivered"/>.</summary>
internal sealed class ClientReplication
{
    private long _deliveredSeq;
    private readonly object _ackGate = new();
    private readonly List<long> _acked = [];
    private bool _recordAcks;

    public ClientReplication(int playerId, SessionNode node, long now)
    {
        PlayerId = playerId;
        Node = node;
        Epoch = node.BaselineEpoch;
        LastTickTs = now;
        Observer = OnDelivered;
    }

    public int PlayerId { get; }

    public SessionNode Node { get; }

    /// <summary>The <see cref="SessionNode.BaselineEpoch"/> this state belongs to; a different one means the node resumed and everything restarts.</summary>
    public int Epoch { get; }

    public List<GhostEntry> Entries { get; } = [];

    public Dictionary<uint, int> Index { get; } = [];

    /// <summary>Despawned ids and when their tombstone ends (protocol.md 10.2).</summary>
    public Dictionary<uint, long> Tombstones { get; } = [];

    public List<PendingEntry> Pending { get; } = [];

    public uint NextGeneration { get; set; }

    /// <summary>The last frame sequence number handed out; frames carry it as <see cref="OutboundFrame.DeliveryToken"/>.</summary>
    public long FrameSeq { get; set; }

    /// <summary>When the oldest unconfirmed frame was sent (0 = none pending).</summary>
    public long PendingSinceTs { get; set; }

    public long LastTickTs { get; set; }

    /// <summary>Bytes the client may still be sent: a token bucket refilled from the byte budget.</summary>
    public double Credit { get; set; }

    public long NextChecksumTs { get; set; }

    public long LastResyncTs { get; set; }

    public INodeConnection? Connection { get; set; }

    /// <summary>The delivery callback registered on the connection (a cached delegate, so registering allocates once).</summary>
    public Action<OutboundFrame> Observer { get; }

    /// <summary>Highest frame sequence the transport confirmed (TCP flush or UDP ack).</summary>
    public long DeliveredSeq => Interlocked.Read(ref _deliveredSeq);

    /// <summary>True while the client's Realtime lane is UDP: frames are confirmed one by one (acks), several may be in flight, baselines use the per-field rule.</summary>
    public bool Datagram { get; set; }

    /// <summary>Counts the ack batches folded (see <see cref="GhostEntry.FoldBatch"/>).</summary>
    public int FoldBatch { get; set; }

    /// <summary>Starts or stops recording the individual tokens <see cref="OnDelivered"/> hears about (datagram mode).</summary>
    public void SetRecordAcks(bool on)
    {
        lock (_ackGate)
        {
            _recordAcks = on;
            _acked.Clear();
        }
    }

    /// <summary>Moves the tokens confirmed since the last call into <paramref name="into"/> (appended).</summary>
    public void DrainAcks(List<long> into)
    {
        lock (_ackGate)
        {
            if (_acked.Count > 0)
            {
                into.AddRange(_acked);
                _acked.Clear();
            }
        }
    }

    /// <summary>
    /// Called by the transport, on any thread, when a frame was delivered. Frames of one lane are delivered in order, but the maximum
    /// is kept anyway so a late or duplicate confirmation can never move it back.
    /// </summary>
    public void OnDelivered(OutboundFrame frame)
    {
        long token = frame.DeliveryToken;
        long seen;
        while (token > (seen = Interlocked.Read(ref _deliveredSeq)) && Interlocked.CompareExchange(ref _deliveredSeq, token, seen) != seen)
        {
        }

        lock (_ackGate)
        {
            if (_recordAcks && _acked.Count < 16384)
            {
                _acked.Add(token);
            }
        }
    }

    public void RemoveAt(int index)
    {
        uint removed = Entries[index].NetId;
        int last = Entries.Count - 1;
        if (index != last)
        {
            var moved = Entries[last];
            Entries[index] = moved;
            Index[moved.NetId] = index;
        }

        Entries.RemoveAt(last);
        Index.Remove(removed);
    }
}
