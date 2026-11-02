namespace X4MP.Launcher.Core;

/// <summary>One override in a profile's content.xml: enabled or disabled for this player.</summary>
public sealed record ProfileExtension(string Id, bool Enabled);

/// <summary>
/// The per-player content.xml (docs/mod-management.md 1.2). It holds overrides only: an extension without an entry
/// uses the default from its own content.xml. The file may list extensions that are no longer installed; those entries
/// are kept and never touched, and the launcher reports them instead.
/// </summary>
public sealed class ProfileContent
{
    private readonly Dictionary<string, bool> _overrides;

    private ProfileContent(string path, IReadOnlyList<ProfileExtension> entries)
    {
        Path = path;
        Entries = entries;
        _overrides = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var entry in entries) _overrides[entry.Id] = entry.Enabled;
    }

    /// <summary>The file the entries came from, even when it does not exist yet.</summary>
    public string Path { get; }

    public IReadOnlyList<ProfileExtension> Entries { get; }

    /// <summary>Reads a profile content.xml; a missing file means "no overrides", which is what a fresh profile has.</summary>
    public static ProfileContent Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return new ProfileContent(path, []);
        }
        catch (UnauthorizedAccessException)
        {
            return new ProfileContent(path, []);
        }

        var entries = new List<ProfileExtension>();
        foreach (var tag in XmlTagReader.Read(text))
        {
            if (tag.Name != "extension") continue;
            var id = tag.Get("id");
            if (string.IsNullOrEmpty(id)) continue;
            entries.Add(new ProfileExtension(id, ContentXml.Truthy(tag.Get("enabled"), true)));
        }

        return new ProfileContent(path, entries);
    }

    /// <summary>The player's override for an id, or null when the profile has no opinion.</summary>
    public bool? Override(string id) => _overrides.TryGetValue(id, out bool enabled) ? enabled : null;

    /// <summary>Whether the game will load this extension: the profile wins over the extension's own default.</summary>
    public bool IsEnabled(LocalExtension extension) => Override(extension.Id) ?? extension.EnabledByDefault;
}

/// <summary>One X4 profile folder: named after the Steam id, one per player on this machine.</summary>
public sealed record X4Profile(string Directory, string SteamId)
{
    public string ContentXmlPath => Path.Combine(Directory, "content.xml");
}

/// <summary>Finds the X4 profile folders below Documents\Egosoft\X4, newest played first.</summary>
public static class X4Profiles
{
    public static string BaseDirectory(string documentsRoot) => Path.Combine(documentsRoot, "Egosoft", "X4");

    public static IReadOnlyList<X4Profile> Find(string documentsRoot)
    {
        var directory = BaseDirectory(documentsRoot);
        if (!Directory.Exists(directory)) return [];
        try
        {
            return new DirectoryInfo(directory)
                .EnumerateDirectories()
                .Where(info => info.Name.Length > 0 && info.Name.All(char.IsAsciiDigit))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Select(info => new X4Profile(info.FullName, info.Name))
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
