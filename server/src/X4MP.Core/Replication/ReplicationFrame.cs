using System.Buffers.Binary;

namespace X4MP.Core.Replication;

/// <summary>
/// Writes the <c>Replication</c> FlatBuffers table straight into a byte buffer, with no <c>FlatBufferBuilder</c> and no allocation:
/// the table has a fixed shape (five fields, one byte vector), so its layout is a constant 60 byte prefix followed by the packed
/// entries. The layout is a valid FlatBuffer (aligned scalars, vtable before the table, vector length right before its bytes), so the
/// generated <c>Replication</c> reader and the C++ verifier accept it; a test decodes it with the generated code.
/// <code>
///   0  u32 root offset (24)          8  vtable: size 14, table size 32, offsets for server_tick, server_time_us, game_time, entry_count, entries
///  24  i32 soffset to the vtable    28  u32 server_tick           32  u16 entry_count      36  u32 offset to the vector
///  40  u64 server_time_us           48  f64 authority_game_time   56  u32 vector length    60  entries...
/// </code>
/// </summary>
public static class ReplicationFrame
{
    /// <summary>Bytes before the first entry.</summary>
    public const int HeaderBytes = 60;

    private const int TickAt = 28;
    private const int CountAt = 32;
    private const int TimeAt = 40;
    private const int GameTimeAt = 48;
    private const int LengthAt = 56;

    /// <summary>Writes the fixed prefix (everything except the entry count and the vector length, which <see cref="Finish"/> fills in).</summary>
    public static void Begin(Span<byte> buffer, uint serverTick, ulong serverTimeUs, double authorityGameTime)
    {
        buffer[..HeaderBytes].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 24); // root: the table
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[8..], 14); // vtable size
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[10..], 32); // table size
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[12..], 4); // server_tick
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[14..], 16); // server_time_us
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[16..], 24); // authority_game_time
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[18..], 8); // entry_count
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[20..], 12); // entries
        BinaryPrimitives.WriteInt32LittleEndian(buffer[24..], 16); // table position minus vtable position
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[TickAt..], serverTick);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[36..], 20); // the vector starts 20 bytes after this field
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[TimeAt..], serverTimeUs);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer[GameTimeAt..], authorityGameTime);
    }

    /// <summary>Completes the frame: entry count and the byte length of the entries. Returns the payload length.</summary>
    public static int Finish(Span<byte> buffer, int entryCount, int entryBytes)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[CountAt..], (ushort)entryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[LengthAt..], (uint)entryBytes);
        return HeaderBytes + entryBytes;
    }
}
