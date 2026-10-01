using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

public enum ClientBehavior
{
    /// <summary>Random waypoints inside one sector.</summary>
    Wander,
    /// <summary>A loop through 3 sectors via their gates.</summary>
    Patrol,
    /// <summary>A random walk over gates: stresses interest changes.</summary>
    Explore,
}

/// <summary>
/// Fake client player ship (server-design 6.2): a deterministic stepper that produces a <c>PlayerState</c> per tick
/// (20 Hz). The client is authoritative for its own ship's motion, so the stepper owns position; jumps flag
/// <see cref="StateFlags.Teleport"/> on the first sample in the new sector.
/// </summary>
public sealed class FakePlayer
{
    public const double TickRateHz = 20.0;
    public const double CruiseSpeed = 250.0;
    private const double ArrivalRadius = 150.0;

    private readonly FakeGalaxy _galaxy;
    private readonly DetRandom _rng;
    private readonly ClientBehavior _behavior;
    private readonly List<ushort> _route = [];
    private int _routeIndex;
    private Vec3 _target;
    private bool _targetIsGate;
    private bool _needWaypoint = true;
    private ushort _gateTo;
    private bool _teleportPending;
    private long _lastTick = -1;

    public FakePlayer(FakeGalaxy galaxy, ulong seed, int index, ClientBehavior behavior)
    {
        _galaxy = galaxy;
        _behavior = behavior;
        _rng = new DetRandom(DetHash.Hash(seed, 0x91A7E5, (ulong)index));
        Sector = (ushort)(1 + (int)(_rng.NextUInt64() % (ulong)galaxy.Sectors.Count));
        Position = RandomPoint();
        if (behavior == ClientBehavior.Patrol)
            BuildRoute();
        PickTarget();
    }

    public ushort Sector { get; private set; }
    public Vec3 Position { get; private set; }
    public double Yaw { get; private set; }
    public double Pitch { get; private set; }
    public uint NetId { get; set; }
    public long SectorChanges { get; private set; }

    /// <summary>The route sectors for Patrol (empty otherwise).</summary>
    public IReadOnlyList<ushort> Route => _route;

    private Vec3 RandomPoint() =>
        new(_rng.NextDouble(-15000, 15000), _rng.NextDouble(-1500, 1500), _rng.NextDouble(-15000, 15000));

    private void BuildRoute()
    {
        // home -> a -> b, then the loop returns b -> a -> home -> a ...: 3 distinct sectors when the graph allows
        _route.Add(Sector);
        var n1 = _galaxy.Neighbors(Sector);
        ushort a = n1[_rng.NextInt(0, n1.Count)].Sector;
        _route.Add(a);
        var n2 = _galaxy.Neighbors(a).Where(n => n.Sector != Sector).ToList();
        if (n2.Count > 0)
            _route.Add(n2[_rng.NextInt(0, n2.Count)].Sector);
        // ping-pong order: indices 0..k-1 then k-2..1
        for (int i = _route.Count - 2; i >= 1; i--)
            _route.Add(_route[i]);
    }

    private void PickTarget()
    {
        switch (_behavior)
        {
            case ClientBehavior.Wander:
                _target = RandomPoint();
                _targetIsGate = false;
                break;
            case ClientBehavior.Patrol:
                if (_needWaypoint)
                {
                    _target = RandomPoint();
                    _targetIsGate = false;
                    _needWaypoint = false;
                }
                else
                {
                    _gateTo = _route[(_routeIndex + 1) % _route.Count];
                    _target = _galaxy.GatePosition(Sector, _gateTo);
                    _targetIsGate = true;
                }
                break;
            default:
                var nbrs = _galaxy.Neighbors(Sector);
                _gateTo = nbrs[_rng.NextInt(0, nbrs.Count)].Sector;
                _target = _galaxy.GatePosition(Sector, _gateTo);
                _targetIsGate = true;
                break;
        }
    }

    /// <summary>Advances to <paramref name="tick"/> (each tick must be stepped once, in order) and returns the sample.</summary>
    public PlayerStateT Step(long tick)
    {
        if (tick <= _lastTick)
            throw new ArgumentException("Ticks must increase.", nameof(tick));
        long steps = _lastTick < 0 ? 0 : tick - _lastTick;
        _lastTick = tick;
        for (long i = 0; i < steps; i++)
            Advance(1.0 / TickRateHz);

        ushort flags = 0;
        if (_teleportPending)
        {
            flags |= (ushort)StateFlags.Teleport;
            _teleportPending = false;
        }
        return new PlayerStateT
        {
            Seq = (uint)(tick + 1),
            SampleTimeUs = (ulong)(tick * (1_000_000L / (long)TickRateHz)),
            NetId = NetId,
            Sector = Sector,
            Flags = flags,
            Px = Quantize.Position(Position.X),
            Py = Quantize.Position(Position.Y),
            Pz = Quantize.Position(Position.Z),
            Yaw = Quantize.Rotation(Yaw),
            Pitch = Quantize.Rotation(Pitch),
            Roll = Quantize.Rotation(0),
            Hull = 255,
            Shield = 255,
        };
    }

    private void Advance(double dt)
    {
        var to = _target - Position;
        double dist = to.Length;
        double stepLen = CruiseSpeed * dt;
        if (dist > 1e-9)
        {
            double horiz = Math.Sqrt(to.X * to.X + to.Z * to.Z);
            Yaw = horiz > 1e-9 ? Math.Atan2(to.X, to.Z) : Yaw;
            Pitch = Math.Atan2(to.Y, horiz);
        }
        if (dist <= Math.Max(stepLen, ArrivalRadius))
        {
            Position = _target;
            if (_targetIsGate && _gateTo != 0)
            {
                // jump: appear at the paired gate of the destination sector
                var paired = _galaxy.GatePosition(_gateTo, Sector);
                Sector = _gateTo;
                Position = paired;
                _teleportPending = true;
                SectorChanges++;
                if (_behavior == ClientBehavior.Patrol)
                {
                    _routeIndex = (_routeIndex + 1) % _route.Count;
                    _needWaypoint = true;
                }
                _targetIsGate = false;
                _gateTo = 0;
            }
            PickTarget();
            return;
        }
        Position += to / dist * stepLen;
    }
}
