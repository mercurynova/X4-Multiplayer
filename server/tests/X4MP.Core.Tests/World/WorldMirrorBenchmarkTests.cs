using System.Diagnostics;
using X4MP.Core.World;
using X4MP.Proto;
using Xunit.Abstractions;
using static X4MP.Core.Tests.World.WorldKit;

namespace X4MP.Core.Tests.World;

/// <summary>M1-06 acceptance: 20k entities at 20 Hz with zero steady-state allocations.</summary>
public sealed class WorldMirrorBenchmarkTests(ITestOutputHelper output)
{
    private const int Entities = 20_000;
    private const int TicksPerCycle = 20;      // one second of game time at 20 Hz
    private const int Sectors = 100;

    /// <summary>Counts what observers see without allocating (the interest manager does comparable work).</summary>
    private sealed class CountingObserver : IWorldObserver
    {
        public long StateChanges;
        public long SectorChanges;

        public void OnEntityStateChanged(MirrorEntity entity, ushort previousSector, StateChange change)
        {
            StateChanges++;
            if ((change & StateChange.Sector) != 0)
            {
                SectorChanges++;
            }
        }
    }

    private static byte[][] BuildCycle()
    {
        var payloads = new byte[TicksPerCycle][];
        for (int t = 0; t < TicksPerCycle; t++)
        {
            var states = new EntityStateT[Entities];
            for (int i = 0; i < Entities; i++)
            {
                uint id = (uint)(i + 1);
                // Every entity moves every tick; every 50th entity also hops between two neighbouring sectors each tick.
                ushort sector = (ushort)(1 + (i % Sectors));
                if (i % 50 == 0 && (t & 1) == 1)
                {
                    sector = (ushort)(1 + ((i + 1) % Sectors));
                }

                states[i] = new EntityStateT
                {
                    NetId = id, Sector = sector, Px = (t * 640) + i, Py = i * 3, Pz = -t, Yaw = (short)(t * 100), Vx = (short)(t + 1),
                };
            }

            payloads[t] = UpdatePayload((uint)t, t * 0.05, states);
        }

        return payloads;
    }

    [Fact]
    public void Ingesting20kEntitiesAt20HzAllocatesNothingInSteadyState()
    {
        var mirror = new WorldMirror(initialCapacity: Entities);
        var observer = new CountingObserver();
        mirror.AddObserver(observer);

        var records = Enumerable.Range(0, Entities)
            .Select(i => Rec((uint)(i + 1), i % 7 == 0 ? EntityKind.ShipL : EntityKind.ShipS, (ushort)(1 + (i % Sectors))))
            .ToArray();
        mirror.ApplySpawn(Decode<EntitySpawn>(MsgType.EntitySpawn, SpawnPayload(records)));
        Assert.Equal(Entities, mirror.HotCount);

        var cycle = BuildCycle();

        // Warm up: JIT, dictionary and sector lists reach their final capacity.
        for (int c = 0; c < 3; c++)
        {
            foreach (var payload in cycle)
            {
                mirror.IngestWorldUpdate(payload);
            }
        }

        const int MeasuredCycles = 10;      // 10 s of game time
        long before = GC.GetAllocatedBytesForCurrentThread();
        long gen0Before = GC.CollectionCount(0);
        long applied = 0;
        var sw = Stopwatch.StartNew();
        for (int c = 0; c < MeasuredCycles; c++)
        {
            foreach (var payload in cycle)
            {
                applied += mirror.IngestWorldUpdate(payload).Applied;
            }
        }

        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        int ticks = MeasuredCycles * TicksPerCycle;
        double msPerTick = sw.Elapsed.TotalMilliseconds / ticks;
        output.WriteLine($"20k entities x {ticks} ticks: {msPerTick:F3} ms/tick ({msPerTick / 50 * 100:F1}% of a 50 ms tick), {applied / sw.Elapsed.TotalSeconds / 1e6:F2} M updates/s, allocated {allocated} B, gen0 GCs {GC.CollectionCount(0) - gen0Before}");

        Assert.Equal((long)Entities * ticks, applied);
        Assert.True(observer.SectorChanges > 0, "the benchmark must exercise sector moves");
        Assert.True(allocated <= 4096, $"steady-state ingestion allocated {allocated} bytes");
        Assert.True(msPerTick < 50, $"an ingest pass of {Entities} entities must fit in one 20 Hz tick, took {msPerTick:F2} ms");
    }

    [Fact]
    public void EvictingAndRespawningSectorsReusesPooledRecords()
    {
        var mirror = new WorldMirror(initialCapacity: 4096);
        var records = Enumerable.Range(0, 2000).Select(i => Rec((uint)(i + 1), EntityKind.ShipS, 3)).ToArray();
        var spawn = Decode<EntitySpawn>(MsgType.EntitySpawn, SpawnPayload(records));
        for (int i = 0; i < 3; i++)
        {
            mirror.ApplySpawn(spawn);
            mirror.EvictTransient(3);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        mirror.ApplySpawn(spawn);
        mirror.EvictTransient(3);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, mirror.Count);
        // Spawn records carry strings, so the decode itself allocates; the entity objects must not (2000 x ~200 B would be 400 KB).
        Assert.True(allocated < 250_000, $"respawn allocated {allocated} bytes");
    }
}
