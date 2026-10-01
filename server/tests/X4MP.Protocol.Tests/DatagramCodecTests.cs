using System.Buffers.Binary;
using X4MP.Proto;

namespace X4MP.Protocol.Tests;

public class DatagramCodecTests
{
    private static readonly DatagramHeader Header = new(0, 0xDEADBEEF, 1, 2, 3);

    [Fact]
    public void HeaderIsByteExact()
    {
        var bytes = new DatagramCodec.Builder(Header).Build();
        Assert.Equal(24, bytes.Length);
        Assert.Equal(
            "584D" + "00" + "00" + "EFBEADDE" + "01000000" + "02000000" + "03000000" + "00000000",
            Convert.ToHexString(bytes));
        Assert.Equal("XM", System.Text.Encoding.ASCII.GetString(bytes, 0, 2));
        Assert.Equal(Header, DatagramCodec.ReadHeader(bytes));
    }

    [Fact]
    public void BareHeaderIsAnAckOnlyDatagram()
    {
        var bytes = new DatagramCodec.Builder(Header).Build();
        Assert.Equal(24, bytes.Length);
        Assert.Empty(DatagramCodec.ReadSubMessages(bytes));
    }

    [Fact]
    public void SubMessagesArePaddedToEightBytes()
    {
        var b = new DatagramCodec.Builder(Header);
        Assert.True(b.TryAdd(MsgType.Ping, [1, 2, 3, 4, 5]));        // 4 + 5 = 9 -> 16
        Assert.Equal(24 + 16, b.Length);
        Assert.True(b.TryAdd(MsgType.PlayerState, new byte[12]));    // 4 + 12 = 16 -> 16
        Assert.Equal(24 + 16 + 16, b.Length);
        Assert.True(b.TryAdd(MsgType.Pong, [9]));                    // 4 + 1 = 5 -> 8
        var bytes = b.Build();
        Assert.Equal(64, bytes.Length);
        Assert.Equal(0, bytes.Length % 8);

        // Ping(0x0005) len 5, payload, 3 zero pad bytes
        Assert.Equal("0500" + "0500" + "0102030405" + "000000", Convert.ToHexString(bytes.AsSpan(24, 16))[..(4 + 4 + 10 + 6)]);

        var subs = DatagramCodec.ReadSubMessages(bytes);
        Assert.Equal([(MsgType.Ping, 28, 5), (MsgType.PlayerState, 44, 12), (MsgType.Pong, 64 - 8 + 4, 1)], subs);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, bytes.AsSpan(subs[0].Offset, subs[0].Length).ToArray());
        // pad bytes are zero
        Assert.All(bytes.AsSpan(33, 7).ToArray(), x => Assert.Equal(0, x));
    }

    [Fact]
    public void BuilderNeverExceeds1200Bytes()
    {
        var b = new DatagramCodec.Builder(Header);
        int added = 0;
        while (b.TryAdd(MsgType.WorldUpdate, new byte[100]))
            added++;
        Assert.Equal((1200 - 24) / 104, added);   // 104 = 4 + 100 (already a multiple of 8)
        Assert.InRange(b.Length, 1, 1200);
        var bytes = b.Build();
        Assert.True(bytes.Length <= DatagramCodec.MaxDatagramBytes);
        Assert.Equal(added, DatagramCodec.ReadSubMessages(bytes).Count);
        _ = DatagramCodec.ReadHeader(bytes);
    }

    [Fact]
    public void OnlyUdpMessageTypesAreAllowed()
    {
        var b = new DatagramCodec.Builder(Header);
        Assert.Throws<ArgumentException>(() => b.TryAdd(MsgType.ChatSend, [1]));
        Assert.Throws<ArgumentException>(() => b.TryAdd(MsgType.Ping, []));
        foreach (var t in new[] { MsgType.Ping, MsgType.Pong, MsgType.UdpHello, MsgType.UdpHelloAck, MsgType.Replication, MsgType.WorldUpdate, MsgType.EntityStatusBatch, MsgType.PlayerState })
            Assert.True(DatagramCodec.IsAllowedOnUdp(t));
        Assert.False(DatagramCodec.IsAllowedOnUdp(MsgType.EntitySpawn));
        Assert.False(DatagramCodec.IsAllowedOnUdp(MsgType.SaveChunk));
    }

    private static byte[] ValidDatagram()
    {
        var b = new DatagramCodec.Builder(Header);
        b.TryAdd(MsgType.Ping, [1, 2, 3, 4, 5]);
        return b.Build();
    }

    [Fact]
    public void BadHeadersAreMalformedDatagrams()
    {
        var good = ValidDatagram();
        void Expect(Action<byte[]> mutate)
        {
            var copy = (byte[])good.Clone();
            mutate(copy);
            Assert.Equal(ViolationCode.MalformedDatagram, Assert.Throws<ProtocolViolation>(() => DatagramCodec.ReadHeader(copy)).Code);
        }

        Expect(d => d[0] = 0x58 + 1);                       // magic
        Expect(d => d[1] = 0);                              // magic
        Expect(d => d[2] = 1);                              // proto_major
        Expect(d => d[3] = 1);                              // flags reserved
        Expect(d => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(20), 1));   // reserved word
        Assert.Throws<ProtocolViolation>(() => DatagramCodec.ReadHeader(good.AsSpan(0, 23)));
        Assert.Throws<ProtocolViolation>(() => DatagramCodec.ReadHeader(new byte[1201]));
        Assert.Throws<ProtocolViolation>(() => DatagramCodec.ReadHeader([]));
        Assert.Equal(1, DatagramCodec.ReadHeader(good.Select((x, i) => i == 2 ? (byte)1 : x).ToArray(), expectedMajor: 1).ProtoMajor);
    }

    [Fact]
    public void BadSubMessagesAreMalformedDatagrams()
    {
        var good = ValidDatagram();
        // truncated sub-message header
        Assert.Throws<ProtocolViolation>(() => DatagramCodec.ReadSubMessages(good.AsSpan(0, 26)));
        // payload overruns
        Assert.Throws<ProtocolViolation>(() => DatagramCodec.ReadSubMessages(good.AsSpan(0, 30)));
        // zero length
        var zero = (byte[])good.Clone();
        zero[26] = 0; zero[27] = 0;
        Assert.Throws<ProtocolViolation>(() => DatagramCodec.ReadSubMessages(zero));
        // length larger than the datagram
        var big = (byte[])good.Clone();
        big[26] = 0xFF; big[27] = 0xFF;
        Assert.Throws<ProtocolViolation>(() => DatagramCodec.ReadSubMessages(big));
        // trailing pad of the final sub-message may be missing
        Assert.Single(DatagramCodec.ReadSubMessages(good.AsSpan(0, 24 + 4 + 5)));
    }

    [Fact]
    public void RandomGarbageOnlyRaisesProtocolViolation()
    {
        var rng = new Random(11);
        for (int i = 0; i < 5000; i++)
        {
            var buf = new byte[rng.Next(0, 300)];
            rng.NextBytes(buf);
            if (buf.Length >= 24 && rng.Next(2) == 0)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(buf, DatagramCodec.Magic);
                buf[2] = 0; buf[3] = 0;
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), 0);
            }
            try
            {
                DatagramCodec.ReadHeader(buf);
                DatagramCodec.ReadSubMessages(buf);
            }
            catch (ProtocolViolation)
            {
            }
        }
    }

    [Fact]
    public void AckBitsWindowSemantics()
    {
        var h = new DatagramHeader(0, 1, 100, Ack: 50, AckBits: 0b101);   // 49 (bit0) and 47 (bit2) received
        Assert.True(h.Acknowledges(50));
        Assert.True(h.Acknowledges(49));
        Assert.False(h.Acknowledges(48));
        Assert.True(h.Acknowledges(47));
        Assert.False(h.Acknowledges(51));
        Assert.False(h.Acknowledges(10));

        var edge = new DatagramHeader(0, 1, 1, Ack: 100, AckBits: 0x80000000);   // bit 31 -> seq 68
        Assert.True(edge.Acknowledges(68));
        Assert.False(edge.Acknowledges(67));

        var wrap = new DatagramHeader(0, 1, 1, Ack: 1, AckBits: 0b11);           // 0 and uint.MaxValue
        Assert.True(wrap.Acknowledges(0));
        Assert.True(wrap.Acknowledges(uint.MaxValue));
    }

    [Fact]
    public void ReplicationPayloadFitsInADatagramSubMessage()
    {
        var entries = ReplicationCodec.Encode([new ReplicationEntry { NetId = 5, Mask = ReplicationMask.Pos | ReplicationMask.Time, PosX = 1, TimeMs = 2 }]);
        var fbb = new Google.FlatBuffers.FlatBufferBuilder(128);
        var rep = new ReplicationT { ServerTick = 1, ServerTimeUs = 2, AuthorityGameTime = 3, EntryCount = 1, Entries = [.. entries] };
        fbb.Finish(Replication.Pack(fbb, rep).Value);
        var b = new DatagramCodec.Builder(Header);
        Assert.True(b.TryAdd(MsgType.Replication, fbb.DataBuffer.ToSizedArray()));
        var dg = b.Build();
        var sub = DatagramCodec.ReadSubMessages(dg).Single();
        var decoded = (Replication)MessageRegistry.Default.Decode(sub.Type, dg.AsSpan(sub.Offset, sub.Length).ToArray());
        Assert.Equal(1, decoded.EntryCount);
        Assert.Equal(entries, decoded.GetEntriesArray());
    }
}
