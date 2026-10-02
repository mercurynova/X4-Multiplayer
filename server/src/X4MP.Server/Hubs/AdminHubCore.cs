using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using X4MP.Core.Interest;
using X4MP.Core.Relay;
using X4MP.Core.Session;
using X4MP.Core.World;
using X4MP.Server.Admin;
using X4MP.Server.Api;
using X4MP.Server.Auth;
using X4MP.Server.Logging;

namespace X4MP.Server.Hubs;

/// <summary>
/// The work behind the hub methods: connection bookkeeping, subscriptions and admin interest. <see cref="AdminHub"/> is a thin shell
/// over it (SignalR creates hubs per call, and hub constructors must only take public types).
/// </summary>
public sealed class AdminHubCore
{
    private const string ViewPrefix = "hub:";

    private readonly AdminSubscriptions _subscriptions;
    private readonly IHubContext<AdminHub, IAdminClient> _hub;
    private readonly SessionActor _actor;
    private readonly InterestManager _interest;
    private readonly WorldMirror _mirror;
    private readonly AdminSessions _sessions;
    private readonly DashboardBuilder _dashboard;
    private readonly RingBufferSink _ring;
    private readonly IChatControl _chat;
    private readonly AdminStore _audit;
    private readonly IOptionsMonitor<AdminHubOptions> _options;
    private readonly ILogger<AdminHubCore> _logger;

    internal AdminHubCore(
        AdminSubscriptions subscriptions,
        IHubContext<AdminHub, IAdminClient> hub,
        SessionActor actor,
        InterestManager interest,
        WorldMirror mirror,
        AdminSessions sessions,
        DashboardBuilder dashboard,
        RingBufferSink ring,
        IChatControl chat,
        AdminStore audit,
        IOptionsMonitor<AdminHubOptions> options,
        ILogger<AdminHubCore> logger)
    {
        _subscriptions = subscriptions;
        _hub = hub;
        _actor = actor;
        _interest = interest;
        _mirror = mirror;
        _sessions = sessions;
        _dashboard = dashboard;
        _ring = ring;
        _chat = chat;
        _audit = audit;
        _options = options;
        _logger = logger;
    }

    public AdminSubscriptions Subscriptions => _subscriptions;

    // ------------------------------------------------------------------ connections

    public void Connected(HubCallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = _options.CurrentValue;
        var pump = new ClientPump<IAdminClient>(
            _hub.Clients.Client(context.ConnectionId),
            options.ClientQueueCapacity,
            TimeSpan.FromSeconds(options.StallSeconds),
            () =>
            {
                LogStalled(context.ConnectionId);
                context.Abort();
            });
        bool isAdmin = context.User?.IsInRole(AdminRoles.Admin) ?? false;
        _subscriptions.Add(new HubClient(context.ConnectionId, context, pump, isAdmin));
    }

    public async Task DisconnectedAsync(string connectionId)
    {
        var client = _subscriptions.Remove(connectionId);
        if (client is null)
        {
            return;
        }

        await client.Pump.DisposeAsync().ConfigureAwait(false);
        await SetAdminViewAsync(connectionId, []).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ topics

    public async Task<DashboardSnapshotDto> SubscribeDashboardAsync(string connectionId)
    {
        _subscriptions.Join(Client(connectionId), HubTopic.Dashboard);
        return await _dashboard.BuildAsync().ConfigureAwait(false);
    }

    public void Unsubscribe(string connectionId, HubTopic topic) => _subscriptions.Leave(Client(connectionId), topic);

    public async Task<GalaxyDto?> SubscribeGalaxyAsync(string connectionId)
    {
        _subscriptions.Join(Client(connectionId), HubTopic.Galaxy);
        return await GalaxyDtoBuilder.BuildAsync(_sessions, _mirror).ConfigureAwait(false);
    }

    public void Subscribe(string connectionId, HubTopic topic) => _subscriptions.Join(Client(connectionId), topic);

    /// <summary>Joins the log tail and returns the newest lines (the backfill); live batches continue after them.</summary>
    public List<LogEntryDto> SubscribeLogs(string connectionId, LogFilterDto? filter)
    {
        var client = Client(connectionId);
        var parsed = LogTail.Parse(filter, out var error) ?? throw new HubException(error);
        client.LogFilter = parsed;
        var (entries, lastSeq) = LogTail.Backfill(_ring, parsed, _options.CurrentValue.LogBackfill);
        client.LogCursor = lastSeq;
        _subscriptions.Join(client, HubTopic.Logs);
        return entries;
    }

    // ------------------------------------------------------------------ sector views (admin interest)

    public async Task SubscribeSectorAsync(string connectionId, uint sectorId)
    {
        if (sectorId is 0 or > ushort.MaxValue)
        {
            throw new HubException("InvalidSector: sector ids are 1 to 65535.");
        }

        var client = Client(connectionId);
        ushort sector = (ushort)sectorId;
        var known = await _actor.CallAsync(() => _mirror.Galaxy.Current is not { } model || model.Find(sector) is not null).ConfigureAwait(false);
        if (!known)
        {
            throw new HubException("UnknownSector: the galaxy has no such sector.");
        }

        int max = _options.CurrentValue.MaxSectorsPerAdmin;
        if (client.AddSector(sector, max, out bool limitHit))
        {
            _subscriptions.SectorAdded();
            await SetAdminViewAsync(connectionId, client.Sectors()).ConfigureAwait(false);
        }
        else if (limitHit)
        {
            throw new HubException($"TooManySectors: at most {max} sector views per admin; unsubscribe one first.");
        }
    }

    public async Task UnsubscribeSectorAsync(string connectionId, uint sectorId)
    {
        if (sectorId is 0 or > ushort.MaxValue)
        {
            return;
        }

        var client = Client(connectionId);
        if (client.RemoveSector((ushort)sectorId))
        {
            _subscriptions.SectorRemoved();
            await SetAdminViewAsync(connectionId, client.Sectors()).ConfigureAwait(false);
        }
    }

    /// <summary>Registers (or, with an empty list, removes) this connection's admin view in the interest manager, on the actor thread.</summary>
    private async Task SetAdminViewAsync(string connectionId, IReadOnlyCollection<ushort> sectors)
    {
        try
        {
            await _actor.CallAsync(() =>
            {
                _interest.SetAdminView(ViewPrefix + connectionId, sectors);
                return true;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the actor is shutting down: its views go with it
        }
    }

    // ------------------------------------------------------------------ chat

    public async Task SendChatAsync(HubCallerContext context, SendChatRequest? request)
    {
        ArgumentNullException.ThrowIfNull(context);
        string actor = context.User?.Identity?.Name ?? "unknown";
        string? ip = context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString();
        var result = await ChatSender.SendAsync(request, actor, ip, _sessions, _chat, _audit).ConfigureAwait(false);
        if (result.Errors is not null)
        {
            throw new HubException("ValidationFailed: " + string.Join(" ", result.Errors.Select(e => e.Key + ": " + string.Join(' ', e.Value))));
        }

        if (result.ConflictCode is not null)
        {
            throw new HubException(result.ConflictCode + ": " + result.ConflictMessage);
        }
    }

    private HubClient Client(string connectionId) =>
        _subscriptions.Find(connectionId) ?? throw new HubException("Unknown connection.");

    private void LogStalled(string connectionId) => AdminHubLog.Stalled(_logger, connectionId);
}

internal static partial class AdminHubLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "admin hub client {ConnectionId} stopped reading; closing its connection")]
    public static partial void Stalled(ILogger logger, string connectionId);
}
