using System.Globalization;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Core.World;
using X4MP.Protocol;
using X4MP.Server.Api;
using X4MP.Server.Auth;

namespace X4MP.Server.Admin;

/// <summary><c>/api/v1/diagnostics/connections</c>, its trace switch, and the <c>/api/v1/galaxy</c> reads.</summary>
internal static class DiagnosticsEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/diagnostics/connections", ConnectionsAsync).RequireAuthorization(AdminPolicies.Viewer);
        routes.MapPost("/api/v1/diagnostics/connections/{id:long}/trace", TraceAsync).RequireAuthorization(AdminPolicies.Admin);
        routes.MapGet("/api/v1/galaxy", GalaxyAsync).RequireAuthorization(AdminPolicies.Viewer);
        routes.MapGet("/api/v1/galaxy/sectors/{id:int}", SectorAsync).RequireAuthorization(AdminPolicies.Viewer);
    }

    // ------------------------------------------------------------------ connections

    private static async Task<IResult> ConnectionsAsync(AdminSessions sessions, IServiceProvider services) =>
        Results.Json(await ConnectionStatsBuilder.BuildAsync(sessions, services), ApiJsonContext.Default.ListConnectionStatsDto);

    private static async Task<IResult> TraceAsync(long id, TraceRequest? body, AdminSessions sessions, IServiceProvider services)
    {
        var errors = new Dictionary<string, string[]>();
        if (body is null)
        {
            errors["body"] = ["Send enabled and sampleEvery."];
        }
        else if (body.SampleEvery is < 1 or > 1000)
        {
            errors["sampleEvery"] = ["Use a value from 1 to 1000."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        bool known = (services.GetService<NodeGateway>()?.AdmittedNodes ?? []).Any(n => n.Connection.Id.Value == id);
        if (!known)
        {
            return Problems.NotFound("The connection");
        }

        _ = sessions;
        return Problems.NotImplemented("Per-connection message tracing is not available in this build; the counters of GET /diagnostics/connections are.");
    }

    // ------------------------------------------------------------------ galaxy

    private static async Task<IResult> GalaxyAsync(AdminSessions sessions, WorldMirror mirror)
    {
        var dto = await GalaxyDtoBuilder.BuildAsync(sessions, mirror);
        return dto is null
            ? Problems.Result(StatusCodes.Status404NotFound, "GalaxyUnavailable", "Not found.", "No galaxy is loaded yet: the authority has not sent its galaxy metadata.")
            : Results.Json(dto, ApiJsonContext.Default.GalaxyDto);
    }

    private static async Task<IResult> SectorAsync(int id, AdminSessions sessions, WorldMirror mirror)
    {
        var detail = await sessions.Actor.CallAsync(() =>
        {
            var model = mirror.Galaxy.Current;
            if (model is null || id is < 1 or > ushort.MaxValue || model.Find((ushort)id) is not { } sector)
            {
                return null;
            }

            var summary = mirror.Summary.TryGetValue(sector.Index, out var s) ? s : (SectorSummaryCounts?)null;
            var players = mirror.PlayerShips.Where(p => p.Sector == sector.Index).Select(p => (p.PlayerId, p.NetId)).ToList();
            var neighbors = model.Graph.Neighbors(sector.Index).ToArray().Select(n => (long)n).ToList();
            return new { Model = model, Sector = sector, Summary = summary, Players = players, Neighbors = neighbors };
        });
        if (detail is null)
        {
            return Problems.NotFound("The sector");
        }

        var live = await sessions.GetLiveAsync();
        var dto = new SectorDetailDto(
            GalaxyDtoBuilder.ToDto(detail.Sector, GalaxyDtoBuilder.ClusterIds(detail.Model)),
            detail.Neighbors,
            detail.Summary is { } c ? c.ShipsXs + c.ShipsS + c.ShipsM + c.ShipsL + c.ShipsXl : null,
            detail.Summary?.Stations,
            [.. detail.Players.Select(p => new SectorPlayerDto(p.PlayerId, live.Snapshot.Nodes.FirstOrDefault(n => n.PlayerId == p.PlayerId)?.Name ?? p.PlayerId.ToString(CultureInfo.InvariantCulture), p.NetId))]);
        return Results.Json(dto, ApiJsonContext.Default.SectorDetailDto);
    }
}
