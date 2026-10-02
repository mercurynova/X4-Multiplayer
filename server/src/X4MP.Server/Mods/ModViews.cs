using System.Globalization;
using Microsoft.Extensions.Options;
using X4MP.Core.Mods;
using X4MP.Core.Session;
using X4MP.Core.Settings;
using X4MP.Persistence;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Server.Api;

namespace X4MP.Server.Mods;

/// <summary>What one caller may do with the mod lists (ADR-045): <see cref="CanEdit"/> for Admin and ModEditor, <see cref="CanSeePlayers"/> from <c>ModListVisibility</c>.</summary>
internal readonly record struct ModAccess(bool CanEdit, bool CanSeePlayers)
{
    /// <summary>
    /// Admin and ModEditor always see everything. A Viewer sees players' lists with <c>AdminsAndViewers</c> and <c>AllPlayers</c> (that value also lets players
    /// see each other in game, which grants nobody the right to edit).
    /// </summary>
    public static ModAccess For(System.Security.Claims.ClaimsPrincipal? user, ModListVisibility visibility)
    {
        bool edit = user is not null && (user.IsInRole(Auth.AdminRoles.Admin) || user.IsInRole(Auth.AdminRoles.ModEditor));
        return new ModAccess(edit, edit || visibility != ModListVisibility.AdminsOnly);
    }
}

/// <summary>Builds the mod-management DTOs from the policy, the stored reports and the live session. Shared by the REST endpoints, the hub and the broadcaster.</summary>
internal sealed class ModViews(
    IModPolicyEditor policy, IModStore store, SessionActor actor, GatewayState gateway, SqliteAdminQueries queries,
    IOptionsMonitor<ModManagementOptions> settings, TimeProvider time)
{
    public IModPolicyEditor Policy => policy;

    public IModStore Store => store;

    public ModListVisibility Visibility => settings.CurrentValue.ModListVisibility;

    public ModAccess Access(System.Security.Claims.ClaimsPrincipal? user) => ModAccess.For(user, Visibility);

    // ------------------------------------------------------------------ links

    public static string? Nexus(string? url) => string.IsNullOrEmpty(url) ? null : url;

    public static string? WorkshopUrl(ulong id) => id == 0 ? null : ModLinks.WorkshopUrl(id);

    public static string? WorkshopSteamUrl(ulong id) => id == 0 ? null : ModLinks.WorkshopSteamUrl(id);

    private static string? Hex(IEnumerable<byte>? hash) => hash is null || !hash.Any() ? null : Convert.ToHexStringLower(hash.ToArray());

    private static string? Hex(byte[]? hash) => hash is { Length: > 0 } ? Convert.ToHexStringLower(hash) : null;

    // ------------------------------------------------------------------ authority

    /// <summary>The authority's extension list "Import from authority" uses and where it came from, or null when there is none.</summary>
    public (IReadOnlyList<ExtensionInfoT> Items, int? PlayerId, DateTimeOffset? At)? AuthorityReport()
    {
        var snapshot = actor.Snapshot;
        if (snapshot.Authority.Status != AuthorityStatus.None && store.LatestReport(snapshot.Authority.PlayerId) is { Items.Count: > 0 } report)
        {
            return (report.Items, report.PlayerId, report.At);
        }

        if (gateway.Authority is { ExtensionList.Count: > 0 } identity)
        {
            return (identity.ExtensionList, snapshot.Authority.Status != AuthorityStatus.None ? snapshot.Authority.PlayerId : null, null);
        }

        return null;
    }

    private IReadOnlyList<ExtensionInfoT>? AuthorityList() => gateway.Authority?.ExtensionList;

    // ------------------------------------------------------------------ policy

    /// <summary>The policy as the page shows it. <paramref name="reports"/> are the newest report of every player (the caller's visibility decides what is counted).</summary>
    public ModPolicyDto PolicyDto(ModPolicyT current, IReadOnlyList<ExtensionReportRecord> reports, ModAccess access)
    {
        var authority = AuthorityReport();
        var dto = new ModPolicyDto(
            current.Version, current.SourceMode.ToString(), current.UnknownDefault.ToString(), current.Enforcement.ToString(), Visibility.ToString(),
            [.. (current.Entries ?? []).Select(e => Entry(e, reports, authority?.Items, access))],
            null, null, authority?.PlayerId, authority?.At);
        return store.LoadPolicy() is { } stored && stored.Version == current.Version ? dto with { UpdatedAt = stored.UpdatedAt, UpdatedBy = stored.UpdatedBy } : dto;
    }

    public ModPolicyDto PolicyDto(ModAccess access) => PolicyDto(policy.Current, store.LatestReports(), access);

    public static ModEntryDto Entry(ModPolicyEntryT e, IReadOnlyList<ExtensionReportRecord> reports, IReadOnlyList<ExtensionInfoT>? authority, ModAccess access)
    {
        ExtensionInfoT? info = authority?.FirstOrDefault(x => x.Id == e.Id);
        int enabled = 0;
        int disabled = 0;
        int missing = 0;
        foreach (var report in reports)
        {
            var have = report.Items.LastOrDefault(x => x.Id == e.Id);
            if (have is null)
            {
                missing++;
            }
            else if (have.Enabled)
            {
                enabled++;
            }
            else
            {
                disabled++;
            }

            info ??= have;
        }

        if (!access.CanSeePlayers)
        {
            enabled = disabled = missing = 0; // the flags above are not secret, the counts are
        }

        var cls = ExtensionReports.Classify(e.Id, info, e.ModClass);
        return new ModEntryDto(
            e.Id, e.Name ?? string.Empty, e.Rule.ToString(), e.Enabled, e.ModClass.ToString(), cls.ToString(), e.VersionRule.ToString(), e.Version ?? string.Empty,
            Hex(e.ContentHash), Nexus(e.NexusUrl), (long)e.WorkshopId, WorkshopUrl(e.WorkshopId), WorkshopSteamUrl(e.WorkshopId), e.Notes ?? string.Empty,
            ExtensionReports.IsAllowlisted(e.Id), info?.HasNativeDll, info?.ReplacesBasegame, info?.SaveDependent, enabled, disabled, missing);
    }

    // ------------------------------------------------------------------ violations and players

    public static ModViolationDto? ViolationDto(ModPolicyViolationT? v) => v is null
        ? null
        : new ModViolationDto(v.PolicyVersion, Refs(v.Install), Refs(v.Enable), Refs(v.Disable), Refs(v.Update));

    private static List<ModRefDto> Refs(List<ModRefT>? list) =>
        [.. (list ?? []).Select(r => new ModRefDto(
            r.Id ?? string.Empty, r.Name ?? string.Empty, r.Version ?? string.Empty, r.HaveVersion ?? string.Empty, Nexus(r.NexusUrl), (long)r.WorkshopId,
            WorkshopUrl(r.WorkshopId), WorkshopSteamUrl(r.WorkshopId), r.Notes ?? string.Empty))];

    /// <summary>What differs between a report and the current policy (null when nothing).</summary>
    public ModPolicyViolationT? CurrentViolation(ExtensionReportRecord report, ModPolicyT current)
    {
        var items = report.Items.Count > ModPolicyConstants.MaxExtensionEntries ? [.. report.Items.Take(ModPolicyConstants.MaxExtensionEntries)] : report.Items;
        var evaluation = ModPolicyEvaluator.Evaluate(items, current, AuthorityList());
        return evaluation.Verdict == ModVerdict.Admit ? null : evaluation.Violation;
    }

    private string NameOf(int playerId, SessionSnapshot snapshot) =>
        snapshot.Nodes.FirstOrDefault(n => n.PlayerId == playerId)?.Name
        ?? queries.FindPlayer(playerId, time.GetUtcNow())?.Name
        ?? "player " + playerId.ToString(CultureInfo.InvariantCulture);

    public PlayerModStatusDto PlayerStatus(ExtensionReportRecord? report, int playerId, ModPolicyT current, SessionSnapshot snapshot)
    {
        var node = snapshot.Nodes.FirstOrDefault(n => n.PlayerId == playerId);
        bool online = node?.Connected ?? false;
        bool authority = snapshot.Authority.Status != AuthorityStatus.None && snapshot.Authority.PlayerId == playerId;
        string name = NameOf(playerId, snapshot);
        if (report is null)
        {
            return new PlayerModStatusDto(playerId, name, online, authority, "NoReport", "None", null, 0, 0, null);
        }

        var violation = CurrentViolation(report, current);
        return new PlayerModStatusDto(
            playerId, name, online, authority, violation is null ? "Matches" : "Violates", report.Outcome.ToString(), report.At, report.PolicyVersion,
            report.Items.Count, ViolationDto(violation));
    }

    public PlayerModStatusDto PlayerStatus(ExtensionReportRecord report) =>
        PlayerStatus(report, report.PlayerId, policy.Current, actor.Snapshot);

    // ------------------------------------------------------------------ the page

    public ModsStateDto State(ModAccess access)
    {
        var current = policy.Current;
        var snapshot = actor.Snapshot;
        var reports = store.LatestReports();
        var players = access.CanSeePlayers ? [.. reports.Select(r => PlayerStatus(r, r.PlayerId, current, snapshot))] : new List<PlayerModStatusDto>();
        return new ModsStateDto(PolicyDto(current, reports, access), players, access.CanEdit, !access.CanSeePlayers, false);
    }

    // ------------------------------------------------------------------ reports of one player

    public static ExtensionDto Extension(ExtensionInfoT e, ModPolicyT current) => new(
        e.Id ?? string.Empty, e.Name ?? string.Empty, e.Version ?? string.Empty, e.Source.ToString(), e.Enabled, e.Egosoft, (long)e.WorkshopId,
        WorkshopUrl(e.WorkshopId != 0 ? e.WorkshopId : ModLinks.WorkshopIdOf(e.Id)), Hex(e.ContentHash), e.HashKind.ToString(), e.HasNativeDll, e.ReplacesBasegame,
        e.SaveDependent, e.ClassHint.ToString(),
        ExtensionReports.Classify(e.Id ?? string.Empty, e, (current.Entries ?? []).FirstOrDefault(x => x.Id == e.Id)?.ModClass ?? ExtensionClass.Unknown).ToString(),
        e.Error ?? string.Empty, e.Warning ?? string.Empty,
        [.. (e.Dependencies ?? []).Select(d => new ExtensionDependencyDto(d.Id ?? string.Empty, d.Optional))],
        (current.Entries ?? []).Any(x => x.Id == e.Id));

    public ExtensionReportDto Report(ExtensionReportRecord r, ModPolicyT current) => new(
        r.PlayerId, r.At, r.Outcome.ToString(), r.PolicyVersion, Hex(r.ExtensionsHash), [.. r.Items.Select(e => Extension(e, current))], ViolationDto(r.Violation),
        ViolationDto(CurrentViolation(r, current)));

    public PlayerExtensionsDto PlayerExtensions(int playerId, string name, int limit)
    {
        var current = policy.Current;
        var reports = store.Reports(playerId, limit);
        return new PlayerExtensionsDto(
            playerId, name, reports.Count > 0 ? Report(reports[0], current) : null,
            [.. reports.Select(r => new ExtensionReportSummaryDto(
                r.At, r.Outcome.ToString(), r.PolicyVersion, Hex(r.ExtensionsHash), r.Items.Count, r.Items.Count(i => i.Enabled), ViolationDto(r.Violation)))]);
    }

    // ------------------------------------------------------------------ catalog

    public static ModCatalogEntryDto CatalogEntry(ModCatalogRecord c, ModPolicyT current) => new(
        c.ExtId, c.Name, Nexus(c.NexusUrl), (long)c.WorkshopId, WorkshopUrl(c.WorkshopId), WorkshopSteamUrl(c.WorkshopId), c.ClassOverride.ToString(), c.Notes,
        c.UpdatedAt, (current.Entries ?? []).Any(e => e.Id == c.ExtId), ExtensionReports.IsAllowlisted(c.ExtId));
}
