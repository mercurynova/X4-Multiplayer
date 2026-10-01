using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace X4MP.Server.Hosting;

/// <summary>One <c>sc.exe</c> / <c>icacls.exe</c> invocation. Arguments are kept as a list, so no shell quoting is involved.</summary>
public sealed record ScCommand(string FileName, IReadOnlyList<string> Arguments, bool IgnoreFailure = false)
{
    /// <summary>Copy-pasteable rendering for a console (used when not elevated, and in logs).</summary>
    public string Render() =>
        FileName + " " + string.Join(' ', Arguments.Select(a => a.Contains(' ', StringComparison.Ordinal) || a.Contains('"', StringComparison.Ordinal)
            ? "\"" + a.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : a));
}

/// <summary>Builds the command sequences for <c>service install</c> / <c>service uninstall</c> (server-design 1.5, M0-12).</summary>
public static class ServiceCommands
{
    public const string DefaultAccount = "NT SERVICE\\";

    public static string AccountFor(string serviceName) => DefaultAccount + serviceName;

    /// <summary>
    /// Create the service under the virtual account <c>NT SERVICE\&lt;name&gt;</c> (auto start, restart after 10 s on
    /// failure) and grant that account modify rights on the data dir. The ACL step comes last because the virtual
    /// account only resolves once the service exists.
    /// </summary>
    public static IReadOnlyList<ScCommand> BuildInstall(string serviceName, string exePath, string dataDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);

        var binPath = $"\"{exePath}\" run --service --name {serviceName} --data-dir \"{dataDir}\"";
        return
        [
            new ScCommand("sc.exe",
            [
                "create", serviceName,
                "binPath=", binPath,
                "start=", "auto",
                "obj=", AccountFor(serviceName),
                "DisplayName=", "X4MP multiplayer server",
            ]),
            new ScCommand("sc.exe", ["description", serviceName, "X4: Foundations multiplayer relay server and web admin GUI."]),
            new ScCommand("sc.exe",
            [
                "failure", serviceName,
                "reset=", "86400",
                "actions=", "restart/10000/restart/10000/restart/10000",
            ]),
            new ScCommand("icacls.exe", [dataDir, "/grant", $"{AccountFor(serviceName)}:(OI)(CI)M"]),
        ];
    }

    /// <summary>Stop (a not-running service is fine) then delete.</summary>
    public static IReadOnlyList<ScCommand> BuildUninstall(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        return
        [
            new ScCommand("sc.exe", ["stop", serviceName], IgnoreFailure: true),
            new ScCommand("sc.exe", ["delete", serviceName]),
        ];
    }

    public static int Install(CliArguments cli, string exePath, TextWriter output, Func<ScCommand, int> runner, bool elevated)
    {
        ArgumentNullException.ThrowIfNull(cli);
        var name = cli.ServiceName ?? ServerHost.DefaultServiceName;
        var dataDir = Path.GetFullPath(cli.DataDir ?? DataDirResolver.ServiceDataDir());
        return Execute(BuildInstall(name, exePath, dataDir), output, runner, elevated, () => Directory.CreateDirectory(dataDir), $"Service '{name}' installed. Start it with: sc.exe start {name}");
    }

    public static int Uninstall(CliArguments cli, TextWriter output, Func<ScCommand, int> runner, bool elevated)
    {
        ArgumentNullException.ThrowIfNull(cli);
        var name = cli.ServiceName ?? ServerHost.DefaultServiceName;
        return Execute(BuildUninstall(name), output, runner, elevated, null, $"Service '{name}' removed.");
    }

    private static int Execute(
        IReadOnlyList<ScCommand> commands,
        TextWriter output,
        Func<ScCommand, int> runner,
        bool elevated,
        Action? beforeRun,
        string successMessage)
    {
        if (!elevated)
        {
            output.WriteLine("Not running elevated. Run these commands from an elevated prompt (or re-run this command as Administrator):");
            output.WriteLine();
            foreach (var command in commands)
            {
                output.WriteLine(command.Render());
            }
            return 1;
        }

        beforeRun?.Invoke();
        foreach (var command in commands)
        {
            output.WriteLine("> " + command.Render());
            var code = runner(command);
            if (code != 0 && !command.IgnoreFailure)
            {
                output.WriteLine($"Failed with exit code {code}.");
                return code;
            }
        }
        output.WriteLine(successMessage);
        return 0;
    }

    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        return IsElevatedWindows();
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevatedWindows()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Runs a command with inherited console output and returns its exit code.</summary>
    public static int RunProcess(ScCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var info = new ProcessStartInfo(command.FileName) { UseShellExecute = false };
        foreach (var argument in command.Arguments)
        {
            info.ArgumentList.Add(argument);
        }
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {command.FileName}.");
        process.WaitForExit();
        return process.ExitCode;
    }
}
