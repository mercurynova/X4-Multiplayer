using System.Globalization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using X4MP.Core.Metrics;
using X4MP.Server.Api;
using X4MP.Server.Auth;

namespace X4MP.Server.Metrics;

/// <summary>Wires the metrics sampler (the hook <c>ServerHost</c> calls) and maps <c>/api/v1/diagnostics/metrics</c>.</summary>
public static class MetricsExtensions
{
    /// <summary>Registers the <see cref="MetricsSampler"/> (600 samples per series) and the 1 Hz service that feeds it.</summary>
    public static IServiceCollection AddServerMetrics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => MetricsSampler.CreateDefault(sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService<MetricsSamplerService>();
        return services;
    }

    public static IEndpointRouteBuilder MapMetricsApi(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapGet("/api/v1/diagnostics/metrics", (string? series, int? window, MetricsSampler sampler) =>
        {
            var names = string.IsNullOrWhiteSpace(series)
                ? null
                : series.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var dtos = sampler.GetSeries(names, Math.Clamp(window ?? sampler.Capacity, 1, sampler.Capacity))
                .Select(s => new MetricSeriesDto(
                    s.Name,
                    s.Unit,
                    s.Kind.ToString().ToLowerInvariant(),
                    s.IntervalSeconds,
                    s.EndedAt?.ToString("O", CultureInfo.InvariantCulture),
                    [.. s.Samples]))
                .ToList();
            return Results.Json(dtos, ApiJsonContext.Default.ListMetricSeriesDto);
        }).RequireAuthorization(AdminPolicies.Viewer);
        return routes;
    }
}

/// <summary>Takes one <see cref="MetricsSampler"/> sample per second.</summary>
internal sealed class MetricsSamplerService(MetricsSampler sampler, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                sampler.Sample();
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
