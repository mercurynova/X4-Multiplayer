using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using X4MP.Server.Api;
using X4MP.Server.Auth;

namespace X4MP.Server.Hubs;

/// <summary>
/// The live channel of the admin GUI at <c>/hubs/admin</c> (server-design 4.6). Connecting needs the same credentials as the REST API
/// (cookie, or an API token as bearer header or <c>?access_token=</c>) and at least the Viewer role; <see cref="SendChat"/> needs Admin.
/// A client chooses what it wants with the <c>Subscribe*</c> methods; the <see cref="AdminBroadcaster"/> only builds and sends what
/// someone subscribed to. The method names are mirrored in <see cref="AdminHubMethods"/>.
/// </summary>
[Authorize(Policy = AdminPolicies.Viewer)]
public sealed class AdminHub(AdminHubCore core) : Hub<IAdminClient>
{
    public override Task OnConnectedAsync()
    {
        core.Connected(Context);
        return base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await core.DisconnectedAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Joins the dashboard topic (1 Hz snapshots, player and session changes, transfers, permission denials) and returns the current snapshot.</summary>
    public Task<DashboardSnapshotDto> SubscribeDashboard() => core.SubscribeDashboardAsync(Context.ConnectionId);

    public void UnsubscribeDashboard() => core.Unsubscribe(Context.ConnectionId, HubTopic.Dashboard);

    /// <summary>Joins the galaxy topic (1 Hz <c>GalaxyFrame</c>) and returns the galaxy, or null while none is loaded.</summary>
    public Task<GalaxyDto?> SubscribeGalaxy() => core.SubscribeGalaxyAsync(Context.ConnectionId);

    public void UnsubscribeGalaxy() => core.Unsubscribe(Context.ConnectionId, HubTopic.Galaxy);

    /// <summary>Views a sector (4 Hz <c>SectorFrame</c>); the sector joins the capture set. At most two views per connection.</summary>
    public Task SubscribeSector(uint sectorId) => core.SubscribeSectorAsync(Context.ConnectionId, sectorId);

    public Task UnsubscribeSector(uint sectorId) => core.UnsubscribeSectorAsync(Context.ConnectionId, sectorId);

    /// <summary>Joins the log tail; returns the newest matching lines (up to 500), then live batches follow.</summary>
    public List<LogEntryDto> SubscribeLogs(LogFilterDto? filter) => core.SubscribeLogs(Context.ConnectionId, filter);

    public void UnsubscribeLogs() => core.Unsubscribe(Context.ConnectionId, HubTopic.Logs);

    public void SubscribeDiagnostics() => core.Subscribe(Context.ConnectionId, HubTopic.Diagnostics);

    public void UnsubscribeDiagnostics() => core.Unsubscribe(Context.ConnectionId, HubTopic.Diagnostics);

    public void SubscribeChat() => core.Subscribe(Context.ConnectionId, HubTopic.Chat);

    public void UnsubscribeChat() => core.Unsubscribe(Context.ConnectionId, HubTopic.Chat);

    /// <summary>
    /// Joins the economy topic (wallet, transaction, loan, trade, event, summary and alert pushes) and returns the current overview. Economy changes
    /// themselves are made through REST (<c>/api/v1/economy</c>), so they are validated and audited in one place.
    /// </summary>
    public Task<EconomySummaryDto> SubscribeEconomy() => core.SubscribeEconomyAsync(Context.ConnectionId);

    public void UnsubscribeEconomy() => core.Unsubscribe(Context.ConnectionId, HubTopic.Economy);

    /// <summary>
    /// Joins the teams topic (team, member, relation, settings and awaiting-team pushes) and returns the current state. Team changes themselves
    /// are made through REST (<c>/api/v1/teams</c>), so they are validated and audited in one place.
    /// </summary>
    public Task<TeamsStateDto> SubscribeTeams() => core.SubscribeTeamsAsync(Context.ConnectionId);

    public void UnsubscribeTeams() => core.Unsubscribe(Context.ConnectionId, HubTopic.Teams);

    /// <summary>Sends an admin chat message (same rules and audit as <c>POST /api/v1/chat</c>). Admin role only.</summary>
    [Authorize(Policy = AdminPolicies.Admin)]
    public Task SendChat(SendChatRequest request) => core.SendChatAsync(Context, request);
}
