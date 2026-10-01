using System.Buffers.Binary;
using Google.FlatBuffers;
using X4MP.Proto;

namespace X4MP.Protocol;

/// <summary>
/// TCP framing (protocol.md 3.2): <c>u32 payload_len | u16 msg_type | u8 flags | u8 lane | payload</c>,
/// little-endian. payload_len is validated against MaxFrameBytes BEFORE any payload buffer is allocated.
/// </summary>
public static class FrameCodec
{
    public const int HeaderSize = 8;

    /// <summary>Default MaxFrameBytes (1 MiB). Chunked content always fits under it.</summary>
    public const int DefaultMaxFrameBytes = 1 << 20;

    /// <summary>
    /// Parses and structurally validates a header: length (non-zero, within <paramref name="maxFrameBytes"/>),
    /// reserved flag bits, Compressed (unsupported in v1) and lane range. Does not look at the catalog.
    /// </summary>
    public static FrameHeader ParseHeader(ReadOnlySpan<byte> header, int maxFrameBytes = DefaultMaxFrameBytes)
    {
        if (header.Length < HeaderSize)
            throw new ProtocolViolation(ViolationCode.TruncatedFrame, $"header needs {HeaderSize} bytes, got {header.Length}");

        uint len = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var type = (MsgType)BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        byte flags = header[6];
        byte lane = header[7];

        if (len == 0)
            throw new ProtocolViolation(ViolationCode.ZeroLengthFrame, $"zero-length payload for type 0x{(ushort)type:X4}");
        if (len > (uint)maxFrameBytes)
            throw new ProtocolViolation(ViolationCode.FrameTooLarge, $"payload_len {len} exceeds MaxFrameBytes {maxFrameBytes}");
        if ((flags & ~(byte)FrameOptions.Compressed) != 0)
            throw new ProtocolViolation(ViolationCode.ReservedFlags, $"reserved flag bits set: 0x{flags:X2}");
        if ((flags & (byte)FrameOptions.Compressed) != 0)
            throw new ProtocolViolation(ViolationCode.CompressedNotSupported, "Compressed flag set but Lz4Frames is not negotiated");
        if (lane > (byte)Lane.Bulk)
            throw new ProtocolViolation(ViolationCode.InvalidLane, $"lane byte {lane}");

        return new FrameHeader(len, type, (FrameOptions)flags, (Lane)lane);
    }

    /// <summary>Writes an 8-byte header into <paramref name="destination"/> (must be at least 8 bytes).</summary>
    public static void WriteHeader(Span<byte> destination, uint payloadLength, MsgType type, FrameOptions flags, Lane lane)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, payloadLength);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], (ushort)type);
        destination[6] = (byte)flags;
        destination[7] = (byte)lane;
    }

    /// <summary>Encodes header + payload into a new array. The lane comes from the catalog.</summary>
    public static byte[] Encode(MsgType type, ReadOnlySpan<byte> payload, int maxFrameBytes = DefaultMaxFrameBytes) =>
        Encode(type, MessageRegistry.Default.GetDescriptor(type).Lane, payload, maxFrameBytes);

    /// <summary>Encodes header + payload into a new array with an explicit lane.</summary>
    public static byte[] Encode(MsgType type, Lane lane, ReadOnlySpan<byte> payload, int maxFrameBytes = DefaultMaxFrameBytes)
    {
        if (payload.Length == 0)
            throw new ArgumentException("A frame payload cannot be empty.", nameof(payload));
        if (payload.Length > maxFrameBytes)
            throw new ArgumentException($"Payload {payload.Length} exceeds MaxFrameBytes {maxFrameBytes}.", nameof(payload));

        var frame = new byte[HeaderSize + payload.Length];
        WriteHeader(frame, (uint)payload.Length, type, FrameOptions.None, lane);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>Encodes a finished FlatBufferBuilder (after <c>Finish</c>) as a frame.</summary>
    public static byte[] Encode(MsgType type, FlatBufferBuilder finished, int maxFrameBytes = DefaultMaxFrameBytes)
    {
        var bb = finished.DataBuffer;
        return Encode(type, new ReadOnlySpan<byte>(bb.ToSizedArray()), maxFrameBytes);
    }

    /// <summary>
    /// Tries to read one complete frame from the start of <paramref name="data"/>. Returns false when more
    /// bytes are needed (nothing is allocated for the payload until the whole frame is present, and the
    /// header is validated first). Throws <see cref="ProtocolViolation"/> on a bad header.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out Frame frame, out int consumed, int maxFrameBytes = DefaultMaxFrameBytes)
    {
        frame = default;
        consumed = 0;
        if (data.Length < HeaderSize)
            return false;

        var header = ParseHeader(data, maxFrameBytes);
        int total = HeaderSize + (int)header.PayloadLength;
        if (data.Length < total)
            return false;

        frame = new Frame(header.Type, header.Flags, header.Lane, data.Slice(HeaderSize, (int)header.PayloadLength).ToArray());
        consumed = total;
        return true;
    }

    /// <summary>
    /// Reads exactly one frame from a stream. Returns null on a clean EOF at a frame boundary; throws
    /// <see cref="ProtocolViolation"/> (TruncatedFrame) if the stream ends mid-frame. The payload array is
    /// allocated only after the header passed validation, so its size is bounded by MaxFrameBytes.
    /// </summary>
    public static async ValueTask<Frame?> ReadFrameAsync(Stream stream, int maxFrameBytes = DefaultMaxFrameBytes, CancellationToken cancellationToken = default)
    {
        var headerBytes = new byte[HeaderSize];
        int got = await ReadFullyAsync(stream, headerBytes, cancellationToken).ConfigureAwait(false);
        if (got == 0)
            return null;
        if (got < HeaderSize)
            throw new ProtocolViolation(ViolationCode.TruncatedFrame, $"stream ended after {got} header bytes");

        var header = ParseHeader(headerBytes, maxFrameBytes);
        var payload = new byte[header.PayloadLength];
        got = await ReadFullyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (got < payload.Length)
            throw new ProtocolViolation(ViolationCode.TruncatedFrame, $"stream ended after {got} of {payload.Length} payload bytes");

        return new Frame(header.Type, header.Flags, header.Lane, payload);
    }

    private static async ValueTask<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0)
                break;
            total += n;
        }
        return total;
    }
}

/// <summary>
/// Incremental frame reassembly for a byte stream delivered in arbitrary slices (a frame may be split
/// across reads, and several frames may arrive in one read). Not thread-safe.
/// </summary>
public sealed class FrameReader
{
    private readonly int _maxFrameBytes;
    private byte[] _buffer = new byte[4096];
    private int _start;
    private int _end;

    public FrameReader(int maxFrameBytes = FrameCodec.DefaultMaxFrameBytes)
    {
        _maxFrameBytes = maxFrameBytes;
    }

    /// <summary>Bytes buffered but not yet returned as frames.</summary>
    public int Buffered => _end - _start;

    /// <summary>Appends received bytes.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (_end + data.Length > _buffer.Length)
        {
            int live = _end - _start;
            if (live + data.Length > _buffer.Length)
            {
                var bigger = new byte[Math.Max(_buffer.Length * 2, live + data.Length)];
                Buffer.BlockCopy(_buffer, _start, bigger, 0, live);
                _buffer = bigger;
            }
            else
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, live);
            }
            _start = 0;
            _end = live;
        }
        data.CopyTo(_buffer.AsSpan(_end));
        _end += data.Length;
    }

    /// <summary>
    /// Returns the next complete frame, or false if more bytes are needed. Throws
    /// <see cref="ProtocolViolation"/> on a bad header; the reader is then unusable (close the connection).
    /// </summary>
    public bool TryRead(out Frame frame)
    {
        if (FrameCodec.TryDecode(_buffer.AsSpan(_start, _end - _start), out frame, out int consumed, _maxFrameBytes))
        {
            _start += consumed;
            if (_start == _end)
                _start = _end = 0;
            return true;
        }
        return false;
    }

    /// <summary>Call at end-of-stream: throws if a partial frame is buffered.</summary>
    public void CompleteOrThrow()
    {
        if (Buffered != 0)
            throw new ProtocolViolation(ViolationCode.TruncatedFrame, $"stream ended with {Buffered} bytes of a partial frame");
    }
}
