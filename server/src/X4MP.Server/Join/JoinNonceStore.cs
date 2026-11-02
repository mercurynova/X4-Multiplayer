using System.Collections.Concurrent;
using System.Security.Cryptography;
using X4MP.Protocol;

namespace X4MP.Server.Join;

/// <summary>
/// Single-use nonces for the join-password proof (mod-management 4.1). A nonce is spent by the first request that presents it, so a
/// captured proof cannot be replayed. The store is bounded: expired entries are dropped as new nonces are issued, and the oldest ones give
/// way when the cap is reached, so an anonymous caller cannot grow it without limit.
/// </summary>
public sealed class JoinNonceStore(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(120);
    private const int MaxPending = 2048;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pending = new(StringComparer.Ordinal);

    /// <summary>How long a nonce stays valid, for the caller that wants to show it.</summary>
    public int ExpiresInSeconds => (int)Lifetime.TotalSeconds;

    /// <summary>Issues a fresh 32-byte nonce and records it until it is spent or expires.</summary>
    public byte[] Issue()
    {
        var nonce = RandomNumberGenerator.GetBytes(HandshakeAuth.NonceLength);
        var now = time.GetUtcNow();
        Prune(now);
        _pending[Convert.ToHexStringLower(nonce)] = now + Lifetime;
        return nonce;
    }

    /// <summary>Spends the nonce: true only for a nonce this store issued and that has not expired.</summary>
    public bool TryConsume(ReadOnlySpan<byte> nonce)
    {
        return _pending.TryRemove(Convert.ToHexStringLower(nonce), out var expires) && expires > time.GetUtcNow();
    }

    private void Prune(DateTimeOffset now)
    {
        if (_pending.Count < MaxPending / 2)
        {
            return;
        }

        foreach (var pair in _pending)
        {
            if (pair.Value <= now)
            {
                _ = _pending.TryRemove(pair.Key, out _);
            }
        }

        if (_pending.Count >= MaxPending)
        {
            foreach (var pair in _pending.OrderBy(p => p.Value).Take(_pending.Count - MaxPending + 1).ToArray())
            {
                _ = _pending.TryRemove(pair.Key, out _);
            }
        }
    }
}
