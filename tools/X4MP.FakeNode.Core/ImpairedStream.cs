using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace X4MP.FakeNode;

/// <summary>
/// How a node reads its socket slowly (<c>--slow-reader</c>): either a steady rate (<see cref="BytesPerSecond"/>) or a pattern that reads normally
/// for <see cref="Run"/> and then stops reading for <see cref="Pause"/>, repeatedly. Parsed from <c>4096</c> (bytes per second) or
/// <c>pause=3/30</c> (read 3 s, stop 30 s).
/// </summary>
public sealed record SlowReaderSpec(int BytesPerSecond, TimeSpan Run, TimeSpan Pause)
{
    /// <summary>The steady rate, or the pattern, as it was asked for (summary lines).</summary>
    public override string ToString() => BytesPerSecond > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{BytesPerSecond} B/s")
        : string.Create(CultureInfo.InvariantCulture, $"pause={Run.TotalSeconds:0.##}/{Pause.TotalSeconds:0.##}");

    public static bool TryParse(string text, out SlowReaderSpec? spec, out string? error)
    {
        spec = null;
        error = null;
        string value = text.Trim();
        if (value.StartsWith("pause=", StringComparison.OrdinalIgnoreCase) || value.StartsWith("pause:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = value[6..].Split('/');
            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double run) && run >= 0
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double pause) && pause > 0)
            {
                spec = new SlowReaderSpec(0, TimeSpan.FromSeconds(run), TimeSpan.FromSeconds(pause));
                return true;
            }

            error = $"--slow-reader pause pattern must be pause=<read seconds>/<pause seconds> (got '{text}')";
            return false;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int rate) && rate >= 1)
        {
            spec = new SlowReaderSpec(rate, TimeSpan.Zero, TimeSpan.Zero);
            return true;
        }

        error = $"--slow-reader must be a rate in bytes per second or pause=<read s>/<pause s> (got '{text}')";
        return false;
    }
}

/// <summary>
/// Failure injection shared by every connection of one node (<c>--latency</c>, <c>--slow-reader</c>): the settings live here so a reconnect
/// (<c>--disconnect-every</c>) keeps them. The slow reader is switched on with <see cref="ActivateSlowReader"/> once the node is in game, because the
/// join (a save download) would take forever at a few kilobytes a second.
/// </summary>
public sealed class StreamImpairment(TimeSpan latency, TimeSpan jitter, SlowReaderSpec? slow, int seed)
{
    private long _slowSinceTicks;
    private long _bytesRead;
    private long _pausedMs;

    /// <summary>Delay added to every chunk read and written (one way).</summary>
    public TimeSpan Latency { get; } = latency;

    /// <summary>Each chunk's delay varies by up to this much either way (order is preserved on TCP).</summary>
    public TimeSpan Jitter { get; } = jitter;

    public SlowReaderSpec? Slow { get; } = slow;

    public int Seed { get; } = seed;

    /// <summary>True while the node reads slowly.</summary>
    public bool SlowActive => Volatile.Read(ref _slowSinceTicks) != 0;

    /// <summary>How long the node has been reading slowly (zero when it is not).</summary>
    public TimeSpan SlowFor => Volatile.Read(ref _slowSinceTicks) is var since and not 0 ? Stopwatch.GetElapsedTime(since) : TimeSpan.Zero;

    /// <summary>Bytes the node has read since the slow reader was switched on.</summary>
    public long BytesReadSlowly => Interlocked.Read(ref _bytesRead);

    /// <summary>Milliseconds the node spent not reading at all (pause pattern).</summary>
    public long PausedMilliseconds => Interlocked.Read(ref _pausedMs);

    public bool DelaysTraffic => Latency > TimeSpan.Zero || Jitter > TimeSpan.Zero;

    public bool NeedsWrapper => DelaysTraffic || Slow is not null;

    public void ActivateSlowReader()
    {
        if (Slow is not null)
            Interlocked.CompareExchange(ref _slowSinceTicks, Stopwatch.GetTimestamp(), 0);
    }

    /// <summary>How long the reader must wait before reading now (0 when it may read).</summary>
    public TimeSpan PauseRemaining()
    {
        long since = Volatile.Read(ref _slowSinceTicks);
        if (since == 0 || Slow is not { BytesPerSecond: 0 } slowSpec)
            return TimeSpan.Zero;
        double elapsed = Stopwatch.GetElapsedTime(since).TotalSeconds;
        double cycle = slowSpec.Run.TotalSeconds + slowSpec.Pause.TotalSeconds;
        double at = elapsed % cycle;
        return at < slowSpec.Run.TotalSeconds ? TimeSpan.Zero : TimeSpan.FromSeconds(cycle - at);
    }

    internal void CountRead(int bytes) => Interlocked.Add(ref _bytesRead, bytes);

    internal void CountPause(TimeSpan span) => Interlocked.Add(ref _pausedMs, (long)span.TotalMilliseconds);
}

/// <summary>
/// A stream wrapper that injects failures into a node's TCP connection: it delays every chunk in both directions (<c>--latency</c>, with order kept,
/// so throughput is unaffected and only the round trip grows) and reads the socket slowly or in bursts (<c>--slow-reader</c>) so the server's send
/// queue for this connection backs up.
/// </summary>
public sealed class ImpairedStream : Stream
{
    private const int FullReadChunk = 16 * 1024;

    private readonly Stream _inner;
    private readonly StreamImpairment _imp;
    private readonly Random _random;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<(byte[] Data, long Due)> _rx = Channel.CreateUnbounded<(byte[], long)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Channel<(byte[] Data, long Due)> _tx = Channel.CreateUnbounded<(byte[], long)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _readPump;
    private readonly Task _writePump;
    private byte[] _current = [];
    private int _currentOffset;
    private long _lastRxDue;
    private long _lastTxDue;
    private volatile Exception? _txError;
    private int _disposed;

    public ImpairedStream(Stream inner, StreamImpairment impairment)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(impairment);
        _inner = inner;
        _imp = impairment;
        _random = new Random(impairment.Seed);
        _readPump = Task.Run(ReadPumpAsync);
        _writePump = _imp.DelaysTraffic ? Task.Run(WritePumpAsync) : Task.CompletedTask;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    private long NextDue(ref long last)
    {
        double ms = _imp.Latency.TotalMilliseconds;
        if (_imp.Jitter > TimeSpan.Zero)
        {
            lock (_random)
                ms += (_random.NextDouble() * 2 - 1) * _imp.Jitter.TotalMilliseconds;
        }

        long due = Stopwatch.GetTimestamp() + (long)(Math.Max(0, ms) * Stopwatch.Frequency / 1000.0);
        if (due < last)
            due = last; // TCP delivers in order: a jittered chunk never overtakes the previous one
        last = due;
        return due;
    }

    private static async Task WaitUntilAsync(long due, CancellationToken ct)
    {
        long left = due - Stopwatch.GetTimestamp();
        if (left > 0)
            await Task.Delay(TimeSpan.FromSeconds((double)left / Stopwatch.Frequency), ct).ConfigureAwait(false);
    }

    // ---- reading: a pump reads the socket as slowly as asked and stamps every chunk with its delivery time

    private async Task ReadPumpAsync()
    {
        var ct = _cts.Token;
        var buffer = new byte[FullReadChunk];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_imp.PauseRemaining() is { } pause && pause > TimeSpan.Zero)
                {
                    _imp.CountPause(pause);
                    await Task.Delay(pause, ct).ConfigureAwait(false);
                    continue;
                }

                bool slowRate = _imp is { SlowActive: true, Slow.BytesPerSecond: > 0 };
                int want = slowRate ? Math.Max(1, _imp.Slow!.BytesPerSecond / 20) : FullReadChunk;
                int n = await _inner.ReadAsync(buffer.AsMemory(0, Math.Min(want, buffer.Length)), ct).ConfigureAwait(false);
                if (n == 0)
                    break;
                if (_imp.PauseRemaining() is { } held && held > TimeSpan.Zero)
                {
                    // the read was already under way when the pause began: what it returned is held back until the pause is over
                    _imp.CountPause(held);
                    await Task.Delay(held, ct).ConfigureAwait(false);
                }

                if (_imp.SlowActive)
                    _imp.CountRead(n);
                _rx.Writer.TryWrite((buffer.AsSpan(0, n).ToArray(), _imp.DelaysTraffic ? NextDue(ref _lastRxDue) : 0));
                if (slowRate)
                    await Task.Delay(TimeSpan.FromSeconds((double)n / _imp.Slow!.BytesPerSecond), ct).ConfigureAwait(false);
            }

            _rx.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _rx.Writer.TryComplete(ex is OperationCanceledException ? null : ex);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
            return 0;
        if (_currentOffset >= _current.Length)
        {
            try
            {
                var (data, due) = await _rx.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (due != 0)
                    await WaitUntilAsync(due, cancellationToken).ConfigureAwait(false);
                _current = data;
                _currentOffset = 0;
            }
            catch (ChannelClosedException ex) when (ex.InnerException is null)
            {
                return 0;
            }
            catch (ChannelClosedException ex)
            {
                throw new IOException(ex.InnerException!.Message, ex.InnerException);
            }
        }

        int count = Math.Min(buffer.Length, _current.Length - _currentOffset);
        _current.AsSpan(_currentOffset, count).CopyTo(buffer.Span);
        _currentOffset += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    // ---- writing: with latency a pump sends each chunk when its time has come

    private async Task WritePumpAsync()
    {
        var ct = _cts.Token;
        try
        {
            await foreach (var (data, due) in _tx.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await WaitUntilAsync(due, ct).ConfigureAwait(false);
                await _inner.WriteAsync(data, ct).ConfigureAwait(false);
                await _inner.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // disposed
        }
        catch (Exception ex)
        {
            _txError = ex;
        }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_imp.DelaysTraffic)
            return _inner.WriteAsync(buffer, cancellationToken);
        if (_txError is { } error)
            throw new IOException(error.Message, error);
        long due;
        lock (_tx)
            due = NextDue(ref _lastTxDue);
        _tx.Writer.TryWrite((buffer.ToArray(), due));
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken cancellationToken) => _imp.DelaysTraffic ? Task.CompletedTask : _inner.FlushAsync(cancellationToken);

    public override void Flush()
    {
        if (!_imp.DelaysTraffic)
            _inner.Flush();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _tx.Writer.TryComplete();
            _cts.Cancel();
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // Let what was queued for sending (a Disconnect) go out before the socket closes.
            _tx.Writer.TryComplete();
            try
            {
                await _writePump.WaitAsync(_imp.Latency + _imp.Jitter + TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // give up on it
            }

            await _cts.CancelAsync().ConfigureAwait(false);
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
