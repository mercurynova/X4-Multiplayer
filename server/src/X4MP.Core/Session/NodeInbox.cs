using X4MP.Core.Net;

namespace X4MP.Core.Session;

/// <summary>
/// The bounded hand-off between one node's reader loop and the <see cref="SessionActor"/> mailbox (inbound flood protection,
/// roadmap follow-up from M1-05). The mailbox itself stays unbounded, but a node can only put a bounded amount into it:
/// <list type="bullet">
/// <item><b><c>PlayerState</c> is latest-wins.</b> The reader parks the newest frame in a one-element slot; an unseen older one is
/// replaced (counted as coalesced) and only one marker input is ever queued, so a client sending 10,000 states per second costs one
/// mailbox entry and one frame.</item>
/// <item><b>Everything else is counted</b> while it waits (<see cref="Pending"/>). A node over <c>InboundQueueFramesPerNode</c> has
/// the excess dropped and counted; sustained dropping (<c>InboundOverflowLimitPerMinute</c>) closes it with <c>RateLimited</c>. The
/// authority is trusted with a reliable stream (a dropped <c>EntitySpawn</c> would desync the world), so it is never dropped: its
/// reader waits for room instead and TCP back-pressure slows the sender.</item>
/// </list>
/// The reader loop is the only producer; the actor is the only consumer.
/// </summary>
internal sealed class NodeInbox(ConnectionStats stats, int capacity, ViolationTracker overflow, bool exempt)
{
    private sealed class Box(InboundFrame frame)
    {
        public InboundFrame Frame { get; } = frame;
    }

    private int _pending;
    private Box? _latest;

    /// <summary>Non-state frames queued for the actor and not yet handled.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>True for the authority: never dropped, the reader waits instead.</summary>
    public bool Exempt => exempt;

    /// <summary>True when this node is over its bound.</summary>
    public bool Full => Pending >= capacity;

    /// <summary>Counts one non-state frame as queued. Pair with <see cref="Leave"/> when the actor handles it.</summary>
    public void Enter() => Interlocked.Increment(ref _pending);

    public void Leave() => Interlocked.Decrement(ref _pending);

    /// <summary>
    /// Parks the newest <c>PlayerState</c>. Returns true when no frame was waiting, i.e. the caller must queue a marker input; false when
    /// an older unseen frame was replaced (the marker already queued will pick up this one).
    /// </summary>
    public bool OfferLatest(InboundFrame frame)
    {
        var previous = Interlocked.Exchange(ref _latest, new Box(frame));
        if (previous is null)
        {
            return true;
        }

        stats.AddInboundCoalesced();
        return false;
    }

    /// <summary>The actor takes the parked <c>PlayerState</c> (null when a newer marker already consumed it).</summary>
    public InboundFrame? TakeLatest() => Interlocked.Exchange(ref _latest, null)?.Frame;

    /// <summary>Drops one frame because the node is over its bound; true once the drops are sustained and the node must be closed.</summary>
    public bool RecordDrop()
    {
        stats.AddInboundDropped();
        return overflow.Record() > overflow.LimitPerMinute;
    }

    /// <summary>The authority's wait: sleeps in short steps until the actor has made room.</summary>
    public async ValueTask WaitForRoomAsync(CancellationToken ct)
    {
        while (Full)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1, ct).ConfigureAwait(false);
        }
    }
}
