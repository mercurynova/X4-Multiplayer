using X4MP.Core.Session;

namespace X4MP.Core.Tests.Session;

public class ClockSyncTests
{
    [Fact]
    public void SymmetricPathGivesHalfTheRoundTripAndTheTrueOffset()
    {
        // Server sends at 1_000_000 us. One way takes 10 ms. The node clock runs 5 s ahead and replies at once.
        const long serverSend = 1_000_000;
        const long offset = 5_000_000;
        long nodeRecv = serverSend + 10_000 + offset;
        long nodeReply = nodeRecv;
        long serverRecv = serverSend + 20_000;

        Assert.Equal(20_000, ClockSync.Rtt(serverSend, nodeRecv, nodeReply, serverRecv));
        Assert.Equal(offset, ClockSync.Offset(serverSend, nodeRecv, nodeReply, serverRecv));

        var clock = new ClockSync();
        Assert.True(clock.AddSample(serverSend, nodeRecv, nodeReply, serverRecv));
        Assert.Equal(20_000, clock.LatestRttUs);
        Assert.Equal(20_000, clock.MinRttUs);
        Assert.Equal(20.0, clock.SmoothedRttMs);
        Assert.Equal(offset, clock.OffsetUs);
    }

    [Fact]
    public void NodeHoldingTheMessageIsSubtractedFromTheRtt()
    {
        // 8 ms each way plus 30 ms inside the node: the network RTT is 16 ms, not 46.
        const long send = 0;
        long recv = 8_000 + 100;
        long reply = recv + 30_000;
        long back = 8_000 + 30_000 + 8_000;
        Assert.Equal(16_000, ClockSync.Rtt(send, recv, reply, back));
        Assert.Equal(100, ClockSync.Offset(send, recv, reply, back));
    }

    [Fact]
    public void AsymmetricPathBiasesTheOffsetByHalfTheAsymmetry()
    {
        // 30 ms out, 10 ms back, clocks identical: the estimate is off by (30-10)/2 = 10 ms.
        long offset = ClockSync.Offset(0, 30_000, 30_000, 40_000);
        Assert.Equal(10_000, offset);
    }

    [Fact]
    public void TheLowestRttSampleDecidesTheOffset()
    {
        var clock = new ClockSync();

        // A congested exchange (200 ms RTT, asymmetric so its offset is wrong by 90 ms) ...
        clock.AddSample(0, 190_000 + 1_000, 190_000 + 1_000, 200_000);
        long congested = clock.OffsetUs;

        // ... then a clean one (4 ms RTT) with the true offset of 1 ms.
        clock.AddSample(1_000_000, 1_000_000 + 2_000 + 1_000, 1_000_000 + 2_000 + 1_000, 1_004_000);

        Assert.NotEqual(1_000, congested);
        Assert.Equal(4_000, clock.MinRttUs);
        Assert.Equal(1_000, clock.OffsetUs);

        // A later, worse sample does not displace the best one.
        clock.AddSample(2_000_000, 2_050_000, 2_050_000, 2_100_000);
        Assert.Equal(1_000, clock.OffsetUs);
        Assert.Equal(100_000, clock.LatestRttUs);
    }

    [Fact]
    public void TheBestSampleAgesOutOfTheWindow()
    {
        var clock = new ClockSync(window: 4);
        clock.AddSample(0, 1_000 + 777, 1_000 + 777, 2_000); // best: RTT 2 ms, offset 777 us
        Assert.Equal(777, clock.OffsetUs);

        for (int i = 1; i <= 4; i++)
        {
            long t = i * 1_000_000L;
            clock.AddSample(t, t + 5_000 + 9_000, t + 5_000 + 9_000, t + 10_000); // RTT 10 ms, offset 9 ms
        }

        Assert.Equal(10_000, clock.MinRttUs);
        Assert.Equal(9_000, clock.OffsetUs);
    }

    [Fact]
    public void RttIsSmoothedWithAnEwmaOfOneEighth()
    {
        var clock = new ClockSync();
        clock.AddSample(0, 0, 0, 80_000);
        Assert.Equal(80_000, clock.SmoothedRttUs);

        clock.AddSample(1_000_000, 1_000_000, 1_000_000, 1_000_000 + 160_000);
        Assert.Equal(80_000 + (160_000 - 80_000) / 8.0, clock.SmoothedRttUs, 3);
    }

    [Fact]
    public void ImplausibleSamplesAreRejected()
    {
        var clock = new ClockSync();
        Assert.False(clock.AddSample(1_000, 0, 5_000, 2_000)); // node held it longer than the whole round trip: negative RTT
        Assert.False(clock.AddSample(0, 0, 0, ClockSync.MaxPlausibleRttUs + 1));
        Assert.Equal(2, clock.Rejected);
        Assert.False(clock.HasSample);
        Assert.Equal(0, clock.SmoothedRttUs);
    }

    [Fact]
    public void ResetForgetsEverything()
    {
        var clock = new ClockSync();
        clock.AddSample(0, 500, 500, 1_000);
        clock.Reset();
        Assert.False(clock.HasSample);
        Assert.Equal(0, clock.OffsetUs);
        Assert.Equal(0, clock.TotalSamples);
    }
}
