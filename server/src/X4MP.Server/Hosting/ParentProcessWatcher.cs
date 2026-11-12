using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace X4MP.Server.Hosting;

/// <summary>
/// Stops the server when the game process that spawned it exits. A save load or /reloadui unloads
/// the extension while the game keeps running, so the server must not be tied to the DLL.
/// Without --parent-pid (manual runs, tests, the Windows service) this service is not registered.
/// </summary>
public sealed partial class ParentProcessWatcher(
    int parentPid,
    IHostApplicationLifetime lifetime,
    ILogger<ParentProcessWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            LogWatching(logger, parentPid);
            await parent.WaitForExitAsync(stoppingToken);
            LogExited(logger, parentPid);
        }
        catch (ArgumentException)
        {
            LogGone(logger, parentPid);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        lifetime.StopApplication();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "parent process {Pid} is running; the server stops when it exits")]
    private static partial void LogWatching(ILogger logger, int pid);

    [LoggerMessage(Level = LogLevel.Information, Message = "parent process {Pid} exited; stopping the server")]
    private static partial void LogExited(ILogger logger, int pid);

    [LoggerMessage(Level = LogLevel.Information, Message = "parent process {Pid} is already gone; stopping the server")]
    private static partial void LogGone(ILogger logger, int pid);
}
