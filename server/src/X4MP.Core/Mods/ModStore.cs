using X4MP.Proto;

namespace X4MP.Core.Mods;

/// <summary>What happened to a <c>ClientHello</c>'s extension report (docs/mod-management.md 7: <c>player_extension_reports.outcome</c>).</summary>
public enum ModReportOutcome
{
    /// <summary>No violations.</summary>
    Admitted,

    /// <summary>Violations, admitted because enforcement is Warn.</summary>
    Warned,

    /// <summary>Violations, refused with <c>ExtensionsMismatch</c>.</summary>
    Rejected,
}

/// <summary>
/// One stored extension report: the full list the node sent and how the policy judged it. <c>PlayerId</c> 0 means a refused connection whose key is bound to no player
/// (<c>KeyHash</c> and <c>AttemptedName</c> identify it; it never claimed the name). Such reports move to the player when that key is later admitted (<see cref="IModStore.AttachKey"/>).
/// </summary>
public sealed record ExtensionReportRecord(
    int PlayerId,
    long? SessionId,
    DateTimeOffset At,
    byte[] ExtensionsHash,
    IReadOnlyList<ExtensionInfoT> Items,
    ModReportOutcome Outcome,
    ModPolicyViolationT? Violation,
    uint PolicyVersion,
    byte[]? KeyHash = null,
    string? AttemptedName = null);

/// <summary>The stored session mod policy: the knobs, the version and the entries (<c>session_mod_policy</c> + <c>session_mod_entries</c>).</summary>
public sealed record StoredModPolicy(
    uint Version,
    ModSourceMode SourceMode,
    UnknownModDefault UnknownDefault,
    ModEnforcement Enforcement,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    IReadOnlyList<ModPolicyEntryT> Entries);

/// <summary>The server-wide memory of what is known about a mod (<c>mod_catalog</c>): links and notes typed once are reused by every session.</summary>
public sealed record ModCatalogRecord(
    string ExtId,
    string Name,
    string? NexusUrl,
    ulong WorkshopId,
    ExtensionClass ClassOverride,
    string? Notes,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Persistence of the mod-management data (task M1-X3): the session mod policy, each player's extension reports and the mod catalog.
/// Policy and catalog writes are synchronous (admin edits are rare and must be durable before the response); report writes are
/// write-behind, but reads see them at once.
/// </summary>
public interface IModStore
{
    /// <summary>Reports kept per player (the janitor rule).</summary>
    public const int ReportsPerPlayer = 20;

    /// <summary>Raised after <see cref="RecordReport"/> (on the thread that recorded it).</summary>
    event Action<ExtensionReportRecord>? ReportRecorded;

    /// <summary>The stored policy, or null when none was ever saved.</summary>
    StoredModPolicy? LoadPolicy();

    /// <summary>Replaces the stored policy and its entries.</summary>
    void SavePolicy(StoredModPolicy policy);

    /// <summary>Stores a report (and learns the names and Workshop ids of its mods into the catalog) and trims the player's history to <see cref="ReportsPerPlayer"/>.</summary>
    void RecordReport(ExtensionReportRecord report);

    /// <summary>Moves the reports of a refused connection (player 0) filed under <paramref name="keyHash"/> to <paramref name="playerId"/> (its key was admitted).</summary>
    void AttachKey(byte[] keyHash, int playerId);

    /// <summary>The newest report of every key that was refused and is bound to no player (newest first); admins see attempts by key here.</summary>
    IReadOnlyList<ExtensionReportRecord> UnboundReports();

    /// <summary>The newest report of a player, or null.</summary>
    ExtensionReportRecord? LatestReport(int playerId);

    /// <summary>The newest <paramref name="limit"/> reports of a player, newest first.</summary>
    IReadOnlyList<ExtensionReportRecord> Reports(int playerId, int limit);

    /// <summary>The newest report of every player that has one.</summary>
    IReadOnlyList<ExtensionReportRecord> LatestReports();

    IReadOnlyList<ModCatalogRecord> Catalog();

    ModCatalogRecord? CatalogEntry(string extId);

    /// <summary>Inserts or replaces a catalog row (an admin edit).</summary>
    void UpsertCatalog(ModCatalogRecord entry);
}

/// <summary>In-memory <see cref="IModStore"/> for tests and for running without persistence.</summary>
public sealed class InMemoryModStore : IModStore
{
    private readonly Lock _gate = new();
    private StoredModPolicy? _policy;
    private readonly Dictionary<int, List<ExtensionReportRecord>> _reports = [];
    private readonly Dictionary<string, ModCatalogRecord> _catalog = new(StringComparer.Ordinal);

    public event Action<ExtensionReportRecord>? ReportRecorded;

    public StoredModPolicy? LoadPolicy()
    {
        lock (_gate)
        {
            return _policy;
        }
    }

    public void SavePolicy(StoredModPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        lock (_gate)
        {
            _policy = policy;
        }
    }

    public void RecordReport(ExtensionReportRecord report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            if (report.PlayerId == 0)
            {
                AddUnbound(_unbound, report);
                ReportRecorded?.Invoke(report);
                return;
            }

            if (!_reports.TryGetValue(report.PlayerId, out var list))
            {
                _reports[report.PlayerId] = list = [];
            }

            list.Insert(0, report);
            if (list.Count > IModStore.ReportsPerPlayer)
            {
                list.RemoveRange(IModStore.ReportsPerPlayer, list.Count - IModStore.ReportsPerPlayer);
            }

            foreach (var e in report.Items)
            {
                ModCatalogLearning.Learn(_catalog, e, report.At);
            }
        }

        ReportRecorded?.Invoke(report);
    }

    private readonly List<ExtensionReportRecord> _unbound = [];

    /// <summary>Unbound reports kept at all (a refusal costs an unauthenticated client nothing, so the table must not grow with it).</summary>
    public const int MaxUnbound = 200;

    /// <summary>Inserts newest-first, at most <see cref="IModStore.ReportsPerPlayer"/> per key and <see cref="MaxUnbound"/> overall.</summary>
    public static void AddUnbound(List<ExtensionReportRecord> list, ExtensionReportRecord report)
    {
        list.Insert(0, report);
        int same = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].KeyHash is { } k && report.KeyHash is { } r && k.AsSpan().SequenceEqual(r) && ++same > IModStore.ReportsPerPlayer)
            {
                list.RemoveAt(i--);
            }
        }

        if (list.Count > MaxUnbound)
        {
            list.RemoveRange(MaxUnbound, list.Count - MaxUnbound);
        }
    }

    public void AttachKey(byte[] keyHash, int playerId)
    {
        ArgumentNullException.ThrowIfNull(keyHash);
        lock (_gate)
        {
            var mine = _unbound.Where(r => r.KeyHash is { } k && k.AsSpan().SequenceEqual(keyHash)).ToList();
            if (mine.Count == 0)
            {
                return;
            }

            _unbound.RemoveAll(mine.Contains);
            if (!_reports.TryGetValue(playerId, out var list))
            {
                _reports[playerId] = list = [];
            }

            list.AddRange(mine.Select(r => r with { PlayerId = playerId }));
            list.Sort((a, b) => b.At.CompareTo(a.At));
            if (list.Count > IModStore.ReportsPerPlayer)
            {
                list.RemoveRange(IModStore.ReportsPerPlayer, list.Count - IModStore.ReportsPerPlayer);
            }
        }
    }

    public IReadOnlyList<ExtensionReportRecord> UnboundReports()
    {
        lock (_gate)
        {
            return [.. _unbound.GroupBy(r => Convert.ToHexString(r.KeyHash ?? [])).Select(g => g.First()).OrderByDescending(r => r.At)];
        }
    }

    public ExtensionReportRecord? LatestReport(int playerId)
    {
        lock (_gate)
        {
            return _reports.TryGetValue(playerId, out var list) && list.Count > 0 ? list[0] : null;
        }
    }

    public IReadOnlyList<ExtensionReportRecord> Reports(int playerId, int limit)
    {
        lock (_gate)
        {
            return _reports.TryGetValue(playerId, out var list) ? [.. list.Take(Math.Max(0, limit))] : [];
        }
    }

    public IReadOnlyList<ExtensionReportRecord> LatestReports()
    {
        lock (_gate)
        {
            return [.. _reports.Values.Where(l => l.Count > 0).Select(l => l[0]).OrderBy(r => r.PlayerId)];
        }
    }

    public IReadOnlyList<ModCatalogRecord> Catalog()
    {
        lock (_gate)
        {
            return [.. _catalog.Values.OrderBy(c => c.ExtId, StringComparer.Ordinal)];
        }
    }

    public ModCatalogRecord? CatalogEntry(string extId)
    {
        lock (_gate)
        {
            return _catalog.GetValueOrDefault(extId);
        }
    }

    public void UpsertCatalog(ModCatalogRecord entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _catalog[entry.ExtId] = entry;
        }
    }
}

/// <summary>What a report teaches the catalog: a mod's name and Workshop id, filled in only where the catalog has none (an admin's edit wins).</summary>
public static class ModCatalogLearning
{
    /// <summary>True when a report row is worth a catalog row (not a DLC, not the X4Native/X4MP extensions).</summary>
    public static bool IsCatalogable(ExtensionInfoT e) =>
        !string.IsNullOrEmpty(e.Id) && !e.Egosoft && e.Source != ExtensionSource.Dlc
        && !X4MP.Protocol.ExtensionReports.IsDlcId(e.Id) && !X4MP.Protocol.ExtensionReports.IsHashExcluded(e.Id);

    public static void Learn(Dictionary<string, ModCatalogRecord> catalog, ExtensionInfoT e, DateTimeOffset now)
    {
        if (!IsCatalogable(e))
        {
            return;
        }

        ulong ws = e.WorkshopId != 0 ? e.WorkshopId : X4MP.Protocol.ModLinks.WorkshopIdOf(e.Id);
        if (!catalog.TryGetValue(e.Id, out var existing))
        {
            catalog[e.Id] = new ModCatalogRecord(e.Id, e.Name ?? string.Empty, null, ws, ExtensionClass.Unknown, null, now);
            return;
        }

        string name = string.IsNullOrEmpty(existing.Name) ? e.Name ?? string.Empty : existing.Name;
        ulong workshop = existing.WorkshopId != 0 ? existing.WorkshopId : ws;
        if (name != existing.Name || workshop != existing.WorkshopId)
        {
            catalog[e.Id] = existing with { Name = name, WorkshopId = workshop };
        }
    }
}
