namespace X4MP.Core.World;

/// <summary>
/// Persistence seam of the world mirror: the <c>galaxy_cache</c>, <c>journal</c> and <c>string_table</c> tables. Writes are
/// write-behind (they must return without waiting for the database); the loads are synchronous because they run once, at
/// the start of a session or when a galaxy is activated, never per frame.
/// </summary>
public interface IWorldStore
{
    /// <summary>The cached <c>GalaxyMetadata</c> payload for a save (lowercase hex SHA-256), or null.</summary>
    byte[]? TryLoadGalaxy(string saveSha256Hex);

    void SaveGalaxy(string saveSha256Hex, byte[] payload, DateTimeOffset at);

    void AppendJournal(JournalRecord record);

    /// <summary>Deletes every journal row of the session with a sequence below <paramref name="beforeSeq"/>.</summary>
    void TruncateJournal(ulong beforeSeq);

    /// <summary>The journal rows of the session, in sequence order.</summary>
    IReadOnlyList<JournalRecord> LoadJournal();

    void AppendStrings(IReadOnlyList<StringTableEntry> entries);

    IReadOnlyList<StringTableEntry> LoadStrings();
}

/// <summary>Discards everything (running without persistence).</summary>
public sealed class NullWorldStore : IWorldStore
{
    public static NullWorldStore Instance { get; } = new();

    public byte[]? TryLoadGalaxy(string saveSha256Hex) => null;

    public void SaveGalaxy(string saveSha256Hex, byte[] payload, DateTimeOffset at)
    {
    }

    public void AppendJournal(JournalRecord record)
    {
    }

    public void TruncateJournal(ulong beforeSeq)
    {
    }

    public IReadOnlyList<JournalRecord> LoadJournal() => [];

    public void AppendStrings(IReadOnlyList<StringTableEntry> entries)
    {
    }

    public IReadOnlyList<StringTableEntry> LoadStrings() => [];
}

/// <summary>Keeps everything in memory (tests; also the fallback when the server runs without a database).</summary>
public sealed class InMemoryWorldStore : IWorldStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _galaxies = [];
    private readonly List<JournalRecord> _journal = [];
    private readonly List<StringTableEntry> _strings = [];

    public byte[]? TryLoadGalaxy(string saveSha256Hex)
    {
        lock (_gate)
        {
            return _galaxies.TryGetValue(saveSha256Hex, out var value) ? value : null;
        }
    }

    public void SaveGalaxy(string saveSha256Hex, byte[] payload, DateTimeOffset at)
    {
        lock (_gate)
        {
            _galaxies[saveSha256Hex] = payload;
        }
    }

    public void AppendJournal(JournalRecord record)
    {
        lock (_gate)
        {
            _journal.Add(record);
        }
    }

    public void TruncateJournal(ulong beforeSeq)
    {
        lock (_gate)
        {
            _journal.RemoveAll(r => r.Seq < beforeSeq);
        }
    }

    public IReadOnlyList<JournalRecord> LoadJournal()
    {
        lock (_gate)
        {
            return [.. _journal];
        }
    }

    public void AppendStrings(IReadOnlyList<StringTableEntry> entries)
    {
        lock (_gate)
        {
            _strings.AddRange(entries);
        }
    }

    public IReadOnlyList<StringTableEntry> LoadStrings()
    {
        lock (_gate)
        {
            return [.. _strings];
        }
    }

    public int GalaxyCount
    {
        get
        {
            lock (_gate)
            {
                return _galaxies.Count;
            }
        }
    }
}
