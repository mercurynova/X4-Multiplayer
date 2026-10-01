using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using X4MP.Core.Net;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Metrics;

/// <summary>
/// The server's <see cref="System.Diagnostics.Metrics"/> instruments (server-design 2.11), meter <c>X4MP.Server</c>:
/// <c>dotnet-counters monitor X4MP.Server</c> shows them. The same calls also keep plain cumulative totals that
/// <see cref="MetricsSampler"/> turns into rates, because a <see cref="Counter{T}"/> cannot be read back.
/// Everything here is lock-free and allocation-free on the hot path.
/// </summary>
/// <remarks>
/// Static on purpose: <see cref="ConnectionStats"/> and the send queues are created without DI. In a test process
/// several hosts share these counters, so tests assert on deltas.
/// </remarks>
public static class ServerMetrics
{
    public const string MeterName = "X4MP.Server";

    public static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly string[] LaneNames = ["control", "realtime", "bulk"];
    private static readonly KeyValuePair<string, object?>[] DirIn = [new("dir", "in")];
    private static readonly KeyValuePair<string, object?>[] DirOut = [new("dir", "out")];
    private static readonly KeyValuePair<string, object?>[][] LaneTags =
        [.. LaneNames.Select(n => new[] { new KeyValuePair<string, object?>("lane", n) })];

    private static readonly Counter<long> BytesCounter = Meter.CreateCounter<long>(
        "x4mp.net.bytes", "By", "Bytes on the wire per direction and lane");

    private static readonly Counter<long> FramesCounter = Meter.CreateCounter<long>(
        "x4mp.net.frames", "{frame}", "Frames per direction");

    private static readonly Counter<long> DroppedCounter = Meter.CreateCounter<long>(
        "x4mp.net.dropped", "{frame}", "Frames refused because a lane was over its watermark or cap");

    private static readonly Counter<long> CoalescedCounter = Meter.CreateCounter<long>(
        "x4mp.net.coalesced", "{frame}", "Realtime frames replaced by a newer frame with the same key");

    private static readonly Counter<long> ViolationsCounter = Meter.CreateCounter<long>(
        "x4mp.net.violations", "{violation}", "Protocol or policy violations");

    private static readonly Counter<long> HandshakesCounter = Meter.CreateCounter<long>(
        "x4mp.net.handshakes", "{handshake}", "Handshake outcomes by result (ok or the refusal code)");

    private static readonly Counter<long> DisconnectsCounter = Meter.CreateCounter<long>(
        "x4mp.net.disconnects", "{connection}", "Connections closed by the server, by reason");

    private static readonly Counter<long> EventsPublished = Meter.CreateCounter<long>(
        "x4mp.events.published", "{event}", "Domain events published on the event bus");

    private static readonly Counter<long> EventsDropped = Meter.CreateCounter<long>(
        "x4mp.events.dropped", "{event}", "Domain events dropped because a subscriber was full");

    private static readonly Histogram<double> NodeRtt = Meter.CreateHistogram<double>(
        "x4mp.session.rtt", "ms", "Round-trip time of server Ping/Pong exchanges with nodes");

    private static readonly long[] BytesInByLane = new long[3];
    private static readonly long[] BytesOutByLane = new long[3];
    private static readonly long[] DroppedByLane = new long[3];
    private static long _framesIn, _framesOut, _coalesced, _violations, _handshakesOk, _handshakesRefused, _disconnects;
    private static long _eventsPublished, _eventsDropped;

    private static readonly ConcurrentDictionary<long, INodeConnection> Connections = new();

    static ServerMetrics()
    {
        Meter.CreateObservableGauge("x4mp.net.connections", () => Connections.Count, "{connection}", "Open node connections");
        Meter.CreateObservableGauge(
            "x4mp.net.send_queue_bytes",
            () => new[]
            {
                new Measurement<long>(QueuedBytes(Lane.Control), LaneTags[0]),
                new Measurement<long>(QueuedBytes(Lane.Realtime), LaneTags[1]),
                new Measurement<long>(QueuedBytes(Lane.Bulk), LaneTags[2]),
            },
            "By",
            "Bytes waiting in the send queues, summed over connections, per lane");
    }

    // --- recording (called from the net core) ---

    public static void RecordReceived(Lane lane, int bytes)
    {
        Interlocked.Add(ref BytesInByLane[(int)lane], bytes);
        Interlocked.Increment(ref _framesIn);
        BytesCounter.Add(bytes, DirIn[0], LaneTags[(int)lane][0]);
        FramesCounter.Add(1, DirIn[0]);
    }

    public static void RecordSent(Lane lane, int bytes)
    {
        Interlocked.Add(ref BytesOutByLane[(int)lane], bytes);
        Interlocked.Increment(ref _framesOut);
        BytesCounter.Add(bytes, DirOut[0], LaneTags[(int)lane][0]);
        FramesCounter.Add(1, DirOut[0]);
    }

    public static void RecordDropped(Lane lane)
    {
        Interlocked.Increment(ref DroppedByLane[(int)lane]);
        DroppedCounter.Add(1, LaneTags[(int)lane][0], new KeyValuePair<string, object?>("reason", lane == Lane.Realtime ? "high_watermark" : "cap"));
    }

    public static void RecordCoalesced()
    {
        Interlocked.Increment(ref _coalesced);
        CoalescedCounter.Add(1);
    }

    public static void RecordViolation()
    {
        Interlocked.Increment(ref _violations);
        ViolationsCounter.Add(1);
    }

    /// <summary>One handshake outcome: <see cref="DisconnectCode"/> for a refusal.</summary>
    public static void RecordHandshakeRefused(DisconnectCode code)
    {
        Interlocked.Increment(ref _handshakesRefused);
        HandshakesCounter.Add(1, new KeyValuePair<string, object?>("result", code.ToString()));
    }

    public static void RecordHandshakeOk()
    {
        Interlocked.Increment(ref _handshakesOk);
        HandshakesCounter.Add(1, new KeyValuePair<string, object?>("result", "ok"));
    }

    public static void RecordDisconnect(DisconnectCode reason)
    {
        Interlocked.Increment(ref _disconnects);
        DisconnectsCounter.Add(1, new KeyValuePair<string, object?>("reason", reason.ToString()));
    }

    /// <summary>One Ping/Pong round trip with a node (the SessionActor calls it per accepted sample).</summary>
    public static void RecordRtt(double milliseconds) => NodeRtt.Record(milliseconds);

    public static void RecordEventPublished()
    {
        Interlocked.Increment(ref _eventsPublished);
        EventsPublished.Add(1);
    }

    public static void RecordEventDropped(string subscriber)
    {
        Interlocked.Increment(ref _eventsDropped);
        EventsDropped.Add(1, new KeyValuePair<string, object?>("subscriber", subscriber));
    }

    /// <summary>Starts counting a connection in the connections and send-queue gauges. Pair with <see cref="UntrackConnection"/>.</summary>
    public static void TrackConnection(INodeConnection connection) => Connections[connection.Id.Value] = connection;

    public static void UntrackConnection(INodeConnection connection) => Connections.TryRemove(connection.Id.Value, out _);

    // --- reading (sampler, tests) ---

    public static int ConnectionCount => Connections.Count;

    public static long QueuedBytes(Lane lane)
    {
        long total = 0;
        foreach (var connection in Connections.Values)
        {
            total += connection.QueuedBytes(lane);
        }

        return total;
    }

    public static long BytesIn(Lane lane) => Interlocked.Read(ref BytesInByLane[(int)lane]);

    public static long BytesOut(Lane lane) => Interlocked.Read(ref BytesOutByLane[(int)lane]);

    public static long Dropped => Interlocked.Read(ref DroppedByLane[0]) + Interlocked.Read(ref DroppedByLane[1]) + Interlocked.Read(ref DroppedByLane[2]);

    public static long FramesIn => Interlocked.Read(ref _framesIn);

    public static long FramesOut => Interlocked.Read(ref _framesOut);

    public static long Coalesced => Interlocked.Read(ref _coalesced);

    public static long Violations => Interlocked.Read(ref _violations);

    public static long HandshakesOk => Interlocked.Read(ref _handshakesOk);

    public static long HandshakesRefused => Interlocked.Read(ref _handshakesRefused);

    public static long Disconnects => Interlocked.Read(ref _disconnects);

    public static long EventsPublishedTotal => Interlocked.Read(ref _eventsPublished);

    public static long EventsDroppedTotal => Interlocked.Read(ref _eventsDropped);
}
