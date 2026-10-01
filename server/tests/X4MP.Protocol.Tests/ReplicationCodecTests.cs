using System.Buffers.Binary;

namespace X4MP.Protocol.Tests;

public class ReplicationCodecTests
{
    private const ReplicationMask All = (ReplicationMask)0xFF;

    private static ReplicationEntry Entry(ReplicationMask mask, uint netId = 0x01020304) => new()
    {
        NetId = netId, Mask = mask,
        Sector = (mask & ReplicationMask.Sector) != 0 ? (ushort)0x1234 : (ushort)0,
        PosX = (mask & ReplicationMask.Pos) != 0 ? 64 : 0,
        PosY = (mask & ReplicationMask.Pos) != 0 ? -64 : 0,
        PosZ = (mask & ReplicationMask.Pos) != 0 ? 1_000_000 : 0,
        Yaw = (mask & ReplicationMask.Rot) != 0 ? (short)16384 : (short)0,
        Pitch = (mask & ReplicationMask.Rot) != 0 ? (short)-16384 : (short)0,
        Roll = (mask & ReplicationMask.Rot) != 0 ? short.MinValue : (short)0,
        VelX = (mask & ReplicationMask.Vel) != 0 ? (short)4 : (short)0,
        VelY = (mask & ReplicationMask.Vel) != 0 ? (short)-4 : (short)0,
        VelZ = (mask & ReplicationMask.Vel) != 0 ? short.MaxValue : (short)0,
        StateFlags = (mask & ReplicationMask.Flags) != 0 ? (ushort)0x8001 : (ushort)0,
        Hull = (mask & ReplicationMask.Status) != 0 ? (byte)255 : (byte)0,
        Shield = (mask & ReplicationMask.Status) != 0 ? (byte)7 : (byte)0,
        TimeMs = (mask & ReplicationMask.Time) != 0 ? (short)-20 : (short)0,
        Ext = (mask & ReplicationMask.Ext) != 0 ? [0xAA, 0xBB, 0xCC] : null,
    };

    /// <summary>Independent reference encoder: appends raw little-endian values in bit order.</summary>
    private static byte[] Reference(in ReplicationEntry e)
    {
        var b = new List<byte>();
        void U16(ushort v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }
        void U32(uint v) { U16((ushort)v); U16((ushort)(v >> 16)); }
        U32(e.NetId);
        b.Add((byte)e.Mask);
        var m = e.Mask;
        if ((m & ReplicationMask.Sector) != 0) U16(e.Sector);
        if ((m & ReplicationMask.Pos) != 0) { U32((uint)e.PosX); U32((uint)e.PosY); U32((uint)e.PosZ); }
        if ((m & ReplicationMask.Rot) != 0) { U16((ushort)e.Yaw); U16((ushort)e.Pitch); U16((ushort)e.Roll); }
        if ((m & ReplicationMask.Vel) != 0) { U16((ushort)e.VelX); U16((ushort)e.VelY); U16((ushort)e.VelZ); }
        if ((m & ReplicationMask.Flags) != 0) U16(e.StateFlags);
        if ((m & ReplicationMask.Status) != 0) { b.Add(e.Hull); b.Add(e.Shield); }
        if ((m & ReplicationMask.Time) != 0) U16((ushort)e.TimeMs);
        if ((m & ReplicationMask.Ext) != 0) { b.Add((byte)e.Ext!.Length); b.AddRange(e.Ext); }
        return [.. b];
    }

    public static IEnumerable<object[]> AllMasks() => Enumerable.Range(0, 256).Select(m => new object[] { (byte)m });

    [Theory]
    [MemberData(nameof(AllMasks))]
    public void EveryMaskCombinationIsByteExactAndRoundTrips(byte maskByte)
    {
        var e = Entry((ReplicationMask)maskByte);
        var expected = Reference(e);

        var buf = new byte[ReplicationCodec.GetSize(e)];
        Assert.Equal(expected.Length, buf.Length);
        Assert.Equal(expected.Length, ReplicationCodec.Write(buf, e));
        Assert.Equal(expected, buf);

        var back = ReplicationCodec.Read(buf, out int consumed);
        Assert.Equal(buf.Length, consumed);
        Assert.Equal(e.NetId, back.NetId);
        Assert.Equal(e.Mask, back.Mask);
        Assert.Equal(Reference(back), expected);
        Assert.Equal(e.Ext, back.Ext);
    }

    [Fact]
    public void LiteralVectorMovingShipIs25Bytes()
    {
        // POS | VEL | TIME
        var e = Entry(ReplicationMask.Pos | ReplicationMask.Vel | ReplicationMask.Time);
        var bytes = ReplicationCodec.Encode([e]);
        Assert.Equal(25, bytes.Length);
        Assert.Equal(
            "04030201" + "4A" + "40000000" + "C0FFFFFF" + "40420F00" + "0400" + "FCFF" + "FF7F" + "ECFF",
            Convert.ToHexString(bytes));
    }

    [Fact]
    public void LiteralVectorTurningShipIs31Bytes()
    {
        var e = Entry(ReplicationMask.Pos | ReplicationMask.Rot | ReplicationMask.Vel | ReplicationMask.Time);
        var bytes = ReplicationCodec.Encode([e]);
        Assert.Equal(31, bytes.Length);
        Assert.Equal(
            "04030201" + "4E" + "40000000" + "C0FFFFFF" + "40420F00" + "0040" + "00C0" + "0080" + "0400" + "FCFF" + "FF7F" + "ECFF",
            Convert.ToHexString(bytes));
    }

    [Fact]
    public void LiteralVectorStatusOnlyIs7Bytes()
    {
        var e = Entry(ReplicationMask.Status);
        Assert.Equal("04030201" + "20" + "FF07", Convert.ToHexString(ReplicationCodec.Encode([e])));
    }

    [Fact]
    public void LiteralVectorFullKeyframeWithoutExtIs37Bytes()
    {
        var e = Entry(ReplicationMask.Sector | ReplicationMask.Pos | ReplicationMask.Rot | ReplicationMask.Vel
                      | ReplicationMask.Flags | ReplicationMask.Status | ReplicationMask.Time);
        var bytes = ReplicationCodec.Encode([e]);
        Assert.Equal(37, bytes.Length);
        Assert.Equal(
            "04030201" + "7F" + "3412" + "40000000" + "C0FFFFFF" + "40420F00" + "0040" + "00C0" + "0080"
            + "0400" + "FCFF" + "FF7F" + "0180" + "FF07" + "ECFF",
            Convert.ToHexString(bytes));
    }

    [Fact]
    public void ExtIsSkippedByReadersAndFollowingEntriesStayAligned()
    {
        var withExt = Entry(ReplicationMask.Status | ReplicationMask.Ext, 7);
        var next = Entry(ReplicationMask.Sector, 8);
        var bytes = ReplicationCodec.Encode([withExt, next]);
        Assert.Equal("07000000" + "A0" + "FF07" + "03AABBCC" + "08000000" + "01" + "3412", Convert.ToHexString(bytes));

        var list = ReplicationCodec.Decode(bytes, 2);
        Assert.Equal(7u, list[0].NetId);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, list[0].Ext);
        Assert.Equal(8u, list[1].NetId);
        Assert.Equal((ushort)0x1234, list[1].Sector);
    }

    [Fact]
    public void EmptyExtIsOneByte()
    {
        var e = new ReplicationEntry { NetId = 9, Mask = ReplicationMask.Ext, Ext = [] };
        Assert.Equal("09000000" + "80" + "00", Convert.ToHexString(ReplicationCodec.Encode([e])));
        var back = ReplicationCodec.Decode(ReplicationCodec.Encode([e]), 1)[0];
        Assert.NotNull(back.Ext);
        Assert.Empty(back.Ext!);
    }

    [Fact]
    public void QuantizedValuesFlowThroughTheCodec()
    {
        var e = new ReplicationEntry
        {
            NetId = 42, Mask = ReplicationMask.Pos | ReplicationMask.Rot | ReplicationMask.Vel | ReplicationMask.Status | ReplicationMask.Time,
            PosX = Quantize.Position(1234.5), PosY = Quantize.Position(-0.25), PosZ = Quantize.Position(500_000),
            Yaw = Quantize.Rotation(Math.PI), Pitch = Quantize.Rotation(-Math.PI / 2), Roll = Quantize.Rotation(3 * Math.PI / 2),
            VelX = Quantize.Velocity(250, false), VelY = Quantize.Velocity(-30000, true), VelZ = 0,
            Hull = Quantize.Fraction(0.5), Shield = Quantize.Fraction(1), TimeMs = Quantize.TimeOffsetMs(1_000_500, 1_000_000),
        };
        var back = ReplicationCodec.Decode(ReplicationCodec.Encode([e]), 1)[0];
        Assert.Equal(79008, back.PosX);
        Assert.Equal(-16, back.PosY);
        Assert.Equal(32_000_000, back.PosZ);
        Assert.Equal(short.MinValue, back.Yaw);
        Assert.Equal((short)-16384, back.Pitch);
        Assert.Equal((short)-16384, back.Roll);
        Assert.Equal((short)1000, back.VelX);
        Assert.Equal((short)-7500, back.VelY);
        Assert.Equal((byte)128, back.Hull);
        Assert.Equal((byte)255, back.Shield);
        Assert.Equal((short)1, back.TimeMs);
    }

    [Fact]
    public void BufferReadsAreLittleEndianRegardlessOfHost()
    {
        var bytes = ReplicationCodec.Encode([Entry(ReplicationMask.Sector)]);
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(5)));
    }

    [Fact]
    public void InvalidNetIdsAreRejectedOnBothSides()
    {
        foreach (uint bad in new uint[] { 0, 0xFFFFFFFF })
        {
            var e = new ReplicationEntry { NetId = bad, Mask = ReplicationMask.Status };
            Assert.Equal(ViolationCode.MalformedReplication, Assert.Throws<ProtocolViolation>(() => ReplicationCodec.Encode([e])).Code);

            var raw = new byte[7];
            BinaryPrimitives.WriteUInt32LittleEndian(raw, bad);
            raw[4] = (byte)ReplicationMask.Status;
            Assert.Equal(ViolationCode.MalformedReplication, Assert.Throws<ProtocolViolation>(() => ReplicationCodec.Decode(raw, 1)).Code);
        }
    }

    [Fact]
    public void EveryTruncationIsAViolationNeverAnotherException()
    {
        var full = Entry(All);
        var bytes = ReplicationCodec.Encode([full]);
        for (int n = 0; n < bytes.Length; n++)
            Assert.Equal(ViolationCode.MalformedReplication, Assert.Throws<ProtocolViolation>(() => ReplicationCodec.Decode(bytes.AsSpan(0, n), 1)).Code);
    }

    [Fact]
    public void ExtLengthOverrunIsAViolation()
    {
        var raw = new byte[] { 5, 0, 0, 0, 0x80, 200, 1, 2, 3 };
        Assert.Equal(ViolationCode.MalformedReplication, Assert.Throws<ProtocolViolation>(() => ReplicationCodec.Decode(raw, 1)).Code);
    }

    [Fact]
    public void EntryCountMustMatchTheBytes()
    {
        var bytes = ReplicationCodec.Encode([Entry(ReplicationMask.Status, 1), Entry(ReplicationMask.Status, 2)]);
        Assert.Equal(2, ReplicationCodec.Decode(bytes, 2).Count);
        Assert.Throws<ProtocolViolation>(() => ReplicationCodec.Decode(bytes, 1));       // trailing bytes
        Assert.Throws<ProtocolViolation>(() => ReplicationCodec.Decode(bytes, 3));       // too few
        Assert.Throws<ProtocolViolation>(() => ReplicationCodec.Decode(bytes, -1));
        Assert.Throws<ProtocolViolation>(() => ReplicationCodec.Decode(bytes, int.MaxValue));
        Assert.Empty(ReplicationCodec.Decode([], 0));
    }

    [Fact]
    public void WriterRejectsInconsistentExt()
    {
        Assert.Throws<ProtocolViolation>(() => ReplicationCodec.Encode([new ReplicationEntry { NetId = 1, Mask = ReplicationMask.Status, Ext = [1] }]));
        Assert.Throws<ProtocolViolation>(() => ReplicationCodec.Encode([new ReplicationEntry { NetId = 1, Mask = ReplicationMask.Ext, Ext = new byte[256] }]));
    }

    [Fact]
    public void RandomGarbageNeverEscapesAsAnotherExceptionType()
    {
        var rng = new Random(5);
        for (int i = 0; i < 5000; i++)
        {
            var buf = new byte[rng.Next(0, 80)];
            rng.NextBytes(buf);
            try
            {
                ReplicationCodec.Decode(buf, rng.Next(0, 6));
            }
            catch (ProtocolViolation)
            {
            }
        }
    }
}
