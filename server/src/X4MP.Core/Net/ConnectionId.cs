namespace X4MP.Core.Net;

/// <summary>Monotonically increasing connection id (server-design 2.2). Never reused within a process.</summary>
public readonly record struct ConnectionId(long Value)
{
    private static long s_next;

    /// <summary>Allocates the next id (thread-safe, starts at 1).</summary>
    public static ConnectionId Next() => new(Interlocked.Increment(ref s_next));

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
