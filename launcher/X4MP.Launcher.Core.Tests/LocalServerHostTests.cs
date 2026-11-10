using X4MP.Launcher.Core;

namespace X4MP.Launcher.Core.Tests;

public class LocalServerHostTests
{
    private static LocalServerPlan Plan => new(
        @"C:\bundle\server\x4mp-server.exe",
        @"C:\state",
        @"C:\state\server-data",
        @"C:\state\logs",
        LocalServerPlan.DefaultNodePort,
        LocalServerPlan.DefaultUdpPort,
        LocalServerPlan.DefaultHttpPort,
        "node-secret",
        @"C:\state\server-data\admin-password.txt");

    [Fact]
    public async Task AStartedServerIsRunningWhenHealthAnswers()
    {
        var process = new FakeProcess();
        var checks = 0;
        using var host = new LocalServerHost(
            process,
            (_, _) => Task.FromResult(++checks > 1));

        var status = await host.StartAsync(Plan);

        Assert.True(status.IsRunning);
        Assert.False(status.External);
        Assert.Equal(4242, status.Pid);
        Assert.Equal(1, process.StartCount);
    }

    [Fact]
    public async Task AnAlreadyHealthyServerIsAdoptedAndNotDuplicated()
    {
        var process = new FakeProcess();
        using var host = new LocalServerHost(process, (_, _) => Task.FromResult(true));

        var status = await host.StartAsync(Plan);

        Assert.True(status.IsRunning);
        Assert.True(status.External);
        Assert.Equal(0, process.StartCount);
    }

    [Fact]
    public async Task AServerThatExitsIsReportedWithItsCode()
    {
        var process = new FakeProcess { ExitOnStart = true, ExitCodeOnStart = 7 };
        using var host = new LocalServerHost(process, (_, _) => Task.FromResult(false));

        var status = await host.StartAsync(Plan);

        Assert.Equal(LocalServerState.Failed, status.State);
        Assert.Equal(7, status.ExitCode);
        Assert.Contains("code 7", status.Error, StringComparison.Ordinal);
        Assert.Contains(Plan.LogDirectory, status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerThatNeverAnswersIsStoppedAfterTheTimeout()
    {
        var process = new FakeProcess();
        using var host = new LocalServerHost(
            process,
            (_, _) => Task.FromResult(false),
            startTimeout: TimeSpan.Zero);

        var status = await host.StartAsync(Plan);

        Assert.Equal(LocalServerState.Failed, status.State);
        Assert.Contains("did not answer", status.Error, StringComparison.Ordinal);
        Assert.True(process.StopCalled);
    }

    [Fact]
    public async Task StopLeavesAnAdoptedServerAlone()
    {
        var process = new FakeProcess();
        using var host = new LocalServerHost(process, (_, _) => Task.FromResult(true));
        await host.StartAsync(Plan);

        var status = host.Stop();

        Assert.Equal(LocalServerState.Stopped, status.State);
        Assert.False(process.StopCalled);
    }

    [Fact]
    public async Task StopStopsAServerThisHostStarted()
    {
        var process = new FakeProcess();
        var checks = 0;
        using var host = new LocalServerHost(
            process,
            (_, _) => Task.FromResult(++checks > 1));
        await host.StartAsync(Plan);

        var status = host.Stop();

        Assert.Equal(LocalServerState.Stopped, status.State);
        Assert.True(process.StopCalled);
        Assert.True(process.Disposed is false);
    }

    [Fact]
    public async Task PollReportsAnExitAfterTheServerWasRunning()
    {
        var process = new FakeProcess();
        var checks = 0;
        using var host = new LocalServerHost(
            process,
            (_, _) => Task.FromResult(++checks > 1));
        await host.StartAsync(Plan);

        process.HasExited = true;
        process.ExitCode = 3;

        var status = host.Poll();

        Assert.Equal(LocalServerState.Exited, status.State);
        Assert.Equal(3, status.ExitCode);
    }

    [Fact]
    public void DisposeDisposesTheProcess()
    {
        var process = new FakeProcess();
        var host = new LocalServerHost(process, (_, _) => Task.FromResult(true));

        host.Dispose();

        Assert.True(process.Disposed);
    }

    [Theory]
    [InlineData("{\"status\":\"ok\"}", true)]
    [InlineData("{\"status\":\"OK\",\"version\":\"1.0\"}", true)]
    [InlineData("{\"status\":\"starting\"}", false)]
    [InlineData("{}", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    public void TheHealthEndpointIsRecognisedByItsStatus(string json, bool expected)
    {
        Assert.Equal(expected, ServerHealth.IsOk(json));
    }

    private sealed class FakeProcess : ILocalServerProcess
    {
        public bool ExitOnStart { get; init; }
        public int ExitCodeOnStart { get; init; }

        public int Id => 4242;
        public bool HasExited { get; set; } = true;
        public int? ExitCode { get; set; }
        public int StartCount { get; private set; }
        public bool StopCalled { get; private set; }
        public bool Disposed { get; private set; }

        public void Start(LocalServerPlan plan)
        {
            StartCount++;
            HasExited = ExitOnStart;
            ExitCode = ExitOnStart ? ExitCodeOnStart : null;
        }

        public bool StopProcess(TimeSpan grace)
        {
            StopCalled = true;
            HasExited = true;
            return true;
        }

        public void Dispose() => Disposed = true;
    }
}
