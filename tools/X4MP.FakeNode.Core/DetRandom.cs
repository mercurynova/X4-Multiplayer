namespace X4MP.FakeNode;

/// <summary>
/// Deterministic hashing and a tiny PRNG (SplitMix64). Everything the fake universe does is derived from these,
/// never from <see cref="Random"/> or wall-clock time, so a seed fully determines the world on every machine.
/// Only IEEE-exact arithmetic (+ - * / sqrt) feeds the generated structure.
/// </summary>
public static class DetHash
{
    public static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public static ulong Hash(ulong a, ulong b) => Mix(a ^ Mix(b ^ 0xA5A5A5A5DEADBEEFUL));

    public static ulong Hash(ulong a, ulong b, ulong c) => Hash(Hash(a, b), c);

    public static ulong Hash(ulong a, ulong b, ulong c, ulong d) => Hash(Hash(a, b, c), d);

    /// <summary>Maps a hash to [0, 1).</summary>
    public static double Unit(ulong h) => (h >> 11) * (1.0 / 9007199254740992.0);
}

/// <summary>Sequential deterministic random stream (SplitMix64).</summary>
public sealed class DetRandom
{
    private ulong _state;

    public DetRandom(ulong seed)
    {
        _state = seed;
    }

    public ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>[0, 1).</summary>
    public double NextDouble() => DetHash.Unit(NextUInt64());

    /// <summary>[min, max).</summary>
    public double NextDouble(double min, double max) => min + (max - min) * NextDouble();

    /// <summary>[minInclusive, maxExclusive).</summary>
    public int NextInt(int minInclusive, int maxExclusive) =>
        minInclusive + (int)(NextUInt64() % (ulong)(maxExclusive - minInclusive));

    public bool Chance(double p) => NextDouble() < p;

    public T Pick<T>(IReadOnlyList<T> items) => items[NextInt(0, items.Count)];

    public void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = NextInt(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
