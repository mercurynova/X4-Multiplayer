namespace X4MP.Server.Auth;

/// <summary>
/// In-memory brute-force protection: a fixed-window attempt limit per client IP (server-design 4.2: 5 per
/// minute) plus a per-username consecutive-failure lockout (10 failures lock the account for 5 minutes).
/// State is process-local by design; the server is a single instance.
/// </summary>
public sealed class LoginThrottle(AdminAuthOptions options, TimeProvider time)
{
    private const int MaxTracked = 10_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset WindowStart, int Count)> _ips = [];
    private readonly Dictionary<string, (int Failures, DateTimeOffset LockedUntil)> _users = [];

    /// <summary>Counts an attempt from <paramref name="ip"/>; false (with a retry hint) once the window is exhausted.</summary>
    public bool TryAcquireAttempt(string ip, out TimeSpan retryAfter)
    {
        var now = time.GetUtcNow();
        var window = TimeSpan.FromSeconds(options.LoginWindowSeconds);
        lock (_gate)
        {
            Prune(now);
            var entry = _ips.TryGetValue(ip, out var e) && now - e.WindowStart < window ? e : (WindowStart: now, Count: 0);
            if (entry.Count >= options.LoginMaxPerWindow)
            {
                retryAfter = entry.WindowStart + window - now;
                return false;
            }

            _ips[ip] = (entry.WindowStart, entry.Count + 1);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    /// <summary>True (with the remaining lock time) while the username is locked.</summary>
    public bool IsLocked(string username, out TimeSpan retryAfter)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_users.TryGetValue(Key(username), out var u) && u.LockedUntil > now)
            {
                retryAfter = u.LockedUntil - now;
                return true;
            }
        }

        retryAfter = TimeSpan.Zero;
        return false;
    }

    /// <summary>Records a failed attempt; returns the consecutive failure count (used to scale the response delay).</summary>
    public int RecordFailure(string username)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            var key = Key(username);
            var (failures, lockedUntil) = _users.TryGetValue(key, out var u) ? u : (0, DateTimeOffset.MinValue);
            if (lockedUntil != DateTimeOffset.MinValue && lockedUntil <= now)
            {
                failures = 0; // a previous lock expired: start over
                lockedUntil = DateTimeOffset.MinValue;
            }

            failures++;
            if (failures >= options.LockoutFailures)
            {
                lockedUntil = now.AddMinutes(options.LockoutMinutes);
            }

            _users[key] = (failures, lockedUntil);
            return failures;
        }
    }

    public void RecordSuccess(string username)
    {
        lock (_gate)
        {
            _users.Remove(Key(username));
        }
    }

    public TimeSpan FailureDelay(int consecutiveFailures) =>
        TimeSpan.FromMilliseconds((long)options.FailureDelayMs * Math.Clamp(consecutiveFailures, 1, 5));

    private static string Key(string username) => username.Trim().ToUpperInvariant();

    private void Prune(DateTimeOffset now)
    {
        if (_ips.Count < MaxTracked && _users.Count < MaxTracked)
        {
            return;
        }

        var window = TimeSpan.FromSeconds(options.LoginWindowSeconds);
        foreach (var k in _ips.Where(p => now - p.Value.WindowStart >= window).Select(p => p.Key).ToList())
        {
            _ips.Remove(k);
        }

        foreach (var k in _users.Where(p => p.Value.LockedUntil <= now).Select(p => p.Key).ToList())
        {
            _users.Remove(k);
        }
    }
}
