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

    /// <summary>
    /// Writes the request and returns its path. An existing file is replaced: the player asked again.
    /// <paramref name="role"/> is <c>client</c> or <c>authority</c>; <paramref name="adminPassword"/> is the node
    /// authority password the server was started with, and is written only for an authority request.
    /// </summary>
    public static string Write(
        string directory,
        string server,
        string name,
        string password,
        DateTimeOffset expiresUtc,
        string? role = null,
        string? adminPassword = null)
    {
        var address = ServerAddress.HostAndPort(server);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Enter the name the other players will see.", nameof(name));
        }

        var normalisedRole = string.IsNullOrWhiteSpace(role) ? null : role.Trim().ToLowerInvariant();
        if (normalisedRole is not (null or "client" or "authority"))
        {
            throw new ArgumentException("The role must be client or authority.", nameof(role));
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "launch.json");
        var request = new LaunchRequest(
            address,
            name.Trim(),
            string.IsNullOrEmpty(password) ? null : password,
            normalisedRole,
            normalisedRole == "authority" && !string.IsNullOrEmpty(adminPassword) ? adminPassword : null,
            expiresUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

        // No BOM and no newline: the mod's parser reads the whole file, and Windows tools like write-launch.ps1
        // have always written it this way.
        File.WriteAllText(path, JsonSerializer.Serialize(request, Json), new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// Writes the request for a host who runs the local sidecar server: the game connects to loopback as the
    /// authority and proves itself with the node password the launcher passed to the server.
    /// </summary>
    public static string WriteHost(
        string directory,
        string name,
        string password,
        string adminPassword,
        DateTimeOffset expiresUtc,
        int nodePort = LocalServerPlan.DefaultNodePort)
    {
        return Write(
            directory,
            LocalServerPlan.HostAddress(nodePort),
            name,
            password,
            expiresUtc,
            "authority",
            adminPassword);
    }

    private sealed record LaunchRequest(
        [property: JsonPropertyName("server")] string Server,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("password")] string? Password,
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("admin_password")] string? AdminPassword,
        [property: JsonPropertyName("expires_utc")] string ExpiresUtc);
}
