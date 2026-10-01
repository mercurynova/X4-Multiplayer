using X4MP.Protocol;

namespace X4MP.Protocol.Tests;

public sealed class DatagramReceiveWindowTests
{
    [Fact]
    public void InOrderDatagramsAreAcceptedAndAckBitsFollow()
    {
        var w = new DatagramReceiveWindow();
        for (uint s = 1; s <= 5; s++)
            Assert.True(w.Accept(s));
        Assert.Equal(5u, w.Ack);
        Assert.Equal(0b1111u, w.AckBits); // 4,3,2,1
        Assert.Equal(0, w.Missing);
    }

    [Fact]
    public void AGapShowsAsAZeroBitAndAnEstimatedLoss()
    {
        var w = new DatagramReceiveWindow();
        w.Accept(1);
        w.Accept(2);
        w.Accept(5);
        Assert.Equal(5u, w.Ack);
        Assert.Equal(0b1100u, w.AckBits & 0b1111); // bit0 = 4 and bit1 = 3 missing, bit2 = 2 and bit3 = 1 present
        Assert.Equal(2, w.Missing);
        Assert.InRange(w.LossPercent, 39f, 41f);
    }

    [Fact]
    public void DuplicatesAndDatagramsOlderThanTheWindowAreRefused()
    {
        var w = new DatagramReceiveWindow();
        Assert.True(w.Accept(100));
        Assert.False(w.Accept(100));
        Assert.True(w.Accept(70)); // late but inside the 64 window
        Assert.False(w.Accept(70));
        Assert.False(w.Accept(36)); // 64 behind
        Assert.Equal(2, w.Duplicates);
        Assert.Equal(1, w.TooOld);
    }

    [Fact]
    public void ALateDatagramFillsItsBit()
    {
        var w = new DatagramReceiveWindow();
        w.Accept(10);
        w.Accept(12);
        Assert.Equal(0b10u, w.AckBits & 0b11); // 11 missing, 10 present
        Assert.True(w.Accept(11));
        Assert.Equal(0b11u, w.AckBits & 0b11);
    }

    [Fact]
    public void SequenceNumbersWrapAround()
    {
        var w = new DatagramReceiveWindow();
        Assert.True(w.Accept(uint.MaxValue - 1));
        Assert.True(w.Accept(uint.MaxValue));
        Assert.True(w.Accept(0));
        Assert.True(w.Accept(1));
        Assert.Equal(1u, w.Ack);
        Assert.Equal(0b111u, w.AckBits);
        Assert.False(w.Accept(uint.MaxValue));
    }

    [Fact]
    public void AHugeJumpClearsTheWindow()
    {
        var w = new DatagramReceiveWindow();
        w.Accept(1);
        Assert.True(w.Accept(1000));
        Assert.Equal(0u, w.AckBits);
        Assert.False(w.Accept(900)); // too old now
    }
}
