using System.Globalization;
using Serilog.Events;
using X4MP.Server.Api;
using X4MP.Server.Logging;

namespace X4MP.Server.Hubs;

/// <summary>Reads the log ring buffer for the hub: filtering and the mapping to <see cref="LogEntryDto"/> (same shape as <c>GET /logs</c>).</summary>
internal static class LogTail
{
    /// <summary>Parses a client's filter; null when the level is not a Serilog level name.</summary>
    public static LogTailFilter? Parse(LogFilterDto? filter, out string? error)
    {
        error = null;
        if (filter is null)
        {
            return LogTailFilter.None;
        }

        LogEventLevel? minimum = null;
        if (!string.IsNullOrWhiteSpace(filter.Level))
        {
            if (!Enum.TryParse<LogEventLevel>(filter.Level.Trim(), ignoreCase: true, out var parsed))
            {
                error = "Use one of: " + string.Join(", ", Enum.GetNames<LogEventLevel>()) + ".";
                return null;
            }

            minimum = parsed;
        }

        return new LogTailFilter(
            minimum,
            string.IsNullOrWhiteSpace(filter.Source) ? null : filter.Source.Trim(),
            string.IsNullOrWhiteSpace(filter.Q) ? null : filter.Q.Trim());
    }

    private static string Unquote(LogEventPropertyValue value) =>
        value is ScalarValue { Value: string s } ? s : value.ToString();

    /// <summary>Maps the event when it passes the filter; null otherwise.</summary>
    public static LogEntryDto? Map(LogEvent entry, long seq, LogTailFilter filter)
    {
        if (filter.Minimum is { } min && entry.Level < min)
        {
            return null;
        }

        string source = entry.Properties.TryGetValue("SourceContext", out var sc) ? Unquote(sc) : string.Empty;
        if (filter.Source is not null && source.IndexOf(filter.Source, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return null;
        }

        string message = entry.RenderMessage(CultureInfo.InvariantCulture);
        if (filter.Query is not null && message.IndexOf(filter.Query, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return null;
        }

        return new LogEntryDto(
            seq, entry.Timestamp, entry.Level.ToString(), source, message, entry.Exception?.ToString(),
            entry.Properties.Where(p => p.Key != "SourceContext").ToDictionary(p => p.Key, p => Unquote(p.Value), StringComparer.Ordinal));
    }

    /// <summary>The newest <paramref name="count"/> matching lines (oldest first) and the highest sequence number seen (matching or not).</summary>
    public static (List<LogEntryDto> Entries, long LastSeq) Backfill(RingBufferSink ring, LogTailFilter filter, int count)
    {
        var (firstSeq, events) = ring.SnapshotWithSequence();
        var result = new List<LogEntryDto>();
        for (int i = events.Count - 1; i >= 0 && result.Count < count; i--)
        {
            if (Map(events[i], firstSeq + i, filter) is { } dto)
            {
                result.Add(dto);
            }
        }

        result.Reverse();
        return (result, firstSeq + events.Count - 1);
    }

    /// <summary>
    /// Lines newer than <paramref name="cursor"/> that pass <paramref name="filter"/>, at most <paramref name="max"/>. Returns the new cursor:
    /// when the batch is full it stops right after the last line it took, so the next call continues there.
    /// </summary>
    public static (List<LogEntryDto> Entries, long Cursor) Since(
        IReadOnlyList<LogEvent> events, long firstSeq, long cursor, LogTailFilter filter, int max)
    {
        var batch = new List<LogEntryDto>();
        long lastSeq = firstSeq + events.Count - 1;
        long next = cursor;
        for (long seq = Math.Max(cursor + 1, firstSeq); seq <= lastSeq; seq++)
        {
            if (Map(events[(int)(seq - firstSeq)], seq, filter) is { } dto)
            {
                batch.Add(dto);
            }

            next = seq;
            if (batch.Count >= max)
            {
                break;
            }
        }

        // Lines that fell out of the ring before we read them are lost; the cursor still jumps over them.
        return (batch, Math.Max(next, Math.Min(cursor, lastSeq)));
    }
}
