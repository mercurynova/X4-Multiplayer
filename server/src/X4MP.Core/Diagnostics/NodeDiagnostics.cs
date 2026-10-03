using X4MP.Proto;

namespace X4MP.Core.Diagnostics;

/// <summary>One line a node forwarded (<c>LogForward</c>), as kept in memory.</summary>
public sealed record NodeLogLine(DateTimeOffset At, LogLevel Level, string Text);

/// <summary>What the server holds about one node's diagnostics.</summary>
public sealed record NodeDiagnosticsSnapshot(
    int PlayerId, string Player, IReadOnlyList<NodeLogLine> Lines, SelfTestTable? SelfTest, long LinesReceived, long LinesDropped);

/// <summary>Limits of the per-node log forwarding (protocol: at most 50 lines/s and 16 KiB/s).</summary>
public sealed record NodeDiagnosticsOptions
{
    public double LinesPerSecond { get; init; } = 50;

    public double LineBurst { get; init; } = 100;

    public double BytesPerSecond { get; init; } = 16 * 1024;

    public double ByteBurst { get; init; } = 32 * 1024;

    /// <summary>Lines kept per node.</summary>
    public int KeepLines { get; init; } = 200;

    /// <summary>Longest kept line (longer text is cut).</summary>
    public int MaxLineLength { get; init; } = 1000;

    /// <summary>Lines of one <c>LogForward</c> message that are looked at (the rest count as dropped).</summary>
    public int MaxLinesPerMessage { get; init; } = 300;
}

/// <summary>The outcome of one <c>LogForward</c> batch.</summary>
/// <param name="Accepted">Lines that passed the rate limit (to be written to the server log).</param>
/// <param name="Dropped">Lines the rate limit refused.</param>
public readonly record struct LogIngestResult(IReadOnlyList<NodeLogLine> Accepted, int Dropped);

/// <summary>A token bucket; the caller passes the clock reading (tests pass explicit times).</summary>
public sealed class TokenBucket(double perSecond, double burst)
{
    private readonly double _burst = burst;
    private double _tokens = burst;
    private DateTimeOffset _last;
    private bool _started;

    public bool TryTake(double amount, DateTimeOffset now)
    {
        Refill(now);
        if (_tokens < amount)
        {
            return false;
        }

        _tokens -= amount;
        return true;
    }

    /// <summary>Takes tokens without a check (the balance may go negative, down to -burst), for traffic exempt from the limit that still counts.</summary>
    public void Spend(double amount, DateTimeOffset now)
    {
        Refill(now);
        _tokens = Math.Max(-_burst, _tokens - amount);
    }

    private void Refill(DateTimeOffset now)
    {
        if (_started)
        {
            double elapsed = Math.Max(0, (now - _last).TotalSeconds);
            _tokens = Math.Min(_burst, _tokens + (elapsed * perSecond));
        }

        _started = true;
        _last = now;
    }
}

/// <summary>
/// The server's memory of what nodes forwarded: a rate limit per node (token buckets for lines and bytes), the last N lines and the last
/// complete self-test table (docs/mod-design.md 8.5.1). Thread-safe; <see cref="Changed"/> is raised outside the lock, with the player id,
/// after lines were kept or a table was committed.
/// </summary>
public sealed class NodeDiagnosticsStore(NodeDiagnosticsOptions? options = null)
{
    /// <summary>A self-test row this long after the previous one starts a new table.</summary>
    public static readonly TimeSpan SelfTestGap = TimeSpan.FromSeconds(2);

    private readonly NodeDiagnosticsOptions _o = options ?? new NodeDiagnosticsOptions();
    private readonly Lock _gate = new();
    private readonly Dictionary<int, State> _nodes = [];

    /// <summary>Raised after a batch changed what is held for a player.</summary>
    public event Action<int>? Changed;

    /// <summary>
    /// Takes one batch from a node. Self-test lines are exempt from the line limit (they are bounded by <see cref="SelfTestParser.MaxRows"/>
    /// per table; their bytes are still charged) and are always kept; everything else is limited per node.
    /// </summary>
    public LogIngestResult Ingest(int playerId, string player, IReadOnlyList<(LogLevel Level, string Text)> lines, DateTimeOffset now)
    {
        var accepted = new List<NodeLogLine>(lines.Count);
        int dropped = 0;
        bool changed = false;
        lock (_gate)
        {
            if (!_nodes.TryGetValue(playerId, out var state))
            {
                state = new State(_o);
                _nodes[playerId] = state;
            }

            state.Player = player;
            for (int i = 0; i < lines.Count; i++)
            {
                var (level, raw) = lines[i];
                string text = raw ?? string.Empty;
                if (text.Length > _o.MaxLineLength)
                {
                    text = text[.._o.MaxLineLength];
                }

                if (text.Any(char.IsControl))
                {
                    text = string.Create(text.Length, text, static (span, t) =>
                    {
                        for (int k = 0; k < span.Length; k++)
                        {
                            span[k] = char.IsControl(t[k]) ? ' ' : t[k];
                        }
                    });
                }

                state.Received++;
                bool selfTest = SelfTestParser.IsSelfTestLine(text);
                if (selfTest)
                {
                    state.ChargeBytes(text.Length, now);
                    state.Feed(text, now);
                }
                else if (i >= _o.MaxLinesPerMessage || !state.TryTake(text.Length, now))
                {
                    dropped++;
                    state.Dropped++;
                    continue;
                }

                var line = new NodeLogLine(now, level, text);
                accepted.Add(line);
                state.Keep(line, _o.KeepLines);
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke(playerId);
        }

        return new LogIngestResult(accepted, dropped);
    }

    public NodeDiagnosticsSnapshot? Get(int playerId)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(playerId, out var s) ? s.Snapshot(playerId) : null;
        }
    }

    public IReadOnlyList<NodeDiagnosticsSnapshot> All()
    {
        lock (_gate)
        {
            return [.. _nodes.Select(kv => kv.Value.Snapshot(kv.Key))];
        }
    }

    private sealed class State(NodeDiagnosticsOptions o)
    {
        private readonly Queue<NodeLogLine> _lines = new();
        private readonly List<SelfTestRow> _current = [];
        private DateTimeOffset _lastRow;
        private readonly TokenBucket _lineBucket = new(o.LinesPerSecond, o.LineBurst);
        private readonly TokenBucket _byteBucket = new(o.BytesPerSecond, o.ByteBurst);
        private bool _closed;

        public string Player { get; set; } = string.Empty;

        public long Received { get; set; }

        public long Dropped { get; set; }

        public SelfTestTable? SelfTest { get; private set; }

        public bool TryTake(int length, DateTimeOffset now) => _lineBucket.TryTake(1, now) && _byteBucket.TryTake(length, now);

        public void ChargeBytes(int length, DateTimeOffset now) => _byteBucket.Spend(length, now);

        public void Keep(NodeLogLine line, int max)
        {
            _lines.Enqueue(line);
            while (_lines.Count > max)
            {
                _lines.Dequeue();
            }
        }

        /// <summary>
        /// Applies a self-test line. Rows build the current table, visible at once (the mod writes no end marker: a table is one burst of rows).
        /// A row starts a new table after a begin or summary line, when its check is already in the table, or when the previous row is
        /// older than <see cref="NodeDiagnosticsStore.SelfTestGap"/>.
        /// </summary>
        public void Feed(string text, DateTimeOffset now)
        {
            switch (SelfTestParser.Parse(text, out var row))
            {
                case SelfTestLineKind.Begin:
                case SelfTestLineKind.End:
                    _closed = true;
                    break;
                case SelfTestLineKind.Row:
                    if (_closed || _current.Count == 0 || now - _lastRow > SelfTestGap || _current.Any(r => r.Name == row!.Name))
                    {
                        _current.Clear();
                    }

                    _closed = false;
                    _lastRow = now;
                    if (_current.Count < SelfTestParser.MaxRows)
                    {
                        _current.Add(row!);
                    }

                    SelfTest = new SelfTestTable(now, [.. _current]);
                    break;
            }
        }

        public NodeDiagnosticsSnapshot Snapshot(int playerId) => new(playerId, Player, [.. _lines], SelfTest, Received, Dropped);
    }
}
