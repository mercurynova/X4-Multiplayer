using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using X4MP.Server.Hosting;

namespace X4MP.Server.Auth;

/// <summary>
/// First-run bootstrap (server-design 4.2): with no admin users, creates <c>admin</c> with a random 20-character
/// password flagged <c>must_change</c>, writes it to <c>&lt;data-dir&gt;/initial-admin-password.txt</c> (owner-only)
/// and prints it once to the console. The password is never passed to the logger.
/// </summary>
public sealed partial class AdminBootstrap(
    AdminStore store,
    AdminAuthOptions options,
    DataDirInfo dataDir,
    ILogger<AdminBootstrap> logger) : IHostedService
{
    public const string InitialUsername = "admin";
    public const string InitialPasswordFileName = "initial-admin-password.txt";

    // No look-alike characters (0/O, 1/l/I).
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public static string InitialPasswordPath(string dataDirectory) => Path.Combine(dataDirectory, InitialPasswordFileName);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (store.CountUsers() > 0)
        {
            return Task.CompletedTask;
        }

        var password = RandomNumberGenerator.GetString(Alphabet, 20);
        store.CreateUser(InitialUsername, password, AdminRoles.Admin, mustChange: true, options.Pbkdf2Iterations);
        var path = InitialPasswordPath(dataDir.Path);
        WriteOwnerOnly(path, password);
        store.Audit("system", "auth.bootstrap", InitialUsername, null);
        LogCreated(InitialUsername, path);
        Console.Out.WriteLine($"x4mp-server: created admin user '{InitialUsername}' with initial password {password} (also in {path}). You must change it at first login.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Deletes the initial password file (called once the password has been changed).</summary>
    public static void DeleteInitialPasswordFile(string dataDirectory)
    {
        try
        {
            File.Delete(InitialPasswordPath(dataDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the password it contains is no longer valid anyway.
        }
    }

    private void WriteOwnerOnly(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var streamOptions = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, streamOptions))
        using (var writer = new StreamWriter(stream))
        {
            writer.WriteLine(content);
        }

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            LogAclFailed(path, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Created admin user {Username}; the initial password is in {Path} and must be changed at first login")]
    private partial void LogCreated(string username, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not restrict permissions on {Path}")]
    private partial void LogAclFailed(string path, Exception ex);
}
