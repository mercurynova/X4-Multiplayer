using System.Text.RegularExpressions;
using X4MP.Protocol;

namespace X4MP.Launcher.Core;

/// <summary>
/// The only three kinds of page the launcher may open: a Steam Workshop item, the Workshop page in a browser, and a
/// Nexus page under x4foundations. <see cref="IsAllowed"/> is a whitelist on the text, not on the caller, because a
/// manifest is written by a server the player does not control.
/// </summary>
public static partial class ExternalLinks
{
    [GeneratedRegex(@"^steam://url/CommunityFilePage/\d{1,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkshopSteamPattern();

    [GeneratedRegex(@"^https://steamcommunity\.com/sharedfiles/filedetails/\?id=\d{1,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex WorkshopWebPattern();

    [GeneratedRegex(@"^https://www\.nexusmods\.com/x4foundations/mods/\d{1,20}/?$", RegexOptions.CultureInvariant)]
    private static partial Regex NexusWebPattern();

    public static string? WorkshopSteam(ulong workshopId) =>
        workshopId > 0 ? ModLinks.WorkshopSteamUrl(workshopId) : null;

    public static string? WorkshopPage(ulong workshopId) =>
        workshopId > 0 ? ModLinks.WorkshopUrl(workshopId) : null;

    /// <summary>The page to open for a Workshop mod, preferring Steam's own handler and falling back to the web page.</summary>
    public static string? WorkshopOpen(ulong workshopId) => WorkshopSteam(workshopId) ?? WorkshopPage(workshopId);

    public static string? NexusPage(string? nexusUrl) =>
        ModLinks.TryNormaliseNexusUrl(nexusUrl, out var normalised) ? normalised : null;

    public static bool IsAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return WorkshopSteamPattern().IsMatch(url)
            || WorkshopWebPattern().IsMatch(url)
            || NexusWebPattern().IsMatch(url);
    }
}
