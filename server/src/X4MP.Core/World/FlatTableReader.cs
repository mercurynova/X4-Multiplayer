using System.Buffers.Binary;

namespace X4MP.Core.World;

/// <summary>
/// A bounds-checked view of one root FlatBuffers table over a <see cref="ReadOnlySpan{T}"/>: reads scalar fields and vectors of
/// fixed-size structs without creating a <c>ByteBuffer</c> (so the hot ingest path of <c>WorldUpdate</c> allocates nothing).
/// Field indices are the schema declaration order. Every access validates against the buffer length; malformed data
/// yields <c>false</c> instead of throwing.
/// </summary>
internal readonly ref struct FlatTableReader
{
    private readonly ReadOnlySpan<byte> _buf;
    private readonly int _table;
    private readonly int _vtable;
    private readonly int _vtableSize;

    private FlatTableReader(ReadOnlySpan<byte> buf, int table, int vtable, int vtableSize)
    {
        _buf = buf;
        _table = table;
        _vtable = vtable;
        _vtableSize = vtableSize;
    }

    public static bool TryRoot(ReadOnlySpan<byte> buf, out FlatTableReader reader)
    {
        reader = default;
        if (buf.Length < 8)
        {
            return false;
        }

        uint root = BinaryPrimitives.ReadUInt32LittleEndian(buf);
        if (root > (uint)buf.Length - 4)
        {
            return false;
        }

        int table = (int)root;
        int soffset = BinaryPrimitives.ReadInt32LittleEndian(buf[table..]);
        long vt = (long)table - soffset;
        if (vt < 0 || vt + 4 > buf.Length)
        {
            return false;
        }

        int vtable = (int)vt;
        int vtableSize = BinaryPrimitives.ReadUInt16LittleEndian(buf[vtable..]);
        if (vtableSize < 4 || vtable + vtableSize > buf.Length)
        {
            return false;
        }

        reader = new FlatTableReader(buf, table, vtable, vtableSize);
        return true;
    }

    /// <summary>Position of the field in the buffer, or false when the field is absent (default value) or lies outside the buffer.</summary>
    private bool TryField(int index, int size, out int pos)
    {
        pos = 0;
        int slot = 4 + (2 * index);
        if (slot + 2 > _vtableSize)
        {
            return false;
        }

        int offset = BinaryPrimitives.ReadUInt16LittleEndian(_buf[(_vtable + slot)..]);
        if (offset == 0)
        {
            return false;
        }

        long at = (long)_table + offset;
        if (at + size > _buf.Length)
        {
            return false;
        }

        pos = (int)at;
        return true;
    }

    public uint GetUInt32(int index) => TryField(index, 4, out int p) ? BinaryPrimitives.ReadUInt32LittleEndian(_buf[p..]) : 0;

    public ulong GetUInt64(int index) => TryField(index, 8, out int p) ? BinaryPrimitives.ReadUInt64LittleEndian(_buf[p..]) : 0;

    public double GetDouble(int index) => TryField(index, 8, out int p) ? BinaryPrimitives.ReadDoubleLittleEndian(_buf[p..]) : 0;

    /// <summary>The element bytes of a vector of fixed-size structs; empty (and true) when the field is absent.</summary>
    public bool TryGetStructVector(int index, int elementSize, out ReadOnlySpan<byte> elements, out int count)
    {
        elements = default;
        count = 0;
        if (!TryField(index, 4, out int p))
        {
            return true;
        }

        uint relative = BinaryPrimitives.ReadUInt32LittleEndian(_buf[p..]);
        long vec = (long)p + relative;
        if (vec + 4 > _buf.Length)
        {
            return false;
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(_buf[(int)vec..]);
        long end = vec + 4 + ((long)length * elementSize);
        if (end > _buf.Length)
        {
            return false;
        }

        elements = _buf.Slice((int)vec + 4, (int)length * elementSize);
        count = (int)length;
        return true;
    }
}
