using Serilog.Core;
using Serilog.Events;

namespace X4MP.Server.Logging;

/// <summary>
/// Bounded, thread-safe in-memory sink that keeps the last N log events for the GUI live tail.
/// Oldest events are overwritten once <see cref="Capacity"/> is reached.
/// </summary>
public sealed class RingBufferSink : ILogEventSink
{
    private readonly object _gate = new();
    private readonly LogEvent[] _buffer;
    private int _next;      // index of the slot the next event is written to
    private int _count;
    private long _total;

    public RingBufferSink(int capacity = 20_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _buffer = new LogEvent[capacity];
    }

    public int Capacity => _buffer.Length;

    public int Count
    {
        get { lock (_gate) { return _count; } }
    }

    /// <summary>Total events ever written (monotonic); lets a tail detect how many it missed.</summary>
    public long TotalWritten
    {
        get { lock (_gate) { return _total; } }
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        lock (_gate)
        {
            _buffer[_next] = logEvent;
            _next = (_next + 1) % _buffer.Length;
            if (_count < _buffer.Length)
            {
                _count++;
            }
            _total++;
        }
    }

    /// <summary>Returns a copy of the buffered events, oldest first.</summary>
    public IReadOnlyList<LogEvent> Snapshot()
    {
        lock (_gate)
        {
            var result = new LogEvent[_count];
            var start = (_next - _count + _buffer.Length) % _buffer.Length;
            for (var i = 0; i < _count; i++)
            {
                result[i] = _buffer[(start + i) % _buffer.Length];
            }
            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_buffer);
            _next = 0;
            _count = 0;
        }
    }
}
