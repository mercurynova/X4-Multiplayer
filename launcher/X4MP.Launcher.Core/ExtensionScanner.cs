using System.Globalization;
using System.Text;
using X4MP.Protocol;

namespace X4MP.Launcher.Core;

/// <summary>What a folder's files say about it, before the id is known.</summary>
internal sealed class Shape
{
    public bool SimContent;
    public bool UiContent;
    public bool UnknownContent;
    public bool NativeDll;
    public bool SubstCat;
    public bool CatFiles;
}

/// <summary>
/// Finds every extension folder on this machine and classifies it the way the game does. This is a port of
/// <c>scan_folder</c>/<c>scan_extensions</c> in mod/native/core/mods/mods.cpp; the launcher must reach the same
/// answer as the game for the same folder, including for broken content.xml files.
/// </summary>
public static class ExtensionScanner
{
    private const int ContentXmlByteCap = 1024 * 1024;
    private const int CatIndexByteCap = 32 * 1024 * 1024;
    private const string DuplicateWarning = "duplicate extension id, this copy is ignored";

    private static readonly string[] SimFolders = ["md", "aiscripts", "libraries", "maps", "index", "assets", "extensions"];
    private static readonly string[] UiFolders = ["t", "ui", "textures", "sound", "sounds", "music", "voice", "video", "shaders", "cutscenes"];
    private static readonly HashSet<string> ClientOnlyIds = new(ModPolicyConstants.ClientOnlyLibraryIds, StringComparer.Ordinal);

    /// <summary>
    /// Scans the install folder, the user folders (documents\Egosoft\X4 and every profile below it) and the Workshop
    /// folders, in that order. The result is sorted by id, source and folder; a later copy of an id is marked as
    /// ignored, which is what the game does.
    /// </summary>
    public static IReadOnlyList<LocalExtension> Scan(GameInstall install, string documentsRoot)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(documentsRoot);

        var records = new List<LocalExtension>();
        AddRoot(records, install.ExtensionsDir, ModSource.Install);

        var gameRoot = Path.Combine(documentsRoot, "Egosoft", "X4");
        AddRoot(records, Path.Combine(gameRoot, "extensions"), ModSource.User);
        foreach (var profile in Subdirectories(gameRoot))
        {
            AddRoot(records, Path.Combine(profile, "extensions"), ModSource.User);
        }

        foreach (var workshopRoot in install.WorkshopRoots)
        {
            AddRoot(records, workshopRoot, ModSource.Workshop);
        }

        records.Sort(static (a, b) =>
        {
            int byId = string.CompareOrdinal(a.Id, b.Id);
            if (byId != 0) return byId;
            int bySource = Rank(a.Source).CompareTo(Rank(b.Source));
            return bySource != 0 ? bySource : string.CompareOrdinal(a.Folder, b.Folder);
        });

        for (int index = 1; index < records.Count; index++)
        {
            var record = records[index];
            if (record.Id.Length == 0 || record.Id != records[index - 1].Id) continue;
            records[index] = record with
            {
                Warning = string.IsNullOrEmpty(record.Warning) ? DuplicateWarning : record.Warning + "; " + DuplicateWarning,
            };
        }

        return records;
    }

    private static int Rank(ModSource source) => source switch
    {
        ModSource.Dlc => 0,
        ModSource.Install => 1,
        ModSource.User => 2,
        _ => 3,
    };

    private static void AddRoot(List<LocalExtension> records, string directory, ModSource source)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var folder in Subdirectories(directory))
        {
            records.Add(ScanFolder(folder, source));
        }
    }

    private static string[] Subdirectories(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        try
        {
            return Directory.EnumerateDirectories(directory).OrderBy(path => path, StringComparer.Ordinal).ToArray();
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

    private static LocalExtension ScanFolder(string folder, ModSource source)
    {
        var content = ReadContentXml(Path.Combine(folder, "content.xml"));
        if (content is null)
        {
            return Unusable(folder, source, "content.xml missing or unreadable");
        }

        var parsed = ContentXml.Parse(content);
        if (parsed is null)
        {
            return Unusable(folder, source, "content.xml has no <content id=...>");
        }

        var shape = new Shape();
        Walk(folder, shape);

        ulong workshopId = 0;
        string? warning = null;
        if (source == ModSource.Workshop)
        {
            var name = Path.GetFileName(folder);
            if (IsWorkshopFolder(name))
            {
                workshopId = ulong.Parse(name, CultureInfo.InvariantCulture);
                if (!string.Equals(parsed.Id, ModPolicyConstants.WorkshopIdPrefix + name, StringComparison.Ordinal))
                {
                    warning = "workshop folder name does not match content.xml id";
                }
            }
        }

        if (workshopId == 0 && parsed.Id.StartsWith(ModPolicyConstants.WorkshopIdPrefix, StringComparison.Ordinal))
        {
            var digits = parsed.Id[ModPolicyConstants.WorkshopIdPrefix.Length..];
            if (IsWorkshopFolder(digits)) workshopId = ulong.Parse(digits, CultureInfo.InvariantCulture);
        }

        var effectiveSource = source == ModSource.Install && parsed.Id.StartsWith(ModPolicyConstants.DlcIdPrefix, StringComparison.Ordinal)
            ? ModSource.Dlc
            : source;

        return new LocalExtension(
            folder,
            effectiveSource,
            parsed.Id,
            parsed.Name,
            ContentXml.NormaliseVersion(parsed.Version),
            parsed.EnabledByDefault,
            parsed.SaveDependent,
            workshopId,
            parsed.Dependencies,
            Classify(parsed.Id, parsed.SaveDependent, shape),
            shape.NativeDll,
            shape.SubstCat,
            null,
            warning);
    }

    private static LocalExtension Unusable(string folder, ModSource source, string error) =>
        new(folder, source, string.Empty, string.Empty, string.Empty, false, false, 0, [], ModClassHint.Unknown, false, false, error, null);

    private static string? ReadContentXml(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > ContentXmlByteCap) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Walk(string folder, Shape shape)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", options);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/').ToLowerInvariant();
            int slash = relative.IndexOf('/');
            if (slash < 0)
            {
                if (relative.EndsWith(".cat", StringComparison.Ordinal))
                {
                    shape.CatFiles = true;
                    if (relative.StartsWith("subst_", StringComparison.Ordinal)) shape.SubstCat = true;
                    ScanCatIndex(file, shape);
                }
                else if (relative == "x4native.json")
                {
                    var text = ReadText(file, ContentXmlByteCap);
                    if (text is not null && text.Contains("\"library\"", StringComparison.Ordinal)) shape.NativeDll = true;
                }
            }
            else
            {
                AddTopLevelFolder(shape, relative[..slash]);
            }

            if (relative.EndsWith(".dll", StringComparison.Ordinal)) shape.NativeDll = true;
        }
    }

    /// <summary>One line per file inside a .cat: "path size timestamp md5"; the path may hold spaces, so peel three tokens off the right.</summary>
    private static void ScanCatIndex(string path, Shape shape)
    {
        var text = ReadText(path, CatIndexByteCap);
        if (text is null) return;
        int start = 0;
        while (start < text.Length)
        {
            int end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            var line = text.AsSpan(start, end - start);
            start = end + 1;
            while (line.Length > 0 && (line[^1] == '\r' || line[^1] == ' ')) line = line[..^1];
            for (int token = 0; token < 3; token++)
            {
                int space = line.LastIndexOf(' ');
                if (space < 0)
                {
                    line = default;
                    break;
                }

                line = line[..space];
            }

            if (!line.IsEmpty) AddTopLevelFolder(shape, TopLevelFolder(line));
        }
    }

    private static string? ReadText(string path, int byteCap)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int length = (int)Math.Min(stream.Length, byteCap);
            var buffer = new byte[length];
            int read = 0;
            while (read < length)
            {
                int count = stream.Read(buffer, read, length - read);
                if (count == 0) break;
                read += count;
            }

            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string TopLevelFolder(ReadOnlySpan<char> path)
    {
        int start = 0;
        while (start < path.Length && (path[start] == '/' || path[start] == '\\')) start++;
        var rest = path[start..];
        int slash = rest.IndexOfAny('/', '\\');
        return slash < 0 ? string.Empty : rest[..slash].ToString().ToLowerInvariant();
    }

    private static void AddTopLevelFolder(Shape shape, string folder)
    {
        if (folder.Length == 0 || string.Equals(folder, "native", StringComparison.OrdinalIgnoreCase)) return;
        if (SimFolders.Contains(folder, StringComparer.Ordinal))
        {
            shape.SimContent = true;
        }
        else if (UiFolders.Contains(folder, StringComparer.Ordinal))
        {
            shape.UiContent = true;
        }
        else
        {
            shape.UnknownContent = true;
        }
    }

    private static ModClassHint Classify(string id, bool saveDependent, Shape shape)
    {
        if (id.StartsWith(ModPolicyConstants.DlcIdPrefix, StringComparison.Ordinal)) return ModClassHint.Dlc;
        if (ClientOnlyIds.Contains(id)) return ModClassHint.ClientOnly;
        if (saveDependent || shape.SimContent || shape.NativeDll) return ModClassHint.Sim;
        if (shape.UiContent && !shape.UnknownContent) return ModClassHint.ClientOnly;
        return ModClassHint.Unknown;
    }

    private static bool IsWorkshopFolder(string name) =>
        name.Length is > 0 and < 20 && name.All(char.IsAsciiDigit);
}
