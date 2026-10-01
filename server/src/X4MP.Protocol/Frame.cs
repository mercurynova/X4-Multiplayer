using X4MP.Proto;

namespace X4MP.Protocol;

/// <summary>Frame header lane byte (protocol.md 3.2). Informational, but must match the catalog.</summary>
public enum Lane : byte
{
    Control = 0,
    Realtime = 1,
    Bulk = 2,
}

/// <summary>Frame header flags byte. bit1..7 are reserved and must be zero.</summary>
[Flags]
public enum FrameOptions : byte
{
    None = 0,
    /// <summary>LZ4 block, payload = u32 raw_len + block. Only with the Lz4Frames capability (off in v1).</summary>
    Compressed = 1,
}

/// <summary>Parsed 8-byte TCP frame header.</summary>
public readonly record struct FrameHeader(uint PayloadLength, MsgType Type, FrameOptions Flags, Lane Lane);

/// <summary>One complete TCP frame. <see cref="Payload"/> is exactly the FlatBuffers buffer.</summary>
public readonly record struct Frame(MsgType Type, FrameOptions Flags, Lane Lane, byte[] Payload);
