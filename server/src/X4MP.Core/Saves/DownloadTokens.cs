using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace X4MP.Core.Saves;

/// <summary>
/// The HTTP fallback's per-node download tokens (protocol.md 6.4): random 128-bit values, bound to one player, valid for a limited time.
/// Issued with the <c>SessionSaveInfo</c> of a node that negotiated <c>SaveHttp</c>, checked by <c>GET /files/saves/{sha}</c>. Thread-safe.
/// </summary>
public sealed class DownloadTokens(TimeProvider time)
{
    private readonly record struct Entry(int PlayerId, DateTimeOffset Expires);

    private readonly ConcurrentDictionary<string, Entry> _tokens = new(StringComparer.Ordinal);

    public int Count => _tokens.Count;

    public string Issue(int playerId, TimeSpan lifetime)
    {
        PurgeExpired();
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _tokens[token] = new Entry(playerId, time.GetUtcNow() + lifetime);
        return token;
    }

    /// <summary>The player a valid token belongs to.</summary>
    public bool TryValidate(string? token, out int playerId)
    {
        playerId = 0;
        if (string.IsNullOrEmpty(token) || !_tokens.TryGetValue(token, out var entry))
        {
            return false;
        }

        if (entry.Expires <= time.GetUtcNow())
        {
            _tokens.TryRemove(token, out _);
            return false;
        }

        playerId = entry.PlayerId;
        return true;
    }

    /// <summary>Drops every token of a player (it left).</summary>
    public void RevokePlayer(int playerId)
    {
        foreach (var pair in _tokens)
        {
            if (pair.Value.PlayerId == playerId)
            {
                _tokens.TryRemove(pair.Key, out _);
            }
        }
    }

    public void Clear() => _tokens.Clear();

    private void PurgeExpired()
    {
        var now = time.GetUtcNow();
        foreach (var pair in _tokens)
        {
            if (pair.Value.Expires <= now)
            {
                _tokens.TryRemove(pair.Key, out _);
            }
        }
    }
}

/// <summary>
/// The server-wide outbound save bandwidth cap (<see cref="SaveOptions.SaveBandwidthCapMBps"/>): one token bucket shared by every in-band
/// download. Zero means unlimited. Thread-safe.
/// </summary>
public sealed class BandwidthLimiter(Func<double> megabytesPerSecond, TimeProvider time)
{
    private readonly object _gate = new();
    private double _tokens;
    private long _last;
    private bool _primed;

    /// <summary>Waits until <paramref name="bytes"/> may be sent under the cap.</summary>
    public async ValueTask AcquireAsync(int bytes, CancellationToken ct)
    {
        double rate = megabytesPerSecond() * 1024 * 1024;
        if (rate <= 0)
        {
            return;
        }

        TimeSpan wait;
        lock (_gate)
        {
            long now = time.GetTimestamp();
            double burst = Math.Max(rate * 0.1, bytes);
            if (!_primed)
            {
                _primed = true;
                _tokens = burst;
            }
            else
            {
                _tokens = Math.Min(burst, _tokens + time.GetElapsedTime(_last, now).TotalSeconds * rate);
            }

            _last = now;
            _tokens -= bytes;
            wait = _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / rate);
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, time, ct).ConfigureAwait(false);
        }
    }
}
