using System.Globalization;
using X4MP.Proto;

namespace X4MP.FakeNode;

/// <summary>Plain double vector (metres, sector-relative unless stated).</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public static Vec3 Lerp(Vec3 a, Vec3 b, double t) => a + (b - a) * t;
}

/// <summary>Generation parameters. Defaults match the real galaxy measured in spike session 1 (152 sectors, ~10.2k ships).</summary>
public sealed record GalaxyOptions
{
    public int SectorCount { get; init; } = 152;
    public int ShipCount { get; init; } = 10225;
    public int MinStationsPerSector { get; init; } = 2;
    public int MaxStationsPerSector { get; init; } = 15;
    public int MinShipsPerSector { get; init; } = 10;
    public int MaxShipsPerSector { get; init; } = 800;
}

public sealed class FakeSector
{
    /// <summary>1-based, assigned by sorting the sector macros (ordinal), like the real GalaxyMetadata.</summary>
    public required ushort Index { get; init; }
    public required string Macro { get; init; }
    public required string ClusterMacro { get; init; }
    public required int ClusterNumber { get; init; }
    public required string Name { get; init; }
    /// <summary>Position on the GUI map (km).</summary>
    public required Vec3 GalaxyPos { get; init; }
    /// <summary>Index into <see cref="FakeGalaxy.Factions"/>.</summary>
    public required int OwnerFaction { get; init; }
}

/// <summary>An undirected gate/highway pair: <see cref="PosInA"/> lies in sector A, <see cref="PosInB"/> in sector B.</summary>
public sealed record FakeLink(ushort A, ushort B, LinkKind Kind, Vec3 PosInA, Vec3 PosInB);

/// <summary>A neighbour of a sector as seen from it.</summary>
public readonly record struct Neighbor(ushort Sector, LinkKind Kind, Vec3 GatePos, Vec3 PairedGatePos);

/// <summary>A station or ship. <see cref="EntityId"/> (1-based) doubles as the fake net_id.</summary>
public sealed class FakeEntity
{
    public required int EntityId { get; init; }
    public required EntityKind Kind { get; init; }
    public required string Macro { get; init; }
    public required int Faction { get; init; }
    public required ushort HomeSector { get; init; }
    public required string Name { get; init; }
    public required string IdCode { get; init; }
    public bool IsStation => Kind == EntityKind.Station;
    /// <summary>Stations: fixed position and heading.</summary>
    public Vec3 StaticPos { get; init; }
    public double StaticYaw { get; init; }
    /// <summary>Ships: cruise speed in m/s (S 300, M 200, L 120, XL 80, XS 350).</summary>
    public double Speed { get; init; }
    /// <summary>Ships: probability per leg of jumping through a gate at the end of the leg.</summary>
    public double JumpChance { get; init; }
    public ulong PatrolSeed { get; init; }
}

/// <summary>
/// Deterministic fake galaxy (server-design 6.2): a pure function of the seed (and options). About 150 sectors
/// on a hex grid of clusters, a connected gate graph (random spanning tree + ~20% extra edges, ~10% highways),
/// ~10k ships with macros/owners, and stations.
/// </summary>
public sealed class FakeGalaxy
{
    public static readonly IReadOnlyList<string> FactionNames =
    [
        "argon", "antigone", "paranid", "holyorder", "teladi", "ministry", "split", "freesplit", "terran", "pioneers",
        "scaleplate", "hatikvah", "alliance", "court", "legend", "trinity", "xenon", "khaak", "yaki", "buccaneers",
    ];

    private static readonly string[] SyllableStart = ["Ar", "Del", "Ka", "Mor", "Ven", "Tal", "Zy", "Or", "Hel", "Sil", "Bra", "Cor", "Dru", "Ely", "Fen", "Gor"];
    private static readonly string[] SyllableMid = ["a", "e", "i", "o", "u", "ae", "ia", "ou"];
    private static readonly string[] SyllableEnd = ["lon", "dar", "thys", "nis", "rax", "mir", "vek", "sa", "gard", "ion"];
    private static readonly string[] FamiliarNames =
    [
        "Argon Prime", "Antigone Memorial", "Heretic's End", "Hatikvah's Choice", "Ocean of Fantasy", "Grand Exchange",
        "Family Zhin", "Tharka's Cascade", "Silent Witness", "Pontifex Maximus",
    ];

    private static readonly string[] StationTypes = ["shipyard_01", "wharf_01", "equipment_dock_01", "trade_01", "factory_01", "defence_01", "pier_01"];

    private static readonly (EntityKind Kind, string Letter, double Share, double Speed, string[] Roles)[] ShipClasses =
    [
        (EntityKind.ShipXS, "xs", 0.06, 350, ["scout_01", "transp_01"]),
        (EntityKind.ShipS, "s", 0.42, 300, ["fighter_01", "heavyfighter_01", "scout_01", "miner_solid_01", "trans_container_01"]),
        (EntityKind.ShipM, "m", 0.34, 200, ["bomber_01", "corvette_01", "miner_liquid_01", "trans_container_01", "gunboat_01"]),
        (EntityKind.ShipL, "l", 0.14, 120, ["destroyer_01", "miner_solid_01", "trans_container_01", "freighter_01"]),
        (EntityKind.ShipXL, "xl", 0.04, 80, ["carrier_01", "destroyer_01", "battleship_01", "builder_01"]),
    ];

    private readonly Dictionary<ushort, List<Neighbor>> _adjacency = [];
    private readonly Dictionary<(ushort, ushort), Vec3> _gatePos = [];

    private FakeGalaxy(ulong seed, GalaxyOptions options)
    {
        Seed = seed;
        Options = options;
    }

    public ulong Seed { get; }
    public GalaxyOptions Options { get; }

    /// <summary>Sectors ordered by index (element i has Index i + 1).</summary>
    public IReadOnlyList<FakeSector> Sectors { get; private set; } = [];

    public IReadOnlyList<FakeLink> Links { get; private set; } = [];

    /// <summary>Stations first, then ships; <c>Entities[EntityId - 1]</c>.</summary>
    public IReadOnlyList<FakeEntity> Entities { get; private set; } = [];

    public int StationCount { get; private set; }
    public int ShipCount => Entities.Count - StationCount;

    public IReadOnlyList<string> Factions { get; } = FactionNames;

    public FakeSector Sector(ushort index) => Sectors[index - 1];

    public IReadOnlyList<Neighbor> Neighbors(ushort sector) => _adjacency[sector];

    /// <summary>Position, in sector <paramref name="from"/>, of the gate leading to <paramref name="to"/>.</summary>
    public Vec3 GatePosition(ushort from, ushort to) => _gatePos[(from, to)];

    public static FakeGalaxy Generate(ulong seed) => Generate(seed, new GalaxyOptions());

    public static FakeGalaxy Generate(ulong seed, GalaxyOptions options)
    {
        if (options.SectorCount < 2)
            throw new ArgumentOutOfRangeException(nameof(options), "SectorCount must be at least 2.");
        var g = new FakeGalaxy(seed, options);
        var rng = new DetRandom(DetHash.Hash(seed, 0x47414C4158590001UL));
        var clusters = g.BuildSectors(rng);
        g.BuildLinks(rng, clusters);
        g.BuildEntities();
        return g;
    }

    /// <summary>Sectors that have at least one gate: a player can fly from them. Every sector of a generated galaxy; a dump may hold isolated ones.</summary>
    public IReadOnlyList<ushort> PlayableSectors => _playable ??= [.. Sectors.Where(s => _adjacency[s.Index].Count > 0).Select(s => s.Index)];

    private IReadOnlyList<ushort>? _playable;

    /// <summary>Gate links in a dump that named a sector the dump does not contain (ignored).</summary>
    public int IgnoredGateTargets { get; private set; }

    /// <summary>
    /// A galaxy with the sectors and gate links of a real dump (<c>--galaxy-file</c>, see <see cref="GalaxyDump"/>): sector macros, cluster macros and
    /// the gate graph come from the file, in the real order (index = rank of the macro, ordinal). Everything the dump does not carry (names, map
    /// positions, owners, gate positions, the ships and stations) is generated from <paramref name="seed"/>, so it stays deterministic. Gate pairs
    /// are undirected; every link is a plain gate.
    /// </summary>
    public static FakeGalaxy FromDump(ulong seed, GalaxyOptions options, GalaxyDump dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        var g = new FakeGalaxy(seed, options);
        var ordered = dump.Sectors.OrderBy(s => s.Macro, StringComparer.Ordinal).ToList();
        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < ordered.Count; i++)
            indexOf[ordered[i].Macro] = i;

        // clusters: the macro the dump names, else "<prefix>_sectorNNN_macro" -> "<prefix>_macro", else the sector itself
        string ClusterOf(GalaxyDumpSector s)
        {
            if (s.Cluster.Length > 0)
                return s.Cluster;
            var m = System.Text.RegularExpressions.Regex.Match(s.Macro, @"^(?<c>.+)_sector\d+_macro$");
            return m.Success ? m.Groups["c"].Value + "_macro" : s.Macro;
        }

        var clusterOrder = ordered.Select(ClusterOf).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var hex = HexSpiral(clusterOrder.Count);
        var inCluster = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var sectors = new FakeSector[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
        {
            string cluster = ClusterOf(ordered[i]);
            int c = clusterOrder.IndexOf(cluster);
            int within = inCluster.GetValueOrDefault(cluster);
            inCluster[cluster] = within + 1;
            var match = System.Text.RegularExpressions.Regex.Match(cluster, @"cluster_(?<n>\d+)");
            int clusterNumber = match.Success && int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : c + 1;
            var sm = System.Text.RegularExpressions.Regex.Match(ordered[i].Macro, @"cluster_(?<c>\d+)_sector(?<s>\d+)");
            string name = sm.Success
                ? string.Create(CultureInfo.InvariantCulture, $"Cluster {sm.Groups["c"].Value} Sector {sm.Groups["s"].Value}")
                : ordered[i].Macro.EndsWith("_macro", StringComparison.Ordinal) ? ordered[i].Macro[..^6] : ordered[i].Macro;
            string candidate = name;
            for (int k = 2; !names.Add(candidate); k++)
                candidate = string.Create(CultureInfo.InvariantCulture, $"{name} {k}");
            double cx = 400.0 * (hex[c].Q + (hex[c].R * 0.5));
            double cz = 400.0 * (hex[c].R * 0.8660254037844386);
            sectors[i] = new FakeSector
            {
                Index = (ushort)(i + 1),
                Macro = ordered[i].Macro,
                ClusterMacro = cluster,
                ClusterNumber = clusterNumber,
                Name = candidate,
                GalaxyPos = new Vec3(cx + (within * 70.0), 0, cz + ((within % 2) * 55.0)),
                OwnerFaction = (int)(DetHash.Hash(seed, 0xFAC, (ulong)c) % 16UL),
            };
        }

        g.Sectors = sectors;
        var pairs = new SortedSet<(int, int)>();
        int ignored = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            foreach (string target in ordered[i].Gates)
            {
                if (!indexOf.TryGetValue(target, out int j))
                    ignored++;
                else if (j != i)
                    pairs.Add(i < j ? (i, j) : (j, i));
            }
        }

        g.IgnoredGateTargets = ignored;
        g.MaterializeLinks(pairs, () => LinkKind.Gate);
        g.BuildEntities();
        return g;
    }

    // ---------------- sectors ----------------

    private sealed record RawSector(int Cluster, int ClusterNumber, int InCluster, string Macro, string ClusterMacro, Vec3 Pos, int Faction);

    private sealed record ClusterInfo(int Number, int Q, int R, List<int> Members);

    private static List<(int Q, int R)> HexSpiral(int count)
    {
        var result = new List<(int, int)> { (0, 0) };
        (int, int)[] dirs = [(1, 0), (1, -1), (0, -1), (-1, 0), (-1, 1), (0, 1)];
        for (int ring = 1; result.Count < count; ring++)
        {
            int q = -ring, r = ring; // start at dir[4] * ring... walk the six sides
            for (int side = 0; side < 6 && result.Count < count; side++)
            {
                for (int step = 0; step < ring && result.Count < count; step++)
                {
                    result.Add((q, r));
                    q += dirs[side].Item1;
                    r += dirs[side].Item2;
                }
            }
        }
        return result;
    }

    private List<ClusterInfo> BuildSectors(DetRandom rng)
    {
        // cluster sizes 1..3 until we have SectorCount sectors
        var sizes = new List<int>();
        int total = 0;
        while (total < Options.SectorCount)
        {
            double u = rng.NextDouble();
            int size = u < 0.30 ? 1 : u < 0.75 ? 2 : 3;
            size = Math.Min(size, Options.SectorCount - total);
            sizes.Add(size);
            total += size;
        }

        var hex = HexSpiral(sizes.Count);
        var clusters = new List<ClusterInfo>();
        var raw = new List<RawSector>();
        for (int c = 0; c < sizes.Count; c++)
        {
            int number = c + 1;
            string clusterMacro = string.Create(CultureInfo.InvariantCulture, $"cluster_{number:D2}_macro");
            int faction = (int)(DetHash.Hash(Seed, 0xFAC, (ulong)c) % 16UL); // the 4 hostile/minor factions own no clusters
            double cx = 400.0 * (hex[c].Q + hex[c].R * 0.5);
            double cz = 400.0 * (hex[c].R * 0.8660254037844386);
            var info = new ClusterInfo(number, hex[c].Q, hex[c].R, []);
            for (int s = 0; s < sizes[c]; s++)
            {
                string macro = string.Create(CultureInfo.InvariantCulture, $"cluster_{number:D2}_sector{s + 1:D3}_macro");
                var pos = new Vec3(cx + s * 70.0, 0, cz + (s % 2) * 55.0);
                info.Members.Add(raw.Count);
                raw.Add(new RawSector(c, number, s + 1, macro, clusterMacro, pos, faction));
            }
            clusters.Add(info);
        }

        // index = rank of the macro in ordinal order (deterministic, like the real mod)
        var order = Enumerable.Range(0, raw.Count).OrderBy(i => raw[i].Macro, StringComparer.Ordinal).ToArray();
        var rank = new int[raw.Count];
        for (int r = 0; r < order.Length; r++)
            rank[order[r]] = r;

        var names = new HashSet<string>(StringComparer.Ordinal);
        var familiarSlots = new Dictionary<int, string>();
        for (int i = 0; i < FamiliarNames.Length && i < raw.Count; i++)
            familiarSlots[(int)(DetHash.Hash(Seed, 0xFA11, (ulong)i) % (ulong)raw.Count)] = FamiliarNames[i];

        var sectors = new FakeSector[raw.Count];
        for (int r = 0; r < order.Length; r++)
        {
            int i = order[r];
            string name = familiarSlots.TryGetValue(i, out var fam) ? fam : MakeName(i);
            string candidate = name;
            for (int n = 2; !names.Add(candidate); n++)
                candidate = string.Create(CultureInfo.InvariantCulture, $"{name} {n}");
            sectors[r] = new FakeSector
            {
                Index = (ushort)(r + 1),
                Macro = raw[i].Macro,
                ClusterMacro = raw[i].ClusterMacro,
                ClusterNumber = raw[i].ClusterNumber,
                Name = candidate,
                GalaxyPos = raw[i].Pos,
                OwnerFaction = raw[i].Faction,
            };
        }
        Sectors = sectors;
        // remap cluster members to sector indices (0-based positions in `sectors`)
        foreach (var cl in clusters)
            for (int m = 0; m < cl.Members.Count; m++)
                cl.Members[m] = rank[cl.Members[m]];
        return clusters;
    }

    private string MakeName(int i)
    {
        var h = DetHash.Hash(Seed, 0xBEEF, (ulong)i);
        string a = SyllableStart[(int)(h % (ulong)SyllableStart.Length)];
        string b = SyllableMid[(int)((h >> 16) % (ulong)SyllableMid.Length)];
        string c = SyllableEnd[(int)((h >> 32) % (ulong)SyllableEnd.Length)];
        return a + b + c;
    }

    // ---------------- links ----------------

    private void BuildLinks(DetRandom rng, List<ClusterInfo> clusters)
    {
        var candidates = new List<(int A, int B)>();
        foreach (var cl in clusters)
            for (int x = 0; x < cl.Members.Count; x++)
                for (int y = x + 1; y < cl.Members.Count; y++)
                    candidates.Add((cl.Members[x], cl.Members[y]));

        var byHex = clusters.ToDictionary(c => (c.Q, c.R));
        (int, int)[] dirs = [(1, 0), (1, -1), (0, -1), (-1, 0), (-1, 1), (0, 1)];
        foreach (var cl in clusters)
        {
            foreach (var (dq, dr) in dirs)
            {
                if (!byHex.TryGetValue((cl.Q + dq, cl.R + dr), out var other) || other.Number < cl.Number)
                    continue;
                int pairs = rng.Chance(0.3) ? 2 : 1;
                for (int k = 0; k < pairs; k++)
                {
                    int a = rng.Pick(cl.Members);
                    int b = rng.Pick(other.Members);
                    if (!candidates.Contains((a, b)))
                        candidates.Add((a, b));
                }
            }
        }

        rng.Shuffle(candidates);
        var parent = Enumerable.Range(0, Sectors.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        var tree = new List<(int, int)>();
        var rest = new List<(int, int)>();
        foreach (var (a, b) in candidates)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb)
            {
                rest.Add((a, b));
                continue;
            }
            parent[ra] = rb;
            tree.Add((a, b));
        }
        if (tree.Count != Sectors.Count - 1)
            throw new InvalidOperationException("Generated candidate graph is not connected.");

        double extraP = Math.Min(1.0, 0.2 * tree.Count / Math.Max(1, rest.Count));
        var chosen = new List<(int, int)>(tree);
        foreach (var e in rest)
            if (rng.Chance(extraP))
                chosen.Add(e);

        MaterializeLinks(chosen, () => rng.Chance(0.10) ? LinkKind.Highway : LinkKind.Gate);
    }

    /// <summary>
    /// Turns sector pairs (0-based positions in <see cref="Sectors"/>) into links with deterministic gate positions (about 18 km from the centre
    /// towards the neighbour) and builds the adjacency tables. <paramref name="kindOf"/> is asked once per pair, in pair order.
    /// </summary>
    private void MaterializeLinks(IEnumerable<(int A, int B)> chosen, Func<LinkKind> kindOf)
    {
        var links = new List<FakeLink>();
        foreach (var (a, b) in chosen)
        {
            ushort ia = (ushort)(a + 1), ib = (ushort)(b + 1);
            if (ia > ib)
                (ia, ib) = (ib, ia);
            var kind = kindOf();
            var delta = Sectors[ib - 1].GalaxyPos - Sectors[ia - 1].GalaxyPos;
            var dir = delta.Length > 0 ? delta / delta.Length : new Vec3(1, 0, 0);
            Vec3 Jitter(ulong salt) => new(
                (DetHash.Unit(DetHash.Hash(Seed, salt, ia, ib)) - 0.5) * 3000,
                (DetHash.Unit(DetHash.Hash(Seed, salt + 1, ia, ib)) - 0.5) * 3000,
                (DetHash.Unit(DetHash.Hash(Seed, salt + 2, ia, ib)) - 0.5) * 3000);
            var posA = dir * 18000 + Jitter(0x6A7E);
            var posB = -dir * 18000 + Jitter(0x6A81);
            links.Add(new FakeLink(ia, ib, kind, posA, posB));
        }
        links.Sort((x, y) => x.A != y.A ? x.A.CompareTo(y.A) : x.B.CompareTo(y.B));
        Links = links;

        foreach (var s in Sectors)
            _adjacency[s.Index] = [];
        foreach (var l in links)
        {
            _adjacency[l.A].Add(new Neighbor(l.B, l.Kind, l.PosInA, l.PosInB));
            _adjacency[l.B].Add(new Neighbor(l.A, l.Kind, l.PosInB, l.PosInA));
            _gatePos[(l.A, l.B)] = l.PosInA;
            _gatePos[(l.B, l.A)] = l.PosInB;
        }
        foreach (var list in _adjacency.Values)
            list.Sort((x, y) => x.Sector.CompareTo(y.Sector));
    }

    // ---------------- stations and ships ----------------

    private void BuildEntities()
    {
        var entities = new List<FakeEntity>();
        int nextId = 1;

        // stations
        foreach (var s in Sectors)
        {
            var r = new DetRandom(DetHash.Hash(Seed, 0x57A7, s.Index));
            double u = r.NextDouble();
            int count = Options.MinStationsPerSector
                + (int)Math.Round((Options.MaxStationsPerSector - Options.MinStationsPerSector) * u * u);
            for (int k = 0; k < count; k++)
            {
                string fac = FactionNames[s.OwnerFaction];
                string type = r.Pick(StationTypes);
                var pos = new Vec3(r.NextDouble(-15000, 15000), r.NextDouble(-1000, 1000), r.NextDouble(-15000, 15000));
                int id = nextId++;
                entities.Add(new FakeEntity
                {
                    EntityId = id,
                    Kind = EntityKind.Station,
                    Macro = $"station_{fac}_{type}_macro",
                    Faction = s.OwnerFaction,
                    HomeSector = s.Index,
                    Name = $"{char.ToUpperInvariant(fac[0])}{fac[1..]} {type.Split('_')[0]} {id}",
                    IdCode = IdCode(id),
                    StaticPos = pos,
                    StaticYaw = r.NextDouble(-Math.PI, Math.PI),
                });
            }
        }
        StationCount = entities.Count;

        // ships: heavy-tailed distribution over sectors, scaled to the target
        var weights = new double[Sectors.Count];
        double sum = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            double u = DetHash.Unit(DetHash.Hash(Seed, 0x5815, (ulong)i));
            weights[i] = 0.1 + 4.0 * u * u;
            sum += weights[i];
        }

        foreach (var s in Sectors)
        {
            int n = (int)Math.Round(weights[s.Index - 1] / sum * Options.ShipCount);
            n = Math.Clamp(n, Options.MinShipsPerSector, Options.MaxShipsPerSector);
            var r = new DetRandom(DetHash.Hash(Seed, 0x5817, s.Index));
            for (int k = 0; k < n; k++)
            {
                double c = r.NextDouble();
                double acc = 0;
                var cls = ShipClasses[^1];
                foreach (var candidate in ShipClasses)
                {
                    acc += candidate.Share;
                    if (c < acc)
                    {
                        cls = candidate;
                        break;
                    }
                }
                int faction = r.Chance(0.7) ? s.OwnerFaction : r.NextInt(0, FactionNames.Count);
                string fac = FactionNames[faction];
                string role = r.Pick(cls.Roles);
                int id = nextId++;
                entities.Add(new FakeEntity
                {
                    EntityId = id,
                    Kind = cls.Kind,
                    Macro = $"ship_{fac[..Math.Min(3, fac.Length)]}_{cls.Letter}_{role}_a_macro",
                    Faction = faction,
                    HomeSector = s.Index,
                    Name = $"{role.Split('_')[0]} {id}",
                    IdCode = IdCode(id),
                    Speed = cls.Speed,
                    JumpChance = 0.02 + 0.13 * r.NextDouble(),
                    PatrolSeed = DetHash.Hash(Seed, 0x9A7, (ulong)id),
                });
            }
        }
        Entities = entities;
    }

    private string IdCode(int id)
    {
        var h = DetHash.Hash(Seed, 0x1DC0, (ulong)id);
        char a = (char)('A' + (int)(h % 26));
        char b = (char)('A' + (int)((h >> 8) % 26));
        char c = (char)('A' + (int)((h >> 16) % 26));
        return string.Create(CultureInfo.InvariantCulture, $"{a}{b}{c}-{(int)((h >> 24) % 1000):D3}");
    }
}
