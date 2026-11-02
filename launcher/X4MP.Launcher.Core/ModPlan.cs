using System.Globalization;
using X4MP.Protocol;

namespace X4MP.Launcher.Core;

/// <summary>What the launcher will have to do about one mod before this session can start.</summary>
public enum PlanAction
{
    Enable,
    Disable,
    VersionMismatch,
    Missing,
}

/// <summary>One line of the plan. A mod can produce two lines: a version mismatch and the toggle it also needs.</summary>
public sealed record PlanItem(
    PlanAction Action,
    string Id,
    string Name,
    string Class,
    string Rule,
    string RequiredVersion,
    string? LocalVersion,
    ulong WorkshopId,
    string? NexusUrl,
    string Notes,
    bool HasNativeDll,
    bool ReplacesBaseGame)
{
    public string ActionText => Action switch
    {
        PlanAction.Enable => "Enable",
        PlanAction.Disable => "Disable",
        PlanAction.VersionMismatch => "Version",
        _ => "Install",
    };

    public string VersionText => Action switch
    {
        PlanAction.VersionMismatch => $"{LocalVersion ?? "?"} -> {RequiredVersion}",
        PlanAction.Missing => RequiredVersion,
        _ => LocalVersion ?? string.Empty,
    };
}

/// <summary>
/// Turns a manifest and the local scan into the plan docs/mod-management.md 4.2 step 3 describes: toggle only
/// installed mods, report missing ones with their links, and report version mismatches without touching files.
/// </summary>
public static class ModPlanner
{
    private static readonly HashSet<string> AlwaysOn = new(ModPolicyConstants.HashExcludedIds, StringComparer.Ordinal);

    public static IReadOnlyList<PlanItem> Plan(JoinManifest manifest, IReadOnlyList<LocalExtension> installed, ProfileContent profile)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(profile);

        var byId = new Dictionary<string, LocalExtension>(StringComparer.Ordinal);
        foreach (var extension in installed)
        {
            if (extension.Error is null && extension.Id.Length > 0) byId.TryAdd(extension.Id, extension);
        }

        var items = new List<PlanItem>();
        var inSession = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mod in manifest.Mods)
        {
            if (string.IsNullOrEmpty(mod.Id) || !inSession.Add(mod.Id)) continue;

            var name = string.IsNullOrEmpty(mod.Name) ? mod.Id : mod.Name;
            var className = string.IsNullOrEmpty(mod.Class) ? nameof(ModClassHint.Unknown) : mod.Class;
            ulong workshopId = mod.WorkshopId > 0 ? (ulong)mod.WorkshopId : ModLinks.WorkshopIdOf(mod.Id);
            string? nexusUrl = string.IsNullOrEmpty(mod.NexusUrl) ? null : mod.NexusUrl;

            if (!byId.TryGetValue(mod.Id, out var extension))
            {
                items.Add(new PlanItem(PlanAction.Missing, mod.Id, name, className, mod.Rule, mod.Version, null,
                    workshopId, nexusUrl, mod.Notes, false, false));
                continue;
            }

            if (!VersionsMatch(extension.Version, mod.Version))
            {
                items.Add(new PlanItem(PlanAction.VersionMismatch, mod.Id, name, className, mod.Rule, mod.Version, extension.Version,
                    workshopId, nexusUrl, mod.Notes, extension.HasNativeDll, extension.ReplacesBaseGame));
            }

            bool desired = mod.Enabled && (mod.Rule is "Required" or "Allowed");
            if (desired == profile.IsEnabled(extension)) continue;

            items.Add(new PlanItem(desired ? PlanAction.Enable : PlanAction.Disable, mod.Id, name, className, mod.Rule, mod.Version,
                extension.Version, workshopId, nexusUrl, mod.Notes, extension.HasNativeDll, extension.ReplacesBaseGame));
        }

        // A simulation mod the server does not know would change the shared game, so it is turned off. Client-only
        // mods are left alone: they cannot desync a session. x4mp and x4native are never turned off.
        foreach (var extension in installed)
        {
            if (extension.Error is not null || extension.Id.Length == 0) continue;
            if (inSession.Contains(extension.Id) || AlwaysOn.Contains(extension.Id)) continue;
            if (extension.Class != ModClassHint.Sim || !profile.IsEnabled(extension)) continue;

            items.Add(new PlanItem(PlanAction.Disable, extension.Id, extension.DisplayName, nameof(ModClassHint.Sim), string.Empty,
                string.Empty, extension.Version, extension.WorkshopId, null, "Not in the server's mod set",
                extension.HasNativeDll, extension.ReplacesBaseGame));
        }

        items.Sort(static (a, b) =>
        {
            int byAction = a.Action.CompareTo(b.Action);
            if (byAction != 0) return byAction;
            int byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.CompareOrdinal(a.Id, b.Id);
        });

        return items;
    }

    /// <summary>
    /// True when the installed version satisfies the manifest entry. An empty requirement means any version; "9.00"
    /// and "9.0" are the same version, while "1.30" and "1.3" are not, because X4 writes minor versions in hundredths.
    /// </summary>
    public static bool VersionsMatch(string? installed, string? required)
    {
        if (string.IsNullOrWhiteSpace(required)) return true;
        if (string.Equals(installed, required, StringComparison.OrdinalIgnoreCase)) return true;
        var have = ParseVersion(installed);
        var need = ParseVersion(required);
        return have is not null && need is not null && have.AsSpan().SequenceEqual(need);
    }

    private static int[]? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('.');
        var numbers = new int[parts.Length];
        for (int index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index])) return null;
        }

        int length = numbers.Length;
        while (length > 1 && numbers[length - 1] == 0) length--;
        return numbers[..length];
    }
}
