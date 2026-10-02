using System.Buffers.Binary;
using X4MP.Proto;

namespace X4MP.Protocol.Tests;

public class FrameCodecTests
{
    private static readonly MessageRegistry Registry = MessageRegistry.Default;
    private static readonly string[] SplitStems = ["0x0005_Ping", "0x0003_Welcome", "0x0107_SaveChunk", "0x0208_Replication", "0x0003_Welcome"];
    private static readonly string[] TrickleStems = ["0x0003_Welcome", "0x0005_Ping"];

    public static IEnumerable<object[]> AllSamples() => SampleMessages.All.Select(s => new object[] { s.Stem });

    private static SampleMessage Sample(string stem) => SampleMessages.All.Single(s => s.Stem == stem);

    private static byte[] FrameFor(SampleMessage s) => FrameCodec.Encode(s.Type, s.Encode());

    [Fact]
    public void HeaderLayoutIsExact()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var frame = FrameCodec.Encode(MsgType.SaveChunk, payload);
        // len=5 LE | type 0x0107 LE | flags 0 | lane Bulk(2)
        Assert.Equal(new byte[] { 5, 0, 0, 0, 0x07, 0x01, 0, 2, 1, 2, 3, 4, 5 }, frame);
    }

    [Fact]
    public void LaneComesFromTheCatalog()
    {
        Assert.Equal(Lane.Control, Registry.GetDescriptor(MsgType.Ping).Lane);
        Assert.Equal(Lane.Realtime, Registry.GetDescriptor(MsgType.Replication).Lane);
        Assert.Equal(Lane.Realtime, Registry.GetDescriptor(MsgType.PlayerState).Lane);
        Assert.Equal(Lane.Bulk, Registry.GetDescriptor(MsgType.SaveChunk).Lane);
    }

    [Fact]
    public void RegistryCoversEveryMsgTypeExceptInvalidAndReserved()
    {
        var expected = Enum.GetValues<MsgType>().Where(t => t != MsgType.Invalid && t != MsgType.DamageReport).Order().ToArray();
        var actual = Registry.Descriptors.Select(d => d.Type).Order().ToArray();
        Assert.Equal(expected, actual);
        Assert.Equal(91, actual.Length);
    }

    [Fact]
    public void SamplesCoverEveryMsgType()
    {
        var covered = SampleMessages.All.Select(s => s.Type).Distinct().Order().ToArray();
        Assert.Equal(Registry.Descriptors.Select(d => d.Type).Order().ToArray(), covered);
        Assert.Equal(SampleMessages.All.Count, SampleMessages.All.Select(s => s.Stem).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(AllSamples))]
    public void EveryMessageRoundTripsThroughFrameAndRegistry(string stem)
    {
        var s = Sample(stem);
        var bytes = FrameFor(s);

        Assert.True(FrameCodec.TryDecode(bytes, out var frame, out int consumed));
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(s.Type, frame.Type);
        Assert.Equal(Registry.GetDescriptor(s.Type).Lane, frame.Lane);

        var decoded = Registry.Decode(frame);
        Assert.Equal(Registry.GetDescriptor(s.Type).ClrType, decoded.GetType());
        Assert.Equal(s.SourceJson, SampleMessage.ToJson(decoded));
    }

    [Fact]
    public void TypedDecodeWorksAndRejectsWrongType()
    {
        var s = Sample("0x0005_Ping");
        FrameCodec.TryDecode(FrameFor(s), out var frame, out _);
        Assert.Equal(1u, Registry.Decode<Ping>(frame).Seq);
        Assert.Equal(ViolationCode.MalformedPayload, Assert.Throws<ProtocolViolation>(() => Registry.Decode<Pong>(frame)).Code);
    }

    [Fact]
    public void ZeroLengthFrameIsAViolation()
    {
        var header = new byte[8];
        FrameCodec.WriteHeader(header, 0, MsgType.Ping, FrameOptions.None, Lane.Control);
        Assert.Equal(ViolationCode.ZeroLengthFrame, Assert.Throws<ProtocolViolation>(() => FrameCodec.ParseHeader(header)).Code);
        Assert.Equal(ViolationCode.ZeroLengthFrame, Assert.Throws<ProtocolViolation>(() => FrameCodec.TryDecode(header, out _, out _)).Code);
        Assert.Equal(ViolationCode.ZeroLengthFrame, Assert.Throws<ProtocolViolation>(() => Registry.Decode(MsgType.Ping, [])).Code);
    }

    [Theory]
    [InlineData(1048577u)]
    [InlineData(uint.MaxValue)]
    [InlineData(0x80000000u)]
    public async Task OversizedFrameIsAViolationBeforeAnyPayloadIsRead(uint len)
    {
        var header = new byte[8];
        FrameCodec.WriteHeader(header, len, MsgType.SaveChunk, FrameOptions.None, Lane.Bulk);

        Assert.Equal(ViolationCode.FrameTooLarge, Assert.Throws<ProtocolViolation>(() => FrameCodec.ParseHeader(header)).Code);

        // The stream holds ONLY the header. If the codec tried to allocate/read the payload first we would
        // get TruncatedFrame instead, so FrameTooLarge proves the check precedes allocation.
        using var stream = new MemoryStream(header);
        var ex = await Assert.ThrowsAsync<ProtocolViolation>(async () => await FrameCodec.ReadFrameAsync(stream));
        Assert.Equal(ViolationCode.FrameTooLarge, ex.Code);

        var reader = new FrameReader();
        reader.Append(header);
        Assert.Equal(ViolationCode.FrameTooLarge, Assert.Throws<ProtocolViolation>(() => reader.TryRead(out _)).Code);
    }

    [Fact]
    public void FrameAtExactlyMaxFrameBytesIsAccepted()
    {
        var header = new byte[8];
        FrameCodec.WriteHeader(header, 1024, MsgType.SaveChunk, FrameOptions.None, Lane.Bulk);
        var h = FrameCodec.ParseHeader(header, maxFrameBytes: 1024);
        Assert.Equal(1024u, h.PayloadLength);
        Assert.Equal(ViolationCode.FrameTooLarge, Assert.Throws<ProtocolViolation>(() => FrameCodec.ParseHeader(header, maxFrameBytes: 1023)).Code);
    }

    [Fact]
    public void EncodeRejectsEmptyAndOversizedPayloads()
    {
        Assert.Throws<ArgumentException>(() => FrameCodec.Encode(MsgType.Ping, ReadOnlySpan<byte>.Empty));
        Assert.Throws<ArgumentException>(() => FrameCodec.Encode(MsgType.Ping, new byte[17], maxFrameBytes: 16));
    }

    [Fact]
    public async Task TruncatedFramesNeedMoreDataAndTruncatedStreamsAreViolations()
    {
        var bytes = FrameFor(Sample("0x0003_Welcome"));
        for (int n = 0; n < bytes.Length; n++)
        {
            Assert.False(FrameCodec.TryDecode(bytes.AsSpan(0, n), out _, out int consumed));
            Assert.Equal(0, consumed);

            if (n == 0)
                continue;
            using var stream = new MemoryStream(bytes, 0, n);
            var ex = await Assert.ThrowsAsync<ProtocolViolation>(async () => await FrameCodec.ReadFrameAsync(stream));
            Assert.Equal(ViolationCode.TruncatedFrame, ex.Code);

            var reader = new FrameReader();
            reader.Append(bytes.AsSpan(0, n));
            Assert.False(reader.TryRead(out _));
            Assert.Equal(ViolationCode.TruncatedFrame, Assert.Throws<ProtocolViolation>(reader.CompleteOrThrow).Code);
        }

        using var empty = new MemoryStream([]);
        Assert.Null(await FrameCodec.ReadFrameAsync(empty));
        Assert.Throws<ProtocolViolation>(() => FrameCodec.ParseHeader(bytes.AsSpan(0, 7)));
    }

    [Theory]
    [InlineData(0x02)]
    [InlineData(0x80)]
    [InlineData(0xFE)]
    public void ReservedFlagBitsAreAViolation(byte flags)
    {
        var header = new byte[8];
        FrameCodec.WriteHeader(header, 4, MsgType.Ping, (FrameOptions)flags, Lane.Control);
        Assert.Equal(ViolationCode.ReservedFlags, Assert.Throws<ProtocolViolation>(() => FrameCodec.ParseHeader(header)).Code);
    }

    [Fact]
    public void CompressedFlagIsRejectedInV1()
    {
        var header = new byte[8];
        FrameCodec.WriteHeader(header, 4, MsgType.Ping, FrameOptions.Compressed, Lane.Control);
        Assert.Equal(ViolationCode.CompressedNotSupported, Assert.Throws<ProtocolViolation>(() => FrameCodec.ParseHeader(header)).Code);
    }

    [Fact]
    public void InvalidLaneByteIsAViolation()
    {
        var header = new byte[8];
        FrameCodec.WriteHeader(header, 4, MsgType.Ping, FrameOptions.None, (Lane)3);
        Assert.Equal(ViolationCode.InvalidLane, Assert.Throws<ProtocolViolation>(() => FrameCodec.ParseHeader(header)).Code);
    }

    [Fact]
    public void LaneMustMatchTheCatalog()
    {
        var payload = Sample("0x0005_Ping").Encode();
        var frame = new Frame(MsgType.Ping, FrameOptions.None, Lane.Bulk, payload);
        Assert.Equal(ViolationCode.LaneMismatch, Assert.Throws<ProtocolViolation>(() => Registry.Decode(frame)).Code);
    }

    [Fact]
    public void UnknownAndReservedTypesAreViolations()
    {
        var payload = Sample("0x0005_Ping").Encode();
        Assert.Equal(ViolationCode.UnknownMessageType, Assert.Throws<ProtocolViolation>(() => Registry.Decode((MsgType)0x7F01, payload)).Code);
        Assert.Equal(ViolationCode.UnknownMessageType, Assert.Throws<ProtocolViolation>(() => Registry.Decode(MsgType.Invalid, payload)).Code);
        Assert.Equal(ViolationCode.ReservedMessageType, Assert.Throws<ProtocolViolation>(() => Registry.Decode(MsgType.DamageReport, payload)).Code);
    }

    [Fact]
    public void GarbagePayloadsAreViolationsNeverOtherExceptions()
    {
        var rng = new Random(12345);
        var types = Registry.Descriptors.Select(d => d.Type).ToArray();
        for (int i = 0; i < 3000; i++)
        {
            var buf = new byte[rng.Next(1, 200)];
            rng.NextBytes(buf);
            var type = types[rng.Next(types.Length)];
            AssertOnlyViolationOrCleanDecode(type, buf);
        }
    }

    [Fact]
    public void EveryTruncationAndBitFlipOfEverySampleIsHandled()
    {
        var rng = new Random(777);
        foreach (var s in SampleMessages.All)
        {
            var payload = s.Encode();
            for (int len = 1; len < payload.Length; len++)
                AssertOnlyViolationOrCleanDecode(s.Type, payload.AsSpan(0, len).ToArray());

            for (int i = 0; i < 40; i++)
            {
                var copy = (byte[])payload.Clone();
                copy[rng.Next(copy.Length)] ^= (byte)(1 << rng.Next(8));
                AssertOnlyViolationOrCleanDecode(s.Type, copy);
            }
        }
    }

    private static void AssertOnlyViolationOrCleanDecode(MsgType type, byte[] payload)
    {
        Google.FlatBuffers.IFlatbufferObject decoded;
        try
        {
            decoded = Registry.Decode(type, payload);
        }
        catch (ProtocolViolation)
        {
            return;
        }

        // If the verifier accepted the buffer, reading all of it must be safe.
        _ = SampleMessage.ToJson(decoded);
    }

    [Fact]
    public void FrameSplitAcrossReadsReassembles()
    {
        var samples = SplitStems.Select(Sample).ToArray();
        var wire = samples.SelectMany(FrameFor).ToArray();

        // one byte at a time
        var reader = new FrameReader();
        var got = new List<Frame>();
        foreach (var b in wire)
        {
            reader.Append([b]);
            while (reader.TryRead(out var f))
                got.Add(f);
        }
        reader.CompleteOrThrow();
        AssertFrames(samples, got);

        // random slices, including several frames per read
        for (int seed = 0; seed < 50; seed++)
        {
            var rng = new Random(seed);
            reader = new FrameReader();
            got.Clear();
            for (int pos = 0; pos < wire.Length;)
            {
                int n = Math.Min(rng.Next(1, 700), wire.Length - pos);
                reader.Append(wire.AsSpan(pos, n));
                pos += n;
                while (reader.TryRead(out var f))
                    got.Add(f);
            }
            reader.CompleteOrThrow();
            AssertFrames(samples, got);
        }
    }

    private static void AssertFrames(SampleMessage[] expected, List<Frame> actual)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Type, actual[i].Type);
            Assert.Equal(expected[i].SourceJson, SampleMessage.ToJson(Registry.Decode(actual[i])));
        }
    }

    [Fact]
    public async Task StreamReaderHandlesOneByteReads()
    {
        var samples = TrickleStems.Select(Sample).ToArray();
        var wire = samples.SelectMany(FrameFor).ToArray();
        using var stream = new TrickleStream(wire);

        var got = new List<Frame>();
        while (await FrameCodec.ReadFrameAsync(stream) is { } f)
            got.Add(f);
        AssertFrames(samples, got);
    }

    [Fact]
    public void MessageHeaderValuesAreLittleEndian()
    {
        var header = new byte[8];
        FrameCodec.WriteHeader(header, 0x01020304, (MsgType)0x0815, FrameOptions.None, Lane.Control);
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32LittleEndian(header));
        Assert.Equal(0x0815, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)));
    }

    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
