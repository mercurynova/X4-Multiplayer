using X4MP.Proto;

namespace X4MP.Core.World;

/// <summary>A gate, highway or accelerator connection between two sectors (<c>SectorLink</c>, without positions).</summary>
public readonly record struct SectorEdge(ushort From, ushort To, LinkKind Kind);

/// <summary>
/// The sector adjacency graph (server-design 2.5): an adjacency list over the 1-based sector indices of
/// <c>GalaxyMetadata</c> with a k-hop neighbour cache. Gates are two-way (a link seen in one direction is mirrored when
/// the reverse is missing); highways, accelerators and other links stay one-way as reported. Built once per save;
/// queries are allocation-free once a (sector, k) pair is cached. Actor-thread only (the cache is not synchronised).
/// </summary>
public sealed class SectorGraph
{
    /// <summary>Largest hop depth the cache serves (<c>PrefetchDepth</c> is clamped to it).</summary>
    public const int MaxHops = 8;

    private readonly ushort[][] _adjacency;
    private readonly bool[] _present;
    private readonly ushort[] _sectors;
    private readonly Dictionary<int, Neighborhood> _cache = [];

    private sealed class Neighborhood(ushort[] sectors, byte[] hops)
    {
        /// <summary>Reachable sectors within k hops, excluding the origin, ordered by hop distance then index.</summary>
        public ushort[] Sectors { get; } = sectors;

        public byte[] Hops { get; } = hops;
    }

    private SectorGraph(ushort[] sectors, ushort[][] adjacency, bool[] present)
    {
        _sectors = sectors;
        _adjacency = adjacency;
        _present = present;
    }

    public static SectorGraph Empty { get; } = new([], [], []);

    public int SectorCount => _sectors.Length;

    /// <summary>All sector indices, ascending.</summary>
    public ReadOnlySpan<ushort> Sectors => _sectors;

    public bool Contains(ushort sector) => sector < _present.Length && _present[sector];

    /// <summary>Builds the graph. Links to or from unknown sectors are ignored; duplicates are merged.</summary>
    public static SectorGraph Build(IEnumerable<ushort> sectors, IEnumerable<SectorEdge> links, bool mirrorGates = true)
    {
        ArgumentNullException.ThrowIfNull(sectors);
        ArgumentNullException.ThrowIfNull(links);
        var ids = sectors.Where(s => s != 0).Distinct().OrderBy(s => s).ToArray();
        if (ids.Length == 0)
        {
            return Empty;
        }

        int size = ids[^1] + 1;
        var present = new bool[size];
        foreach (var id in ids)
        {
            present[id] = true;
        }

        var sets = new SortedSet<ushort>?[size];
        void AddEdge(ushort from, ushort to)
        {
            if (from == to || from >= size || to >= size || !present[from] || !present[to])
            {
                return;
            }

            (sets[from] ??= []).Add(to);
        }

        foreach (var link in links)
        {
            AddEdge(link.From, link.To);
            if (mirrorGates && link.Kind == LinkKind.Gate)
            {
                AddEdge(link.To, link.From);
            }
        }

        var adjacency = new ushort[size][];
        for (int i = 0; i < size; i++)
        {
            adjacency[i] = sets[i] is { } set ? [.. set] : [];
        }

        var graph = new SectorGraph(ids, adjacency, present);
        graph.Precompute(1);
        return graph;
    }

    /// <summary>Sectors one hop away from <paramref name="sector"/> (direct exits).</summary>
    public ReadOnlySpan<ushort> Neighbors(ushort sector) => Contains(sector) ? _adjacency[sector] : [];

    /// <summary>
    /// Every sector reachable from <paramref name="sector"/> in at most <paramref name="hops"/> hops, nearest first and
    /// without the origin. The array is cached: do not modify it.
    /// </summary>
    public ReadOnlySpan<ushort> WithinHops(ushort sector, int hops)
    {
        if (hops <= 0 || !Contains(sector))
        {
            return [];
        }

        return Get(sector, Math.Min(hops, MaxHops)).Sectors;
    }

    /// <summary>Hop distance (1..<paramref name="maxHops"/>) from <paramref name="from"/> to <paramref name="to"/>, 0 for the same sector, -1 when farther or unreachable.</summary>
    public int HopDistance(ushort from, ushort to, int maxHops)
    {
        if (from == to)
        {
            return Contains(from) ? 0 : -1;
        }

        if (maxHops <= 0 || !Contains(from) || !Contains(to))
        {
            return -1;
        }

        var hood = Get(from, Math.Min(maxHops, MaxHops));
        int index = Array.IndexOf(hood.Sectors, to); // ordered by hop, not by index

        return index < 0 ? -1 : hood.Hops[index];
    }

    /// <summary>Number of cached (sector, k) entries (diagnostics, tests).</summary>
    public int CachedEntries => _cache.Count;

    private void Precompute(int k)
    {
        foreach (var id in _sectors)
        {
            Get(id, k);
        }
    }

    private Neighborhood Get(ushort sector, int k)
    {
        int key = (sector << 8) | k;
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var result = Compute(sector, k);
        _cache[key] = result;
        return result;
    }

    private Neighborhood Compute(ushort origin, int k)
    {
        var seen = new HashSet<ushort> { origin };
        var frontier = new List<ushort> { origin };
        var sectors = new List<ushort>();
        var hops = new List<byte>();
        for (int hop = 1; hop <= k && frontier.Count > 0; hop++)
        {
            var next = new List<ushort>();
            foreach (var current in frontier)
            {
                foreach (var neighbor in _adjacency[current])
                {
                    if (seen.Add(neighbor))
                    {
                        next.Add(neighbor);
                    }
                }
            }

            next.Sort();
            foreach (var n in next)
            {
                sectors.Add(n);
                hops.Add((byte)hop);
            }

            frontier = next;
        }

        return new Neighborhood([.. sectors], [.. hops]);
    }
}
