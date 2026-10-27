using System.Buffers.Binary;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Transport.Recording;

/// <summary>
/// The <c>.x4rec</c> file format (M4-26; docs/m4-plan.md W6). One file is a 48-byte header followed by
/// fixed 24-byte record headers, each immediately followed by its payload padded to 8 bytes:
/// <code>
/// file header: u32 magic 'X4MR' | u16 version | u16 flags | u32 proto | u32 header_bytes
///              u64 created_ms | u64 session_seed | u32 reserved | u32 reserved | u64 reserved
/// record:      u64 t_us | u16 msg_type | u8 lane | u8 flags | u32 stream | u32 len | u32 reserved | payload[len]
/// </code>
/// Everything is little-endian, the same as the wire format, so a record can be written from the frame bytes
/// without a translation table. <c>t_us</c> is monotonic time since the recorder started (never wall clock), so a
/// replay can run at the original pace or as fast as possible from the same file.
///
/// <para>
/// The payload is the FlatBuffers body only, without the 8-byte TCP frame header: type and lane are stored
/// explicitly, so re-encoding with <see cref="FrameCodec.Encode(MsgType, Lane, ReadOnlySpan{byte}, int)"/>
/// reproduces the original frame byte for byte (that is the "byte-identical replay" acceptance criterion).
/// </para>
/// </summary>
public static class RecordingFormat
{
    /// <summary>File magic, read as a little-endian u32: the ASCII bytes <c>X4MR</c>.</summary>
    public const uint Magic = 0x524D_3458;

    /// <summary>Format version written by this build. A reader refuses anything newer.</summary>
    public const ushort Version = 1;

    /// <summary>Size of the per-file header in bytes.</summary>
    public const int FileHeaderSize = 48;

    /// <summary>Size of a record header in bytes; the payload follows immediately.</summary>
    public const int RecordHeaderSize = 24;

    /// <summary>Record boundaries are padded to this many bytes.</summary>
    public const int Alignment = 8;

    /// <summary>Default size at which a recording rotates into its next part (64 MiB).</summary>
    public const long DefaultMaxPartBytes = 64L << 20;

    /// <summary>File extension of a recording part, including the dot.</summary>
    public const string Extension = ".x4rec";

    /// <summary>Default base name of a recording part, before the five-digit part number.</summary>
    public const string DefaultPrefix = "x4mp-rec";

    /// <summary>Rounds <paramref name="value"/> up to the next multiple of <see cref="Alignment"/>.</summary>
    public static int Align(int value) => (value + Alignment - 1) & ~(Alignment - 1);

    /// <summary>Protocol stamp stored in the header: major in the high half, minor in the low half.</summary>
    public static uint ProtocolStamp => ((uint)ProtocolConstants.ProtocolMajor << 16) | ProtocolConstants.ProtocolMinor;

    /// <summary>Name of part <paramref name="part"/> (1-based) in <paramref name="directory"/>.</summary>
    public static string PartPath(string directory, string prefix, int part) =>
        Path.Combine(directory, $"{prefix}-{part:D5}{Extension}");

    /// <summary>Writes the 48-byte file header into <paramref name="destination"/>.</summary>
    public static void WriteFileHeader(Span<byte> destination, in RecordingFileHeader header)
    {
        if (destination.Length < FileHeaderSize)
            throw new ArgumentException($"file header needs {FileHeaderSize} bytes, got {destination.Length}.", nameof(destination));

        destination[..FileHeaderSize].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], header.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], header.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], header.ProtocolStamp);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], FileHeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], header.CreatedUnixMs);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], header.SessionSeed);
    }

    /// <summary>Parses a file header. Returns false (and no header) when the bytes are not an X4MF-style x4rec header.</summary>
    public static bool TryReadFileHeader(ReadOnlySpan<byte> source, out RecordingFileHeader header)
    {
        header = default;
        if (source.Length < FileHeaderSize)
            return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic)
            return false;

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(source[4..]);
        if (version == 0 || version > Version)
            return false;

        uint headerBytes = BinaryPrimitives.ReadUInt32LittleEndian(source[12..]);
        if (headerBytes != FileHeaderSize)
            return false;

        header = new RecordingFileHeader(
            version,
            BinaryPrimitives.ReadUInt16LittleEndian(source[6..]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[24..]));
        return true;
    }

    /// <summary>Writes a 24-byte record header into <paramref name="destination"/>.</summary>
    public static void WriteRecordHeader(
        Span<byte> destination,
        long timeUs,
        MsgType type,
        Lane lane,
        RecordingMark flags,
        uint stream,
        int payloadLength)
    {
        if (destination.Length < RecordHeaderSize)
            throw new ArgumentException($"record header needs {RecordHeaderSize} bytes, got {destination.Length}.", nameof(destination));

        BinaryPrimitives.WriteUInt64LittleEndian(destination, (ulong)timeUs);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], (ushort)type);
        destination[10] = (byte)lane;
        destination[11] = (byte)flags;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], stream);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], (uint)payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], 0);
    }

    /// <summary>Total on-disk size of a record with a payload of <paramref name="payloadLength"/> bytes.</summary>
    public static int RecordSize(int payloadLength) => RecordHeaderSize + Align(payloadLength);
}

/// <summary>The 48-byte per-file header of an <c>.x4rec</c> part.</summary>
/// <param name="Version">Format version.</param>
/// <param name="Flags">Reserved, zero today.</param>
/// <param name="ProtocolStamp">Protocol major/minor the recording was taken with.</param>
/// <param name="CreatedUnixMs">Wall clock of the moment the part was opened (diagnostics only).</param>
/// <param name="SessionSeed">Seed of the recorded session, so a replay can be tied back to a save (0 = unknown).</param>
public readonly record struct RecordingFileHeader(
    ushort Version,
    ushort Flags,
    uint ProtocolStamp,
    ulong CreatedUnixMs,
    ulong SessionSeed);

/// <summary>One recorded message: the frame without its 8-byte TCP header, plus where and when it was sent.</summary>
/// <param name="TimeUs">Monotonic microseconds since the recorder started.</param>
/// <param name="Type">Message type (frame header field).</param>
/// <param name="Lane">Lane the frame was accepted on (frame header field).</param>
/// <param name="Flags">How the frame left the server; see <see cref="RecordingMark"/>.</param>
/// <param name="Stream">Connection the frame was addressed to (0 = server-wide / synthetic).</param>
/// <param name="Payload">The FlatBuffers body, without the frame header.</param>
public readonly record struct RecordingRecord(
    long TimeUs,
    MsgType Type,
    Lane Lane,
    RecordingMark Flags,
    uint Stream,
    byte[] Payload)
{
    /// <summary>Payload length in bytes.</summary>
    public int PayloadLength => Payload.Length;

    /// <summary>Re-encodes the frame exactly as it went on the wire.</summary>
    public byte[] EncodeFrame(int maxFrameBytes = FrameCodec.DefaultMaxFrameBytes) =>
        FrameCodec.Encode(Type, Lane, Payload, maxFrameBytes);
}

/// <summary>Per-record flags stored in the record header.</summary>
[Flags]
public enum RecordingMark : byte
{
    /// <summary>Nothing special: the frame went through the TCP queue.</summary>
    None = 0,

    /// <summary>The frame was handed to the UDP Realtime datagram path (M1-09).</summary>
    Datagram = 1,

    /// <summary>Written by a driver, not by a live session (replay/mirror bookkeeping).</summary>
    Synthetic = 2,
}
