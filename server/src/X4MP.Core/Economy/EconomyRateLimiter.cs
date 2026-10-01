namespace X4MP.Core.Economy;

/// <summary>
/// Sliding-window limit on economy requests per player (server-design 2.14 rule 6: 5 per 10 s). Every request that is
/// let through takes a slot; refused ones do not, so a spammer recovers as soon as the window has passed.
/// </summary>
public sealed class EconomyRateLimiter
{
    public const int DefaultLimit = 5;

    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time;
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly Dictionary<int, Queue<DateTimeOffset>> _hits = [];

    public EconomyRateLimiter(TimeProvider? time = null, int limit = DefaultLimit, TimeSpan? window = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        _time = time ?? TimeProvider.System;
        _limit = limit;
        _window = window ?? DefaultWindow;
    }

    /// <summary>True (and the slot is taken) when the player may send another request now.</summary>
    public bool TryAcquire(int playerId)
    {
        var now = _time.GetUtcNow();
        if (!_hits.TryGetValue(playerId, out var queue))
        {
            queue = new Queue<DateTimeOffset>();
            _hits[playerId] = queue;
        }

        while (queue.Count > 0 && now - queue.Peek() >= _window)
        {
            queue.Dequeue();
        }

        if (queue.Count >= _limit)
        {
            return false;
        }

        queue.Enqueue(now);
        return true;
    }

    /// <summary>Forgets a player (they left).</summary>
    public void Forget(int playerId) => _hits.Remove(playerId);
}
