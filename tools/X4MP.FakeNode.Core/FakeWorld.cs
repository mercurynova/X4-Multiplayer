using System.Collections.Concurrent;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

/// <summary>Unquantised ground truth of one entity at one instant.</summary>
public readonly record struct Kinematics(ushort Sector, Vec3 Pos, Vec3 Vel, double Yaw, double Pitch, double Roll, ushort Flags);

/// <summary>
/// Deterministic, tick-based fake simulation (server-design 6.2): entity state is a closed-form pure function
/// of <c>(seed, time)</c>, so any client can compute ground truth for any time without stepping history.
/// <para>
/// Time is cut into legs of <see cref="LegSeconds"/>. During a leg a ship flies a waypoint loop (3-5 points)
/// inside one sector at its class speed. At the end of a leg a ship jumps through a gate with a per-ship
/// probability: during the last <see cref="TransitSeconds"/> it flies from the loop to the exit gate, and in the
/// next leg it enters at the paired gate and flies back onto the loop of the new sector. A ship therefore belongs
/// to exactly one sector for a whole leg. Stations are static.
/// </para>
/// </summary>
public sealed class FakeWorld
{
    public const double TickRateHz = 20.0;
    public const double LegSeconds = 120.0;
    public const double TransitSeconds = 30.0;
    public const long TicksPerLeg = (long)(LegSeconds * TickRateHz);

    private sealed record Loop(Vec3[] Points, double[] Cumulative, double Perimeter);

    private readonly object _pathLock = new();
    private readonly ushort[]?[] _paths;
    private readonly (ushort Sector, Loop Loop)?[] _loops;
    private readonly ConcurrentDictionary<long, int[][]> _occupancy = new();
    private readonly int[][] _stationsBySector;

    public FakeWorld(FakeGalaxy galaxy)
    {
        Galaxy = galaxy;
        _paths = new ushort[]?[galaxy.Entities.Count + 1];
        _loops = new (ushort, Loop)?[galaxy.Entities.Count + 1];
        _stationsBySector = new int[galaxy.Sectors.Count + 1][];
        for (int s = 1; s <= galaxy.Sectors.Count; s++)
            _stationsBySector[s] = [.. galaxy.Entities.Where(e => e.IsStation && e.HomeSector == s).Select(e => e.EntityId)];
    }

    public FakeGalaxy Galaxy { get; }

    public static double TickToSeconds(long tick) => tick / TickRateHz;

    public static long LegOfTick(long tick) => tick / TicksPerLeg;

    public static long LegOfTime(double seconds) => (long)Math.Floor(Math.Max(0, seconds) / LegSeconds);

    // ---------------- sector membership ----------------

    private ushort SectorOfLeg(FakeEntity e, long leg)
    {
        if (e.IsStation)
            return e.HomeSector;
        lock (_pathLock)
        {
            var path = _paths[e.EntityId];
            if (path is null || path.Length <= leg)
            {
                int newLen = (int)Math.Max(leg + 1, path is null ? 8 : path.Length * 2);
                var grown = new ushort[newLen];
                int have = 0;
                if (path is not null)
                {
                    Array.Copy(path, grown, path.Length);
                    have = path.Length;
                }
                if (have == 0)
                {
                    grown[0] = e.HomeSector;
                    have = 1;
                }
                for (int m = have; m < newLen; m++)
                {
                    ushort target = JumpTarget(e, m - 1, grown[m - 1]);
                    grown[m] = target != 0 ? target : grown[m - 1];
                }
                _paths[e.EntityId] = path = grown;
            }
            return path[leg];
        }
    }

    /// <summary>The sector this entity jumps to at the end of <paramref name="leg"/>, or 0 for no jump.</summary>
    private ushort JumpTarget(FakeEntity e, long leg, ushort sectorAtLeg)
    {
        if (DetHash.Unit(DetHash.Hash(e.PatrolSeed, (ulong)leg, 1)) >= e.JumpChance)
            return 0;
        var nbrs = Galaxy.Neighbors(sectorAtLeg);
        if (nbrs.Count == 0)
            return 0;
        int k = (int)(DetHash.Hash(e.PatrolSeed, (ulong)leg, 2) % (ulong)nbrs.Count);
        return nbrs[k].Sector;
    }

    /// <summary>Sector of the entity at <paramref name="timeSeconds"/>.</summary>
    public ushort SectorAt(int entityId, double timeSeconds) =>
        SectorOfLeg(Galaxy.Entities[entityId - 1], LegOfTime(timeSeconds));

    /// <summary>Entity ids (stations + ships) present in <paramref name="sector"/> during <paramref name="leg"/>, ascending.</summary>
    public IReadOnlyList<int> EntitiesInSector(ushort sector, long leg) => Occupancy(leg)[sector];

    private int[][] Occupancy(long leg)
    {
        if (_occupancy.TryGetValue(leg, out var cached))
            return cached;
        var lists = new List<int>[Galaxy.Sectors.Count + 1];
        for (int s = 1; s < lists.Length; s++)
            lists[s] = [.. _stationsBySector[s]];
        for (int i = Galaxy.StationCount; i < Galaxy.Entities.Count; i++)
        {
            var e = Galaxy.Entities[i];
            lists[SectorOfLeg(e, leg)].Add(e.EntityId);
        }
        var result = new int[lists.Length][];
        result[0] = [];
        for (int s = 1; s < lists.Length; s++)
            result[s] = [.. lists[s]];

        foreach (var old in _occupancy.Keys.Where(k => k < leg - 2))
            _occupancy.TryRemove(old, out _);
        _occupancy[leg] = result;
        return result;
    }

    // ---------------- kinematics ----------------

    private Loop GetLoop(FakeEntity e, ushort sector)
    {
        if (_loops[e.EntityId] is { } cached && cached.Sector == sector)
            return cached.Loop;
        int n = 3 + (int)(DetHash.Hash(e.PatrolSeed, sector, 0x100) % 3UL);
        var pts = new Vec3[n];
        for (int i = 0; i < n; i++)
        {
            pts[i] = new Vec3(
                (DetHash.Unit(DetHash.Hash(e.PatrolSeed, sector, 0x200 + (ulong)i)) - 0.5) * 40000,
                (DetHash.Unit(DetHash.Hash(e.PatrolSeed, sector, 0x300 + (ulong)i)) - 0.5) * 3000,
                (DetHash.Unit(DetHash.Hash(e.PatrolSeed, sector, 0x400 + (ulong)i)) - 0.5) * 40000);
        }
        var cum = new double[n + 1];
        for (int i = 0; i < n; i++)
            cum[i + 1] = cum[i] + (pts[(i + 1) % n] - pts[i]).Length;
        var loop = new Loop(pts, cum, cum[n]);
        _loops[e.EntityId] = (sector, loop);
        return loop;
    }

    private static (Vec3 Pos, Vec3 Vel) LoopAt(FakeEntity e, Loop loop, double t)
    {
        double phase = DetHash.Unit(DetHash.Hash(e.PatrolSeed, 0x500)) * loop.Perimeter;
        double d = (e.Speed * t + phase) % loop.Perimeter;
        int seg = 0;
        while (seg < loop.Points.Length - 1 && d >= loop.Cumulative[seg + 1])
            seg++;
        var a = loop.Points[seg];
        var b = loop.Points[(seg + 1) % loop.Points.Length];
        double len = loop.Cumulative[seg + 1] - loop.Cumulative[seg];
        if (len <= 0)
            return (a, default);
        double f = (d - loop.Cumulative[seg]) / len;
        return (Vec3.Lerp(a, b, f), (b - a) / len * e.Speed);
    }

    /// <summary>Ground truth of <paramref name="entityId"/> at <paramref name="t"/> seconds of game time.</summary>
    public Kinematics GetKinematics(int entityId, double t)
    {
        var e = Galaxy.Entities[entityId - 1];
        if (e.IsStation)
            return new Kinematics(e.HomeSector, e.StaticPos, default, e.StaticYaw, 0, 0, 0);

        if (t < 0)
            t = 0;
        long leg = LegOfTime(t);
        double tau = t - leg * LegSeconds;
        ushort sector = SectorOfLeg(e, leg);
        var loop = GetLoop(e, sector);

        Vec3 pos, vel;
        ushort flags = 0;
        bool arrived = leg > 0 && SectorOfLeg(e, leg - 1) != sector;
        ushort exitTo = JumpTarget(e, leg, sector);

        if (arrived && tau < TransitSeconds)
        {
            var gate = Galaxy.GatePosition(sector, SectorOfLeg(e, leg - 1));
            var onLoop = LoopAt(e, loop, leg * LegSeconds + TransitSeconds).Pos;
            pos = Vec3.Lerp(gate, onLoop, tau / TransitSeconds);
            vel = (onLoop - gate) / TransitSeconds;
            if (tau < 1.0 / TickRateHz)
                flags |= (ushort)StateFlags.Teleport;
        }
        else if (exitTo != 0 && tau >= LegSeconds - TransitSeconds)
        {
            var onLoop = LoopAt(e, loop, leg * LegSeconds + (LegSeconds - TransitSeconds)).Pos;
            var gate = Galaxy.GatePosition(sector, exitTo);
            pos = Vec3.Lerp(onLoop, gate, (tau - (LegSeconds - TransitSeconds)) / TransitSeconds);
            vel = (gate - onLoop) / TransitSeconds;
        }
        else
        {
            (pos, vel) = LoopAt(e, loop, t);
        }

        double horiz = Math.Sqrt(vel.X * vel.X + vel.Z * vel.Z);
        double yaw = horiz > 1e-9 ? Math.Atan2(vel.X, vel.Z) : 0;
        double pitch = vel.Length > 1e-9 ? Math.Atan2(vel.Y, horiz) : 0;
        return new Kinematics(sector, pos, vel, yaw, pitch, 0, flags);
    }

    /// <summary>The quantised wire state (protocol.md 11) of an entity at <paramref name="t"/> seconds.</summary>
    public EntityStateT GetState(int entityId, double t)
    {
        var k = GetKinematics(entityId, t);
        return new EntityStateT
        {
            NetId = FakeNetIds.ToNetId(entityId),
            Sector = k.Sector,
            Flags = k.Flags,
            Px = Quantize.Position(k.Pos.X),
            Py = Quantize.Position(k.Pos.Y),
            Pz = Quantize.Position(k.Pos.Z),
            Yaw = Quantize.Rotation(k.Yaw),
            Pitch = Quantize.Rotation(k.Pitch),
            Roll = Quantize.Rotation(k.Roll),
            Vx = Quantize.Velocity(k.Vel.X, false),
            Vy = Quantize.Velocity(k.Vel.Y, false),
            Vz = Quantize.Velocity(k.Vel.Z, false),
        };
    }

    /// <summary>State at a tick.</summary>
    public EntityStateT GetStateAtTick(int entityId, long tick) => GetState(entityId, TickToSeconds(tick));

    /// <summary>Canonical 32-byte encoding of an entity state (for determinism checks).</summary>
    public static byte[] StateBytes(EntityStateT s)
    {
        var b = new byte[32];
        var sp = b.AsSpan();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(sp, s.NetId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(sp[4..], s.Sector);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(sp[6..], s.Flags);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(sp[8..], s.Px);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(sp[12..], s.Py);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(sp[16..], s.Pz);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(sp[20..], s.Yaw);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(sp[22..], s.Pitch);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(sp[24..], s.Roll);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(sp[26..], s.Vx);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(sp[28..], s.Vy);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(sp[30..], s.Vz);
        return b;
    }
}

/// <summary>Fake net_ids: the entity id itself, so every verifier can map a net_id back to ground truth.</summary>
public static class FakeNetIds
{
    public static uint ToNetId(int entityId) => (uint)entityId;

    public static int ToEntityId(uint netId) => (int)netId;
}
