namespace X4MP.Protocol;

/// <summary>
/// Quantisation of protocol.md section 11, shared by EntityState, PlayerState and the Replication codec.
/// <para>
/// Rounding is round-half-away-from-zero (C++ <c>std::llround</c>), computed in <c>double</c>. Every
/// float input must be finite: NaN and infinity (and values that do not fit the target integer) throw
/// <see cref="ProtocolViolation"/> with <see cref="ViolationCode.InvalidValue"/> (the server rejects them
/// in any float field, server-design.md 7.3).
/// </para>
/// </summary>
public static class Quantize
{
    /// <summary>Position unit: 1/64 m. Range +-33,554,432 m (int32).</summary>
    public const double PositionStepsPerMetre = 64.0;

    /// <summary>Rotation scale: 32768 counts per half turn. 65536 counts = 2*pi, so it wraps.</summary>
    public const double RotationCountsPerRadian = 32768.0 / Math.PI;

    /// <summary>Fine velocity: 0.25 m/s per count (+-8191.75 m/s).</summary>
    public const double VelocityStepsPerMps = 4.0;

    /// <summary>Coarse velocity (StateFlags.VelCoarse): 4 m/s per count (+-131,068 m/s).</summary>
    public const double CoarseMpsPerStep = 4.0;

    /// <summary>Largest |m/s| representable with fine velocity.</summary>
    public const double MaxFineVelocityMps = short.MaxValue / VelocityStepsPerMps;

    /// <summary>Replication TIME is an i16 millisecond offset (about +-32 s).</summary>
    public const int MaxTimeOffsetMs = short.MaxValue;

    public static double RequireFinite(double value, string what)
    {
        if (!double.IsFinite(value))
            throw new ProtocolViolation(ViolationCode.InvalidValue, $"{what} is not finite ({value})");
        return value;
    }

    private static double RoundAway(double x) => Math.Round(x, MidpointRounding.AwayFromZero);

    // ---- position ----

    /// <summary>Metres (sector-relative) to i32 1/64 m.</summary>
    public static int Position(double metres)
    {
        double q = RoundAway(RequireFinite(metres, "position") * PositionStepsPerMetre);
        if (q < int.MinValue || q > int.MaxValue)
            throw new ProtocolViolation(ViolationCode.InvalidValue, $"position {metres} m is outside +-33,554 km");
        return (int)q;
    }

    public static double PositionToMetres(int quantised) => quantised / PositionStepsPerMetre;

    // ---- rotation ----

    /// <summary>
    /// Radians to i16 (rad * 32768/pi), wrapping: any angle is accepted and reduced modulo 2*pi, so pi and
    /// -pi both give -32768 and 2*pi gives 0.
    /// </summary>
    public static short Rotation(double radians)
    {
        double q = RoundAway(RequireFinite(radians, "rotation") * RotationCountsPerRadian);
        if (Math.Abs(q) >= 4.0e18)
            throw new ProtocolViolation(ViolationCode.InvalidValue, $"rotation {radians} rad is too large to wrap");
        return unchecked((short)(long)q);
    }

    /// <summary>i16 back to radians in [-pi, pi).</summary>
    public static double RotationToRadians(short quantised) => quantised / RotationCountsPerRadian;

    // ---- velocity ----

    /// <summary>m/s to i16 (fine: m/s * 4, coarse: m/s / 4). Out of range throws; see <see cref="NeedsCoarseVelocity"/>.</summary>
    public static short Velocity(double metresPerSecond, bool coarse)
    {
        double v = RequireFinite(metresPerSecond, "velocity");
        double q = RoundAway(coarse ? v / CoarseMpsPerStep : v * VelocityStepsPerMps);
        if (q < short.MinValue || q > short.MaxValue)
            throw new ProtocolViolation(ViolationCode.InvalidValue, $"velocity {metresPerSecond} m/s is outside the {(coarse ? "coarse" : "fine")} range");
        return (short)q;
    }

    public static double VelocityToMps(short quantised, bool coarse) =>
        coarse ? quantised * CoarseMpsPerStep : quantised / VelocityStepsPerMps;

    /// <summary>True when any component exceeds the fine range, so the entity must set StateFlags.VelCoarse.</summary>
    public static bool NeedsCoarseVelocity(double vx, double vy, double vz) =>
        Math.Abs(RequireFinite(vx, "velocity")) > MaxFineVelocityMps
        || Math.Abs(RequireFinite(vy, "velocity")) > MaxFineVelocityMps
        || Math.Abs(RequireFinite(vz, "velocity")) > MaxFineVelocityMps;

    // ---- hull and shield ----

    /// <summary>Fraction 0..1 (values outside are clamped; NaN/infinity throw) to u8 0..255.</summary>
    public static byte Fraction(double fraction)
    {
        double f = RequireFinite(fraction, "hull/shield fraction");
        return (byte)RoundAway(Math.Clamp(f, 0.0, 1.0) * 255.0);
    }

    public static double FractionFromByte(byte quantised) => quantised / 255.0;

    // ---- time ----

    /// <summary>
    /// Replication TIME: (sampleTimeUs - referenceTimeUs) in whole milliseconds, rounded half away from zero.
    /// Throws if the offset does not fit an i16 (about +-32 s).
    /// </summary>
    public static short TimeOffsetMs(long sampleTimeUs, long referenceTimeUs)
    {
        long d;
        try
        {
            d = checked(sampleTimeUs - referenceTimeUs);
        }
        catch (OverflowException ex)
        {
            throw new ProtocolViolation(ViolationCode.InvalidValue, "time offset overflow", ex);
        }

        long ms = d >= 0 ? (d + 500) / 1000 : -((-d + 500) / 1000);
        if (ms < short.MinValue || ms > short.MaxValue)
            throw new ProtocolViolation(ViolationCode.InvalidValue, $"time offset {ms} ms does not fit i16");
        return (short)ms;
    }

    /// <summary>Inverse of <see cref="TimeOffsetMs"/>: reference + offset * 1000 microseconds.</summary>
    public static long TimeFromOffsetMs(short offsetMs, long referenceTimeUs) => referenceTimeUs + offsetMs * 1000L;
}
