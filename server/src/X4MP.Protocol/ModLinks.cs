using System.Globalization;
using System.Text.RegularExpressions;

namespace X4MP.Protocol;

/// <summary>Link rules from docs/mod-management.md 3.6: Steam Workshop links are derived from the id, Nexus links are validated and normalised.</summary>
public static partial class ModLinks
{
    [GeneratedRegex(ModPolicyConstants.NexusUrlRegex, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NexusPattern();

    /// <summary><c>ws_&lt;n&gt;</c> gives <c>n</c>; anything else gives 0.</summary>
    public static ulong WorkshopIdOf(string? extensionId)
    {
        if (extensionId is not null
            && extensionId.StartsWith(ModPolicyConstants.WorkshopIdPrefix, StringComparison.Ordinal)
            && ulong.TryParse(extensionId.AsSpan(ModPolicyConstants.WorkshopIdPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
        {
            return id;
        }

        return 0;
    }

    /// <summary>The https Workshop page of an item (empty for id 0).</summary>
    public static string WorkshopUrl(ulong workshopId) =>
        workshopId == 0 ? string.Empty : ModPolicyConstants.WorkshopUrlFormat.Replace("{0}", workshopId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>The <c>steam://</c> Workshop page of an item (empty for id 0).</summary>
    public static string WorkshopSteamUrl(ulong workshopId) =>
        workshopId == 0 ? string.Empty : ModPolicyConstants.WorkshopSteamUrlFormat.Replace("{0}", workshopId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>Validates a pasted Nexus URL (x4foundations only) and returns the normalised form, or false.</summary>
    public static bool TryNormaliseNexusUrl(string? url, out string normalised)
    {
        normalised = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var match = NexusPattern().Match(url.Trim());
        if (!match.Success)
        {
            return false;
        }

        normalised = ModPolicyConstants.NexusUrlFormat.Replace("{0}", match.Groups[2].Value, StringComparison.Ordinal);
        return true;
    }
}
