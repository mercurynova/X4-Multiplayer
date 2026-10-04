using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Net;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Tests.Net;

public class SendQueueTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static OutboundFrame F(Lane lane, int payload = 8, ulong key = 0, MsgType type = MsgType.Ping) =>
        OutboundFrame.Create(type, lane, new byte[payload], key);

    private static SendQueueOptions Small() => new()
    {
        ControlSoftCapBytes = 200,
        ControlHardCapBytes = 400,
        RealtimeLowWatermarkBytes = 100,
        RealtimeHighWatermarkBytes = 300,
        BulkCapBytes = 1000,
        SlowConsumerTimeout = TimeSpan.FromSeconds(15),
    };

    private static List<OutboundFrame> DrainAll(SendQueue q)
    {
        var list = new List<OutboundFrame>();
        while (q.TryDequeue(out var f))
        {
            list.Add(f);
        }

        return list;
    }

    [Fact]
    public void DrainsInStrictPriorityControlRealtimeBulk()
    {
        var q = new SendQueue(new SendQueueOptions());
        var bulk = F(Lane.Bulk, type: MsgType.SaveChunk);
        var rt1 = F(Lane.Realtime, type: MsgType.PlayerState);
        var ctl1 = F(Lane.Control);
        var rt2 = F(Lane.Realtime, type: MsgType.PlayerState);
        var ctl2 = F(Lane.Control);
        foreach (var f in new[] { bulk, rt1, ctl1, rt2, ctl2 })
        {
            Assert.Equal(SendResult.Queued, q.TrySend(f));
        }

        var order = DrainAll(q);
        Assert.Equal([ctl1, ctl2, rt1, rt2, bulk], order);
    }

    [Fact]
    public void ControlIsFifoAndNeverCoalesced()
    {
        var q = new SendQueue(new SendQueueOptions());
        var a = F(Lane.Control, key: 7);
        var b = F(Lane.Control, key: 7);
        Assert.Equal(SendResult.Queued, q.TrySend(a));
        Assert.Equal(SendResult.Queued, q.TrySend(b));
        Assert.Equal([a, b], DrainAll(q));
    }

    [Fact]
    public void RealtimeFrameWithSameKeyReplacesPendingFrameInPlace()
    {
        var q = new SendQueue(new SendQueueOptions());
        var first = F(Lane.Realtime, 10, key: 1);
        var other = F(Lane.Realtime, 10, key: 2);
        var latest = F(Lane.Realtime, 30, key: 1);
        Assert.Equal(SendResult.Queued, q.TrySend(first));
        Assert.Equal(SendResult.Queued, q.TrySend(other));
        Assert.Equal(SendResult.Coalesced, q.TrySend(latest));

        Assert.Equal(2, q.PendingFrames(Lane.Realtime));
        Assert.Equal(other.Length + latest.Length, q.PendingBytes(Lane.Realtime));
        Assert.Equal(1, q.Stats.FramesCoalesced);
        Assert.Equal(1, first.RefCount); // queue let go of the replaced frame

        Assert.Equal([latest, other], DrainAll(q)); // replaced in place: keeps the original queue position
    }

    [Fact]
    public void ZeroKeyNeverCoalescesAndKeyIsFreeAfterDequeue()
    {
        var q = new SendQueue(new SendQueueOptions());
        Assert.Equal(SendResult.Queued, q.TrySend(F(Lane.Realtime, key: 0)));
        Assert.Equal(SendResult.Queued, q.TrySend(F(Lane.Realtime, key: 0)));
        Assert.Equal(SendResult.Queued, q.TrySend(F(Lane.Realtime, key: 5)));
        DrainAll(q);
        // The key slot must not outlive its entry, or a later frame would "replace" a dequeued one.
        Assert.Equal(SendResult.Queued, q.TrySend(F(Lane.Realtime, key: 5)));
        Assert.Equal(1, q.PendingFrames(Lane.Realtime));
    }

    [Fact]
    public void RealtimeAboveHighWatermarkIsDroppedAndConnectionStaysOpen()
    {
        var q = new SendQueue(Small());
        var f = F(Lane.Realtime, 92); // 100 bytes framed
        Assert.Equal(100, f.Length);
        Assert.Equal(SendResult.Queued, q.TrySend(f));
        Assert.Equal(SendResult.Queued, q.TrySend(F(Lane.Realtime, 92)));
        Assert.Equal(SendResult.Queued, q.TrySend(F(Lane.Realtime, 92)));
        Assert.Equal(300, q.PendingBytes(Lane.Realtime));
        Assert.Equal(SendResult.DroppedLane, q.TrySend(F(Lane.Realtime, 92)));
        Assert.Equal(SendResult.DroppedLane, q.TrySend(F(Lane.Realtime, 92, key: 9)));
        Assert.Equal(2, q.Stats.FramesDropped);
        Assert.Equal(2, q.Stats.Dropped(Lane.Realtime));
        Assert.False(q.IsClosed);
        Assert.Equal(SendResult.Queued, q.TrySend(F(Lane.Control))); // other lanes unaffected
    }

    [Fact]
    public void CoalescingReplacementThatWouldExceedHighWatermarkIsDropped()
    {
        var q = new SendQueue(Small());
        Assert.Equal(SendResult.Queued, q.TrySend(F(Lane.Realtime, 92, key: 1)));
        Assert.Equal(SendResult.Queued, q.TrySend(F(Lane.Realtime, 92, key: 2)));
        Assert.Equal(SendResult.DroppedLane, q.TrySend(F(Lane.Realtime, 292, key: 1))); // 100+300 > 300
        Assert.Equal(SendResult.Coalesced, q.TrySend(F(Lane.Realtime, 192, key: 1))); // 100+200 == 300
    }

    [Fact]
    public void CanAcceptRealtimeFollowsLowWatermark()
    {
        var q = new SendQueue(Small());
        Assert.True(q.CanAcceptRealtime);
        q.TrySend(F(Lane.Realtime, 42)); // 50 bytes
        Assert.True(q.CanAcceptRealtime);
        q.TrySend(F(Lane.Realtime, 42)); // 100 bytes == low watermark
        Assert.False(q.CanAcceptRealtime);
        Release(DrainAll(q));
        Assert.True(q.CanAcceptRealtime);
    }

    [Fact]
    public void BulkIsDroppedAboveItsSafetyCapAndReleasesFramesOnDequeue()
    {
        var q = new SendQueue(Small());
        var f = F(Lane.Bulk, 492, type: MsgType.SaveChunk); // 500 bytes
        Assert.Equal(SendResult.Queued, q.TrySend(f));
        Assert.Equal(SendResult.Queued, q.TrySend(F(Lane.Bulk, 492, type: MsgType.SaveChunk)));
        Assert.Equal(SendResult.DroppedLane, q.TrySend(F(Lane.Bulk, 8, type: MsgType.SaveChunk)));
        Assert.Equal(2, f.RefCount);
        Release(DrainAll(q));
        Assert.Equal(1, f.RefCount);
    }

    [Fact]
    public void ControlSoftCapIsAHintOnly()
    {
        var q = new SendQueue(Small());
        Assert.False(q.ControlOverSoftCap);
        Assert.Equal(SendResult.Queued, q.TrySend(F(Lane.Control, 192))); // 200 == soft cap
        Assert.True(q.ControlOverSoftCap);
        Assert.Equal(SendResult.Queued, q.TrySend(F(Lane.Control, 8)));
        Assert.False(q.IsClosed);
    }

    [Fact]
    public void ControlHardCapClosesWithSlowConsumer()
    {
        DisconnectCode? raised = null;
        var q = new SendQueue(Small(), onOverflow: c => raised = c);
        Assert.Equal(SendResult.Queued, q.TrySend(F(Lane.Control, 192))); // 200
        Assert.Equal(SendResult.Queued, q.TrySend(F(Lane.Control, 192))); // 400 == hard cap
        Assert.Null(raised);
        Assert.Equal(SendResult.ClosedOverflow, q.TrySend(F(Lane.Control, 8)));
        Assert.Equal(DisconnectCode.SlowConsumer, raised);
        Assert.Equal(SendResult.Closed, q.TrySend(F(Lane.Control, 8)));
        Assert.Equal(SendResult.Closed, q.TrySend(F(Lane.Realtime, 8)));
        Assert.True(q.IsClosed);
    }

    [Fact]
    public void ControlFrameOlderThanTimeoutClosesWithSlowConsumer()
    {
        var time = new FakeTimeProvider();
        DisconnectCode? raised = null;
        var q = new SendQueue(new SendQueueOptions(), time, onOverflow: c => raised = c);
        Assert.Equal(SendResult.Queued, q.TrySend(F(Lane.Control)));
        time.Advance(TimeSpan.FromSeconds(14.9));
        Assert.Equal(SendResult.Queued, q.TrySend(F(Lane.Control)));
        Assert.Null(raised);
        time.Advance(TimeSpan.FromSeconds(0.2)); // oldest frame is now 15.1 s old
        Assert.Equal(SendResult.ClosedOverflow, q.TrySend(F(Lane.Control)));
        Assert.Equal(DisconnectCode.SlowConsumer, raised);
    }

    [Fact]
    public void PeriodicCheckClosesAStalledConsumerEvenWithoutNewSends()
    {
        var time = new FakeTimeProvider();
        DisconnectCode? raised = null;
        var q = new SendQueue(new SendQueueOptions(), time, onOverflow: c => raised = c);
        q.TrySend(F(Lane.Control));
        time.Advance(TimeSpan.FromSeconds(10));
        q.CheckSlowConsumer();
        Assert.Null(raised);
        time.Advance(TimeSpan.FromSeconds(6));
        q.CheckSlowConsumer();
        Assert.Equal(DisconnectCode.SlowConsumer, raised);
        q.CheckSlowConsumer();
        Assert.Equal(DisconnectCode.SlowConsumer, raised); // raised once
    }

    [Fact]
    public void AgeIsMeasuredFromTheOldestQueuedFrameNotLastDrain()
    {
        var time = new FakeTimeProvider();
        DisconnectCode? raised = null;
        var q = new SendQueue(new SendQueueOptions(), time, onOverflow: c => raised = c);
        q.TrySend(F(Lane.Control));
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.True(q.TryDequeue(out var f)); // drained in time (the writer keeps up)
        f.Release();
        Assert.Equal(SendResult.Queued, q.TrySend(F(Lane.Control)));
        Assert.Null(raised);
    }

    [Fact]
    public void CompleteDropsRealtimeAndBulkKeepsControlAndAppendsFinalFrame()
    {
        var q = new SendQueue(new SendQueueOptions());
        var ctl = F(Lane.Control);
        var rt = F(Lane.Realtime, key: 3);
        var blk = F(Lane.Bulk);
        var fin = F(Lane.Control, type: MsgType.Disconnect);
        q.TrySend(ctl);
        q.TrySend(rt);
        q.TrySend(blk);
        q.Complete(discardControl: false, fin);
        Assert.Equal(SendResult.Closed, q.TrySend(F(Lane.Control)));
        Assert.Equal([ctl, fin], DrainAll(q));
        Assert.Equal(1, rt.RefCount);
        Assert.Equal(1, blk.RefCount);
    }

    [Fact]
    public async Task WaitToReadWakesOnSendAndEndsOnCompleteWhenEmpty()
    {
        var q = new SendQueue(new SendQueueOptions());
        var waiting = q.WaitToReadAsync(CancellationToken.None).AsTask();
        await Task.Delay(30);
        Assert.False(waiting.IsCompleted);
        q.TrySend(F(Lane.Control));
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Release(DrainAll(q));

        var ended = q.WaitToReadAsync(CancellationToken.None).AsTask();
        q.Complete(discardControl: false);
        Assert.False(await ended.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task OneEncodeFansOutToManyQueuesAndBufferIsReleasedOnce()
    {
        var queues = Enumerable.Range(0, 3).Select(_ => new SendQueue(new SendQueueOptions())).ToArray();
        var frame = F(Lane.Control);
        foreach (var q in queues)
        {
            Assert.Equal(SendResult.Queued, q.TrySend(frame));
        }

        Assert.Equal(4, frame.RefCount);
        frame.Release(); // producer lets go
        foreach (var q in queues)
        {
            Assert.True(q.TryDequeue(out var f));
            Assert.Same(frame, f);
            f.Release();
        }

        Assert.Equal(0, frame.RefCount);
        Assert.Throws<ObjectDisposedException>(() => frame.AddRef());
        await Task.CompletedTask;
    }

    // ---- writer loop ----

    [Fact]
    public async Task WriterLoopWritesFramesToPipeInPriorityOrderAndReportsFlushed()
    {
        var pipe = new Pipe();
        var q = new SendQueue(new SendQueueOptions());
        var stats = q.Stats;
        var flushed = new List<MsgType>();
        var bulk = F(Lane.Bulk, 8, type: MsgType.SaveChunk);
        var rt = F(Lane.Realtime, 8, type: MsgType.PlayerState);
        var ctl = F(Lane.Control, 8, type: MsgType.Pong);
        q.TrySend(bulk);
        q.TrySend(rt);
        q.TrySend(ctl);

        var writer = ConnectionWriter.RunAsync(q, pipe.Writer, stats, 64 * 1024, f => flushed.Add(f.MessageType), CancellationToken.None);
        var received = new List<MsgType>();
        while (received.Count < 3)
        {
            var r = await pipe.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            var buf = r.Buffer.ToArray();
            for (int off = 0; off + FrameCodec.HeaderSize <= buf.Length; off += FrameCodec.HeaderSize + 8)
            {
                received.Add((MsgType)BitConverter.ToUInt16(buf, off + 4));
            }

            pipe.Reader.AdvanceTo(r.Buffer.End);
        }

        Assert.Equal([MsgType.Pong, MsgType.PlayerState, MsgType.SaveChunk], received);
        q.Complete(discardControl: false);
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([MsgType.Pong, MsgType.PlayerState, MsgType.SaveChunk], flushed);
        Assert.Equal(3, stats.FramesSent);
        Assert.Equal(3 * 16, stats.BytesSent);
        Assert.Equal(1, ctl.RefCount);
    }

    [Fact]
    public async Task NonReadingPeerNeverBlocksProducers()
    {
        // The peer never reads: pipe pause threshold is tiny, so the writer loop stalls inside FlushAsync.
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 4096, resumeWriterThreshold: 2048, useSynchronizationContext: false));
        DisconnectCode? overflow = null;
        var options = new SendQueueOptions
        {
            ControlSoftCapBytes = 32 * 1024,
            ControlHardCapBytes = 128 * 1024,
            RealtimeLowWatermarkBytes = 8 * 1024,
            RealtimeHighWatermarkBytes = 32 * 1024,
        };
        var stats = new ConnectionStats();
        var q = new SendQueue(options, stats: stats, onOverflow: c => overflow = c);
        using var abort = new CancellationTokenSource();
        var writer = ConnectionWriter.RunAsync(q, pipe.Writer, stats, 64 * 1024, null, abort.Token);

        var sw = Stopwatch.StartNew();
        int dropped = 0, coalesced = 0, closedOverflow = 0;
        for (int i = 0; i < 200_000; i++)
        {
            var rt = F(Lane.Realtime, 120, key: (ulong)(i % 400) + 1);
            switch (q.TrySend(rt))
            {
                case SendResult.DroppedLane: dropped++; break;
                case SendResult.Coalesced: coalesced++; break;
            }

            rt.Release();

            if (i % 20 == 0)
            {
                var ctl = F(Lane.Control, 120);
                if (q.TrySend(ctl) is SendResult.ClosedOverflow)
                {
                    closedOverflow++;
                }

                ctl.Release();
            }
        }

        sw.Stop();
        output.WriteLine($"400k TrySend against a non-reading peer: {sw.ElapsedMilliseconds} ms; dropped={dropped} coalesced={coalesced} closedOverflow={closedOverflow}");

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "producers must not wait on the stalled consumer");
        Assert.False(writer.IsCompleted); // writer is stuck in the flush, nothing else is
        Assert.True(dropped > 0);
        Assert.True(coalesced > 0);
        Assert.Equal(DisconnectCode.SlowConsumer, overflow); // Control hard cap reached
        Assert.True(closedOverflow > 0);

        abort.Cancel();
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(q.IsClosed);
    }

    // ---- performance ----

    [Fact]
    public void TrySendIsAllocationFree() => MeasureTrySend();

    /// <summary>Wall-clock speed: best of 300 rounds of 1000 sends (machine load cannot fail it, a real slowdown still does). Runs in the nightly Perf job.</summary>
    [Fact]
    [Trait("Category", "Perf")]
    public void TrySendIsFast()
    {
        var (ctlNs, rtNs, keyedNs) = MeasureTrySend();
#if DEBUG
        double limit = 2000; // Debug builds are not representative; the allocation assertion above still holds.
#else
        double limit = 200;
#endif
        Assert.True(ctlNs < limit, $"control TrySend took {ctlNs:F1} ns");
        Assert.True(rtNs < limit, $"realtime TrySend took {rtNs:F1} ns");
        Assert.True(keyedNs < limit, $"coalescing TrySend took {keyedNs:F1} ns");
    }

    private (double Control, double Realtime, double Keyed) MeasureTrySend()
    {
        var options = new SendQueueOptions { ControlHardCapBytes = 1L << 30, ControlSoftCapBytes = 1L << 30, RealtimeHighWatermarkBytes = 1L << 30, RealtimeLowWatermarkBytes = 1L << 30 };
        var q = new SendQueue(options);
        var control = F(Lane.Control, 56);
        var realtime = F(Lane.Realtime, 56, type: MsgType.PlayerState); // key 0: plain append
        var keyed = F(Lane.Realtime, 56, key: 42, type: MsgType.Replication); // coalescing path

        const int batch = 1000;
        const int rounds = 300;

        double MeasureNs(Func<SendResult> send, Action drain)
        {
            // Warm-up grows rings and dictionaries, and lets tiered JIT promote the path.
            for (int w = 0; w < 50; w++)
            {
                for (int i = 0; i < batch; i++)
                {
                    send();
                }

                drain();
            }

            // Best of the rounds, not the mean of all of them (same idea as the ReplicationBench fix in M2): one round is ~1000 sends (tens of
            // microseconds), so a descheduled thread or a noisy neighbour spoils a few rounds and a mean over 300 of them on a loaded machine
            // reads 2-5x too slow. The fastest round is what the code can do; a real regression slows EVERY round, so the limit still bites.
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            long bestTicks = long.MaxValue;
            for (int r = 0; r < rounds; r++)
            {
                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < batch; i++)
                {
                    send();
                }

                bestTicks = Math.Min(bestTicks, Stopwatch.GetTimestamp() - t0);
                drain();
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            // A per-send allocation would show up as >= 300,000 bytes here. A few KB are runtime noise (tiered-JIT
            // tier-up and OSR can allocate on this thread mid-measurement), which made an exact 0 flaky.
            Assert.True(allocated < 16 * 1024, $"TrySend allocated {allocated} bytes over {batch * rounds} sends");
            return bestTicks * 1e9 / Stopwatch.Frequency / batch;
        }

        void Drain()
        {
            while (q.TryDequeue(out var f))
            {
                f.Release();
            }
        }

        double ctlNs = MeasureNs(() => q.TrySend(control), Drain);
        double rtNs = MeasureNs(() => q.TrySend(realtime), Drain);
        double keyedNs = MeasureNs(() => q.TrySend(keyed), Drain);
        output.WriteLine($"TrySend ns/op: control={ctlNs:F1} realtime={rtNs:F1} realtime-coalescing={keyedNs:F1}");

        return (ctlNs, rtNs, keyedNs);
    }

    private static void Release(IEnumerable<OutboundFrame> frames)
    {
        foreach (var f in frames)
        {
            f.Release();
        }
    }
}
