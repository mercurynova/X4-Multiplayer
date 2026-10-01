using System.Buffers.Binary;
using X4MP.Proto;

namespace X4MP.Protocol;

/// <summary>The 24-byte UDP datagram header (protocol.md 3.3).</summary>
/// <param name="ProtoMajor">proto_major.</param>
/// <param name="ConnId">conn_id from Welcome.</param>
/// <param name="Seq">Sender's datagram sequence (per direction).</param>
/// <param name="Ack">Highest seq received from the peer.</param>
/// <param name="AckBits">bit i = (Ack - 1 - i) also received (32-packet window).</param>
public readonly record struct DatagramHeader(byte ProtoMajor, uint ConnId, uint Seq, uint Ack, uint AckBits)
{
    /// <summary>True if this header acknowledges datagram <paramref name="seq"/> (wrap-safe).</summary>
    public bool Acknowledges(uint seq)
    {
        if (seq == Ack)
            return true;
        uint behind = unchecked(Ack - 1 - seq); // 0 => seq == Ack-1 => bit 0
        return behind < 32 && (AckBits & (1u << (int)behind)) != 0;
    }
}

/// <summary>
/// UDP datagram framing (protocol.md 3.3):
/// <c>magic u16 0x4D58 | proto_major u8 | flags u8 (=0) | conn_id u32 | seq u32 | ack u32 | ack_bits u32 | reserved u32 (=0)</c>,
/// then sub-messages <c>msg_type u16 | len u16 | payload[len] | zero pad to an 8-byte boundary</c> (offsets
/// measured from the start of the datagram). Maximum datagram size is 1200 bytes.
/// </summary>
public static class DatagramCodec
{
    public const int HeaderSize = 24;
    public const int MaxDatagramBytes = 1200;
    public const ushort Magic = 0x4D58; // bytes 'X','M' little-endian
    public const int SubMessageHeaderSize = 4;

    /// <summary>Message types that may travel in a datagram (protocol.md 3.3). Others are dropped and counted.</summary>
    public static bool IsAllowedOnUdp(MsgType type) => type is
        MsgType.Ping or MsgType.Pong or MsgType.UdpHello or MsgType.UdpHelloAck or MsgType.Replication
        or MsgType.WorldUpdate or MsgType.EntityStatusBatch or MsgType.PlayerState;

    public static void WriteHeader(Span<byte> destination, in DatagramHeader h)
    {
        if (destination.Length < HeaderSize)
            throw new ArgumentException("Destination needs 24 bytes.", nameof(destination));
        BinaryPrimitives.WriteUInt16LittleEndian(destination, Magic);
        destination[2] = h.ProtoMajor;
        destination[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], h.ConnId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], h.Seq);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], h.Ack);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], h.AckBits);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], 0);
    }

    /// <summary>
    /// Parses the header. Throws <see cref="ProtocolViolation"/> (MalformedDatagram) on a short or oversized
    /// datagram, wrong magic, a proto_major other than <paramref name="expectedMajor"/>, or non-zero reserved
    /// bits.
    /// </summary>
    public static DatagramHeader ReadHeader(ReadOnlySpan<byte> datagram, byte expectedMajor = (byte)ProtocolConstants.ProtocolMajor)
    {
        if (datagram.Length < HeaderSize)
            throw new ProtocolViolation(ViolationCode.MalformedDatagram, $"datagram of {datagram.Length} bytes is shorter than the 24-byte header");
        if (datagram.Length > MaxDatagramBytes)
            throw new ProtocolViolation(ViolationCode.MalformedDatagram, $"datagram of {datagram.Length} bytes exceeds {MaxDatagramBytes}");
        if (BinaryPrimitives.ReadUInt16LittleEndian(datagram) != Magic)
            throw new ProtocolViolation(ViolationCode.MalformedDatagram, "bad magic");
        if (datagram[2] != expectedMajor)
            throw new ProtocolViolation(ViolationCode.MalformedDatagram, $"proto_major {datagram[2]} (expected {expectedMajor})");
        if (datagram[3] != 0 || BinaryPrimitives.ReadUInt32LittleEndian(datagram[20..]) != 0)
            throw new ProtocolViolation(ViolationCode.MalformedDatagram, "reserved header bits are not zero");

        return new DatagramHeader(
            datagram[2],
            BinaryPrimitives.ReadUInt32LittleEndian(datagram[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(datagram[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(datagram[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(datagram[16..]));
    }

    /// <summary>Bytes a sub-message with a payload of <paramref name="payloadLength"/> occupies, pad included.</summary>
    public static int SubMessageSize(int payloadLength) => (SubMessageHeaderSize + payloadLength + 7) & ~7;

    /// <summary>
    /// Enumerates the sub-messages of a datagram (header must already be valid). Throws
    /// <see cref="ProtocolViolation"/> if a sub-message header or payload overruns the datagram or a length
    /// is zero. The trailing pad of the final sub-message may be missing. Pad bytes are ignored on read.
    /// </summary>
    public static List<(MsgType Type, int Offset, int Length)> ReadSubMessages(ReadOnlySpan<byte> datagram)
    {
        var result = new List<(MsgType, int, int)>();
        int p = HeaderSize;
        while (p < datagram.Length)
        {
            if (datagram.Length - p < SubMessageHeaderSize)
                throw new ProtocolViolation(ViolationCode.MalformedDatagram, $"truncated sub-message header at offset {p}");
            var type = (MsgType)BinaryPrimitives.ReadUInt16LittleEndian(datagram[p..]);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(datagram[(p + 2)..]);
            if (len == 0)
                throw new ProtocolViolation(ViolationCode.MalformedDatagram, $"zero-length sub-message {type} at offset {p}");
            if (datagram.Length - p - SubMessageHeaderSize < len)
                throw new ProtocolViolation(ViolationCode.MalformedDatagram, $"sub-message {type} length {len} overruns the datagram");
            result.Add((type, p + SubMessageHeaderSize, len));
            p += SubMessageSize(len);
        }
        return result;
    }

    /// <summary>Builds a datagram: header plus padded sub-messages, never exceeding 1200 bytes.</summary>
    public sealed class Builder
    {
        private readonly byte[] _buffer = new byte[MaxDatagramBytes];
        private int _length = HeaderSize;

        public Builder(in DatagramHeader header)
        {
            WriteHeader(_buffer, header);
        }

        /// <summary>Current datagram size.</summary>
        public int Length => _length;

        /// <summary>
        /// Appends a sub-message if it fits (including its pad) and the type is allowed on UDP. Returns
        /// false if it does not fit; throws for an empty payload, a payload over 65535 bytes, or a
        /// message type that may not use UDP.
        /// </summary>
        public bool TryAdd(MsgType type, ReadOnlySpan<byte> payload)
        {
            if (!IsAllowedOnUdp(type))
                throw new ArgumentException($"{type} is not allowed on UDP.", nameof(type));
            if (payload.Length is 0 or > ushort.MaxValue)
                throw new ArgumentException("Sub-message payload must be 1..65535 bytes.", nameof(payload));

            int size = SubMessageSize(payload.Length);
            if (_length + size > MaxDatagramBytes)
                return false;

            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_length), (ushort)type);
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_length + 2), (ushort)payload.Length);
            payload.CopyTo(_buffer.AsSpan(_length + SubMessageHeaderSize));
            // pad bytes are already zero (fresh buffer, never reused after Build)
            _length += size;
            return true;
        }

        /// <summary>The finished datagram (a bare 24-byte header if nothing was added: an ack-only datagram).</summary>
        public byte[] Build() => _buffer.AsSpan(0, _length).ToArray();
    }
}
