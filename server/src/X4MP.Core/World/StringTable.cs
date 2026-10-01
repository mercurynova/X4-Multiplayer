using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.World;

/// <summary>One interned string (<c>StringEntry</c>).</summary>
public readonly record struct StringTableEntry(uint Index, StringKind Kind, string Value);

public enum StringApplyResult
{
    /// <summary>A new index was stored.</summary>
    Added,

    /// <summary>The index already held this value (a resend).</summary>
    Duplicate,

    /// <summary>The index already holds a different value: the first one is kept.</summary>
    Conflict,

    /// <summary>Index 0 (reserved: none/unchanged) or a null value.</summary>
    Invalid,
}

/// <summary>
/// The session string table (protocol.md 5, ADR-010): macro names, faction ids, ware ids and texts interned as
/// <c>u32</c> references. The authority allocates the indices and sends <c>StringTableAdd</c>; the server keeps the full
/// table, replays it to a joining node (<see cref="EncodeChunks"/>) and to a new authority, and tells the authority
/// where to continue (<see cref="NextIndex"/>, <c>AuthorityAssign.string_table_next</c>). Actor-thread only.
/// </summary>
public sealed class StringTable
{
    /// <summary>Entries per replayed <c>StringTableAdd</c> frame (keeps each well under the frame limit).</summary>
    public const int DefaultChunkEntries = 2000;

    private readonly Dictionary<uint, StringTableEntry> _byIndex = [];
    private readonly Dictionary<(StringKind, string), uint> _byValue = [];
    private uint _max;

    public int Count => _byIndex.Count;

    /// <summary>The next unused index (max + 1, at least 1).</summary>
    public uint NextIndex => _max + 1;

    /// <summary>Entries the authority sent that clashed with an earlier value (a bug on the authority; diagnostics).</summary>
    public int Conflicts { get; private set; }

    public StringApplyResult Add(uint index, StringKind kind, string? value)
    {
        if (index == 0 || value is null)
        {
            return StringApplyResult.Invalid;
        }

        if (_byIndex.TryGetValue(index, out var existing))
        {
            if (existing.Kind == kind && string.Equals(existing.Value, value, StringComparison.Ordinal))
            {
                return StringApplyResult.Duplicate;
            }

            Conflicts++;
            return StringApplyResult.Conflict;
        }

        _byIndex[index] = new StringTableEntry(index, kind, value);
        _byValue.TryAdd((kind, value), index);
        if (index > _max)
        {
            _max = index;
        }

        return StringApplyResult.Added;
    }

    public bool TryGet(uint index, out string value)
    {
        if (_byIndex.TryGetValue(index, out var entry))
        {
            value = entry.Value;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>The string for <paramref name="index"/>, or null (also for index 0).</summary>
    public string? Get(uint index) => _byIndex.TryGetValue(index, out var entry) ? entry.Value : null;

    public bool TryFind(StringKind kind, string value, out uint index) => _byValue.TryGetValue((kind, value), out index);

    /// <summary>Every entry in index order.</summary>
    public IReadOnlyList<StringTableEntry> Snapshot()
    {
        var list = new List<StringTableEntry>(_byIndex.Values);
        list.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        return list;
    }

    /// <summary>
    /// Applies a decoded <c>StringTableAdd</c>; returns the entries that were new (to persist and to forward), in order.
    /// </summary>
    public List<StringTableEntry> Apply(StringTableAdd message)
    {
        var added = new List<StringTableEntry>();
        for (int i = 0; i < message.EntriesLength; i++)
        {
            if (message.Entries(i) is not { } e)
            {
                continue;
            }

            if (Add(e.Index, e.Kind, e.Value) == StringApplyResult.Added)
            {
                added.Add(new StringTableEntry(e.Index, e.Kind, e.Value));
            }
        }

        return added;
    }

    /// <summary>Loads persisted entries (no duplicate checks beyond <see cref="Add"/>).</summary>
    public void Load(IEnumerable<StringTableEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var e in entries)
        {
            Add(e.Index, e.Kind, e.Value);
        }
    }

    public void Clear()
    {
        _byIndex.Clear();
        _byValue.Clear();
        _max = 0;
        Conflicts = 0;
    }

    /// <summary>The whole table as <c>StringTableAdd</c> payloads of at most <paramref name="maxEntries"/> entries each (join replay, authority migration).</summary>
    public List<byte[]> EncodeChunks(int maxEntries = DefaultChunkEntries) => EncodeChunks(Snapshot(), maxEntries);

    public static List<byte[]> EncodeChunks(IReadOnlyList<StringTableEntry> entries, int maxEntries = DefaultChunkEntries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        var result = new List<byte[]>();
        for (int offset = 0; offset < entries.Count; offset += maxEntries)
        {
            var t = new StringTableAddT { Entries = [] };
            int end = Math.Min(entries.Count, offset + maxEntries);
            for (int i = offset; i < end; i++)
            {
                t.Entries.Add(new StringEntryT { Index = entries[i].Index, Kind = entries[i].Kind, Value = entries[i].Value });
            }

            result.Add(MessageEncoder.EncodePayload(b => StringTableAdd.Pack(b, t), 1024 + (end - offset) * 48));
        }

        return result;
    }
}
