using X4MP.Proto;

namespace X4MP.Core.Relay;

/// <summary>
/// Derives a ship's velocity from its <c>PlayerState</c> samples (protocol.md 13: the client sends none, X4 exports no velocity getter).
/// The velocity is the least-squares slope of position over time across the last <see cref="WindowSize"/> samples (3), which smooths
/// the jitter of a 20 Hz stream that a plain two-point difference amplifies. The window restarts on a discontinuity: another sector,
/// a <c>Teleport</c> flag, a docked ship (its position is meaningless), a long gap, or time that does not advance.
/// </summary>
public sealed class MotionEstimator
{
    public const int WindowSize = 3;

    /// <summary>Samples further apart than this do not belong to one motion (a stall, a reload).</summary>
    public const double MaxGapSeconds = 1.0;

    private const double MetresPerUnit = 1.0 / 64.0;

    private readonly double[] _t = new double[WindowSize];
    private readonly double[] _x = new double[WindowSize];
    private readonly double[] _y = new double[WindowSize];
    private readonly double[] _z = new double[WindowSize];
    private int _count;
    private ushort _sector;

    /// <summary>Samples in the window.</summary>
    public int Samples => _count;

    /// <summary>The latest estimate in metres per second (zero until two samples agree on a motion).</summary>
    public (double X, double Y, double Z) Velocity { get; private set; }

    /// <summary>Speed in metres per second.</summary>
    public double Speed => Math.Sqrt((Velocity.X * Velocity.X) + (Velocity.Y * Velocity.Y) + (Velocity.Z * Velocity.Z));

    public void Reset()
    {
        _count = 0;
        Velocity = default;
    }

    /// <summary>Adds one sample (positions in 1/64 m, <paramref name="sampleTimeUs"/> from the sender) and returns the new estimate.</summary>
    public (double X, double Y, double Z) Add(ushort sector, ushort flags, ulong sampleTimeUs, int px, int py, int pz)
    {
        const ushort Docked = (ushort)StateFlags.Docked;
        const ushort Teleport = (ushort)StateFlags.Teleport;

        double t = sampleTimeUs / 1_000_000.0;
        if ((flags & Docked) != 0)
        {
            Reset();
            _sector = sector;
            return Velocity;
        }

        bool restart = _count == 0
            || sector != _sector
            || (flags & Teleport) != 0
            || t <= _t[_count - 1]
            || t - _t[_count - 1] > MaxGapSeconds;
        if (restart)
        {
            _count = 0;
            Velocity = default;
        }

        _sector = sector;
        if (_count == WindowSize)
        {
            for (int i = 1; i < WindowSize; i++)
            {
                _t[i - 1] = _t[i];
                _x[i - 1] = _x[i];
                _y[i - 1] = _y[i];
                _z[i - 1] = _z[i];
            }

            _count--;
        }

        _t[_count] = t;
        _x[_count] = px * MetresPerUnit;
        _y[_count] = py * MetresPerUnit;
        _z[_count] = pz * MetresPerUnit;
        _count++;
        if (_count >= 2)
        {
            Velocity = (Slope(_x), Slope(_y), Slope(_z));
        }

        return Velocity;
    }

    private double Slope(double[] values)
    {
        double tMean = 0;
        double vMean = 0;
        for (int i = 0; i < _count; i++)
        {
            tMean += _t[i];
            vMean += values[i];
        }

        tMean /= _count;
        vMean /= _count;
        double num = 0;
        double den = 0;
        for (int i = 0; i < _count; i++)
        {
            double dt = _t[i] - tMean;
            num += dt * (values[i] - vMean);
            den += dt * dt;
        }

        return den > 0 ? num / den : 0;
    }
}
