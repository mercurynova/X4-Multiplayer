using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;

namespace X4MP.Launcher.Core;

/// <summary>
/// The process operations the host needs, kept behind an interface so the lifecycle and its tests do not depend on
/// Windows. The WPF project supplies the real implementation with a Job Object and a graceful console stop.
/// </summary>
public interface ILocalServerProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    void Start(LocalServerPlan plan);

    /// <summary>Asks the server to stop and waits up to <paramref name="grace"/>; true when it has exited.</summary>
    bool StopProcess(TimeSpan grace);
}

public enum LocalServerState
{
    Stopped,
    Starting,
    Running,
    Failed,
    Exited,
}

public sealed record LocalServerStatus(
    LocalServerState State,
    int? Pid = null,
    int? ExitCode = null,
    bool External = false,
    string? Error = null)
{
    public bool IsRunning => State == LocalServerState.Running;
}

/// <summary>
/// Starts the sidecar, waits until its health endpoint answers, and stops it again. A server that is already healthy
/// (started by an earlier launcher, or by the game with <c>auto_stop</c> off) is adopted instead of duplicated; the
/// launcher does not stop a server it did not start.
/// </summary>
public sealed class LocalServerHost : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly ILocalServerProcess _process;
    private readonly Func<LocalServerPlan, CancellationToken, Task<bool>> _healthy;
    private readonly TimeSpan _startTimeout;
    private readonly TimeSpan _stopGrace;
    private LocalServerPlan? _plan;
    private bool _external;
    private bool _disposed;

    public LocalServerHost(
        ILocalServerProcess process,
        Func<LocalServerPlan, CancellationToken, Task<bool>>? healthy = null,
        TimeSpan? startTimeout = null,
        TimeSpan? stopGrace = null)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _healthy = healthy ?? ServerHealth.CheckAsync;
        _startTimeout = startTimeout ?? TimeSpan.FromSeconds(30);
        _stopGrace = stopGrace ?? TimeSpan.FromSeconds(8);
    }

    public LocalServerStatus Status => Poll();

    public async Task<LocalServerStatus> StartAsync(LocalServerPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_plan is not null)
        {
            var current = Poll();
            if (current.State is LocalServerState.Running or LocalServerState.Starting)
            {
                return current;
            }

            _plan = null;
            _external = false;
        }

        // A healthy server on the same admin port is already what the host needs. This also covers a previous
        // launcher that crashed after starting it, and a server the game left running on purpose.
        if (await _healthy(plan, cancellationToken))
        {
            _plan = plan;
            _external = true;
            return new LocalServerStatus(LocalServerState.Running, External: true);
        }

        try
        {
            _process.Start(plan);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            return new LocalServerStatus(LocalServerState.Failed, Error: error.Message);
        }

        _plan = plan;
        _external = false;
        var started = DateTime.UtcNow;
        try
        {
            while (true)
            {
                if (_process.HasExited)
                {
                    var code = _process.ExitCode;
                    _plan = null;
                    return new LocalServerStatus(
                        LocalServerState.Failed,
                        ExitCode: code,
                        Error: $"The local server exited with code {code}. See {plan.LogDirectory}.");
                }

                if (await _healthy(plan, cancellationToken))
                {
                    return new LocalServerStatus(LocalServerState.Running, Pid: _process.Id);
                }

                if (DateTime.UtcNow - started >= _startTimeout)
                {
                    _process.StopProcess(_stopGrace);
                    _plan = null;
                    return new LocalServerStatus(
                        LocalServerState.Failed,
                        Error: $"The local server did not answer at {plan.HealthUri} within "
                            + $"{_startTimeout.TotalSeconds:0} s. See {plan.LogDirectory}.");
                }

                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _process.StopProcess(_stopGrace);
            _plan = null;
            throw;
        }
    }

    /// <summary>Stops only a server this host started. An adopted server is left running and simply forgotten.</summary>
    public LocalServerStatus Stop()
    {
        if (_plan is null)
        {
            return new LocalServerStatus(LocalServerState.Stopped);
        }

        if (!_external)
        {
            _process.StopProcess(_stopGrace);
        }

        _plan = null;
        _external = false;
        return new LocalServerStatus(LocalServerState.Stopped);
    }

    public LocalServerStatus Poll()
    {
        if (_plan is null)
        {
            return new LocalServerStatus(LocalServerState.Stopped);
        }

        if (_external)
        {
            return new LocalServerStatus(LocalServerState.Running, External: true);
        }

        if (_process.HasExited)
        {
            return new LocalServerStatus(
                LocalServerState.Exited,
                ExitCode: _process.ExitCode,
                Error: $"The local server exited with code {_process.ExitCode}. See {_plan.LogDirectory}.");
        }

        return new LocalServerStatus(LocalServerState.Running, Pid: _process.Id);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _process.Dispose();
    }
}

/// <summary>The one unauthenticated endpoint the launcher uses to know the sidecar is ready.</summary>
public static class ServerHealth
{
    private static readonly HttpClient Client = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    public static async Task<bool> CheckAsync(LocalServerPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync(plan.HealthUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            return IsOk(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The two-second client timeout, not the caller cancelling: the server is still starting.
            return false;
        }
    }

    public static bool IsOk(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
