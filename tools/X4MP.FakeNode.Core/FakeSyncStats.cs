using System.Globalization;

namespace X4MP.FakeNode;

/// <summary>What one bot received about one player ship: how often entries arrived and how long the longest silences were.</summary>
public sealed record SyncSummary(
    uint NetId, string Name, long Entries, double SpanSeconds, double RateHz, long NearEntries, double NearRateHz,
    double GapP50Ms, double GapP95Ms, double GapMaxMs, double SpeedMaxMps)
{
    /// <summary>One <c>[sync]</c>-style line (the mod logs <c>[sync] net=.. player=.. err_p50/p95/max lat_p95 speed_max</c>; a bot cannot see the true path, so it logs the arrival side).</summary>
    public string ToLine(string bot) => string.Create(CultureInfo.InvariantCulture,
        $"[{bot}] [sync] net={NetId} player={Name} entries={Entries} span_s={SpanSeconds:F1} rate_hz={RateHz:F1} near_entries={NearEntries} near_rate_hz={NearRateHz:F1} " +
        $"gap_ms p50/p95/max={GapP50Ms:F0}/{GapP95Ms:F0}/{GapMaxMs:F0} speed_max={SpeedMaxMps:F0}");
}

/// <summary>
/// Arrival statistics of the Replication entries of one player ship (m3-plan 4.5: Near 20 Hz). "Near" means the ship was in the receiver's sector and
/// within 15 km when the entry arrived. <c>near_rate</c> counts only the gaps between two consecutive near entries that are shorter than
/// <see cref="FlowGapSeconds"/>, so a gate jump or a sector change (a pause by design) does not drag the rate down; the gap percentiles and the maximum
/// look at every gap and show those pauses.
/// </summary>
public sealed class FakeSyncStats
{
    /// <summary>Gaps up to this long count as "flowing" for the rate.</summary>
    public const double FlowGapSeconds = 0.25;

    /// <summary>Entries are kept for the percentiles up to this many (a 20 Hz stream is 72 000 per hour); beyond it only the counters grow.</summary>
    private const int MaxSamples = 100_000;

    private readonly object _gate = new();
    private readonly List<float> _gaps = [];
    private double _first = double.NaN;
    private double _last = double.NaN;
    private double _lastNear = double.NaN;
    private long _entries;
    private long _nearEntries;
    private long _flowPairs;
    private double _flowSeconds;
    private double _speedMax;

    public void Record(double now, bool near, double speedMps)
    {
        lock (_gate)
        {
            _entries++;
            if (double.IsNaN(_first))
                _first = now;
            if (!double.IsNaN(_last) && _gaps.Count < MaxSamples)
                _gaps.Add((float)(now - _last));
            _last = now;
            if (speedMps > _speedMax)
                _speedMax = speedMps;
            if (near)
            {
                _nearEntries++;
                if (!double.IsNaN(_lastNear) && now - _lastNear < FlowGapSeconds)
                {
                    _flowPairs++;
                    _flowSeconds += now - _lastNear;
                }

                _lastNear = now;
            }
            else
            {
                _lastNear = double.NaN; // the stream left the near set: the next near entry starts a new run
            }
        }
    }

    public SyncSummary Summarize(uint netId, string name)
    {
        lock (_gate)
        {
            var sorted = _gaps.Order().ToArray();
            double Pct(double p) => sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)] * 1000.0;
            double span = double.IsNaN(_first) ? 0 : _last - _first;
            return new SyncSummary(
                netId, name, _entries, span, span > 0 ? (_entries - 1) / span : 0, _nearEntries,
                _flowSeconds > 0 ? _flowPairs / _flowSeconds : 0,
                Pct(0.5), Pct(0.95), sorted.Length == 0 ? 0 : sorted[^1] * 1000.0, _speedMax);
        }
    }
}
