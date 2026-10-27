using Microsoft.Extensions.Logging;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Transport.Recording;

/// <summary>How fast a <see cref="RecordingReplayer"/> feeds the recorded stream.</summary>
public enum ReplayMode
{
    /// <summary>Ignore the timestamps and deliver as fast as the sink accepts (tests, mirror runs).</summary>
    AsFastAsPossible = 0,

    /// <summary>Honour the original spacing, scaled by <see cref="RecordingReplayer.Speed"/>.</summary>
    RealTime = 1,
}

/// <summary>Counts of a recorded or replayed stream, keyed by message type. This is the unit divergence is measured in.</summary>
public sealed class ReplayStats
{
    private readonly Dictionary<MsgType, int> _counts = [];
    private readonly HashSet<uint> _streams = [];

    /// <summary>Messages seen.</summary>
    public int Total { get; private set; }

    /// <summary>Timestamp of the first message.</summary>
    public long FirstTimeUs { get; private set; }

    /// <summary>Timestamp of the last message.</summary>
    public long LastTimeUs { get; private set; }

    /// <summary>Distinct connection ids seen (0 counts as one).</summary>
    public int Streams => _streams.Count;

    /// <summary>Span between the first and the last message.</summary>
    public TimeSpan Duration => TimeSpan.FromMicroseconds(LastTimeUs - FirstTimeUs);

    /// <summary>Per-type counts, ordered by message type for stable output.</summary>
    public IReadOnlyDictionary<MsgType, int> Counts => _counts;

    /// <summary>Count of one message type (0 when absent).</summary>
    public int CountOf(MsgType type) => _counts.TryGetValue(type, out int n) ? n : 0;

    /// <summary>Folds one message into the counters.</summary>
    public void Add(in RecordingRecord record)
    {
        if (Total == 0)
        {
            FirstTimeUs = record.TimeUs;
        }

        LastTimeUs = record.TimeUs;
        Total++;
        _counts[record.Type] = CountOf(record.Type) + 1;
        _streams.Add(record.Stream);
    }

    /// <summary>Folds a whole stream into the counters.</summary>
    public static ReplayStats Of(IEnumerable<RecordingRecord> records)
    {
        var stats = new ReplayStats();
        foreach (var record in records)
        {
            stats.Add(record);
        }

        return stats;
    }

    /// <summary>One line per type, highest count first: the shape a replay log prints.</summary>
    public string Describe()
    {
        var parts = new List<string>(_counts.Count);
        foreach (var pair in _counts.OrderByDescending(static p => p.Value).ThenBy(static p => (ushort)p.Key))
        {
            parts.Add($"{pair.Key}={pair.Value}");
        }

        return $"messages={Total} streams={Streams} span={Duration.TotalSeconds:F1}s {string.Join(' ', parts)}";
    }
}

/// <summary>
/// Feeds a recorded stream into any sink (M4-27; docs/m4-plan.md W6). The sink is a delegate on purpose: a test
/// feeds a <c>FakeNode</c>, a mirror run feeds a second server's gateway, and both use the same pacing code.
/// </summary>
public sealed class RecordingReplayer
{
    private readonly IReadOnlyList<RecordingRecord> _records;
    private readonly TimeProvider _time;

    /// <summary>Creates a replayer over an already-read recording.</summary>
    public RecordingReplayer(IReadOnlyList<RecordingRecord> records, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Pace of <see cref="ReplayMode.RealTime"/>; 2.0 replays twice as fast, 0.5 twice as slow.</summary>
    public double Speed { get; init; } = 1.0;

    /// <summary>Mode used by <see cref="RunAsync(Func{RecordingRecord, ValueTask}, CancellationToken)"/>.</summary>
    public ReplayMode Mode { get; init; } = ReplayMode.AsFastAsPossible;

    /// <summary>Records the sink has accepted.</summary>
    public ReplayStats Stats { get; } = new();

    /// <summary>Replays every record into <paramref name="deliver"/>, honouring <see cref="Mode"/>.</summary>
    public async Task RunAsync(Func<RecordingRecord, ValueTask> deliver, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        long start = _time.GetTimestamp();
        long originUs = _records.Count > 0 ? _records[0].TimeUs : 0;

        foreach (var record in _records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Mode == ReplayMode.RealTime && Speed > 0)
            {
                long dueUs = (long)((record.TimeUs - originUs) / Speed);
                long elapsedUs = (long)_time.GetElapsedTime(start).TotalMicroseconds;
                long waitUs = dueUs - elapsedUs;
                if (waitUs > 0)
                {
                    await Task.Delay(TimeSpan.FromMicroseconds(waitUs), _time, cancellationToken).ConfigureAwait(false);
                }
            }

            await deliver(record).ConfigureAwait(false);
            Stats.Add(record);
        }
    }

    /// <summary>Replays every record into a synchronous sink.</summary>
    public Task RunAsync(Action<RecordingRecord> deliver, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deliver);
        return RunAsync(
            record =>
            {
                deliver(record);
                return ValueTask.CompletedTask;
            },
            cancellationToken);
    }

    /// <summary>
    /// Replays a recording into a live connection: each record is re-encoded into its original frame and pushed
    /// through the same <c>TrySend</c> path a session uses, so a mirror server sees exactly the wire bytes.
    /// </summary>
    public Task RunIntoAsync(X4MP.Core.Net.INodeConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return RunAsync(
            record =>
            {
                var frame = X4MP.Core.Net.OutboundFrame.Create(record.Type, record.Payload, coalesceKey: 0);
                connection.TrySend(frame);
                return ValueTask.CompletedTask;
            },
            cancellationToken);
    }

    /// <summary>Reads a part (or a whole series) and returns a replayer over it.</summary>
    public static RecordingReplayer FromSeries(
        string directory,
        string prefix = RecordingFormat.DefaultPrefix,
        TimeProvider? time = null)
    {
        return new RecordingReplayer(RecordingReader.ReadSeries(directory, prefix), time);
    }
}

/// <summary>
/// Divergence between two streams of the same session (M4-28; docs/m4-plan.md W6): the authority's recording and the
/// one a mirror server produced while consuming it. Both are compared message by message - type, lane, stream and
/// payload bytes - because "same counts" alone would accept a stream that reordered or corrupted messages.
/// </summary>
/// <param name="AuthorityMessages">Messages in the authority stream.</param>
/// <param name="MirrorMessages">Messages in the mirror stream.</param>
/// <param name="Missing">Authority messages the mirror never produced.</param>
/// <param name="Extra">Mirror messages the authority never sent.</param>
/// <param name="FieldMismatches">Messages present in both but differing in type, lane or stream.</param>
/// <param name="PayloadMismatches">Messages with the same header but different payload bytes.</param>
/// <param name="FirstDivergenceIndex">Index of the first differing message, or -1 when identical.</param>
/// <param name="TypeCountDeltas">Per-type count difference, only for types that differ.</param>
public readonly record struct DivergenceReport(
    int AuthorityMessages,
    int MirrorMessages,
    int Missing,
    int Extra,
    int FieldMismatches,
    int PayloadMismatches,
    int FirstDivergenceIndex,
    IReadOnlyDictionary<MsgType, int> TypeCountDeltas)
{
    /// <summary>True when the two streams are message-for-message identical.</summary>
    public bool Identical =>
        Missing == 0 && Extra == 0 && FieldMismatches == 0 && PayloadMismatches == 0;

    /// <summary>Total number of divergent messages, the number a mirror run reports.</summary>
    public int Divergence => Missing + Extra + FieldMismatches + PayloadMismatches;

    /// <summary>One line for the log.</summary>
    public string Describe() =>
        Identical
            ? $"divergence=0 authority={AuthorityMessages} mirror={MirrorMessages}"
            : $"divergence={Divergence} authority={AuthorityMessages} mirror={MirrorMessages} missing={Missing} extra={Extra} fields={FieldMismatches} payload={PayloadMismatches} first_at={FirstDivergenceIndex}";
}

/// <summary>Compares two recordings of the same session and reports how far the mirror drifted.</summary>
public static class RecordingDivergence
{
    private static readonly Action<ILogger, string, Exception?> s_logSame =
        LoggerMessage.Define<string>(
            Microsoft.Extensions.Logging.LogLevel.Information,
            new EventId(3, "MirrorIdentical"),
            "mirror: {Report}");

    private static readonly Action<ILogger, string, Exception?> s_logDrift =
        LoggerMessage.Define<string>(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            new EventId(4, "MirrorDiverged"),
            "mirror: {Report}");

    /// <summary>Message-by-message comparison of an authority recording against a mirror recording.</summary>
    /// <param name="authority">The authority's stream.</param>
    /// <param name="mirror">The mirror's stream.</param>
    /// <param name="compareStreams">
    /// Compare the connection id as well. False for a mirror run: connection ids are assigned per server, so a mirror
    /// legitimately renumbers them while the game-level net ids inside the payloads stay identical.
    /// </param>
    public static DivergenceReport Compare(
        IReadOnlyList<RecordingRecord> authority,
        IReadOnlyList<RecordingRecord> mirror,
        bool compareStreams = true)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(mirror);

        int missing = 0;
        int extra = 0;
        int fields = 0;
        int payload = 0;
        int first = -1;

        int shared = Math.Min(authority.Count, mirror.Count);
        for (int i = 0; i < shared; i++)
        {
            var a = authority[i];
            var b = mirror[i];
            bool sameField = a.Type == b.Type && a.Lane == b.Lane && (!compareStreams || a.Stream == b.Stream);
            bool samePayload = sameField && a.Payload.AsSpan().SequenceEqual(b.Payload);
            if (!sameField)
            {
                fields++;
            }
            else if (!samePayload)
            {
                payload++;
            }

            if (!sameField || !samePayload)
            {
                if (first < 0)
                {
                    first = i;
                }
            }
        }

        if (authority.Count > mirror.Count)
        {
            missing = authority.Count - mirror.Count;
            if (first < 0)
            {
                first = shared;
            }
        }
        else if (mirror.Count > authority.Count)
        {
            extra = mirror.Count - authority.Count;
            if (first < 0)
            {
                first = shared;
            }
        }

        var authorityStats = ReplayStats.Of(authority);
        var mirrorStats = ReplayStats.Of(mirror);
        var deltas = new Dictionary<MsgType, int>();
        foreach (var pair in authorityStats.Counts)
        {
            int delta = pair.Value - mirrorStats.CountOf(pair.Key);
            if (delta != 0)
            {
                deltas[pair.Key] = delta;
            }
        }

        foreach (var pair in mirrorStats.Counts)
        {
            if (!authorityStats.Counts.ContainsKey(pair.Key))
            {
                deltas[pair.Key] = -pair.Value;
            }
        }

        return new DivergenceReport(
            authority.Count,
            mirror.Count,
            missing,
            extra,
            fields,
            payload,
            first,
            deltas);
    }

    /// <summary>Logs the verdict of a mirror run; the caller decides whether divergence is fatal.</summary>
    public static void Log(ILogger? logger, in DivergenceReport report)
    {
        if (logger is null)
        {
            return;
        }

        string line = report.Describe();
        if (report.Identical)
        {
            s_logSame(logger, line, null);
        }
        else
        {
            s_logDrift(logger, line, null);
        }
    }
}
