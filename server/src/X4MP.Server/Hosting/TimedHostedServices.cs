using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace X4MP.Server.Hosting;

/// <summary>
/// Logs one line per hosted service when it stops (name and milliseconds), so a slow shutdown names the service that held it up. The host stops
/// services one after another in reverse order, each awaited, so the lines also show the order and where the time went.
/// </summary>
public sealed partial class TimedHostedService(IHostedService inner, ILogger logger) : IHostedService, IAsyncDisposable, IDisposable
{
    public IHostedService Inner => inner;

    public Task StartAsync(CancellationToken cancellationToken) => inner.StartAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            await inner.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            long milliseconds = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            string name = inner.GetType().Name;
            LogStopped(name, milliseconds);
        }
    }

    // The container owns the inner instance only through this wrapper, so disposal is forwarded.
    public void Dispose() => (inner as IDisposable)?.Dispose();

    public ValueTask DisposeAsync()
    {
        if (inner is IAsyncDisposable async)
        {
            return async.DisposeAsync();
        }

        (inner as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "stopped hosted service {Service} in {Milliseconds} ms")]
    private partial void LogStopped(string service, long milliseconds);
}

public static class TimedHostedServiceExtensions
{
    /// <summary>Wraps every <see cref="IHostedService"/> registration already in the collection with <see cref="TimedHostedService"/> (order is kept).</summary>
    public static IServiceCollection TimeHostedServiceStops(this IServiceCollection services)
    {
        for (int i = 0; i < services.Count; i++)
        {
            var d = services[i];
            if (d.ServiceType != typeof(IHostedService) || d.IsKeyedService || d.ImplementationType == typeof(TimedHostedService))
            {
                continue;
            }

            var original = d;
            services[i] = ServiceDescriptor.Singleton<IHostedService>(sp =>
            {
                IHostedService inner = original switch
                {
                    { ImplementationInstance: IHostedService instance } => instance,
                    { ImplementationFactory: { } factory } => (IHostedService)factory(sp),
                    { ImplementationType: { } type } => (IHostedService)ActivatorUtilities.CreateInstance(sp, type),
                    _ => throw new InvalidOperationException("unsupported hosted service registration"),
                };
                return new TimedHostedService(inner, sp.GetRequiredService<ILoggerFactory>().CreateLogger("X4MP.Server.Shutdown"));
            });
        }

        return services;
    }
}
