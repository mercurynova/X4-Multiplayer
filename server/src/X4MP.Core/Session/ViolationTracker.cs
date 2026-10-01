namespace X4MP.Core.Session;

/// <summary>
/// Sliding one-minute violation counter for one connection (ADR-026: more than 20 per minute closes the
/// connection and temp-bans the IP). Not thread-safe by design: only the reader loop records.
/// </summary>
public sealed class ViolationTracker(int limitPerMinute, TimeProvider? time = null)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Queue<long> _stamps = new();

    public int LimitPerMinute { get; } = limitPerMinute;

    /// <summary>Violations inside the current one-minute window.</summary>
    public int Count
    {
        get
        {
            Prune();
            return _stamps.Count;
        }
    }

    /// <summary>True once more than <see cref="LimitPerMinute"/> violations fell inside the window.</summary>
    public bool Exceeded => Count > LimitPerMinute;

    /// <summary>Records one violation and returns the count inside the window.</summary>
    public int Record()
    {
        Prune();
        _stamps.Enqueue(_time.GetTimestamp());
        return _stamps.Count;
    }

    private void Prune()
    {
        while (_stamps.Count > 0 && _time.GetElapsedTime(_stamps.Peek()) > Window)
        {
            _stamps.Dequeue();
        }
    }
}
