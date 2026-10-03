using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

/// <summary>A ship a fake client flies: one <c>PlayerState</c> per 20 Hz tick, in order.</summary>
public interface IShipMotion
{
    ushort Sector { get; }

    Vec3 Position { get; }

    /// <summary>The net_id of the avatar (0 until the authority answered the <c>PlayerShip</c> request).</summary>
    uint NetId { get; set; }

    /// <summary>Advances to <paramref name="tick"/> (each tick once, in order) and returns the sample.</summary>
    PlayerStateT NextSample(long tick);
}

/// <summary>How a wingman keeps station around its target.</summary>
public enum WingmanMode
{
    /// <summary>Circles the target in a horizontal ring (radius <c>--wingman-radius</c>).</summary>
    Orbit,

    /// <summary>Holds a fixed slot behind and beside the target, turning with its heading.</summary>
    Formation,
}

/// <summary>
/// A client that flies near another player's replicated ship (<c>--wingman NAME</c>, m3-plan M3-05): it reads the target's newest pose from its own
/// ghost table (so it exercises Replication end to end), extrapolates it by the age of the sample, and flies to a slot around it at up to
/// <see cref="Speed"/> m/s. When the target changes sector (a gate jump) the wingman follows at once with a <see cref="StateFlags.Teleport"/> sample
/// next to the target's new position. Until the target is known it holds its place.
/// </summary>
public sealed class FakeWingman : IShipMotion
{
    public const double TickRateHz = FakePlayer.TickRateHz;
    private const double MaxExtrapolationSeconds = 1.0;
    private const double OrbitRadiansPerSecond = 0.25;

    private readonly Func<FakeClientSession.PlayerPose?> _target;
    private readonly int _slot;
    private readonly WingmanMode _mode;
    private double _time;
    private long _lastTick = -1;
    private bool _teleportPending = true; // the first sample places the ship at its avatar
    private double _yaw;
    private double _pitch;

    public FakeWingman(Func<FakeClientSession.PlayerPose?> target, ushort sector, Vec3 position, int slot, double speedMps, double radiusMetres, WingmanMode mode = WingmanMode.Orbit)
    {
        _target = target;
        _slot = slot;
        _mode = mode;
        Sector = sector;
        Position = position;
        Speed = speedMps;
        Radius = radiusMetres;
    }

    public ushort Sector { get; private set; }

    public Vec3 Position { get; private set; }

    public uint NetId { get; set; }

    /// <summary>Top speed, m/s (<c>--wingman-speed</c>).</summary>
    public double Speed { get; }

    /// <summary>Distance from the target, metres (<c>--wingman-radius</c>).</summary>
    public double Radius { get; }

    /// <summary>Sector changes made to follow the target.</summary>
    public long SectorChanges { get; private set; }

    /// <summary>Samples taken while the target was known (a wingman that never finds its target stays at 0).</summary>
    public long FollowedSamples { get; private set; }

    /// <summary>The place next to <paramref name="pose"/> this wingman wants to be, at the wingman's own time.</summary>
    public Vec3 Slot(FakeClientSession.PlayerPose pose)
    {
        double age = Math.Min(pose.AgeSeconds, MaxExtrapolationSeconds);
        var anchor = pose.Pos + (pose.Vel * age);
        double height = 40.0 * ((_slot % 3) - 1);
        if (_mode == WingmanMode.Orbit)
        {
            double phase = (_slot * 2.399963229728653) + (OrbitRadiansPerSecond * _time);
            return anchor + new Vec3(Math.Cos(phase) * Radius, height, Math.Sin(phase) * Radius);
        }

        // forward = (sin yaw, ., cos yaw) as in FakePlayer; slots alternate left and right behind the target
        double side = ((_slot % 2 == 0) ? 1.0 : -1.0) * (0.5 + (_slot / 2)) * Radius * 0.5;
        double back = -Radius * 0.5 * (1 + (_slot / 4));
        var forward = new Vec3(Math.Sin(pose.Yaw), 0, Math.Cos(pose.Yaw));
        var right = new Vec3(Math.Cos(pose.Yaw), 0, -Math.Sin(pose.Yaw));
        return anchor + (forward * back) + (right * side) + new Vec3(0, height, 0);
    }

    public PlayerStateT NextSample(long tick) => Step(tick);

    public PlayerStateT Step(long tick)
    {
        if (tick <= _lastTick)
            throw new ArgumentException("Ticks must increase.", nameof(tick));
        long steps = _lastTick < 0 ? 0 : tick - _lastTick;
        _lastTick = tick;
        var pose = _target();
        for (long i = 0; i < steps; i++)
            Advance(1.0 / TickRateHz, pose);

        ushort flags = 0;
        if (_teleportPending)
        {
            flags |= (ushort)StateFlags.Teleport;
            _teleportPending = false;
        }

        if (pose is not null)
            FollowedSamples++;
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
            Yaw = Quantize.Rotation(_yaw),
            Pitch = Quantize.Rotation(_pitch),
            Roll = Quantize.Rotation(0),
            Hull = 255,
            Shield = 255,
        };
    }

    private void Advance(double dt, FakeClientSession.PlayerPose? pose)
    {
        _time += dt;
        if (pose is null)
            return;
        var slot = Slot(pose);
        if (pose.Sector != Sector)
        {
            // the target jumped: follow through the gate in one step, next to where it arrived
            Sector = pose.Sector;
            Position = slot;
            _teleportPending = true;
            SectorChanges++;
            _yaw = pose.Yaw;
            _pitch = 0;
            return;
        }

        var to = slot - Position;
        double dist = to.Length;
        double step = Speed * dt;
        if (dist <= step)
        {
            Position = slot;
            _yaw = pose.Yaw;
            _pitch = pose.Pitch;
            return;
        }

        double horizontal = Math.Sqrt((to.X * to.X) + (to.Z * to.Z));
        if (horizontal > 1e-9)
            _yaw = Math.Atan2(to.X, to.Z);
        _pitch = Math.Atan2(to.Y, horizontal);
        Position += to / dist * step;
    }
}
