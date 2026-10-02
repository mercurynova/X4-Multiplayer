using X4MP.FakeNode;
using X4MP.Proto;

namespace X4MP.Server.Tests.Net;

/// <summary>
/// Early-stop conditions for FakeNode runs (<see cref="LiveRunOptions.StopWhen"/>): a live test ends its swarm as soon as the thing it asserts on
/// has been observed instead of sleeping the whole <c>--duration</c> (which stays the upper bound, so a hang still ends and the asserts then fail).
/// The predicates read live counters from another thread and must tolerate a half-built node.
/// </summary>
internal static class LiveStop
{
    /// <summary>Replication frames each client must have applied before a run counts as verified (about 3 s of streaming: the first second or two after the join is the noisy part).</summary>
    public const long MinFramesPerClient = 40;

    public static IEnumerable<LiveNodeStats> Clients(IReadOnlyList<LiveNodeStats> stats) => stats.Where(s => s.Role == Role.Client);

    /// <summary>The expected number of clients is in game and has verified at least <paramref name="entries"/> replication entries in total.</summary>
    public static bool Verified(IReadOnlyList<LiveNodeStats> stats, int clients, long entries) =>
        stats.Count(s => s.Role == Role.Client && s.InGame && s.Session is { ReplicationFrames: >= MinFramesPerClient }) >= clients
        && Clients(stats).Sum(s => s.Session?.Verifier.EntriesChecked ?? 0) >= entries;

    public static bool Everyone(IReadOnlyList<LiveNodeStats> stats, int nodes) =>
        stats.Count >= nodes && stats.All(s => s.InGame);

    public static long OrdersSent(IReadOnlyList<LiveNodeStats> stats) => stats.Sum(s => s.OrdersSent);

    public static long OrdersAnswered(IReadOnlyList<LiveNodeStats> stats) => stats.Sum(s => s.OrdersAccepted + s.OrdersRejected);
}
