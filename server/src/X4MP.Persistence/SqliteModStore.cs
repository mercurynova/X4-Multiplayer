using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using X4MP.Core.Mods;
using X4MP.Proto;

namespace X4MP.Persistence;

/// <summary>
/// SQLite implementation of <see cref="IModStore"/> over the tables of migration 0008. Policy and catalog writes run synchronously in their own
/// transaction (admin edits are rare). Reports go through the write-behind <see cref="PersistenceWriter"/>; the newest
/// <see cref="IModStore.ReportsPerPlayer"/> reports of every player seen in this process are also kept in memory, so a read right after a write sees it.
/// The janitor rule (keep 20 per player) runs inside the insert transaction.
/// </summary>
public sealed class SqliteModStore(SqliteConnectionFactory factory, PersistenceWriter writer) : IModStore
{
    /// <summary>The policy is stored under this <c>session_id</c> (one standing policy for the server).</summary>
    public const long PolicyKey = 0;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();
    private readonly Dictionary<int, List<ExtensionReportRecord>> _cache = [];

    public event Action<ExtensionReportRecord>? ReportRecorded;

    private static string Stamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string EnumName<T>(T value) where T : struct, Enum => value.ToString();

    private static T ParseEnum<T>(string? text, T fallback) where T : struct, Enum =>
        System.Enum.TryParse<T>(text, ignoreCase: true, out var value) && System.Enum.IsDefined(value) ? value : fallback;

    // ------------------------------------------------------------------ policy

    public StoredModPolicy? LoadPolicy()
    {
        using var connection = factory.Open();
        var row = connection.QuerySingleOrDefault<PolicyRow>(
            "SELECT version AS Version, source_mode AS SourceMode, unknown_default AS UnknownDefault, enforcement AS Enforcement, updated_at AS UpdatedAt, updated_by AS UpdatedBy FROM session_mod_policy WHERE session_id = @key",
            new { key = PolicyKey });
        if (row is null)
        {
            return null;
        }

        var entries = connection.Query<EntryRow>(
            "SELECT ext_id AS ExtId, name AS Name, rule AS Rule, enabled AS Enabled, class AS Class, version_rule AS VersionRule, version AS Version, content_hash AS ContentHash, nexus_url AS NexusUrl, workshop_id AS WorkshopId, notes AS Notes FROM session_mod_entries WHERE session_id = @key ORDER BY sort, ext_id",
            new { key = PolicyKey })
            .Select(r => new ModPolicyEntryT
            {
                Id = r.ExtId,
                Name = r.Name ?? string.Empty,
                Rule = ParseEnum(r.Rule, ModRule.Required),
                Enabled = r.Enabled != 0,
                ModClass = ParseEnum(r.Class, ExtensionClass.Unknown),
                VersionRule = ParseEnum(r.VersionRule, VersionRule.Exact),
                Version = r.Version ?? string.Empty,
                ContentHash = r.ContentHash is { Length: > 0 } hash ? [.. hash] : null,
                NexusUrl = r.NexusUrl ?? string.Empty,
                WorkshopId = (ulong)Math.Max(0, r.WorkshopId ?? 0),
                Notes = r.Notes ?? string.Empty,
            })
            .ToList();
        return new StoredModPolicy(
            (uint)row.Version,
            ParseEnum(row.SourceMode, ModSourceMode.AuthorityDefines),
            ParseEnum(row.UnknownDefault, UnknownModDefault.AllowClientOnly),
            ParseEnum(row.Enforcement, ModEnforcement.Strict),
            Parse(row.UpdatedAt),
            row.UpdatedBy,
            entries);
    }

    public void SavePolicy(StoredModPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        using var connection = factory.Open();
        using var tx = connection.BeginTransaction();
        connection.Execute("DELETE FROM session_mod_entries WHERE session_id = @key", new { key = PolicyKey }, tx);
        connection.Execute("DELETE FROM session_mod_policy WHERE session_id = @key", new { key = PolicyKey }, tx);
        connection.Execute(
            "INSERT INTO session_mod_policy (session_id, version, source_mode, unknown_default, enforcement, updated_at, updated_by) VALUES (@key, @version, @mode, @unknown, @enforcement, @at, @by)",
            new { key = PolicyKey, version = (long)policy.Version, mode = EnumName(policy.SourceMode), unknown = EnumName(policy.UnknownDefault), enforcement = EnumName(policy.Enforcement), at = Stamp(policy.UpdatedAt), by = policy.UpdatedBy },
            tx);
        int sort = 0;
        foreach (var e in policy.Entries)
        {
            connection.Execute(
                """
                INSERT INTO session_mod_entries (session_id, ext_id, name, rule, enabled, class, version_rule, version, content_hash, nexus_url, workshop_id, notes, sort)
                VALUES (@key, @id, @name, @rule, @enabled, @class, @versionRule, @version, @hash, @nexus, @workshop, @notes, @sort)
                """,
                new
                {
                    key = PolicyKey,
                    id = e.Id,
                    name = e.Name ?? string.Empty,
                    rule = EnumName(e.Rule),
                    enabled = e.Enabled ? 1 : 0,
                    @class = EnumName(e.ModClass),
                    versionRule = EnumName(e.VersionRule),
                    version = e.Version,
                    hash = e.ContentHash is { Count: > 0 } ? e.ContentHash.ToArray() : null,
                    nexus = e.NexusUrl,
                    workshop = (long)e.WorkshopId,
                    notes = e.Notes,
                    sort = sort++,
                },
                tx);
        }

        tx.Commit();
    }

    // ------------------------------------------------------------------ reports

    public void RecordReport(ExtensionReportRecord report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            var list = CacheFor(report.PlayerId);
            list.Insert(0, report);
            if (list.Count > IModStore.ReportsPerPlayer)
            {
                list.RemoveRange(IModStore.ReportsPerPlayer, list.Count - IModStore.ReportsPerPlayer);
            }

            foreach (var e in report.Items)
            {
                ModCatalogLearning.Learn(_learned, e, report.At);
            }
        }

        string itemsJson = JsonSerializer.Serialize(report.Items.Select(StoredExtension.From).ToList(), Json);
        string? violationJson = report.Violation is null ? null : JsonSerializer.Serialize(StoredViolation.From(report.Violation), Json);
        var catalog = report.Items.Where(ModCatalogLearning.IsCatalogable).ToList();
        string stamp = Stamp(report.At);
        writer.TryEnqueue((connection, tx) =>
        {
            connection.Execute(
                "INSERT INTO player_extension_reports (player_id, session_id, ts, ext_hash, items_json, outcome, violation_json, policy_version) VALUES (@player, @session, @ts, @hash, @items, @outcome, @violation, @version)",
                new { player = report.PlayerId, session = report.SessionId, ts = stamp, hash = report.ExtensionsHash, items = itemsJson, outcome = EnumName(report.Outcome), violation = violationJson, version = (long)report.PolicyVersion },
                tx);
            connection.Execute(
                "DELETE FROM player_extension_reports WHERE player_id = @player AND id NOT IN (SELECT id FROM player_extension_reports WHERE player_id = @player ORDER BY ts DESC, id DESC LIMIT @keep)",
                new { player = report.PlayerId, keep = IModStore.ReportsPerPlayer },
                tx);
            foreach (var e in catalog)
            {
                ulong ws = e.WorkshopId != 0 ? e.WorkshopId : X4MP.Protocol.ModLinks.WorkshopIdOf(e.Id);
                connection.Execute(
                    """
                    INSERT INTO mod_catalog (ext_id, name, workshop_id, updated_at) VALUES (@id, @name, @ws, @ts)
                    ON CONFLICT(ext_id) DO UPDATE SET
                      name = CASE WHEN COALESCE(mod_catalog.name, '') = '' THEN excluded.name ELSE mod_catalog.name END,
                      workshop_id = CASE WHEN COALESCE(mod_catalog.workshop_id, 0) = 0 THEN excluded.workshop_id ELSE mod_catalog.workshop_id END
                    """,
                    new { id = e.Id, name = e.Name ?? string.Empty, ws = (long)ws, ts = stamp },
                    tx);
            }
        });
        ReportRecorded?.Invoke(report);
    }

    public ExtensionReportRecord? LatestReport(int playerId)
    {
        lock (_gate)
        {
            return CacheFor(playerId) is { Count: > 0 } list ? list[0] : null;
        }
    }

    public IReadOnlyList<ExtensionReportRecord> Reports(int playerId, int limit)
    {
        lock (_gate)
        {
            return [.. CacheFor(playerId).Take(Math.Clamp(limit, 0, IModStore.ReportsPerPlayer))];
        }
    }

    public IReadOnlyList<ExtensionReportRecord> LatestReports()
    {
        List<int> players;
        using (var connection = factory.Open())
        {
            players = [.. connection.Query<long>("SELECT DISTINCT player_id FROM player_extension_reports").Select(p => (int)p)];
        }

        lock (_gate)
        {
            players.AddRange(_cache.Keys);
            return [.. players.Distinct().Order().Select(p => CacheFor(p) is { Count: > 0 } list ? list[0] : null).OfType<ExtensionReportRecord>()];
        }
    }

    /// <summary>The player's cached reports (newest first); loads them from the database the first time. Call with the lock held.</summary>
    private List<ExtensionReportRecord> CacheFor(int playerId)
    {
        if (_cache.TryGetValue(playerId, out var list))
        {
            return list;
        }

        using var connection = factory.Open();
        list = [.. connection.Query<ReportRow>(
                "SELECT player_id AS PlayerId, session_id AS SessionId, ts AS Ts, ext_hash AS Hash, items_json AS Items, outcome AS Outcome, violation_json AS Violation, policy_version AS PolicyVersion FROM player_extension_reports WHERE player_id = @playerId ORDER BY ts DESC, id DESC LIMIT @limit",
                new { playerId, limit = IModStore.ReportsPerPlayer })
            .Select(ToRecord)];
        _cache[playerId] = list;
        return list;
    }

    private static ExtensionReportRecord ToRecord(ReportRow r) => new(
        (int)r.PlayerId,
        r.SessionId,
        Parse(r.Ts),
        r.Hash ?? [],
        [.. (JsonSerializer.Deserialize<List<StoredExtension>>(r.Items, Json) ?? []).Select(e => e.ToProto())],
        ParseEnum(r.Outcome, ModReportOutcome.Admitted),
        r.Violation is null ? null : JsonSerializer.Deserialize<StoredViolation>(r.Violation, Json)?.ToProto(),
        (uint)Math.Max(0, r.PolicyVersion));

    // ------------------------------------------------------------------ catalog

    // What reports taught the catalog in this process. The database rows are written behind; a row that exists there (an admin's edit or an earlier
    // lesson) always wins, so this only fills the gap until the write lands.
    private readonly Dictionary<string, ModCatalogRecord> _learned = new(StringComparer.Ordinal);

    public IReadOnlyList<ModCatalogRecord> Catalog()
    {
        using var connection = factory.Open();
        var rows = connection.Query<CatalogRow>(
                "SELECT ext_id AS ExtId, name AS Name, nexus_url AS NexusUrl, workshop_id AS WorkshopId, class_override AS ClassOverride, notes AS Notes, updated_at AS UpdatedAt FROM mod_catalog ORDER BY ext_id")
            .Select(ToCatalog)
            .ToDictionary(c => c.ExtId, StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var (id, learned) in _learned)
            {
                rows.TryAdd(id, learned);
            }
        }

        return [.. rows.Values.OrderBy(c => c.ExtId, StringComparer.Ordinal)];
    }

    public ModCatalogRecord? CatalogEntry(string extId)
    {
        using var connection = factory.Open();
        var row = connection.QuerySingleOrDefault<CatalogRow>(
            "SELECT ext_id AS ExtId, name AS Name, nexus_url AS NexusUrl, workshop_id AS WorkshopId, class_override AS ClassOverride, notes AS Notes, updated_at AS UpdatedAt FROM mod_catalog WHERE ext_id = @extId",
            new { extId });
        if (row is not null)
        {
            return ToCatalog(row);
        }

        lock (_gate)
        {
            return _learned.GetValueOrDefault(extId);
        }
    }

    public void UpsertCatalog(ModCatalogRecord entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var connection = factory.Open();
        connection.Execute(
            """
            INSERT INTO mod_catalog (ext_id, name, nexus_url, workshop_id, class_override, notes, updated_at) VALUES (@id, @name, @nexus, @ws, @class, @notes, @at)
            ON CONFLICT(ext_id) DO UPDATE SET name = excluded.name, nexus_url = excluded.nexus_url, workshop_id = excluded.workshop_id,
              class_override = excluded.class_override, notes = excluded.notes, updated_at = excluded.updated_at
            """,
            new { id = entry.ExtId, name = entry.Name, nexus = entry.NexusUrl, ws = (long)entry.WorkshopId, @class = EnumName(entry.ClassOverride), notes = entry.Notes, at = Stamp(entry.UpdatedAt) });
    }

    private static ModCatalogRecord ToCatalog(CatalogRow r) => new(
        r.ExtId, r.Name ?? string.Empty, string.IsNullOrEmpty(r.NexusUrl) ? null : r.NexusUrl, (ulong)Math.Max(0, r.WorkshopId ?? 0),
        ParseEnum(r.ClassOverride, ExtensionClass.Unknown), string.IsNullOrEmpty(r.Notes) ? null : r.Notes, Parse(r.UpdatedAt));

    // ------------------------------------------------------------------ rows and stored shapes

    private sealed class PolicyRow
    {
        public long Version { get; set; }

        public string SourceMode { get; set; } = string.Empty;

        public string UnknownDefault { get; set; } = string.Empty;

        public string Enforcement { get; set; } = string.Empty;

        public string UpdatedAt { get; set; } = string.Empty;

        public string UpdatedBy { get; set; } = string.Empty;
    }

    private sealed class EntryRow
    {
        public string ExtId { get; set; } = string.Empty;

        public string? Name { get; set; }

        public string? Rule { get; set; }

        public long Enabled { get; set; }

        public string? Class { get; set; }

        public string? VersionRule { get; set; }

        public string? Version { get; set; }

        public byte[]? ContentHash { get; set; }

        public string? NexusUrl { get; set; }

        public long? WorkshopId { get; set; }

        public string? Notes { get; set; }
    }

    private sealed class ReportRow
    {
        public long PlayerId { get; set; }

        public long? SessionId { get; set; }

        public string Ts { get; set; } = string.Empty;

        public byte[]? Hash { get; set; }

        public string Items { get; set; } = "[]";

        public string? Outcome { get; set; }

        public string? Violation { get; set; }

        public long PolicyVersion { get; set; }
    }

    private sealed class CatalogRow
    {
        public string ExtId { get; set; } = string.Empty;

        public string? Name { get; set; }

        public string? NexusUrl { get; set; }

        public long? WorkshopId { get; set; }

        public string? ClassOverride { get; set; }

        public string? Notes { get; set; }

        public string UpdatedAt { get; set; } = string.Empty;
    }

    /// <summary>The stored form of an <see cref="ExtensionInfoT"/>: enum names, the content hash as hex.</summary>
    private sealed record StoredExtension(
        string? Id, string? Name, string? Version, ExtensionSource Source, bool Enabled, bool Egosoft, ulong WorkshopId, string? Hash, HashKind HashKind,
        bool Dll, bool ReplacesBase, bool SaveDependent, ExtensionClass Class, string? Error, string? Warning, List<StoredDependency>? Deps)
    {
        public static StoredExtension From(ExtensionInfoT e) => new(
            e.Id, e.Name, e.Version, e.Source, e.Enabled, e.Egosoft, e.WorkshopId,
            e.ContentHash is { Count: > 0 } ? Convert.ToHexString(e.ContentHash.ToArray()) : null, e.HashKind, e.HasNativeDll, e.ReplacesBasegame,
            e.SaveDependent, e.ClassHint, e.Error, e.Warning,
            e.Dependencies is { Count: > 0 } ? [.. e.Dependencies.Select(d => new StoredDependency(d.Id, d.Optional))] : null);

        public ExtensionInfoT ToProto() => new()
        {
            Id = Id ?? string.Empty,
            Name = Name ?? string.Empty,
            Version = Version ?? string.Empty,
            Source = Source,
            Enabled = Enabled,
            Egosoft = Egosoft,
            WorkshopId = WorkshopId,
            ContentHash = string.IsNullOrEmpty(Hash) ? [] : [.. Convert.FromHexString(Hash)],
            HashKind = HashKind,
            HasNativeDll = Dll,
            ReplacesBasegame = ReplacesBase,
            SaveDependent = SaveDependent,
            ClassHint = Class,
            Error = Error ?? string.Empty,
            Warning = Warning ?? string.Empty,
            Dependencies = [.. (Deps ?? []).Select(d => new ExtensionDependencyT { Id = d.Id ?? string.Empty, Optional = d.Optional })],
        };
    }

    private sealed record StoredDependency(string? Id, bool Optional);

    private sealed record StoredViolation(uint PolicyVersion, List<StoredRef> Install, List<StoredRef> Enable, List<StoredRef> Disable, List<StoredRef> Update)
    {
        public static StoredViolation From(ModPolicyViolationT v) => new(
            v.PolicyVersion, Refs(v.Install), Refs(v.Enable), Refs(v.Disable), Refs(v.Update));

        private static List<StoredRef> Refs(List<ModRefT>? list) =>
            [.. (list ?? []).Select(r => new StoredRef(r.Id, r.Name, r.Version, r.HaveVersion, r.NexusUrl, r.WorkshopId, r.Notes))];

        public ModPolicyViolationT ToProto() => new()
        {
            PolicyVersion = PolicyVersion,
            Install = ToRefs(Install),
            Enable = ToRefs(Enable),
            Disable = ToRefs(Disable),
            Update = ToRefs(Update),
        };

        private static List<ModRefT> ToRefs(List<StoredRef>? list) =>
            [.. (list ?? []).Select(r => new ModRefT
            {
                Id = r.Id ?? string.Empty, Name = r.Name ?? string.Empty, Version = r.Version ?? string.Empty, HaveVersion = r.Have ?? string.Empty,
                NexusUrl = r.Nexus ?? string.Empty, WorkshopId = r.Workshop, Notes = r.Notes ?? string.Empty,
            })];
    }

    private sealed record StoredRef(string? Id, string? Name, string? Version, string? Have, string? Nexus, ulong Workshop, string? Notes);
}
