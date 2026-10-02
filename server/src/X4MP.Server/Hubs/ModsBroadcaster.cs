using Microsoft.Extensions.Hosting;
using X4MP.Core.Mods;
using X4MP.Proto;
using X4MP.Server.Api;
using X4MP.Server.Mods;

namespace X4MP.Server.Hubs;

/// <summary>
/// The mods topic of the admin hub (task M1-X4). A policy change pushes <c>ModPolicyChanged</c> to every subscriber (the per-mod player counts only for clients that may see
/// players' mod lists) followed by <c>PlayerModsReported</c> for each player, whose status against the new policy may have changed. A stored extension report pushes
/// <c>PlayerModsReported</c> for that player to the clients that may see it. Nothing is built while the topic has no subscribers; the work is done off the thread that raised
/// the event (a handshake, a REST call) so neither waits for the hub.
/// </summary>
internal sealed partial class ModsBroadcaster(AdminSubscriptions subs, ModViews views, ILogger<ModsBroadcaster> logger) : IHostedService
{
    // One consumer, so pushes leave in the order the events came (a newer policy is never overtaken by an older one).
    private readonly System.Threading.Channels.Channel<Action> _work = System.Threading.Channels.Channel.CreateUnbounded<Action>(
        new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

    private Task _loop = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        views.Policy.Changed += OnPolicyChanged;
        views.Store.ReportRecorded += OnReport;
        _loop = Task.Run(async () =>
        {
            await foreach (var work in _work.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                Run(work);
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        views.Policy.Changed -= OnPolicyChanged;
        views.Store.ReportRecorded -= OnReport;
        _work.Writer.TryComplete();
        await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnPolicyChanged(ModPolicyT policy)
    {
        _ = policy; // the newest policy is read when the push runs
        if (subs.Count(HubTopic.Mods) > 0)
        {
            _work.Writer.TryWrite(() => PushPolicy(views.Policy.Current));
        }
    }

    private void OnReport(ExtensionReportRecord report)
    {
        if (report.PlayerId != 0 && subs.Count(HubTopic.Mods) > 0) // a refusal bound to no player has no row to update
        {
            _work.Writer.TryWrite(() => PushReport(report));
        }
    }

    private void Run(Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            LogPushFailed(ex);
        }
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "pushing to the mods topic failed")]
    private partial void LogPushFailed(Exception ex);

    private void PushPolicy(ModPolicyT policy)
    {
        var clients = subs.In(HubTopic.Mods).ToList();
        if (clients.Count == 0)
        {
            return;
        }

        var reports = views.Store.LatestReports();
        ModPolicyDto? withPlayers = null;
        ModPolicyDto? withoutPlayers = null;
        List<PlayerModStatusDto>? statuses = null;
        foreach (var client in clients)
        {
            var access = views.Access(client.Context.User);
            ModPolicyDto dto = access.CanSeePlayers
                ? withPlayers ??= views.PolicyDto(policy, reports, access)
                : withoutPlayers ??= views.PolicyDto(policy, reports, access);
            client.Pump.Post(c => c.ModPolicyChanged(dto), "mods-policy");
            if (!access.CanSeePlayers)
            {
                continue;
            }

            statuses ??= [.. reports.Select(r => views.PlayerStatus(r))];
            foreach (var status in statuses)
            {
                var s = status;
                client.Pump.Post(c => c.PlayerModsReported(s), "mods-player:" + s.PlayerId);
            }
        }
    }

    private void PushReport(ExtensionReportRecord report)
    {
        PlayerModStatusDto? status = null;
        foreach (var client in subs.In(HubTopic.Mods).ToList())
        {
            if (!views.Access(client.Context.User).CanSeePlayers)
            {
                continue;
            }

            status ??= views.PlayerStatus(report);
            var s = status;
            client.Pump.Post(c => c.PlayerModsReported(s), "mods-player:" + s.PlayerId);
        }
    }
}
