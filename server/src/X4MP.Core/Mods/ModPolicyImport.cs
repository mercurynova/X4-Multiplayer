using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Mods;

/// <summary>
/// "Import from authority" (docs/mod-management.md 3.3): turns the authority's last extension report into policy entries. Enabled Dlc and Sim
/// extensions become <c>Required</c> (exact version), enabled client-only ones (including the shipped library allowlist) become <c>Allowed</c>
/// (any version); disabled ones and <c>x4native</c>/<c>x4mp</c> are not imported. Versions and content hashes are copied, Workshop ids filled
/// from the report or derived from a <c>ws_&lt;n&gt;</c> id, and links and notes known from the catalog are applied to new entries.
/// </summary>
public static class ModPolicyImport
{
    /// <param name="authority">The authority's report.</param>
    /// <param name="existing">The current entries.</param>
    /// <param name="merge">
    /// True: entries already in the list keep the admin's rule, switch, class override, version rule, links and notes (only the version, the hash,
    /// an empty name and a missing Workshop id are refreshed) and entries the authority does not have stay. False: the list is replaced.
    /// </param>
    /// <param name="catalog">Looks up what the server remembers about a mod (links, notes, class override); may return null.</param>
    public static List<ModPolicyEntryT> FromAuthority(
        IReadOnlyList<ExtensionInfoT> authority, IReadOnlyList<ModPolicyEntryT> existing, bool merge, Func<string, ModCatalogRecord?>? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(existing);
        var imported = new List<ModPolicyEntryT>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in authority.OrderBy(x => x.Name ?? x.Id, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            if (!e.Enabled || string.IsNullOrEmpty(e.Id) || ExtensionReports.IsHashExcluded(e.Id) || !seen.Add(e.Id))
            {
                continue;
            }

            var cls = ExtensionReports.Classify(e.Id, e);
            bool library = cls == ExtensionClass.ClientOnly;
            var known = catalog?.Invoke(e.Id);
            ulong ws = e.WorkshopId != 0 ? e.WorkshopId : ModLinks.WorkshopIdOf(e.Id);
            if (ws == 0 && known is { WorkshopId: not 0 })
            {
                ws = known.WorkshopId;
            }

            imported.Add(new ModPolicyEntryT
            {
                Id = e.Id,
                Name = string.IsNullOrEmpty(e.Name) ? known?.Name ?? e.Id : e.Name,
                Rule = library ? ModRule.Allowed : ModRule.Required,
                Enabled = true,
                ModClass = known?.ClassOverride ?? ExtensionClass.Unknown,
                VersionRule = library ? VersionRule.Any : VersionRule.Exact,
                Version = e.Version ?? string.Empty,
                ContentHash = library || e.ContentHash is not { Count: > 0 } ? null : [.. e.ContentHash],
                NexusUrl = known?.NexusUrl ?? string.Empty,
                WorkshopId = ws,
                Notes = known?.Notes ?? string.Empty,
            });
        }

        if (!merge)
        {
            return imported;
        }

        var result = existing.Select(Copy).ToList();
        var byId = result.ToDictionary(r => r.Id, StringComparer.Ordinal);
        foreach (var fresh in imported)
        {
            if (!byId.TryGetValue(fresh.Id, out var current))
            {
                result.Add(fresh);
                continue;
            }

            current.Version = fresh.Version;
            current.ContentHash = fresh.ContentHash;
            if (string.IsNullOrEmpty(current.Name))
            {
                current.Name = fresh.Name;
            }

            if (current.WorkshopId == 0)
            {
                current.WorkshopId = fresh.WorkshopId;
            }
        }

        return result;
    }

    private static ModPolicyEntryT Copy(ModPolicyEntryT e) => new()
    {
        Id = e.Id, Name = e.Name, Rule = e.Rule, Enabled = e.Enabled, ModClass = e.ModClass, VersionRule = e.VersionRule, Version = e.Version,
        ContentHash = e.ContentHash is null ? null : [.. e.ContentHash], NexusUrl = e.NexusUrl, WorkshopId = e.WorkshopId, Notes = e.Notes,
    };
}
