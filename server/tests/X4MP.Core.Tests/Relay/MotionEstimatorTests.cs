using X4MP.Core.Relay;
using X4MP.Proto;

namespace X4MP.Core.Tests.Relay;

public class MotionEstimatorTests
{
    private static ulong Us(double ms) => (ulong)(ms * 1000);

    [Fact]
    public void ConstantMotionGivesItsExactVelocity()
    {
        var estimator = new MotionEstimator();
        estimator.Add(1, 0, Us(0), 0, 0, 0);
        estimator.Add(1, 0, Us(50), 640, 320, -64);
        var v = estimator.Add(1, 0, Us(100), 1280, 640, -128);

        Assert.Equal(200.0, v.X, 9);
        Assert.Equal(100.0, v.Y, 9);
        Assert.Equal(-20.0, v.Z, 9);
        Assert.Equal(3, estimator.Samples);
    }

    [Fact]
    public void TheWindowKeepsOnlyTheLastThreeSamples()
    {
        var estimator = new MotionEstimator();
        for (int i = 0; i < 10; i++)
        {
            estimator.Add(1, 0, Us(i * 50), i * 640, 0, 0);
        }

        var v = estimator.Add(1, 0, Us(500), 6400 + 3200, 0, 0); // a jump of 60 m in the last step

        Assert.Equal(3, estimator.Samples);
        Assert.Equal(700.0, v.X, 6); // (150 m - 80 m) / 0.1 s over the last three samples only
    }

    [Fact]
    public void AJitteredSampleIsSmoothedNotAmplified()
    {
        var estimator = new MotionEstimator();
        estimator.Add(1, 0, Us(0), 0, 0, 0);
        estimator.Add(1, 0, Us(50), 640 + 64, 0, 0); // 1 m of noise on the middle sample
        var v = estimator.Add(1, 0, Us(100), 1280, 0, 0);

        Assert.Equal(200.0, v.X, 9); // symmetric noise cancels over the three samples; the last-two-sample difference would say 180
    }

    [Theory]
    [InlineData("sector")]
    [InlineData("teleport")]
    [InlineData("gap")]
    [InlineData("time")]
    public void ADiscontinuityRestartsTheWindow(string kind)
    {
        var estimator = new MotionEstimator();
        estimator.Add(1, 0, Us(0), 0, 0, 0);
        estimator.Add(1, 0, Us(50), 640, 0, 0);

        var v = kind switch
        {
            "sector" => estimator.Add(2, 0, Us(100), 99999, 0, 0),
            "teleport" => estimator.Add(1, (ushort)StateFlags.Teleport, Us(100), 99999, 0, 0),
            "gap" => estimator.Add(1, 0, Us(5000), 99999, 0, 0),
            _ => estimator.Add(1, 0, Us(50), 99999, 0, 0), // time did not advance
        };

        Assert.Equal(1, estimator.Samples);
        Assert.Equal(0.0, v.X);
    }

    [Fact]
    public void ADockedShipHasNoVelocity()
    {
        var estimator = new MotionEstimator();
        estimator.Add(1, 0, Us(0), 0, 0, 0);
        estimator.Add(1, 0, Us(50), 640, 0, 0);

        var v = estimator.Add(1, (ushort)StateFlags.Docked, Us(100), 5, 5, 5);

        Assert.Equal((0.0, 0.0, 0.0), v);
        Assert.Equal(0, estimator.Samples);
    }
}
