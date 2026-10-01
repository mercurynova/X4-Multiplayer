using System.Buffers.Binary;

namespace X4MP.Protocol;

/// <summary>Field mask of a Replication entry (protocol.md 10.2). Fields follow in bit order.</summary>
[Flags]
public enum ReplicationMask : byte
{
    None = 0,
    /// <summary>u16 sector index.</summary>
    Sector = 1 << 0,
    /// <summary>3 x i32, 1/64 m, sector-relative.</summary>
    Pos = 1 << 1,
    /// <summary>3 x i16 (yaw, pitch, roll), rad * 32768/pi.</summary>
    Rot = 1 << 2,
    /// <summary>3 x i16, 0.25 m/s (4 m/s when StateFlags.VelCoarse).</summary>
    Vel = 1 << 3,
    /// <summary>u16 StateFlags.</summary>
    Flags = 1 << 4,
    /// <summary>u8 hull, u8 shield.</summary>
    Status = 1 << 5,
    /// <summary>i16 sample-time offset in ms from Replication.server_time_us.</summary>
    Time = 1 << 6,
    /// <summary>u8 len + len bytes; reserved, receivers skip.</summary>
    Ext = 1 << 7,
}

/// <summary>
/// One Replication entry. Only the fields named by <see cref="Mask"/> are meaningful (and written/read);
/// the rest are zero. Fields carry absolute values; an omitted field means "same as the client's baseline".
/// </summary>
public struct ReplicationEntry
{
    public uint NetId { get; set; }
    public ReplicationMask Mask { get; set; }
    public ushort Sector { get; set; }
    public int PosX { get; set; }
    public int PosY { get; set; }
    public int PosZ { get; set; }
    public short Yaw { get; set; }
    public short Pitch { get; set; }
    public short Roll { get; set; }
    public short VelX { get; set; }
    public short VelY { get; set; }
    public short VelZ { get; set; }
    public ushort StateFlags { get; set; }
    public byte Hull { get; set; }
    public byte Shield { get; set; }
    public short TimeMs { get; set; }

    /// <summary>EXT payload (at most 255 bytes); required (possibly empty) iff <see cref="ReplicationMask.Ext"/> is set.</summary>
    public byte[]? Ext { get; set; }
}

/// <summary>
/// The hand-specified Replication entry codec (protocol.md 10.2): little-endian, unaligned,
/// <c>net_id:u32 mask:u8 [fields in bit order]</c>, carried in <c>Replication.entries</c>.
/// </summary>
public static class ReplicationCodec
{
    /// <summary>net_id + mask.</summary>
    public const int MinEntrySize = 5;

    public const uint ReservedNetId = 0xFFFFFFFF;

    /// <summary>Encoded size of an entry (including EXT bytes).</summary>
    public static int GetSize(in ReplicationEntry e)
    {
        int n = MinEntrySize;
        var m = e.Mask;
        if ((m & ReplicationMask.Sector) != 0) n += 2;
        if ((m & ReplicationMask.Pos) != 0) n += 12;
        if ((m & ReplicationMask.Rot) != 0) n += 6;
        if ((m & ReplicationMask.Vel) != 0) n += 6;
        if ((m & ReplicationMask.Flags) != 0) n += 2;
        if ((m & ReplicationMask.Status) != 0) n += 2;
        if ((m & ReplicationMask.Time) != 0) n += 2;
        if ((m & ReplicationMask.Ext) != 0) n += 1 + (e.Ext?.Length ?? 0);
        return n;
    }

    /// <summary>Writes one entry; returns bytes written. Throws <see cref="ProtocolViolation"/> on invalid input.</summary>
    public static int Write(Span<byte> destination, in ReplicationEntry e)
    {
        if (e.NetId == 0 || e.NetId == ReservedNetId)
            throw new ProtocolViolation(ViolationCode.MalformedReplication, $"net_id 0x{e.NetId:X8} is not allowed");
        var m = e.Mask;
        if ((m & ReplicationMask.Ext) != 0)
        {
            if ((e.Ext?.Length ?? 0) > byte.MaxValue)
                throw new ProtocolViolation(ViolationCode.MalformedReplication, "EXT longer than 255 bytes");
        }
        else if (e.Ext is { Length: > 0 })
        {
            throw new ProtocolViolation(ViolationCode.MalformedReplication, "EXT bytes set but mask bit EXT is clear");
        }

        int size = GetSize(e);
        if (destination.Length < size)
            throw new ArgumentException($"Destination needs {size} bytes, has {destination.Length}.", nameof(destination));

        BinaryPrimitives.WriteUInt32LittleEndian(destination, e.NetId);
        destination[4] = (byte)m;
        int p = MinEntrySize;
        if ((m & ReplicationMask.Sector) != 0) { BinaryPrimitives.WriteUInt16LittleEndian(destination[p..], e.Sector); p += 2; }
        if ((m & ReplicationMask.Pos) != 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination[p..], e.PosX);
            BinaryPrimitives.WriteInt32LittleEndian(destination[(p + 4)..], e.PosY);
            BinaryPrimitives.WriteInt32LittleEndian(destination[(p + 8)..], e.PosZ);
            p += 12;
        }
        if ((m & ReplicationMask.Rot) != 0)
        {
            BinaryPrimitives.WriteInt16LittleEndian(destination[p..], e.Yaw);
            BinaryPrimitives.WriteInt16LittleEndian(destination[(p + 2)..], e.Pitch);
            BinaryPrimitives.WriteInt16LittleEndian(destination[(p + 4)..], e.Roll);
            p += 6;
        }
        if ((m & ReplicationMask.Vel) != 0)
        {
            BinaryPrimitives.WriteInt16LittleEndian(destination[p..], e.VelX);
            BinaryPrimitives.WriteInt16LittleEndian(destination[(p + 2)..], e.VelY);
            BinaryPrimitives.WriteInt16LittleEndian(destination[(p + 4)..], e.VelZ);
            p += 6;
        }
        if ((m & ReplicationMask.Flags) != 0) { BinaryPrimitives.WriteUInt16LittleEndian(destination[p..], e.StateFlags); p += 2; }
        if ((m & ReplicationMask.Status) != 0) { destination[p] = e.Hull; destination[p + 1] = e.Shield; p += 2; }
        if ((m & ReplicationMask.Time) != 0) { BinaryPrimitives.WriteInt16LittleEndian(destination[p..], e.TimeMs); p += 2; }
        if ((m & ReplicationMask.Ext) != 0)
        {
            int len = e.Ext?.Length ?? 0;
            destination[p++] = (byte)len;
            e.Ext.AsSpan().CopyTo(destination[p..]);
            p += len;
        }
        return p;
    }

    /// <summary>Encodes entries back to back (the value of <c>Replication.entries</c>).</summary>
    public static byte[] Encode(ReadOnlySpan<ReplicationEntry> entries)
    {
        int total = 0;
        foreach (ref readonly var e in entries)
            total += GetSize(e);

        var buffer = new byte[total];
        int p = 0;
        foreach (ref readonly var e in entries)
            p += Write(buffer.AsSpan(p), e);
        return buffer;
    }

    /// <summary>
    /// Reads one entry from the start of <paramref name="source"/>. Throws <see cref="ProtocolViolation"/>
    /// (MalformedReplication) if the bytes end early, net_id is 0 or reserved, or EXT overruns the buffer.
    /// EXT bytes are skipped semantically but returned in <see cref="ReplicationEntry.Ext"/>.
    /// </summary>
    public static ReplicationEntry Read(ReadOnlySpan<byte> source, out int consumed)
    {
        if (source.Length < MinEntrySize)
            throw new ProtocolViolation(ViolationCode.MalformedReplication, $"entry needs at least {MinEntrySize} bytes, got {source.Length}");

        var e = new ReplicationEntry
        {
            NetId = BinaryPrimitives.ReadUInt32LittleEndian(source),
            Mask = (ReplicationMask)source[4],
        };
        if (e.NetId == 0 || e.NetId == ReservedNetId)
            throw new ProtocolViolation(ViolationCode.MalformedReplication, $"net_id 0x{e.NetId:X8} is not allowed");

        var m = e.Mask;
        int fixedSize = GetSize(e); // EXT contributes 1 (len byte) because Ext is still null
        if (source.Length < fixedSize)
            throw new ProtocolViolation(ViolationCode.MalformedReplication, $"entry for net_id {e.NetId} is truncated ({source.Length} of {fixedSize} bytes)");

        int p = MinEntrySize;
        if ((m & ReplicationMask.Sector) != 0) { e.Sector = BinaryPrimitives.ReadUInt16LittleEndian(source[p..]); p += 2; }
        if ((m & ReplicationMask.Pos) != 0)
        {
            e.PosX = BinaryPrimitives.ReadInt32LittleEndian(source[p..]);
            e.PosY = BinaryPrimitives.ReadInt32LittleEndian(source[(p + 4)..]);
            e.PosZ = BinaryPrimitives.ReadInt32LittleEndian(source[(p + 8)..]);
            p += 12;
        }
        if ((m & ReplicationMask.Rot) != 0)
        {
            e.Yaw = BinaryPrimitives.ReadInt16LittleEndian(source[p..]);
            e.Pitch = BinaryPrimitives.ReadInt16LittleEndian(source[(p + 2)..]);
            e.Roll = BinaryPrimitives.ReadInt16LittleEndian(source[(p + 4)..]);
            p += 6;
        }
        if ((m & ReplicationMask.Vel) != 0)
        {
            e.VelX = BinaryPrimitives.ReadInt16LittleEndian(source[p..]);
            e.VelY = BinaryPrimitives.ReadInt16LittleEndian(source[(p + 2)..]);
            e.VelZ = BinaryPrimitives.ReadInt16LittleEndian(source[(p + 4)..]);
            p += 6;
        }
        if ((m & ReplicationMask.Flags) != 0) { e.StateFlags = BinaryPrimitives.ReadUInt16LittleEndian(source[p..]); p += 2; }
        if ((m & ReplicationMask.Status) != 0) { e.Hull = source[p]; e.Shield = source[p + 1]; p += 2; }
        if ((m & ReplicationMask.Time) != 0) { e.TimeMs = BinaryPrimitives.ReadInt16LittleEndian(source[p..]); p += 2; }
        if ((m & ReplicationMask.Ext) != 0)
        {
            int len = source[p++];
            if (source.Length - p < len)
                throw new ProtocolViolation(ViolationCode.MalformedReplication, $"EXT length {len} overruns the entry buffer");
            e.Ext = source.Slice(p, len).ToArray();
            p += len;
        }

        consumed = p;
        return e;
    }

    /// <summary>
    /// Decodes <c>Replication.entries</c>: exactly <paramref name="entryCount"/> entries that consume the
    /// whole buffer. Anything else (short, trailing bytes, bad entry) is a <see cref="ProtocolViolation"/>.
    /// </summary>
    public static List<ReplicationEntry> Decode(ReadOnlySpan<byte> entries, int entryCount)
    {
        if (entryCount < 0 || (long)entryCount * MinEntrySize > entries.Length)
            throw new ProtocolViolation(ViolationCode.MalformedReplication, $"entry_count {entryCount} cannot fit in {entries.Length} bytes");

        var result = new List<ReplicationEntry>(entryCount);
        int p = 0;
        for (int i = 0; i < entryCount; i++)
        {
            result.Add(Read(entries[p..], out int used));
            p += used;
        }
        if (p != entries.Length)
            throw new ProtocolViolation(ViolationCode.MalformedReplication, $"{entries.Length - p} trailing bytes after {entryCount} entries");
        return result;
    }
}
