using System.Diagnostics;
using X4MP.Protocol;

namespace X4MP.Core.Metrics;

public enum MetricKind
{
    /// <summary>The sample is the value read at sampling time.</summary>
    Gauge,

    /// <summary>The probe reads a cumulative total; the sample is its increase per second since the previous sample.</summary>
    Rate,
}

/// <summary>One series the sampler records: a name, a unit and a way to read the current value (or cumulative total).</summary>
public sealed record MetricProbe(string Name, string Unit, MetricKind Kind, Func<double> Read);

/// <summary>A recorded series, oldest sample first.</summary>
public sealed record MetricSeries(string Name, string Unit, MetricKind Kind, int IntervalSeconds, DateTimeOffset? EndedAt, IReadOnlyList<double> Samples);

/// <summary>
/// Samples its probes once per second (the caller drives <see cref="Sample"/>, normally a hosted service) into one
/// ring buffer per series: 600 samples, so 10 minutes (server-design 2.11). Time comes from the
/// <see cref="TimeProvider"/>, so tests step a fake clock.
/// </summary>
public sealed class MetricsSampler
{
    public const int DefaultCapacity = 600;

    private sealed class Series(MetricProbe probe, int capacity)
    {
        public MetricProbe Probe { get; } = probe;
        public double[] Ring { get; } = new double[capacity];
        public int Count { get; set; }
        public int Next { get; set; }
        public double LastTotal { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly int _capacity;
    private readonly List<Series> _series = [];
    private long _lastTimestamp;
    private DateTimeOffset? _lastAt;

    public MetricsSampler(TimeProvider? time = null, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _time = time ?? TimeProvider.System;
        _capacity = capacity;
        _lastTimestamp = _time.GetTimestamp();
    }

    public int Capacity => _capacity;

    /// <summary>A sampler with the standard network probes (connections, bytes, frames, drops, queue depth, process).</summary>
    public static MetricsSampler CreateDefault(TimeProvider? time = null)
    {
        var sampler = new MetricsSampler(time);
        foreach (var probe in DefaultProbes())
        {
            sampler.AddProbe(probe);
        }

        return sampler;
    }

    public static IEnumerable<MetricProbe> DefaultProbes()
    {
        yield return new("net.connections", "connections", MetricKind.Gauge, () => ServerMetrics.ConnectionCount);
        foreach (var lane in Enum.GetValues<Lane>())
        {
            var name = lane.ToString().ToLowerInvariant();
            yield return new($"net.bytes_in.{name}", "B/s", MetricKind.Rate, () => ServerMetrics.BytesIn(lane));
            yield return new($"net.bytes_out.{name}", "B/s", MetricKind.Rate, () => ServerMetrics.BytesOut(lane));
            yield return new($"net.send_queue_bytes.{name}", "B", MetricKind.Gauge, () => ServerMetrics.QueuedBytes(lane));
        }

        yield return new("net.frames_in", "frames/s", MetricKind.Rate, () => ServerMetrics.FramesIn);
        yield return new("net.frames_out", "frames/s", MetricKind.Rate, () => ServerMetrics.FramesOut);
        yield return new("net.dropped", "frames/s", MetricKind.Rate, () => ServerMetrics.Dropped);
        yield return new("net.coalesced", "frames/s", MetricKind.Rate, () => ServerMetrics.Coalesced);
        yield return new("net.violations", "violations/s", MetricKind.Rate, () => ServerMetrics.Violations);
        yield return new("net.handshakes_refused", "handshakes/s", MetricKind.Rate, () => ServerMetrics.HandshakesRefused);
        yield return new("events.dropped", "events/s", MetricKind.Rate, () => ServerMetrics.EventsDroppedTotal);
        yield return new("tick.p50_ms", "ms", MetricKind.Gauge, () => ServerMetrics.TickPercentileMs(50));
        yield return new("tick.p99_ms", "ms", MetricKind.Gauge, () => ServerMetrics.TickPercentileMs(99));
        yield return new("tick.count", "ticks/s", MetricKind.Rate, () => ServerMetrics.TickCount);
        yield return new("process.cpu_percent", "% of one core", MetricKind.Rate, () => Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds * 100.0);
        yield return new("gc.heap_mb", "MB", MetricKind.Gauge, () => GC.GetTotalMemory(false) / (1024.0 * 1024.0));
        yield return new("gc.gen0_collections", "collections/s", MetricKind.Rate, () => GC.CollectionCount(0));
        yield return new("gc.gen2_collections", "collections/s", MetricKind.Rate, () => GC.CollectionCount(2));
        yield return new("gc.allocated_bytes", "B/s", MetricKind.Rate, () => GC.GetTotalAllocatedBytes(false));
        yield return new("process.working_set_mb", "MB", MetricKind.Gauge, () => Environment.WorkingSet / (1024.0 * 1024.0));
    }

    /// <summary>Adds a series. Its first sample is taken at the next <see cref="Sample"/>.</summary>
    public void AddProbe(MetricProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        lock (_gate)
        {
            if (_series.Any(s => s.Probe.Name == probe.Name))
            {
                throw new InvalidOperationException($"Series '{probe.Name}' already exists.");
            }

            _series.Add(new Series(probe, _capacity) { LastTotal = probe.Kind == MetricKind.Rate ? probe.Read() : 0 });
        }
    }

    public IReadOnlyList<string> Names
    {
        get
        {
            lock (_gate)
            {
                return _series.Select(s => s.Probe.Name).ToList();
            }
        }
    }

    /// <summary>Takes one sample of every series. Rates use the elapsed time since the previous call.</summary>
    public void Sample()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            var seconds = _time.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
            _lastTimestamp = now;
            _lastAt = _time.GetUtcNow();
            foreach (var s in _series)
            {
                double value;
                if (s.Probe.Kind == MetricKind.Rate)
                {
                    var total = s.Probe.Read();
                    value = seconds > 0 ? Math.Max(0, (total - s.LastTotal) / seconds) : 0;
                    s.LastTotal = total;
                }
                else
                {
                    value = s.Probe.Read();
                }

                s.Ring[s.Next] = value;
                s.Next = (s.Next + 1) % _capacity;
                if (s.Count < _capacity)
                {
                    s.Count++;
                }
            }
        }
    }

    /// <summary>
    /// The recorded series, oldest sample first. <paramref name="names"/> filters (null or empty = all);
    /// <paramref name="window"/> keeps at most that many of the newest samples.
    /// </summary>
    public IReadOnlyList<MetricSeries> GetSeries(IReadOnlyCollection<string>? names = null, int window = int.MaxValue)
    {
        lock (_gate)
        {
            var result = new List<MetricSeries>();
            foreach (var s in _series)
            {
                if (names is { Count: > 0 } && !names.Contains(s.Probe.Name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var take = Math.Min(s.Count, Math.Max(0, window));
                var samples = new double[take];
                for (int i = 0; i < take; i++)
                {
                    samples[i] = s.Ring[((s.Next - take + i) % _capacity + _capacity) % _capacity];
                }

                result.Add(new MetricSeries(s.Probe.Name, s.Probe.Unit, s.Probe.Kind, 1, _lastAt, samples));
            }

            return result;
        }
    }
}
