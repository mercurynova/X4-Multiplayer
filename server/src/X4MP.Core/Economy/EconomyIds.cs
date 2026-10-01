using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace X4MP.Core.Economy;

/// <summary>Time-sortable 26-character transaction ids (ULID layout: 48-bit millisecond time + 80 random bits, Crockford base32).</summary>
public sealed class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private static readonly UInt128 RandomMask = (UInt128.One << 80) - 1;

    private long _lastMs = -1;
    private UInt128 _lastRandom;

    /// <summary>Strictly increasing within one instance, even inside the same millisecond.</summary>
    public string Next(DateTimeOffset at)
    {
        var ms = Math.Max(at.ToUnixTimeMilliseconds(), _lastMs);
        if (ms == _lastMs)
        {
            _lastRandom = (_lastRandom + 1) & RandomMask;
        }
        else
        {
            Span<byte> bytes = stackalloc byte[10];
            RandomNumberGenerator.Fill(bytes);
            UInt128 random = 0;
            foreach (var b in bytes)
            {
                random = (random << 8) | b;
            }

            _lastRandom = random >> 1; // leave headroom so the in-millisecond increment cannot overflow
            _lastMs = ms;
        }

        var chars = new char[26];
        var time = (ulong)ms;
        for (var i = 9; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(time & 31)];
            time >>= 5;
        }

        var rest = _lastRandom;
        for (var i = 25; i >= 10; i--)
        {
            chars[i] = Alphabet[(int)(rest & 31)];
            rest >>= 5;
        }

        return new string(chars);
    }
}

/// <summary>Canonical payload hashes for request idempotency (<c>economy_requests.payload_hash</c>).</summary>
public static class PayloadHasher
{
    /// <summary>SHA-256 over the request type and the invariant text of every value, in order.</summary>
    public static byte[] Hash(string type, params object?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var text = type + "|" + string.Join('|', values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty));
        return SHA256.HashData(Encoding.UTF8.GetBytes(text));
    }
}
