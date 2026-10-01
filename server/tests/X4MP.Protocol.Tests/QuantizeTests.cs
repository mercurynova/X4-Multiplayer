namespace X4MP.Protocol.Tests;

public class QuantizeTests
{
    private static ViolationCode CodeOf(Action act) => Assert.Throws<ProtocolViolation>(act).Code;

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(1.0, 64)]
    [InlineData(-1.0, -64)]
    [InlineData(0.0078125, 1)]       // 1/128 m = 0.5 step -> away from zero
    [InlineData(-0.0078125, -1)]
    [InlineData(0.0078124, 0)]
    [InlineData(500_000.0, 32_000_000)]
    [InlineData(33_554_431.984375, int.MaxValue)]
    [InlineData(-33_554_432.0, int.MinValue)]
    public void PositionVectors(double metres, int expected) => Assert.Equal(expected, Quantize.Position(metres));

    [Theory]
    [InlineData(33_554_432.0)]
    [InlineData(-33_554_432.02)]
    [InlineData(1e30)]
    public void PositionOutOfRangeIsRejected(double metres) =>
        Assert.Equal(ViolationCode.InvalidValue, CodeOf(() => Quantize.Position(metres)));

    [Fact]
    public void PositionRoundTripErrorIsBelowHalfAStep()
    {
        var rng = new Random(1);
        for (int i = 0; i < 10_000; i++)
        {
            double m = (rng.NextDouble() - 0.5) * 1_000_000;
            Assert.InRange(Math.Abs(Quantize.PositionToMetres(Quantize.Position(m)) - m), 0, 0.5 / 64 + 1e-9);
        }
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(Math.PI / 2, 16384)]
    [InlineData(-Math.PI / 2, -16384)]
    [InlineData(Math.PI, -32768)]            // +pi wraps onto -pi
    [InlineData(-Math.PI, -32768)]
    [InlineData(2 * Math.PI, 0)]
    [InlineData(-2 * Math.PI, 0)]
    [InlineData(3 * Math.PI / 2, -16384)]    // wraps
    [InlineData(-3 * Math.PI / 2, 16384)]
    [InlineData(Math.PI / 2 + 2 * Math.PI, 16384)]
    [InlineData(7 * Math.PI, -32768)]
    [InlineData(1e-9, 0)]
    public void RotationVectorsIncludingWrap(double radians, int expected) =>
        Assert.Equal((short)expected, Quantize.Rotation(radians));

    [Fact]
    public void RotationSmallestStepIsAboutPoint0055Degrees()
    {
        double step = Math.PI / 32768;
        Assert.Equal(1, Quantize.Rotation(step));
        Assert.Equal(1, Quantize.Rotation(step * 0.5));   // half step rounds away from zero
        Assert.Equal(0, Quantize.Rotation(step * 0.49));
        Assert.Equal(-1, Quantize.Rotation(-step * 0.5));
        Assert.InRange(step * 180 / Math.PI, 0.0054, 0.0056);
    }

    [Fact]
    public void RotationRoundTripWrapsIntoMinusPiToPi()
    {
        for (double a = -10; a <= 10; a += 0.37)
        {
            double back = Quantize.RotationToRadians(Quantize.Rotation(a));
            Assert.InRange(back, -Math.PI, Math.PI);
            double diff = Math.Abs(Math.IEEERemainder(back - a, 2 * Math.PI));
            Assert.True(diff <= Math.PI / 32768 + 1e-9, $"angle {a}: diff {diff}");
        }
    }

    [Theory]
    [InlineData(0.0, false, 0)]
    [InlineData(1.0, false, 4)]
    [InlineData(-1.0, false, -4)]
    [InlineData(0.125, false, 1)]
    [InlineData(-0.125, false, -1)]
    [InlineData(8191.75, false, 32767)]
    [InlineData(-8192.0, false, -32768)]
    [InlineData(4.0, true, 1)]
    [InlineData(2.0, true, 1)]               // 0.5 step -> away from zero
    [InlineData(-2.0, true, -1)]
    [InlineData(1.99, true, 0)]
    [InlineData(131_068.0, true, 32767)]
    [InlineData(-131_072.0, true, -32768)]
    public void VelocityVectors(double mps, bool coarse, int expected) =>
        Assert.Equal((short)expected, Quantize.Velocity(mps, coarse));

    [Theory]
    [InlineData(8192.0, false)]
    [InlineData(-8192.25, false)]
    [InlineData(131_072.0, true)]
    public void VelocityOutOfRangeIsRejected(double mps, bool coarse) =>
        Assert.Equal(ViolationCode.InvalidValue, CodeOf(() => Quantize.Velocity(mps, coarse)));

    [Fact]
    public void CoarseVelocityIsChosenOnlyWhenNeeded()
    {
        Assert.False(Quantize.NeedsCoarseVelocity(8191.75, -8191.75, 0));
        Assert.True(Quantize.NeedsCoarseVelocity(0, 8192, 0));
        Assert.True(Quantize.NeedsCoarseVelocity(0, 0, -20_000));
        Assert.Equal(4.0, Quantize.VelocityToMps(1, coarse: true));
        Assert.Equal(0.25, Quantize.VelocityToMps(1, coarse: false));
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(1.0, 255)]
    [InlineData(0.5, 128)]                   // 127.5 -> away from zero
    [InlineData(2.0, 255)]                   // clamped
    [InlineData(-0.5, 0)]
    [InlineData(1.0 / 255, 1)]
    public void FractionVectors(double f, int expected) => Assert.Equal((byte)expected, Quantize.Fraction(f));

    [Theory]
    [InlineData(0L, 0)]
    [InlineData(499L, 0)]
    [InlineData(500L, 1)]
    [InlineData(-499L, 0)]
    [InlineData(-500L, -1)]
    [InlineData(1_500L, 2)]
    [InlineData(-1_500L, -2)]
    [InlineData(32_767_499L, 32767)]
    [InlineData(-32_768_000L, -32768)]
    public void TimeOffsetVectors(long deltaUs, int expectedMs)
    {
        const long reference = 5_000_000_000;
        Assert.Equal((short)expectedMs, Quantize.TimeOffsetMs(reference + deltaUs, reference));
    }

    [Theory]
    [InlineData(32_767_500L)]
    [InlineData(-32_768_501L)]
    public void TimeOffsetOutOfRangeIsRejected(long deltaUs) =>
        Assert.Equal(ViolationCode.InvalidValue, CodeOf(() => Quantize.TimeOffsetMs(1_000_000_000 + deltaUs, 1_000_000_000)));

    [Fact]
    public void TimeRoundTrip() => Assert.Equal(1_007_000L, Quantize.TimeFromOffsetMs(7, 1_000_000));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NaNAndInfinityAreRejectedEverywhere(double bad)
    {
        Assert.Equal(ViolationCode.InvalidValue, CodeOf(() => Quantize.Position(bad)));
        Assert.Equal(ViolationCode.InvalidValue, CodeOf(() => Quantize.Rotation(bad)));
        Assert.Equal(ViolationCode.InvalidValue, CodeOf(() => Quantize.Velocity(bad, false)));
        Assert.Equal(ViolationCode.InvalidValue, CodeOf(() => Quantize.Velocity(bad, true)));
        Assert.Equal(ViolationCode.InvalidValue, CodeOf(() => Quantize.Fraction(bad)));
        Assert.Equal(ViolationCode.InvalidValue, CodeOf(() => Quantize.NeedsCoarseVelocity(bad, 0, 0)));
        Assert.Equal(ViolationCode.InvalidValue, CodeOf(() => Quantize.RequireFinite(bad, "x")));
    }

    [Fact]
    public void SinglePrecisionNaNIsAlsoRejected() =>
        Assert.Equal(ViolationCode.InvalidValue, CodeOf(() => Quantize.Position(float.NaN)));
}
