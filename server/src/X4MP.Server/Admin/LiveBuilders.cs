using X4MP.Core.Events;
using X4MP.Core.Interest;
using X4MP.Core.Metrics;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Core.World;
using X4MP.Protocol;
using X4MP.Server.Api;

namespace X4MP.Server.Admin;

/// <summary>Builds the connection statistics list (REST <c>/diagnostics/connections</c> and the hub's <c>Diagnostics</c> push).</summary>
internal static class ConnectionStatsBuilder
{
    private static LaneStatsDto LaneOf(ConnectionStats stats, Lane lane) =>
        new(stats.BytesReceivedOn(lane), stats.BytesSentOn(lane), stats.Dropped(lane), stats.Coalesced(lane), stats.MaxQueuedBytes(lane));

    public static async Task<List<ConnectionStatsDto>> BuildAsync(AdminSessions sessions, IServiceProvider services)
    {
        var snapshot = await sessions.Actor.GetSnapshotAsync().ConfigureAwait(false);
        var now = sessions.Time.GetUtcNow();
        var list = new List<ConnectionStatsDto>();
        foreach (var node in services.GetService<NodeGateway>()?.AdmittedNodes ?? [])
        {
            var stats = node.Connection.Stats;
            var info = snapshot.Nodes.FirstOrDefault(n => n.PlayerId == node.PlayerId);
            double flushAvgMs = stats.FlushCount == 0 ? 0 : stats.FlushTicksTotal * 1000.0 / System.Diagnostics.Stopwatch.Frequency / stats.FlushCount;
            list.Add(new ConnectionStatsDto(
                node.Connection.Id.Value, node.Name, node.Roles.ToString(), "tcp", node.RemoteAddress.ToString(),
                info is null ? 0 : (long)Math.Max(0, (now - info.JoinedAt).TotalSeconds), info?.RttMs ?? 0,
                stats.BytesReceived, stats.BytesSent, stats.FramesReceived, stats.FramesSent, stats.FramesCoalesced, stats.FramesDropped,
                stats.Violations, stats.InboundDropped, flushAvgMs, stats.FlushTicksMax * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
                LaneOf(stats, Lane.Control), LaneOf(stats, Lane.Realtime), LaneOf(stats, Lane.Bulk)));
        }

        return [.. list.OrderBy(c => c.ConnectionId)];
    }
}

/// <summary>Builds <see cref="GalaxyDto"/> from the mirror's galaxy model (REST <c>/galaxy</c> and the hub's <c>SubscribeGalaxy</c>).</summary>
internal static class GalaxyDtoBuilder
{
    public static SectorDto ToDto(GalaxySector sector, Dictionary<string, long> clusters) => new(
        sector.Index, sector.Macro, sector.Name, clusters.TryGetValue(sector.ClusterMacro, out var cluster) ? cluster : 0,
        new Vec2Dto(sector.GalaxyPos.X, sector.GalaxyPos.Z), null);

    public static Dictionary<string, long> ClusterIds(GalaxyModel model)
    {
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var sector in model.Sectors)
        {
            ids.TryAdd(sector.ClusterMacro, ids.Count + 1);
        }

        return ids;
    }

    /// <summary>The galaxy, or null while the authority has not sent its metadata.</summary>
    public static async Task<GalaxyDto?> BuildAsync(AdminSessions sessions, WorldMirror mirror)
    {
        var model = await sessions.Actor.CallAsync(() => mirror.Galaxy.Current).ConfigureAwait(false);
        if (model is null)
        {
            return null;
        }

        var clusters = ClusterIds(model);
        return new GalaxyDto(
            model.SaveSha256Hex,
            [.. clusters.Select(c => new ClusterDto(c.Value, c.Key))],
            [.. model.Sectors.Select(s => ToDto(s, clusters))],
            [.. model.Links.Select(l => new GateLinkDto(l.From, l.To, l.Kind.ToString().ToLowerInvariant()))]);
    }
}

/// <summary>The active alerts, kept from the <c>AlertRaised</c>/<c>AlertCleared</c> events (fed by the broadcaster's subscription).</summary>
public sealed class ActiveAlerts
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, AlertDto> _active = new(StringComparer.Ordinal);

    /// <summary>Applies an event; returns the alert to push, or null when the event is not an alert.</summary>
    public AlertDto? Apply(DomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        lock (_gate)
        {
            switch (domainEvent)
            {
                case AlertRaised raised:
                    var alert = new AlertDto(raised.At, raised.Severity.ToString(), raised.Code, raised.Text, true);
                    _active[raised.Code] = alert;
                    return alert;
                case AlertCleared cleared:
                    var previous = _active.GetValueOrDefault(cleared.Code);
                    _active.Remove(cleared.Code);
                    return new AlertDto(cleared.At, previous?.Severity ?? nameof(AlertSeverity.Info), cleared.Code, previous?.Text ?? string.Empty, false);
                default:
                    return null;
            }
        }
    }

    public List<AlertDto> Snapshot()
    {
        lock (_gate)
        {
            return [.. _active.Values.OrderBy(a => a.At)];
        }
    }
}

/// <summary>Builds the dashboard snapshot (REST <c>/dashboard</c> and the hub's 1 Hz push).</summary>
internal sealed class DashboardBuilder(
    AdminSessions sessions, NetOptions net, WorldMirror mirror, InterestManager interest, MetricsSampler sampler, ActiveAlerts alerts)
{
    public async Task<DashboardSnapshotDto> BuildAsync()
    {
        var live = await sessions.GetLiveAsync().ConfigureAwait(false);
        var now = sessions.Time.GetUtcNow();
        var players = live.Snapshot.Nodes.Select(n => AdminMapping.ToLive(n, now, live)).ToList();
        var (entities, captured) = await sessions.Actor.CallAsync(() => (mirror.Count, interest.LastCaptureSectors.Count)).ConfigureAwait(false);
        return new DashboardSnapshotDto(
            now, live.Exists ? sessions.Summary(live) : null, live.Snapshot.Nodes.Count(n => n.Connected), net.MaxPlayers,
            AdminMapping.ToDto(live.Snapshot.Authority), players, entities, Traffic(), captured, 0, alerts.Snapshot());
    }

    private TrafficDto Traffic()
    {
        double Last(string name)
        {
            var series = sampler.GetSeries([name], 1);
            return series.Count > 0 && series[0].Samples.Count > 0 ? series[0].Samples[^1] : 0;
        }

        string[] lanes = ["control", "realtime", "bulk"];
        double bytesIn = lanes.Sum(l => Last("net.bytes_in." + l));
        double bytesOut = lanes.Sum(l => Last("net.bytes_out." + l));
        var dropped = sampler.GetSeries(["net.dropped"], 60);
        long droppedLast60 = dropped.Count > 0 ? (long)Math.Round(dropped[0].Samples.Sum()) : 0;
        return new TrafficDto(bytesIn / 1000.0, bytesOut / 1000.0, Last("net.frames_in"), Last("net.frames_out"), droppedLast60, 0);
    }
}
