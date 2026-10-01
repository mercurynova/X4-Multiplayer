using X4MP.Protocol;

namespace X4MP.Core.Net;

/// <summary>Per-connection counters. Writers use Interlocked; readers may see slightly stale values.</summary>
public sealed class ConnectionStats
{
    private long _bytesReceived, _bytesSent, _framesReceived, _framesSent, _coalesced, _dropped, _violations;
    private long _flushCount, _flushTicksTotal, _flushTicksMax;
    private readonly long[] _laneDropped = new long[3];
    private readonly long[] _laneCoalesced = new long[3];
    private readonly long[] _laneMaxQueuedBytes = new long[3];

    public long BytesReceived => Interlocked.Read(ref _bytesReceived);
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long FramesReceived => Interlocked.Read(ref _framesReceived);
    public long FramesSent => Interlocked.Read(ref _framesSent);

    /// <summary>Realtime frames replaced by a newer frame with the same coalesce key.</summary>
    public long FramesCoalesced => Interlocked.Read(ref _coalesced);

    /// <summary>Frames refused because their lane was over its high watermark or cap.</summary>
    public long FramesDropped => Interlocked.Read(ref _dropped);

    /// <summary>Protocol or policy violations counted against this connection.</summary>
    public long Violations => Interlocked.Read(ref _violations);

    public long FlushCount => Interlocked.Read(ref _flushCount);

    /// <summary>Sum of <see cref="System.Diagnostics.Stopwatch"/> ticks spent awaiting flushes.</summary>
    public long FlushTicksTotal => Interlocked.Read(ref _flushTicksTotal);

    public long FlushTicksMax => Interlocked.Read(ref _flushTicksMax);

    public long Dropped(Lane lane) => Interlocked.Read(ref _laneDropped[(int)lane]);

    public long Coalesced(Lane lane) => Interlocked.Read(ref _laneCoalesced[(int)lane]);

    /// <summary>High-water mark of queued bytes on a lane.</summary>
    public long MaxQueuedBytes(Lane lane) => Interlocked.Read(ref _laneMaxQueuedBytes[(int)lane]);

    public void AddReceived(int bytes)
    {
        Interlocked.Add(ref _bytesReceived, bytes);
        Interlocked.Increment(ref _framesReceived);
    }

    public void AddSent(int bytes)
    {
        Interlocked.Add(ref _bytesSent, bytes);
        Interlocked.Increment(ref _framesSent);
    }

    public void AddViolation() => Interlocked.Increment(ref _violations);

    internal void AddCoalesced(Lane lane)
    {
        Interlocked.Increment(ref _coalesced);
        Interlocked.Increment(ref _laneCoalesced[(int)lane]);
    }

    internal void AddDropped(Lane lane)
    {
        Interlocked.Increment(ref _dropped);
        Interlocked.Increment(ref _laneDropped[(int)lane]);
    }

    internal void ObserveQueued(Lane lane, long bytes) => RaiseTo(ref _laneMaxQueuedBytes[(int)lane], bytes);

    internal void AddFlush(long ticks)
    {
        Interlocked.Increment(ref _flushCount);
        Interlocked.Add(ref _flushTicksTotal, ticks);
        RaiseTo(ref _flushTicksMax, ticks);
    }

    private static void RaiseTo(ref long slot, long value)
    {
        long seen = Volatile.Read(ref slot);
        while (value > seen)
        {
            long prior = Interlocked.CompareExchange(ref slot, value, seen);
            if (prior == seen)
            {
                return;
            }

            seen = prior;
        }
    }
}
