using System.Globalization;
using X4MP.Proto;

namespace X4MP.Core.Saves;

/// <summary>A file in the store.</summary>
public sealed record StoredFile(string Sha256, UploadKind Kind, long SizeBytes, DateTimeOffset ModifiedAt, string Path);

/// <summary>
/// The content-addressed file store (server-design 3.1): <c>&lt;data&gt;/saves/&lt;sha256&gt;.xml.gz</c> for saves and
/// <c>&lt;sha256&gt;.x4mf</c> for manifests, partial uploads in <c>&lt;data&gt;/uploads/&lt;kind&gt;-&lt;sha256&gt;.part</c>. File names are
/// derived from a validated 64-digit hex hash only, never from user input, which rules out path traversal. Thread-safe (stateless).
/// </summary>
public sealed class SaveFileStore
{
    public const string SaveExtension = ".xml.gz";
    public const string ManifestExtension = ".x4mf";
    public const string PartExtension = ".part";

    public SaveFileStore(string dataDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDir);
        SavesDir = Path.Combine(dataDir, "saves");
        UploadsDir = Path.Combine(dataDir, "uploads");
        Directory.CreateDirectory(SavesDir);
        Directory.CreateDirectory(UploadsDir);
    }

    public string SavesDir { get; }

    public string UploadsDir { get; }

    /// <summary>Lowercase hex of a SHA-256.</summary>
    public static string Hex(ReadOnlySpan<byte> sha256) => Convert.ToHexStringLower(sha256);

    /// <summary>True for exactly 64 lowercase hex digits.</summary>
    public static bool IsValidSha(string? sha256) =>
        sha256 is { Length: 64 } && sha256.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>The local file name a node gives its copy (protocol.md 6.4): <c>x4mp_&lt;sha12&gt;.xml.gz</c>.</summary>
    public static string LocalFileName(string sha256) => "x4mp_" + sha256[..12] + SaveExtension;

    public string PathOf(string sha256, UploadKind kind)
    {
        if (!IsValidSha(sha256))
        {
            throw new ArgumentException("Not a SHA-256 in lowercase hex.", nameof(sha256));
        }

        return Path.Combine(SavesDir, sha256 + (kind == UploadKind.Save ? SaveExtension : ManifestExtension));
    }

    public string PartPathOf(string sha256, UploadKind kind)
    {
        if (!IsValidSha(sha256))
        {
            throw new ArgumentException("Not a SHA-256 in lowercase hex.", nameof(sha256));
        }

        return Path.Combine(UploadsDir, (kind == UploadKind.Save ? "save-" : "manifest-") + sha256 + PartExtension);
    }

    public bool Exists(string sha256, UploadKind kind) => IsValidSha(sha256) && File.Exists(PathOf(sha256, kind));

    /// <summary>The size of a stored file, or null.</summary>
    public long? SizeOf(string sha256, UploadKind kind) =>
        IsValidSha(sha256) && new FileInfo(PathOf(sha256, kind)) is { Exists: true } info ? info.Length : null;

    /// <summary>Finds a stored file whatever its kind (the HTTP route does not say).</summary>
    public StoredFile? Find(string sha256)
    {
        if (!IsValidSha(sha256))
        {
            return null;
        }

        foreach (var kind in new[] { UploadKind.Save, UploadKind.Manifest })
        {
            var info = new FileInfo(PathOf(sha256, kind));
            if (info.Exists)
            {
                return new StoredFile(sha256, kind, info.Length, info.LastWriteTimeUtc, info.FullName);
            }
        }

        return null;
    }

    /// <summary>Moves a verified part file into place atomically. An identical file already there wins and the part is deleted.</summary>
    public void Promote(string partPath, string sha256, UploadKind kind)
    {
        string target = PathOf(sha256, kind);
        if (File.Exists(target))
        {
            TryDelete(partPath);
            return;
        }

        File.Move(partPath, target);
    }

    public bool Delete(string sha256, UploadKind kind)
    {
        if (!IsValidSha(sha256))
        {
            return false;
        }

        string path = PathOf(sha256, kind);
        if (!File.Exists(path))
        {
            return false;
        }

        TryDelete(path);
        return true;
    }

    public IEnumerable<StoredFile> Enumerate()
    {
        foreach (var path in Directory.EnumerateFiles(SavesDir))
        {
            string name = Path.GetFileName(path);
            UploadKind kind;
            string sha;
            if (name.EndsWith(SaveExtension, StringComparison.Ordinal))
            {
                kind = UploadKind.Save;
                sha = name[..^SaveExtension.Length];
            }
            else if (name.EndsWith(ManifestExtension, StringComparison.Ordinal))
            {
                kind = UploadKind.Manifest;
                sha = name[..^ManifestExtension.Length];
            }
            else
            {
                continue;
            }

            if (IsValidSha(sha) && new FileInfo(path) is { Exists: true } info)
            {
                yield return new StoredFile(sha, kind, info.Length, info.LastWriteTimeUtc, info.FullName);
            }
        }
    }

    /// <summary>Partial uploads: path, size and last write time.</summary>
    public IEnumerable<FileInfo> EnumerateParts() =>
        Directory.EnumerateFiles(UploadsDir, "*" + PartExtension).Select(static p => new FileInfo(p));

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort; the janitor tries again
        }
    }

    /// <summary>Formats a hex SHA-256 the way logs and the GUI show it (first 12 digits).</summary>
    public static string Abbrev(string sha256) => sha256.Length <= 12 ? sha256 : sha256[..12];

    internal static string Stamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
