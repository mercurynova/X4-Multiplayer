using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode.Tests;

/// <summary>What the replication work added to the fake authority: sample-time stamping of spawns and focus spheres.</summary>
public sealed class AuthorityFocusTests
{
    private static T Decode<T>(OutMessage m) where T : struct, Google.FlatBuffers.IFlatbufferObject =>
        MessageRegistry.Default.Decode<T>(new Frame(m.Type, FrameOptions.None, MessageRegistry.Default.GetDescriptor(m.Type).Lane, m.Payload));

    private static ushort BusySector(FakeWorld w) =>
        w.Galaxy.Sectors.Select(s => s.Index).OrderByDescending(s => w.EntitiesInSector(s, 0).Count).First();

    [Fact]
    public void SpawnsCarryTheGameTimeTheirStatesWereSampledAt()
    {
        var w = new FakeWorld(FakeGalaxy.Generate(42));
        var a = new FakeAuthority(w);
        ushort sector = BusySector(w);
        a.OnCaptureSet(new CaptureSetT { Epoch = 1, Sectors = [new CaptureSectorT { Sector = sector, RateHz = 5 }] }, 0);

        for (long tick = 0; tick < 20; tick++)
        {
            var output = a.Tick(tick);
            var spawn = output.FirstOrDefault(m => m.Type == MsgType.EntitySpawn);
            if (spawn is null)
                continue;

            Assert.Equal(tick / FakeWorld.TickRateHz, Decode<EntitySpawn>(spawn).GameTime, 9);
            return;
        }

        Assert.Fail("the authority never spawned anything");
    }

    [Fact]
    public void EntitiesInsideAFocusSphereStreamAtTheSpheresRateTheRestAtTheSectorRate()
    {
        var w = new FakeWorld(FakeGalaxy.Generate(42));
        var a = new FakeAuthority(w);
        ushort sector = BusySector(w);
        var members = w.EntitiesInSector(sector, 0).Where(id => !w.Galaxy.Entities[id - 1].IsStation).ToList();
        int near = members[0];
        var centre = w.GetKinematics(near, 0).Pos;
        // a sphere around one ship; the ship is the only one of the sector that is certainly inside it
        a.OnCaptureSet(new CaptureSetT
        {
            Epoch = 1,
            Sectors = [new CaptureSectorT { Sector = sector, RateHz = 5 }],
            Focus = [new CaptureFocusT { Sector = sector, Center = new Vec3fT { X = (float)centre.X, Y = (float)centre.Y, Z = (float)centre.Z }, RadiusM = 1f, RateHz = 20 }],
        }, 0);

        var counts = new Dictionary<uint, int>();
        const int Ticks = 40;
        for (long tick = 0; tick < Ticks; tick++)
        {
            foreach (var m in a.Tick(tick).Where(m => m.Type == MsgType.WorldUpdate))
            {
                foreach (var s in Decode<WorldUpdate>(m).UnPack().States)
                    counts[s.NetId] = counts.GetValueOrDefault(s.NetId) + 1;
            }
        }

        // the sphere moves with no one: only while the ship stays within 1 m of where it was at tick 0 it streams at 20 Hz; the sphere is
        // tested every tick at the ship's position then, so the first updates are at the focus rate
        uint id = FakeNetIds.ToNetId(near);
        var other = members.Skip(1).Select(FakeNetIds.ToNetId).Where(counts.ContainsKey).ToList();
        Assert.True(other.Count > 0);
        Assert.All(other, o => Assert.InRange(counts[o], 8, 12));      // 5 Hz for 2 s
        Assert.InRange(counts[id], 9, Ticks);                           // at least the sector rate, more while inside the sphere
        Assert.True(counts[id] >= counts[other[0]]);
    }
}
