using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using X4MP.Server.Hosting;

namespace X4MP.Server.Tests;

public class ParentProcessWatcherTests
{
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public Task Stopped => _stopped.Task;
        public void StopApplication() => _stopped.TrySetResult();
    }

    [Fact]
    public async Task StopsWhenTheParentProcessExits()
    {
        using var parent = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var lifetime = new FakeLifetime();
        var watcher = new ParentProcessWatcher(parent.Id, lifetime, NullLogger<ParentProcessWatcher>.Instance);

        await watcher.StartAsync(CancellationToken.None);
        await lifetime.Stopped.WaitAsync(TimeSpan.FromSeconds(10));
        await watcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopsAtOnceWhenTheParentIsAlreadyGone()
    {
        var lifetime = new FakeLifetime();
        var watcher = new ParentProcessWatcher(int.MaxValue, lifetime, NullLogger<ParentProcessWatcher>.Instance);

        await watcher.StartAsync(CancellationToken.None);
        await lifetime.Stopped.WaitAsync(TimeSpan.FromSeconds(10));
        await watcher.StopAsync(CancellationToken.None);
    }
}
