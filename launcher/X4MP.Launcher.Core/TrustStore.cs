using System.Text.Json;

namespace X4MP.Launcher.Core;

/// <summary>
/// The servers the player has said "yes" to once. First contact is the only moment a server can ask to change which
/// mods are enabled, so the answer is remembered per normalised address in
/// <c>%LOCALAPPDATA%\X4MP\launcher\trusted-servers.json</c>. A missing or unreadable file means nothing is trusted.
/// </summary>
public sealed class TrustStore
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly List<string> _servers = [];

    private TrustStore(string path) => _path = path;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "X4MP", "launcher", "trusted-servers.json");

    public IReadOnlyList<string> Servers => _servers;

    public static TrustStore Load(string path)
    {
        var store = new TrustStore(path);
        if (!File.Exists(path)) return store;

        try
        {
            var data = JsonSerializer.Deserialize<TrustFile>(File.ReadAllText(path), Json);
            if (data?.Servers is null) return store;
            foreach (var server in data.Servers)
            {
                if (!string.IsNullOrWhiteSpace(server) && !store._servers.Contains(server, StringComparer.Ordinal))
                {
                    store._servers.Add(server);
                }
            }
        }
        catch (JsonException)
        {
            // A file that cannot be read is treated as "nothing trusted yet": the next prompt simply asks again.
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return store;
    }

    public bool IsTrusted(string? address)
    {
        string key;
        try
        {
            key = ServerAddress.Key(address);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return _servers.Contains(key, StringComparer.Ordinal);
    }

    /// <summary>Remembers a server. Returns false when it was already known.</summary>
    public bool Trust(string? address)
    {
        var key = ServerAddress.Key(address);
        if (_servers.Contains(key, StringComparer.Ordinal)) return false;
        _servers.Add(key);
        Save();
        return true;
    }

    /// <summary>Writes the list. A failure to save is not fatal: the next check asks once more.</summary>
    public bool Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(new TrustFile(CurrentVersion, _servers), Json));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record TrustFile(int Version, List<string> Servers);
}
