using System.Runtime.InteropServices;
using Google.FlatBuffers;
using X4MP.Core.Interest;
using X4MP.Core.Metrics;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Core.Settings;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Replication;

public sealed partial class ReplicationModule
{
    /// <summary>One entry that wants to go out this tick.</summary>
    private struct Candidate
    {
        public int Index;
        public float Priority;
        public ReplicationMask Mask;
        public bool Full;
        public bool Keyframe;
        public int Size;
    }

    private static readonly Comparison<Candidate> ByPriorityDescending = static (a, b) => b.Priority.CompareTo(a.Priority);

    private Candidate[] _candidates = new Candidate[512];
    private readonly List<long> _ackScratch = new(64);

    /// <summary>Datagram mode: an entry that did not change since it was sent is sent again only after this long without an ack (it was probably lost).</summary>
    private const double ResendAfterSeconds = 0.25;

    /// <summary>Datagram mode: no new frames while this many entries are unconfirmed (a dead path must not grow the list without bound).</summary>
    private const int MaxEntriesInFlight = 30000;

    private void TickClient(ClientReplication c, long now, ReplicationOptions opt)
    {
        var node = c.Node;
        if (node.Phase != NodePhase.InGame || !node.IsAttached || node.BaselineEpoch != c.Epoch)
        {
            return; // the lifecycle callbacks drop the state; nothing is sent meanwhile
        }

        double dt = Math.Clamp(ToSeconds(now - c.LastTickTs), 0, 1);
        c.LastTickTs = now;

        bool datagram = _transport.UsesDatagram(c.PlayerId);
        if (datagram != c.Datagram)
        {
            SwitchMode(c, datagram);
        }

        if (datagram)
        {
            CommitDatagram(c, now, opt);
        }
        else
        {
            CommitDelivered(c, now, opt);
        }

        PurgeTombstones(c, now);
        MaybeSendChecksum(c, now, opt);

        // Refill the byte budget (a token bucket: unused budget carries over for a tick or two, never more).
        double bytesPerSecond = BytesPerSecond(opt);
        double maxCredit = Math.Max(bytesPerSecond * 0.1, opt.MaxFramePayloadBytes + ReplicationMath.FrameOverheadBytes);
        c.Credit = Math.Min(c.Credit + (bytesPerSecond * dt), maxCredit);

        if (datagram)
        {
            if (c.Pending.Count > MaxEntriesInFlight)
            {
                Stats.InFlightSkips++; // the path stopped acknowledging anything
                return;
            }
        }
        else if (c.Pending.Count > 0)
        {
            Stats.InFlightSkips++; // TCP: one tick of frames in flight at a time (protocol.md 10.3, "changed then reverted")
            return;
        }

        if (c.Entries.Count == 0)
        {
            return;
        }

        if (!_transport.CanAcceptRealtime(c.PlayerId))
        {
            Stats.LaneSkips++;
            return;
        }

        int selected = Collect(c, now, opt, out bool overBudget);
        if (selected == 0)
        {
            return;
        }

        if (overBudget)
        {
            _candidates.AsSpan(0, selected).Sort(ByPriorityDescending);
        }

        Send(c, now, opt, selected);
    }

    // ------------------------------------------------------------------ pass 1: what is due

    /// <summary>
    /// Walks the client's ghosts and fills <see cref="_candidates"/> with the entries that are due: changed against the baseline and past
    /// their tier interval, or due for a keyframe, or brand new. Returns the count; <paramref name="overBudget"/> says the sum of their
    /// sizes exceeds the client's byte credit (so they must be ranked).
    /// </summary>
    private int Collect(ClientReplication c, long now, ReplicationOptions opt, out bool overBudget)
    {
        var interestOptions = _interestOptions();
        int pid = c.PlayerId;
        var entries = CollectionsMarshal.AsSpan(c.Entries);
        if (_candidates.Length < entries.Length)
        {
            _candidates = new Candidate[Math.Max(entries.Length, _candidates.Length * 2)];
        }

        bool datagram = c.Datagram;
        bool hasShip = _mirror.TryGetPlayerShip(pid, out var ship);
        long nearUnits = (long)interestOptions.NearRadiusM * 64;
        int count = 0;
        long totalBytes = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            ref var g = ref entries[i];
            if (now < g.EligibleTs || !_mirror.TryGet(g.NetId, out var e) || e.ControllerPlayer == pid)
            {
                continue; // spawn-before-state hold, not mirrored any more, or the client's own ship
            }

            var tier = _interest.TierOf(pid, e);
            if (tier == InterestTier.None)
            {
                continue;
            }

            double sinceKeyframe = ToSeconds(now - g.LastKeyframeTs);
            double keyframeInterval = ReplicationMath.KeyframeIntervalSeconds(opt, tier);
            bool keyframeDue = sinceKeyframe >= keyframeInterval;
            bool full = g.NeedsFull || keyframeDue;
            bool changed = g.NeedsFull || e.Version != g.BaseVersion;
            if (!changed && !keyframeDue)
            {
                continue;
            }

            double age = ToSeconds(now - g.LastSentTs);
            if (datagram && g.InFlight > 0 && e.Version == g.SentVersion && age < ResendAfterSeconds)
            {
                continue; // nothing new since the entry that is on its way: wait for its ack before sending it again
            }

            int rate = ReplicationMath.RateHz(interestOptions, tier, e.IsPlayerShip);
            if (!full && !ReplicationMath.IsDue(age, rate))
            {
                continue;
            }

            var mask = full ? ReplicationMath.FullMask : ReplicationMath.DeltaMask(g.Base, e);
            if (datagram && !full && g.InFlight > 0 && g.SentMask != ReplicationMask.None)
            {
                // "Unacked in flight differs" (protocol.md 10.3): a field equal to the baseline is still sent when a datagram that may yet
                // arrive carried another value for it (changed, then reverted).
                var hole = ReplicationMath.DeltaMask(g.Sent, e) & g.SentMask & ~ReplicationMask.Time;
                if (hole != ReplicationMask.None)
                {
                    mask |= hole;
                    if ((mask & ReplicationMath.Timed) != 0)
                    {
                        mask |= ReplicationMask.Time;
                    }
                }
            }

            if (mask == ReplicationMask.None)
            {
                g.BaseVersion = e.Version; // the version moved but nothing visible on the wire changed
                continue;
            }

            float priority;
            if (changed)
            {
                double nearFraction = 1;
                if (tier == InterestTier.Near && hasShip && nearUnits > 0)
                {
                    nearFraction = Math.Sqrt(NearGrid.DistanceSquared(e, ship.Px, ship.Py, ship.Pz)) / nearUnits;
                }

                // Player ships rank like Near entities wherever they are: other players watch them.
                priority = ReplicationMath.Priority(e.IsPlayerShip ? InterestTier.Near : tier, age, rate, nearFraction);
                if (g.NeedsFull)
                {
                    priority += 1000f; // a ghost with no state yet comes before every refinement
                }
            }
            else
            {
                priority = ReplicationMath.KeyframePriority(e.IsPlayerShip ? InterestTier.Near : tier, sinceKeyframe, keyframeInterval);
            }

            int size = ReplicationMath.EntrySize(mask);
            _candidates[count++] = new Candidate { Index = i, Priority = priority, Mask = mask, Full = full, Keyframe = keyframeDue && !g.NeedsFull, Size = size };
            totalBytes += size;
        }

        int framesNeeded = (int)((totalBytes / Math.Max(1, opt.MaxFramePayloadBytes - ReplicationFrame.HeaderBytes)) + 1);
        overBudget = totalBytes + ((long)framesNeeded * ReplicationMath.FrameOverheadBytes) > c.Credit;
        return count;
    }

    // ------------------------------------------------------------------ pass 2: pack and send

    private void Send(ClientReplication c, long now, ReplicationOptions opt, int selected)
    {
        var entries = CollectionsMarshal.AsSpan(c.Entries);
        double referenceGameTime = _mirror.AuthorityGameTime;
        ulong referenceTimeUs = _mirror.AuthorityCaptureTimeUs;
        int maxPayload = Math.Clamp(opt.MaxFramePayloadBytes, ReplicationFrame.HeaderBytes + 64, _scratch.Length);
        var buffer = _scratch.AsSpan();

        int frameStartPending = c.Pending.Count;
        int framePos = ReplicationFrame.HeaderBytes;
        int frameEntries = 0;
        bool open = false;
        bool stop = false;
        long frameSeq = 0;

        for (int k = 0; k < selected && !stop; k++)
        {
            ref var cand = ref _candidates[k];
            int overhead = open ? 0 : ReplicationMath.FrameOverheadBytes;
            if (cand.Size + overhead > c.Credit)
            {
                continue; // does not fit the budget: a smaller entry further down may
            }

            if (open && framePos + cand.Size > maxPayload)
            {
                if (!FlushFrame(c, now, buffer, frameEntries, framePos - ReplicationFrame.HeaderBytes, frameStartPending, frameSeq))
                {
                    stop = true;
                    break;
                }

                open = false;
                frameStartPending = c.Pending.Count;
                if (cand.Size + ReplicationMath.FrameOverheadBytes > c.Credit)
                {
                    continue;
                }
            }

            if (!open)
            {
                ReplicationFrame.Begin(buffer, _serverTick, referenceTimeUs, referenceGameTime);
                framePos = ReplicationFrame.HeaderBytes;
                frameEntries = 0;
                frameSeq = ++c.FrameSeq;
                open = true;
                c.Credit -= ReplicationMath.FrameOverheadBytes;
            }

            ref var g = ref entries[cand.Index];
            _mirror.TryGet(g.NetId, out var e);
            var entry = ReplicationMath.MakeEntry(e, cand.Mask, referenceGameTime);
            framePos += ReplicationCodec.Write(buffer[framePos..], in entry);
            frameEntries++;
            c.Credit -= cand.Size;
            c.Pending.Add(new PendingEntry
            {
                FrameSeq = frameSeq,
                Generation = g.Generation,
                Version = e.Version,
                Full = cand.Full,
                PrevSentTs = g.LastSentTs,
                PrevKeyframeTs = g.LastKeyframeTs,
                SentTs = now,
                Entry = entry,
            });
            g.LastSentTs = now;
            if (cand.Full)
            {
                g.LastKeyframeTs = now;
            }

            if (cand.Full)
            {
                Stats.FullEntriesSent++;
            }

            if (cand.Keyframe)
            {
                Stats.KeyframesSent++;
            }
        }

        if (open && !stop)
        {
            FlushFrame(c, now, buffer, frameEntries, framePos - ReplicationFrame.HeaderBytes, frameStartPending, frameSeq);
        }
    }

    /// <summary>Finishes and queues the open frame. On a refusal the entries it carried are rolled back (nothing about them changed) and false is returned.</summary>
    private bool FlushFrame(ClientReplication c, long now, Span<byte> buffer, int entryCount, int entryBytes, int pendingStart, long frameSeq)
    {
        int payloadLength = ReplicationFrame.Finish(buffer, entryCount, entryBytes);
        var frame = OutboundFrame.Create(MsgType.Replication, Lane.Realtime, buffer[..payloadLength]);
        frame.DeliveryToken = frameSeq;
        SendResult result;
        try
        {
            result = _transport.SendRealtime(c.PlayerId, frame);
        }
        finally
        {
            frame.Release();
        }

        if (result is SendResult.Queued or SendResult.Coalesced)
        {
            if (c.PendingSinceTs == 0)
            {
                c.PendingSinceTs = now;
            }

            if (c.Datagram)
            {
                var ghosts = CollectionsMarshal.AsSpan(c.Entries);
                for (int p = pendingStart; p < c.Pending.Count; p++)
                {
                    var pending = c.Pending[p];
                    if (c.Index.TryGetValue(pending.Entry.NetId, out int index) && ghosts[index].Generation == pending.Generation)
                    {
                        ref var g = ref ghosts[index];
                        g.InFlight++;
                        g.SentVersion = pending.Version;
                        g.Sent.Apply(in pending.Entry);
                        g.SentMask |= pending.Entry.Mask & ~ReplicationMask.Time;
                    }
                }
            }

            Stats.FramesSent++;
            Stats.EntriesSent += entryCount;
            Stats.BytesSent += payloadLength + FrameCodec.HeaderSize;
            ServerMetrics.RecordReplicationFrame(entryCount, payloadLength + FrameCodec.HeaderSize);
            return true;
        }

        // DroppedLane / Closed / ClosedOverflow: the client got nothing. Undo the bookkeeping so the entities stay due.
        Stats.FramesDropped++;
        var entries = CollectionsMarshal.AsSpan(c.Entries);
        for (int p = c.Pending.Count - 1; p >= pendingStart; p--)
        {
            var pending = c.Pending[p];
            if (c.Index.TryGetValue(pending.Entry.NetId, out int index) && entries[index].Generation == pending.Generation)
            {
                entries[index].LastSentTs = pending.PrevSentTs;
                entries[index].LastKeyframeTs = pending.PrevKeyframeTs;
            }
        }

        c.Pending.RemoveRange(pendingStart, c.Pending.Count - pendingStart);
        c.FrameSeq = frameSeq - 1;
        return false;
    }

    // ------------------------------------------------------------------ confirmation

    /// <summary>
    /// Folds the entries of every confirmed frame into the baselines (protocol.md 10.3). A confirmation of an entity that despawned, or was
    /// spawned again, since the frame went out is ignored (its generation is gone). Frames unconfirmed after the timeout are lost: their
    /// entities restart with a full entry.
    /// </summary>
    private void CommitDelivered(ClientReplication c, long now, ReplicationOptions opt)
    {
        if (c.Pending.Count == 0)
        {
            return;
        }

        long delivered = c.DeliveredSeq;
        var entries = CollectionsMarshal.AsSpan(c.Entries);
        int done = 0;
        while (done < c.Pending.Count && c.Pending[done].FrameSeq <= delivered)
        {
            var p = c.Pending[done];
            if (c.Index.TryGetValue(p.Entry.NetId, out int index) && entries[index].Generation == p.Generation)
            {
                ref var g = ref entries[index];
                g.Base.Apply(in p.Entry);
                g.BaseVersion = p.Version;
                if (p.Full)
                {
                    g.NeedsFull = false;
                }
            }

            done++;
        }

        if (done > 0)
        {
            c.Pending.RemoveRange(0, done);
            c.PendingSinceTs = c.Pending.Count == 0 ? 0 : now;
        }

        if (c.Pending.Count > 0 && now - c.PendingSinceTs > Seconds(opt.InFlightTimeoutMs / 1000.0))
        {
            // Not confirmed in time (lost datagrams, a stalled socket): it may or may not arrive later, so never trust the baseline of these again.
            foreach (var p in c.Pending)
            {
                if (c.Index.TryGetValue(p.Entry.NetId, out int index) && entries[index].Generation == p.Generation)
                {
                    entries[index].NeedsFull = true;
                }
            }

            c.Pending.Clear();
            c.PendingSinceTs = 0;
            Stats.FramesLost++;
        }
    }

    /// <summary>
    /// Datagram mode (protocol.md 10.3, UDP): folds the entries of every acknowledged datagram into the baselines and retires the entries that
    /// were not acknowledged within the in-flight timeout. Several frames are in flight at once and acks arrive in any order, so:
    /// <list type="bullet">
    /// <item>an entry is folded only if it is not older than what the baseline holds (older data never overwrites newer);</item>
    /// <item>of the acked entries of one ghost in the same batch only the newest is folded: the client may have received them out of order and
    /// ignored the older one, so the fields only it carried stay "not known to be held" and are simply sent again;</item>
    /// <item>a timed-out entry changes nothing: the baseline was not advanced, so whatever still differs is sent again by the normal delta
    /// (and the "in flight differs" rule covers a reverted value until the entry is retired).</item>
    /// </list>
    /// </summary>
    private void CommitDatagram(ClientReplication c, long now, ReplicationOptions opt)
    {
        var acked = _ackScratch;
        acked.Clear();
        c.DrainAcks(acked);
        if (c.Pending.Count == 0)
        {
            return;
        }

        long timeout = Seconds(opt.InFlightTimeoutMs / 1000.0);
        if (acked.Count == 0 && now - c.Pending[0].SentTs <= timeout)
        {
            return;
        }

        acked.Sort();
        var pending = CollectionsMarshal.AsSpan(c.Pending);
        var ghosts = CollectionsMarshal.AsSpan(c.Entries);
        int a = 0;
        bool anyDone = false;
        for (int i = 0; i < pending.Length; i++)
        {
            ref var p = ref pending[i];
            while (a < acked.Count && acked[a] < p.FrameSeq)
            {
                a++;
            }

            if (a < acked.Count && acked[a] == p.FrameSeq)
            {
                p.State = 1;
                anyDone = true;
            }
            else if (now - p.SentTs > timeout)
            {
                p.State = 2;
                anyDone = true;
            }
        }

        if (!anyDone)
        {
            return;
        }

        int batch = ++c.FoldBatch;
        for (int i = pending.Length - 1; i >= 0; i--)
        {
            ref var p = ref pending[i];
            if (p.State != 1 || !c.Index.TryGetValue(p.Entry.NetId, out int index) || ghosts[index].Generation != p.Generation)
            {
                continue;
            }

            ref var g = ref ghosts[index];
            if (g.FoldBatch == batch)
            {
                continue; // a newer entry of this ghost was acked in the same batch
            }

            g.FoldBatch = batch;
            if (p.Version >= g.BaseVersion)
            {
                g.Base.Apply(in p.Entry);
                g.BaseVersion = p.Version;
                if (p.Full)
                {
                    g.NeedsFull = false;
                }
            }
        }

        int kept = 0;
        long lastLostFrame = 0;
        for (int i = 0; i < pending.Length; i++)
        {
            var p = pending[i];
            if (p.State == 0)
            {
                pending[kept++] = p;
                continue;
            }

            if (c.Index.TryGetValue(p.Entry.NetId, out int index) && ghosts[index].Generation == p.Generation)
            {
                ref var g = ref ghosts[index];
                if (g.InFlight > 0 && --g.InFlight == 0)
                {
                    g.SentMask = ReplicationMask.None;
                }
            }

            if (p.State == 2 && p.FrameSeq != lastLostFrame)
            {
                lastLostFrame = p.FrameSeq;
                Stats.FramesLost++;
            }
        }

        c.Pending.RemoveRange(kept, c.Pending.Count - kept);
        Stats.FramesAcked += acked.Count;
    }

    /// <summary>
    /// The client's Realtime lane changed between TCP and UDP. What was in flight on the old path may or may not still arrive, so its ghosts restart
    /// with a full entry, and the per-field in-flight state starts over.
    /// </summary>
    private static void SwitchMode(ClientReplication c, bool datagram)
    {
        var ghosts = CollectionsMarshal.AsSpan(c.Entries);
        foreach (var p in c.Pending)
        {
            if (c.Index.TryGetValue(p.Entry.NetId, out int index) && ghosts[index].Generation == p.Generation)
            {
                ghosts[index].NeedsFull = true;
            }
        }

        c.Pending.Clear();
        c.PendingSinceTs = 0;
        for (int i = 0; i < ghosts.Length; i++)
        {
            ghosts[i].InFlight = 0;
            ghosts[i].SentMask = ReplicationMask.None;
        }

        c.Datagram = datagram;
        c.SetRecordAcks(datagram);
    }

    private static void PurgeTombstones(ClientReplication c, long now)
    {
        if (c.Tombstones.Count == 0)
        {
            return;
        }

        List<uint>? expired = null;
        foreach (var (netId, until) in c.Tombstones)
        {
            if (until <= now)
            {
                (expired ??= []).Add(netId);
            }
        }

        if (expired is not null)
        {
            foreach (uint id in expired)
            {
                c.Tombstones.Remove(id);
            }
        }
    }

    // ------------------------------------------------------------------ desync guard

    private void MaybeSendChecksum(ClientReplication c, long now, ReplicationOptions opt)
    {
        if (opt.ChecksumIntervalSeconds <= 0 || now < c.NextChecksumTs)
        {
            return;
        }

        c.NextChecksumTs = now + Seconds(opt.ChecksumIntervalSeconds);
        SendChecksum(c);
    }

    // Separate method: the lambda below captures locals, and the compiler would allocate its closure on entry of the method that declares them.
    private void SendChecksum(ClientReplication c)
    {
        uint count = 0;
        ulong hash = 0;
        foreach (uint netId in _interest.HeldBy(c.PlayerId))
        {
            count++;
            hash ^= InterestHash.Mix(netId);
        }

        var frame = ControlFrames.Encode(
            MsgType.InterestChecksum,
            fbb => X4MP.Proto.InterestChecksum.CreateInterestChecksum(fbb, _serverTick, count, hash).Value,
            48);
        try
        {
            if (_transport.SendControl(c.PlayerId, frame) is SendResult.Queued or SendResult.Coalesced)
            {
                Stats.ChecksumsSent++;
            }
        }
        finally
        {
            frame.Release();
        }
    }
}
