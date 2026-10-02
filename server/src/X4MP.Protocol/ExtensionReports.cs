using System.Security.Cryptography;
using System.Text;
using X4MP.Proto;

namespace X4MP.Protocol;

/// <summary>Classification, the extensions hash, and legacy-report handling (docs/mod-management.md 2 and 3.1).</summary>
public static class ExtensionReports
{
    private static readonly HashSet<string> Allowlist = new(ModPolicyConstants.ClientOnlyLibraryIds, StringComparer.Ordinal);
    private static readonly HashSet<string> HashExcluded = new(ModPolicyConstants.HashExcludedIds, StringComparer.Ordinal);

    /// <summary>The shipped client-only library allowlist (ADR-043).</summary>
    public static bool IsAllowlisted(string? id) => id is not null && Allowlist.Contains(id);

    /// <summary><c>x4native</c> and <c>x4mp</c>: compared through the version fields, never part of the policy.</summary>
    public static bool IsHashExcluded(string? id) => id is not null && HashExcluded.Contains(id);

    public static bool IsDlcId(string? id) => id is not null && id.StartsWith(ModPolicyConstants.DlcIdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Effective class: allowlist, then Dlc by id/source/hint, then the admin override (<paramref name="adminClass"/> other than Unknown),
    /// then the node's hint. Unknown counts as Sim until an admin classifies it.
    /// </summary>
    public static ExtensionClass Classify(string id, ExtensionInfoT? info, ExtensionClass adminClass = ExtensionClass.Unknown)
    {
        if (IsAllowlisted(id))
        {
            return ExtensionClass.ClientOnly;
        }

        if (IsDlcId(id) || info is { Source: ExtensionSource.Dlc } || info is { ClassHint: ExtensionClass.Dlc })
        {
            return ExtensionClass.Dlc;
        }

        if (adminClass != ExtensionClass.Unknown)
        {
            return adminClass;
        }

        var hint = info?.ClassHint ?? ExtensionClass.Unknown;
        return hint == ExtensionClass.Unknown ? ExtensionClass.Sim : hint;
    }

    /// <summary>
    /// The extensions the node reports: <c>extension_list</c> when present, else the legacy <c>id@version</c> strings
    /// (treated as enabled, class from the id). Empty hello = empty list.
    /// </summary>
    public static IReadOnlyList<ExtensionInfoT> FromHello(ClientHelloT hello)
    {
        if (hello.ExtensionList is { Count: > 0 } list)
        {
            return list;
        }

        var result = new List<ExtensionInfoT>();
        foreach (var line in hello.Extensions ?? [])
        {
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            int at = line.IndexOf('@', StringComparison.Ordinal);
            string id = at < 0 ? line : line[..at];
            string version = at < 0 ? string.Empty : line[(at + 1)..];
            bool dlc = IsDlcId(id);
            ulong ws = ModLinks.WorkshopIdOf(id);
            result.Add(new ExtensionInfoT
            {
                Id = id,
                Version = version,
                Name = id,
                Enabled = true,
                Source = dlc ? ExtensionSource.Dlc : ws != 0 ? ExtensionSource.Workshop : ExtensionSource.Install,
                WorkshopId = ws,
                ClassHint = dlc ? ExtensionClass.Dlc : ExtensionClass.Unknown,
            });
        }

        return result;
    }

    /// <summary>
    /// SHA-256 of the sorted <c>id@version</c> lines (<see cref="ModPolicyConstants.HashLineFormat"/>) of the enabled Dlc and Sim
    /// extensions; the allowlist and x4native/x4mp are excluded (mod-management 3.1).
    /// </summary>
    public static byte[] ComputeHash(IEnumerable<ExtensionInfoT> extensions)
    {
        var sb = new StringBuilder();
        foreach (var e in extensions.Where(e => e.Enabled && !string.IsNullOrEmpty(e.Id) && !IsHashExcluded(e.Id) && !IsAllowlisted(e.Id))
                     .Where(e => Classify(e.Id, e) is ExtensionClass.Dlc or ExtensionClass.Sim)
                     .OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            sb.Append(ModPolicyConstants.HashLineFormat.Replace("{id}", e.Id, StringComparison.Ordinal).Replace("{version}", e.Version ?? string.Empty, StringComparison.Ordinal));
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    }
}
