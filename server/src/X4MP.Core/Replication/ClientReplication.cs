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

    public ReplicationEntry Entry;
}

/// <summary>Replication state of one client node. Created when its first ghost is added, dropped when it leaves the game. Actor-thread only except <see cref="OnDelivered"/>.</summary>
internal sealed class ClientReplication
{
    private long _deliveredSeq;

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
