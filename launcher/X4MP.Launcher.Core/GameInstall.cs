namespace X4MP.Launcher.Core;

/// <summary>The game folder and the Workshop folders that belong to it.</summary>
public sealed record GameInstall(string GameDir, IReadOnlyList<string> WorkshopRoots)
{
    /// <summary>The folder the game loads installed extensions from.</summary>
    public string ExtensionsDir => Path.Combine(GameDir, "extensions");
}

/// <summary>Reads the library list of a Steam installation from steamapps/libraryfolders.vdf.</summary>
public static class SteamLibraryFolders
{
    /// <summary>
    /// Every <c>"path" "..."</c> value, with the doubled backslashes of the VDF format undone. Only the path lines are
    /// read: the launcher needs the library folders, not Steam's app bookkeeping.
    /// </summary>
    public static IReadOnlyList<string> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var paths = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var key = line.IndexOf("\"path\"", StringComparison.Ordinal);
            if (key < 0) continue;
            int open = line.IndexOf('"', key + 6);
            if (open < 0) continue;
            int close = line.IndexOf('"', open + 1);
            if (close < 0) continue;
            var path = line[(open + 1)..close].Replace("\\\\", "\\", StringComparison.Ordinal);
            if (path.Length > 0) paths.Add(path);
        }

        return paths;
    }
}

/// <summary>Finds an X4 installation from the Steam roots the caller collected, plus explicit game folders.</summary>
public static class X4InstallFinder
{
    public const string GameFolderName = "X4 Foundations";
    public const string WorkshopAppId = "392160";

    /// <summary>
    /// Expands every Steam root through its libraryfolders.vdf, adds the usual <c>steamapps\common\X4 Foundations</c>
    /// candidate of each library, then returns the first folder that holds X4.exe. Null when none does.
    /// </summary>
    public static GameInstall? Find(IEnumerable<string> steamRoots, IEnumerable<string>? extraGameDirs = null)
    {
        ArgumentNullException.ThrowIfNull(steamRoots);
        var libraries = new List<string>();
        foreach (var root in steamRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            libraries.Add(root);
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            try
            {
                libraries.AddRange(SteamLibraryFolders.Parse(File.ReadAllText(vdf)));
            }
            catch (IOException)
            {
                // An unreadable library list is not fatal: the roots we already have may be enough.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var distinct = libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidates = distinct.Select(library => Path.Combine(library, "steamapps", "common", GameFolderName)).ToList();
        if (extraGameDirs is not null) candidates.AddRange(extraGameDirs);

        var gameDir = candidates.FirstOrDefault(candidate => candidate.Length > 0 && File.Exists(Path.Combine(candidate, "X4.exe")));
        if (gameDir is null) return null;

        var workshopRoots = distinct
            .Select(library => Path.Combine(library, "steamapps", "workshop", "content", WorkshopAppId))
            .ToArray();
        return new GameInstall(Path.GetFullPath(gameDir), workshopRoots);
    }
}
