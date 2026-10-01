namespace X4MP.Server.Api;

/// <summary>
/// One series of <c>GET /api/v1/diagnostics/metrics</c>: up to 600 one-second samples, oldest first.
/// <c>Kind</c> is <c>gauge</c> (value at sampling time) or <c>rate</c> (increase per second).
/// </summary>
[TsContract]
public sealed record MetricSeriesDto(string Name, string Unit, string Kind, int IntervalSeconds, string? EndedAt, List<double> Samples);
