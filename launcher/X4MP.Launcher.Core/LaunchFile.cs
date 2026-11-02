using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace X4MP.Launcher.Core;

/// <summary>
/// The one-shot <c>launch.json</c> the mod consumes when X4 starts (docs/mod-design.md 2.6): the server to join,
/// the player name, the session password when there is one, and an expiry so a file nobody used cannot connect
/// the player days later. The mod reads it once, deletes it before the handshake, and never logs the password.
/// </summary>
public static class LaunchFile
{
    /// <summary>How long a written request stays valid. X4 is usually up in a minute or two.</summary>
    public const int LifetimeMinutes = 10;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Where the mod looks for its files: the extension folder in portable mode (it holds <c>x4mp.portable</c> or
    /// its own <c>x4mp.json</c>), otherwise <c>%LocalAppData%\X4MP</c>. Same rule as <c>mod/native/host/paths.cpp</c>.
    /// </summary>
    public static string DirectoryFor(GameInstall? install, string localAppData)
    {
        var extension = install is null ? null : Path.Combine(install.ExtensionsDir, "x4mp");
        if (extension is not null
            && (File.Exists(Path.Combine(extension, "x4mp.json")) || File.Exists(Path.Combine(extension, "x4mp.portable"))))
        {
            return extension;
        }

        return Path.Combine(localAppData, "X4MP");
    }

    /// <summary>Writes the request and returns its path. An existing file is replaced: the player asked again.</summary>
    public static string Write(
        string directory, string server, string name, string password, DateTimeOffset expiresUtc)
    {
        var address = ServerAddress.HostAndPort(server);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Enter the name the other players will see.", nameof(name));
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "launch.json");
        var request = new LaunchRequest(
            address,
            name.Trim(),
            string.IsNullOrEmpty(password) ? null : password,
            expiresUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

        // No BOM and no newline: the mod's parser reads the whole file, and Windows tools like write-launch.ps1
        // have always written it this way.
        File.WriteAllText(path, JsonSerializer.Serialize(request, Json), new UTF8Encoding(false));
        return path;
    }

    private sealed record LaunchRequest(
        [property: JsonPropertyName("server")] string Server,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("password")] string? Password,
        [property: JsonPropertyName("expires_utc")] string ExpiresUtc);
}
