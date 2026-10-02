using X4MP.Core.World;
using X4MP.Proto;

namespace X4MP.Core.Saves;

/// <summary>What the save's <c>&lt;info&gt;</c> block says (best effort; any field may be missing).</summary>
public sealed record SaveMeta(string? GameVersion, string? SaveTime, string? PlayerName, long? PlayerMoney)
{
    public static SaveMeta Empty { get; } = new(null, null, null, null);
}

/// <summary>One stored save (the <c>saves</c> table row; manifests are files only).</summary>
/// <param name="Source"><c>authority</c> or <c>admin-upload</c>.</param>
/// <param name="GhostsCleaned">False for a save the authority flagged as containing ghosts: stored, but never made current.</param>
public sealed record SaveRecord(
    string Sha256,
    long SizeBytes,
    string DisplayName,
    string Source,
    string? UploadedBy,
    DateTimeOffset UploadedAt,
    SaveMeta Meta,
    bool GhostsCleaned,
    bool Pinned = false,
    string? OriginalFileName = null);

/// <summary>A checkpoint that became the session's current save (the <c>checkpoints</c> table row).</summary>
public sealed record CheckpointRecord(
    CheckpointId Id,
    string SaveSha256,
    string ManifestSha256,
    long ManifestSize,
    ulong JournalSeq,
    double GameTime,
    uint NextNetId,
    bool GhostsCleaned,
    DateTimeOffset At);

/// <summary>
/// Persistence seam of the save service (<c>saves</c>, <c>checkpoints</c>, <c>sessions.save_id/current_save_id</c>). Writes must return
/// without waiting for the database; reads may touch it (the HTTP side calls them off the actor thread).
/// </summary>
public interface ISaveCatalog
{
    /// <summary>Records a stored save. Idempotent by SHA-256 (a second add keeps the first row).</summary>
    void AddSave(SaveRecord save);

    SaveRecord? Find(string sha256);

    /// <summary>Every save, newest first.</summary>
    IReadOnlyList<SaveRecord> List();

    /// <summary>Renames or pins a save. False when it does not exist.</summary>
    bool Update(string sha256, string? displayName, bool? pinned);

    /// <summary>
    /// Deletes the row of a save with its checkpoint rows and returns the manifest SHA-256s those checkpoints pointed at (the caller
    /// deletes the files). Empty when there was no such save.
    /// </summary>
    IReadOnlyList<string> Delete(string sha256);

    void AddCheckpoint(long sessionId, CheckpointRecord checkpoint);

    /// <summary>Sets <c>sessions.current_save_id</c> (and <c>save_id</c> too when <paramref name="initial"/>).</summary>
    void SetSessionSave(long sessionId, string sha256, bool initial);

    /// <summary>SHA-256s of saves a session that has not ended still references (never deleted by the janitor).</summary>
    IReadOnlySet<string> ReferencedSha256();

    /// <summary>Manifest SHA-256s of the checkpoints that point at a stored save.</summary>
    IReadOnlySet<string> ManifestSha256();
}

/// <summary>Keeps everything in memory (tests; also the fallback without a database).</summary>
public sealed class InMemorySaveCatalog : ISaveCatalog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SaveRecord> _saves = new(StringComparer.Ordinal);
    private readonly List<(long Session, CheckpointRecord Checkpoint)> _checkpoints = [];
    private readonly Dictionary<long, (string? Current, string? Initial)> _sessions = [];

    public void AddSave(SaveRecord save)
    {
        lock (_gate)
        {
            _saves.TryAdd(save.Sha256, save);
        }
    }

    public SaveRecord? Find(string sha256)
    {
        lock (_gate)
        {
            return _saves.GetValueOrDefault(sha256);
        }
    }

    public IReadOnlyList<SaveRecord> List()
    {
        lock (_gate)
        {
            return [.. _saves.Values.OrderByDescending(s => s.UploadedAt)];
        }
    }

    public bool Update(string sha256, string? displayName, bool? pinned)
    {
        lock (_gate)
        {
            if (!_saves.TryGetValue(sha256, out var save))
            {
                return false;
            }

            _saves[sha256] = save with { DisplayName = displayName ?? save.DisplayName, Pinned = pinned ?? save.Pinned };
            return true;
        }
    }

    public IReadOnlyList<string> Delete(string sha256)
    {
        lock (_gate)
        {
            if (!_saves.Remove(sha256))
            {
                return [];
            }

            var manifests = _checkpoints.Where(c => c.Checkpoint.SaveSha256 == sha256).Select(c => c.Checkpoint.ManifestSha256).Distinct().ToList();
            _checkpoints.RemoveAll(c => c.Checkpoint.SaveSha256 == sha256);
            return manifests;
        }
    }

    public void AddCheckpoint(long sessionId, CheckpointRecord checkpoint)
    {
        lock (_gate)
        {
            _checkpoints.Add((sessionId, checkpoint));
        }
    }

    public void SetSessionSave(long sessionId, string sha256, bool initial)
    {
        lock (_gate)
        {
            var (_, first) = _sessions.GetValueOrDefault(sessionId);
            _sessions[sessionId] = (sha256, initial ? sha256 : first);
        }
    }

    public IReadOnlySet<string> ReferencedSha256()
    {
        lock (_gate)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (current, initial) in _sessions.Values)
            {
                if (current is not null)
                {
                    set.Add(current);
                }

                if (initial is not null)
                {
                    set.Add(initial);
                }
            }

            return set;
        }
    }

    public IReadOnlySet<string> ManifestSha256()
    {
        lock (_gate)
        {
            return _checkpoints.Where(c => _saves.ContainsKey(c.Checkpoint.SaveSha256)).Select(c => c.Checkpoint.ManifestSha256).ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>Checkpoints recorded so far (tests).</summary>
    public IReadOnlyList<CheckpointRecord> Checkpoints
    {
        get
        {
            lock (_gate)
            {
                return [.. _checkpoints.Select(c => c.Checkpoint)];
            }
        }
    }
}

/// <summary>An in-band transfer in progress, for the GUI (Sessions &amp; Saves, Players).</summary>
public sealed record TransferSnapshot(
    uint Id,
    bool IsUpload,
    int PlayerId,
    string PlayerName,
    string Sha256,
    UploadKind Kind,
    long Size,
    long Done,
    long StartOffset,
    DateTimeOffset StartedAt);

/// <summary>
/// Called once per session when its first checkpoint became current, on the actor thread: the place where the session is seeded from the
/// save (money into the economy, ADR-033; later the per-team HQ and starting blueprints, ADR-048/049).
/// </summary>
public interface ISaveSeedHook
{
    void OnInitialSaveStored(InitialSaveInfo info);
}

/// <summary>The first current checkpoint of a session.</summary>
/// <param name="PlayerMoney">The <c>money</c> attribute of the save's <c>&lt;player&gt;</c> element (whole credits, not cents), if present.</param>
public sealed record InitialSaveInfo(long SessionId, int AuthorityPlayerId, CheckpointId Checkpoint, string SaveSha256, SaveMeta Meta, double GameTime, uint NextNetId);
