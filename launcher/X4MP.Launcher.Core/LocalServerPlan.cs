using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace X4MP.Launcher.Core;

/// <summary>
/// Everything the launcher needs to run the sidecar server next to X4: the bundle, the data folder, the three
/// ports and the node authority password. The defaults match <c>mod/native/features/server/server_launcher.cpp</c>,
/// so a server started here and a server started from the game's own screen use the same files and the same password.
/// </summary>
public sealed record LocalServerPlan(
    string ExePath,
    string StateDirectory,
    string DataDirectory,
    string LogDirectory,
    int NodePort,
    int UdpPort,
    int HttpPort,
    string AdminPassword,
    string AdminPasswordFile)
{
    public const int DefaultNodePort = 47780;
    public const int DefaultUdpPort = 47781;
    public const int DefaultHttpPort = 47790;

    /// <summary>The address the mod's launch request uses: the node endpoint, not the HTTP admin GUI.</summary>
    public static string HostAddress(int nodePort = DefaultNodePort) =>
        "127.0.0.1:" + nodePort.ToString(CultureInfo.InvariantCulture);

    public Uri HealthUri => new($"http://127.0.0.1:{HttpPort}/healthz");

    public string AdminAddress => $"http://127.0.0.1:{HttpPort}/";

    public string WorkingDirectory => Path.GetDirectoryName(ExePath) ?? StateDirectory;

    public IReadOnlyList<string> Arguments =>
    [
        "--data-dir",
        DataDirectory,
        "--port",
        HttpPort.ToString(CultureInfo.InvariantCulture),
    ];

    public IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["X4MP__Net__NodeTcpEndpoint"] = "0.0.0.0:" + NodePort.ToString(CultureInfo.InvariantCulture),
        ["X4MP__Net__UdpPort"] = UdpPort.ToString(CultureInfo.InvariantCulture),
        ["X4MP__Net__AdminPassword"] = AdminPassword,
    };

    /// <summary>
    /// Builds the plan for an installed game. The server bundle is looked for beside the launcher first (the release
    /// zip puts it in <c>server\</c>), then in the game's <c>extensions\x4mp\server</c> folder.
    /// </summary>
    public static LocalServerPlan Create(
        GameInstall? install,
        string localAppData,
        string launcherDirectory,
        int nodePort = DefaultNodePort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherDirectory);
        if (nodePort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(nodePort), "The server port must be between 1 and 65535.");
        }

        var stateDirectory = LaunchFile.DirectoryFor(install, localAppData);
        var dataDirectory = Path.Combine(stateDirectory, "server-data");
        var logDirectory = Path.Combine(stateDirectory, "logs");
        var exe = FindServerExecutable(install, launcherDirectory);
        var passwordFile = Path.Combine(dataDirectory, "admin-password.txt");
        var adminPassword = ReadOrCreateAdminPassword(dataDirectory, passwordFile);

        return new LocalServerPlan(
            exe,
            stateDirectory,
            dataDirectory,
            logDirectory,
            nodePort,
            nodePort + 1,
            nodePort + 10,
            adminPassword,
            passwordFile);
    }

    /// <summary>The first <c>x4mp-server.exe</c> that exists, or a message naming every place that was tried.</summary>
    public static string FindServerExecutable(GameInstall? install, string launcherDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherDirectory);
        var candidates = new List<string>
        {
            Path.Combine(launcherDirectory, "server", "x4mp-server.exe"),
            Path.Combine(launcherDirectory, "x4mp-server.exe"),
        };
        if (install is not null)
        {
            candidates.Add(Path.Combine(install.ExtensionsDir, "x4mp", "server", "x4mp-server.exe"));
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException(
            "The local server was not found. Looked in: " + string.Join("; ", candidates)
            + ". Install the release package, which ships server\\x4mp-server.exe next to the launcher.",
            candidates[0]);
    }

    /// <summary>
    /// The node authority password, shared with the mod's own launcher: an existing
    /// <c>admin-password.txt</c> is reused, otherwise a 20-character password is written there. The alphabet
    /// leaves out look-alikes because the player may have to type this on another PC.
    /// </summary>
    private static string ReadOrCreateAdminPassword(string dataDirectory, string passwordFile)
    {
        if (File.Exists(passwordFile))
        {
            try
            {
                var line = File.ReadLines(passwordFile).FirstOrDefault()?.TrimEnd('\r', ' ');
                if (line is { Length: >= 8 })
                {
                    return line;
                }
            }
            catch (IOException)
            {
                // An unreadable file is replaced by a fresh password; the server would refuse the old one anyway.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var password = new StringBuilder(20);
        for (var i = 0; i < 20; i++)
        {
            password.Append(alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]);
        }

        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(passwordFile, password + "\n", new UTF8Encoding(false));
        return password.ToString();
    }
}
