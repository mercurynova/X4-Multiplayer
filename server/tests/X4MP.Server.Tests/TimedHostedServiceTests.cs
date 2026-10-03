using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using X4MP.Server.Hosting;

namespace X4MP.Server.Tests;

/// <summary>Every hosted service logs one line with its stop duration at shutdown (M2-13 follow-up of the slow-host-stop flake).</summary>
public sealed class TimedHostedServiceTests
{
    private sealed class Slow(List<string> order) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(120, cancellationToken);
            order.Add(nameof(Slow));
        }
    }

    private sealed class Quick(List<string> order) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            order.Add(nameof(Quick));
            return Task.CompletedTask;
        }
    }

    private sealed class Throws : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }

    private sealed class Collector : ILoggerProvider, ILogger
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task EachServiceLogsOneStopLineWithItsDuration()
    {
        var order = new List<string>();
        var log = new Collector();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(log));
        services.AddHostedService(_ => new Quick(order));
        services.AddHostedService<Throws>();
        services.AddHostedService(_ => new Slow(order));
        services.TimeHostedServiceStops();
        await using var provider = services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>().ToList();
        Assert.Equal(3, hosted.Count);
        foreach (var s in hosted)
        {
            await s.StartAsync(CancellationToken.None);
        }

        foreach (var s in Enumerable.Reverse(hosted))
        {
            try
            {
                await s.StopAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // the host logs a failing stop and goes on; the timing line must still be there
            }
        }

        Assert.Equal(["Slow", "Quick"], order);
        string[] lines = [.. log.Lines.Where(l => l.StartsWith("stopped hosted service", StringComparison.Ordinal))];
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("stopped hosted service Slow in ", lines[0], StringComparison.Ordinal);
        Assert.True(long.Parse(lines[0].Split(' ')[^2], System.Globalization.CultureInfo.InvariantCulture) >= 100, lines[0]);
        Assert.Contains(lines, l => l.StartsWith("stopped hosted service Throws in ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("stopped hosted service Quick in ", StringComparison.Ordinal));
    }
}
