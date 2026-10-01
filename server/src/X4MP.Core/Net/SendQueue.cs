using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Net;

/// <summary>Caps and watermarks of one <see cref="SendQueue"/> (ADR-026, server-design 2.3).</summary>
public sealed record SendQueueOptions
{
    public long ControlSoftCapBytes { get; init; } = 8L * 1024 * 1024;
    public long ControlHardCapBytes { get; init; } = 32L * 1024 * 1024;
    public TimeSpan SlowConsumerTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public long RealtimeLowWatermarkBytes { get; init; } = 64 * 1024;
    public long RealtimeHighWatermarkBytes { get; init; } = 256 * 1024;
    public long BulkCapBytes { get; init; } = 16L * 1024 * 1024;

    public static SendQueueOptions From(NetOptions net)
    {
        ArgumentNullException.ThrowIfNull(net);
        return new SendQueueOptions
        {
            ControlSoftCapBytes = net.ControlSoftCapBytes,
            ControlHardCapBytes = net.ControlHardCapBytes,
            SlowConsumerTimeout = TimeSpan.FromSeconds(net.SlowConsumerTimeoutSeconds),
            RealtimeLowWatermarkBytes = net.RealtimeLowWatermarkBytes,
            RealtimeHighWatermarkBytes = net.RealtimeHighWatermarkBytes,
            BulkCapBytes = net.BulkCapBytes,
        };
    }
}

/// <summary>
/// The per-connection outbound queue (server-design 2.3): three lanes drained in strict priority order
/// Control, Realtime, Bulk by ONE consumer (the writer loop). Any number of producer threads call
/// <see cref="TrySend"/>, which is O(1), takes a very short uncontended-in-practice lock, never awaits and,
/// in steady state, never allocates.
/// <list type="bullet">
/// <item>Control: reliable FIFO. Soft cap is a hint (<see cref="ControlOverSoftCap"/>); the hard cap, or an
/// oldest frame older than the slow-consumer timeout, seals the queue and raises the overflow callback
/// (the connection then closes with SlowConsumer).</item>
/// <item>Realtime: a frame with the same non-zero coalesce key as a still-pending frame replaces it
/// in place (latest wins). Above the high watermark new frames are dropped (<see cref="SendResult.DroppedLane"/>).</item>
/// <item>Bulk: drained only when Control and Realtime are empty; dropped above a safety cap.</item>
/// </list>
/// The queue takes its own reference to every accepted frame; the consumer releases the reference it
/// receives from <see cref="TryDequeue"/>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Name fixed by server-design 2.3 and the roadmap.")]
public sealed class SendQueue
{
    private struct Entry
    {
        public OutboundFrame Frame;
        public long Timestamp;
        public ulong Key;
    }

    /// <summary>Growable circular buffer (capacity is a power of two).</summary>
    private sealed class Ring
    {
        private Entry[] _items = new Entry[64];
        private int _head;
        private long _popped;

        public int Count { get; private set; }

        public long Bytes { get; set; }

        /// <summary>Sequence number the next pushed entry will get.</summary>
        public long NextSeq => _popped + Count;

        public long HeadSeq => _popped;

        public ref Entry Head => ref _items[_head];

        public long Push(in Entry entry)
        {
            if (Count == _items.Length)
            {
                var bigger = new Entry[_items.Length * 2];
                for (int i = 0; i < Count; i++)
                {
                    bigger[i] = _items[(_head + i) & (_items.Length - 1)];
                }

                _items = bigger;
                _head = 0;
            }

            long seq = NextSeq;
            _items[(_head + Count) & (_items.Length - 1)] = entry;
            Count++;
            Bytes += entry.Frame.Length;
            return seq;
        }

        public ref Entry At(long seq) => ref _items[(_head + (int)(seq - _popped)) & (_items.Length - 1)];

        public Entry Pop()
        {
            Entry e = _items[_head];
            _items[_head] = default;
            _head = (_head + 1) & (_items.Length - 1);
            Count--;
            _popped++;
            Bytes -= e.Frame.Length;
            return e;
        }
    }

    private readonly object _gate = new();
    private readonly Ring _control = new();
    private readonly Ring _realtime = new();
    private readonly Ring _bulk = new();
    private readonly Dictionary<ulong, long> _realtimeKeys = [];
    private TaskCompletionSource? _waiter;
    private readonly SendQueueOptions _options;
    private readonly TimeProvider _time;
    private readonly ConnectionStats _stats;
    private readonly Action<DisconnectCode>? _onOverflow;
    private bool _sealed;
    private bool _overflowed;

    public SendQueue(SendQueueOptions options, TimeProvider? time = null, ConnectionStats? stats = null, Action<DisconnectCode>? onOverflow = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _time = time ?? TimeProvider.System;
        _stats = stats ?? new ConnectionStats();
        _onOverflow = onOverflow;
    }

    public ConnectionStats Stats => _stats;

    /// <summary>True while the Realtime lane is below its low watermark (pull-model hint).</summary>
    public bool CanAcceptRealtime
    {
        get
        {
            lock (_gate)
            {
                return !_sealed && !_overflowed && _realtime.Bytes < _options.RealtimeLowWatermarkBytes;
            }
        }
    }

    /// <summary>True once the Control lane passed its soft cap.</summary>
    public bool ControlOverSoftCap
    {
        get
        {
            lock (_gate)
            {
                return _control.Bytes >= _options.ControlSoftCapBytes;
            }
        }
    }

    /// <summary>True after the queue overflowed (hard cap or age) or was completed.</summary>
    public bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _sealed || _overflowed;
            }
        }
    }

    /// <summary>Bytes pending on a lane (queued, not yet handed to the writer).</summary>
    public long PendingBytes(Lane lane)
    {
        lock (_gate)
        {
            return RingFor(lane).Bytes;
        }
    }

    /// <summary>Frames pending on a lane.</summary>
    public int PendingFrames(Lane lane)
    {
        lock (_gate)
        {
            return RingFor(lane).Count;
        }
    }

    /// <summary>Age of the oldest pending Control frame, or zero if none.</summary>
    public TimeSpan OldestControlAge
    {
        get
        {
            lock (_gate)
            {
                return _control.Count == 0 ? TimeSpan.Zero : _time.GetElapsedTime(_control.Head.Timestamp);
            }
        }
    }

    private Ring RingFor(Lane lane) => lane switch
    {
        Lane.Control => _control,
        Lane.Realtime => _realtime,
        _ => _bulk,
    };

    /// <summary>See the class remarks. Never blocks, never throws for flow control.</summary>
    public SendResult TrySend(OutboundFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        SendResult result;
        OutboundFrame? replaced = null;
        TaskCompletionSource? wake = null;
        bool overflow = false;

        lock (_gate)
        {
            if (_sealed || _overflowed)
            {
                return SendResult.Closed;
            }

            int len = frame.Length;
            switch (frame.Lane)
            {
                case Lane.Control:
                {
                    long now = _time.GetTimestamp();
                    if (_control.Count > 0 && _time.GetElapsedTime(_control.Head.Timestamp, now) > _options.SlowConsumerTimeout)
                    {
                        _overflowed = true;
                        overflow = true;
                        result = SendResult.ClosedOverflow;
                    }
                    else if (_control.Bytes + len > _options.ControlHardCapBytes)
                    {
                        _overflowed = true;
                        overflow = true;
                        result = SendResult.ClosedOverflow;
                    }
                    else
                    {
                        _control.Push(new Entry { Frame = frame.AddRef(), Timestamp = now });
                        _stats.ObserveQueued(Lane.Control, _control.Bytes);
                        result = SendResult.Queued;
                    }

                    break;
                }

                case Lane.Realtime:
                {
                    ulong key = frame.CoalesceKey;
                    if (key != 0 && _realtimeKeys.TryGetValue(key, out long seq))
                    {
                        ref Entry slot = ref _realtime.At(seq);
                        long newBytes = _realtime.Bytes - slot.Frame.Length + len;
                        if (newBytes > _options.RealtimeHighWatermarkBytes)
                        {
                            _stats.AddDropped(Lane.Realtime);
                            return SendResult.DroppedLane;
                        }

                        replaced = slot.Frame;
                        slot.Frame = frame.AddRef();
                        _realtime.Bytes = newBytes;
                        _stats.AddCoalesced(Lane.Realtime);
                        _stats.ObserveQueued(Lane.Realtime, newBytes);
                        result = SendResult.Coalesced;
                    }
                    else if (_realtime.Bytes + len > _options.RealtimeHighWatermarkBytes)
                    {
                        _stats.AddDropped(Lane.Realtime);
                        return SendResult.DroppedLane;
                    }
                    else
                    {
                        long newSeq = _realtime.Push(new Entry { Frame = frame.AddRef(), Key = key });
                        if (key != 0)
                        {
                            _realtimeKeys[key] = newSeq;
                        }

                        _stats.ObserveQueued(Lane.Realtime, _realtime.Bytes);
                        result = SendResult.Queued;
                    }

                    break;
                }

                default:
                {
                    if (_bulk.Bytes + len > _options.BulkCapBytes)
                    {
                        _stats.AddDropped(Lane.Bulk);
                        return SendResult.DroppedLane;
                    }

                    _bulk.Push(new Entry { Frame = frame.AddRef() });
                    _stats.ObserveQueued(Lane.Bulk, _bulk.Bytes);
                    result = SendResult.Queued;
                    break;
                }
            }

            if (!overflow && _waiter is not null)
            {
                wake = _waiter;
                _waiter = null;
            }
        }

        replaced?.Release();
        wake?.TrySetResult();

        if (overflow)
        {
            RaiseOverflow();
        }

        return result;
    }

    /// <summary>
    /// Called periodically by the connection (the writer may be stuck in a flush): closes with
    /// SlowConsumer if the oldest Control frame has waited longer than the timeout.
    /// </summary>
    public void CheckSlowConsumer()
    {
        bool overflow = false;
        lock (_gate)
        {
            if (!_sealed && !_overflowed && _control.Count > 0
                && _time.GetElapsedTime(_control.Head.Timestamp) > _options.SlowConsumerTimeout)
            {
                _overflowed = true;
                overflow = true;
            }
        }

        if (overflow)
        {
            RaiseOverflow();
        }
    }

    private void WakeWriter()
    {
        TaskCompletionSource? wake;
        lock (_gate)
        {
            wake = _waiter;
            _waiter = null;
        }

        wake?.TrySetResult();
    }

    private void RaiseOverflow()
    {
        WakeWriter(); // Complete() follows from the callback
        _onOverflow?.Invoke(DisconnectCode.SlowConsumer);
    }

    /// <summary>
    /// Writer side. Removes the next frame in priority order. The caller owns the returned reference and
    /// must <see cref="OutboundFrame.Release"/> it.
    /// </summary>
    public bool TryDequeue(out OutboundFrame frame)
    {
        lock (_gate)
        {
            if (_control.Count > 0)
            {
                frame = _control.Pop().Frame;
                return true;
            }

            if (_realtime.Count > 0)
            {
                Entry e = _realtime.Pop();
                if (e.Key != 0 && _realtimeKeys.TryGetValue(e.Key, out long seq) && seq == _realtime.HeadSeq - 1)
                {
                    _realtimeKeys.Remove(e.Key);
                }

                frame = e.Frame;
                return true;
            }

            if (_bulk.Count > 0)
            {
                frame = _bulk.Pop().Frame;
                return true;
            }
        }

        frame = null!;
        return false;
    }

    /// <summary>
    /// Writer side. Completes with true when a frame is pending, false when the queue is sealed and empty
    /// (or overflowed). Throws <see cref="OperationCanceledException"/> if <paramref name="ct"/> fires.
    /// </summary>
    public async ValueTask<bool> WaitToReadAsync(CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_control.Count > 0 || _realtime.Count > 0 || _bulk.Count > 0)
                {
                    return true;
                }

                if (_sealed || _overflowed)
                {
                    return false;
                }

                _waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _waiter.Task;
            }

            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Seals the queue: later <see cref="TrySend"/> calls return <see cref="SendResult.Closed"/>. Pending
    /// Realtime and Bulk frames are dropped; pending Control frames are kept unless
    /// <paramref name="discardControl"/>. <paramref name="final"/> (typically a Disconnect) is appended to
    /// the Control lane regardless of caps. Idempotent.
    /// </summary>
    public void Complete(bool discardControl, OutboundFrame? final = null)
    {
        List<OutboundFrame>? dropped = null;
        lock (_gate)
        {
            if (_sealed)
            {
                return;
            }

            _sealed = true;
            dropped = [];
            Drain(_realtime, dropped);
            _realtimeKeys.Clear();
            Drain(_bulk, dropped);
            if (discardControl)
            {
                Drain(_control, dropped);
            }

            if (final is not null)
            {
                _control.Push(new Entry { Frame = final.AddRef(), Timestamp = _time.GetTimestamp() });
            }

        }

        foreach (var f in dropped)
        {
            f.Release();
        }

        WakeWriter();
    }

    private static void Drain(Ring ring, List<OutboundFrame> into)
    {
        while (ring.Count > 0)
        {
            into.Add(ring.Pop().Frame);
        }
    }
}
