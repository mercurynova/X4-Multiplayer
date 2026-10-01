using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using X4MP.Proto;

namespace X4MP.FakeNode.Tests;

public sealed class GalaxyAndWorldTests
{
    private static string GalaxyFingerprint(FakeGalaxy g)
    {
        var sb = new StringBuilder();
        foreach (var s in g.Sectors)
            sb.Append(s.Index).Append(s.Macro).Append(s.Name).Append(s.GalaxyPos).Append(s.OwnerFaction).Append(';');
        foreach (var l in g.Links)
            sb.Append(l.A).Append('-').Append(l.B).Append(l.Kind).Append(l.PosInA).Append(l.PosInB).Append(';');
        foreach (var e in g.Entities)
            sb.Append(e.EntityId).Append(e.Macro).Append(e.Faction).Append(e.HomeSector).Append(e.Name).Append(e.IdCode)
              .Append(e.StaticPos).Append(e.Speed).Append(e.JumpChance).Append(e.PatrolSeed).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string WorldFingerprint(FakeWorld w, long[] ticks, int step)
    {
        using var sha = SHA256.Create();
        foreach (long tick in ticks)
            for (int id = 1; id <= w.Galaxy.Entities.Count; id += step)
            {
                var bytes = FakeWorld.StateBytes(w.GetStateAtTick(id, tick));
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    [Fact]
    public void SameSeedGivesIdenticalGalaxy() =>
        Assert.Equal(GalaxyFingerprint(FakeGalaxy.Generate(42)), GalaxyFingerprint(FakeGalaxy.Generate(42)));

    [Fact]
    public void DifferentSeedsGiveDifferentGalaxies() =>
        Assert.NotEqual(GalaxyFingerprint(FakeGalaxy.Generate(42)), GalaxyFingerprint(FakeGalaxy.Generate(43)));

    [Fact]
    public void GalaxyHasRealisticShape()
    {
        var g = FakeGalaxy.Generate(42);
        Assert.Equal(152, g.Sectors.Count);
        Assert.InRange(g.ShipCount, 9000, 11500);
        Assert.InRange(g.StationCount, 300, 2400);

        var macro = new Regex(@"^cluster_\d{2}_sector\d{3}_macro$");
        Assert.All(g.Sectors, s => Assert.Matches(macro, s.Macro));
        // index = 1-based rank of the macro in ordinal order
        Assert.Equal(g.Sectors.Select(s => s.Macro).Order(StringComparer.Ordinal), g.Sectors.Select(s => s.Macro));
        Assert.Equal(Enumerable.Range(1, 152).Select(i => (ushort)i), g.Sectors.Select(s => s.Index));
        Assert.Equal(152, g.Sectors.Select(s => s.Name).Distinct().Count());
    }

    [Fact]
    public void GateGraphIsConnectedAndSymmetric()
    {
        var g = FakeGalaxy.Generate(42);
        var seen = new HashSet<ushort> { 1 };
        var queue = new Queue<ushort>([1]);
        while (queue.Count > 0)
            foreach (var n in g.Neighbors(queue.Dequeue()))
                if (seen.Add(n.Sector))
                    queue.Enqueue(n.Sector);
        Assert.Equal(g.Sectors.Count, seen.Count);

        // spanning tree + ~20% extra edges (the highway/gate mix is a property of the links)
        Assert.InRange(g.Links.Count, 151 * 1.1, 151 * 1.45);
        Assert.Contains(g.Links, l => l.Kind == LinkKind.Highway);
        foreach (var l in g.Links)
        {
            Assert.Contains(g.Neighbors(l.A), n => n.Sector == l.B);
            Assert.Contains(g.Neighbors(l.B), n => n.Sector == l.A);
            Assert.Equal(l.PosInA, g.GatePosition(l.A, l.B));
            Assert.Equal(l.PosInB, g.GatePosition(l.B, l.A));
        }
    }

    [Fact]
    public void WorldIsAPureFunctionOfSeedAndTick()
    {
        long[] ticks = [0, 1, 599, 2399, 2400, 2401, 2880, 2999, 7200, 48_000];
        var a = new FakeWorld(FakeGalaxy.Generate(42));
        var b = new FakeWorld(FakeGalaxy.Generate(42));
        Assert.Equal(WorldFingerprint(a, ticks, 7), WorldFingerprint(b, ticks, 7));
        // evaluation order must not matter (no hidden history)
        var c = new FakeWorld(FakeGalaxy.Generate(42));
        _ = c.GetStateAtTick(5000, 48_000);
        Assert.Equal(FakeWorld.StateBytes(a.GetStateAtTick(5000, 7200)), FakeWorld.StateBytes(c.GetStateAtTick(5000, 7200)));
        Assert.Equal(WorldFingerprint(a, ticks, 7), WorldFingerprint(c, ticks, 7));
    }

    [Fact]
    public void DifferentSeedsGiveDifferentWorlds()
    {
        long[] ticks = [0, 1000, 5000];
        var a = new FakeWorld(FakeGalaxy.Generate(1));
        var b = new FakeWorld(FakeGalaxy.Generate(2));
        Assert.NotEqual(WorldFingerprint(a, ticks, 11), WorldFingerprint(b, ticks, 11));
    }

    [Fact]
    public void EveryShipIsInExactlyOneSectorAndMatchesItsState()
    {
        var w = new FakeWorld(FakeGalaxy.Generate(42));
        foreach (long leg in new long[] { 0, 1, 5, 20 })
        {
            long tick = leg * FakeWorld.TicksPerLeg + 1234 % FakeWorld.TicksPerLeg;
            var all = new List<int>();
            foreach (var s in w.Galaxy.Sectors)
            {
                var ids = w.EntitiesInSector(s.Index, leg);
                all.AddRange(ids);
                foreach (int id in ids.Take(15))
                    Assert.Equal(s.Index, w.GetStateAtTick(id, tick).Sector);
            }
            Assert.Equal(w.Galaxy.Entities.Count, all.Count);
            Assert.Equal(all.Count, all.Distinct().Count());
        }
    }

    [Fact]
    public void ShipsJumpBetweenConnectedSectors()
    {
        var w = new FakeWorld(FakeGalaxy.Generate(42));
        int jumps = 0;
        for (int id = w.Galaxy.StationCount + 1; id <= w.Galaxy.Entities.Count; id += 3)
            for (long leg = 0; leg < 40; leg++)
            {
                ushort from = w.SectorAt(id, leg * FakeWorld.LegSeconds);
                ushort to = w.SectorAt(id, (leg + 1) * FakeWorld.LegSeconds);
                if (from == to)
                    continue;
                jumps++;
                Assert.Contains(w.Galaxy.Neighbors(from), n => n.Sector == to);
            }
        Assert.True(jumps > 100, $"expected plenty of jumps, saw {jumps}");
    }

    [Fact]
    public void MotionIsContinuousAndWithinSpeedLimits()
    {
        var w = new FakeWorld(FakeGalaxy.Generate(42));
        int teleports = 0;
        for (int id = w.Galaxy.StationCount + 1; id <= w.Galaxy.Entities.Count; id += 97)
        {
            var e = w.Galaxy.Entities[id - 1];
            var prev = w.GetKinematics(id, 0);
            for (long tick = 1; tick < 3 * FakeWorld.TicksPerLeg; tick++)
            {
                var cur = w.GetKinematics(id, FakeWorld.TickToSeconds(tick));
                if (cur.Sector != prev.Sector)
                {
                    teleports++;
                    Assert.True((cur.Flags & (ushort)StateFlags.Teleport) != 0, "sector change must carry the Teleport flag");
                }
                else
                {
                    double moved = (cur.Pos - prev.Pos).Length;
                    // loops fly at class speed, gate transits cover at most ~45 km in 30 s
                    Assert.True(moved <= Math.Max(e.Speed, 2500) / FakeWorld.TickRateHz + 1e-6, $"entity {id} moved {moved} m in one tick");
                }
                prev = cur;
            }
        }
        Assert.True(teleports > 0);
    }

    [Fact]
    public void StationsAreStatic()
    {
        var w = new FakeWorld(FakeGalaxy.Generate(42));
        var a = w.GetState(1, 0);
        var b = w.GetState(1, 99_999);
        Assert.Equal(FakeWorld.StateBytes(a), FakeWorld.StateBytes(b));
        Assert.Equal(0, a.Vx);
    }
}
