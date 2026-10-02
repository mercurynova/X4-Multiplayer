using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using X4MP.Core.Economy;
using X4MP.Core.Events;
using X4MP.Core.Interest;
using X4MP.Core.Relay;
using X4MP.Core.Saves;
using X4MP.Core.Session;
using X4MP.Core.World;
using X4MP.Protocol;
using X4MP.Server.Admin;
using X4MP.Server.Api;
using X4MP.Server.Economy;
using X4MP.Server.Logging;
using X4MP.Server.Settings;

namespace X4MP.Server.Hubs;

/// <summary>
/// Pushes live data to the admin hub's clients (server-design 4.6). It owns every push: it subscribes to the event bus with its own bounded
/// queue (the bus never waits for it), runs one timer per periodic topic, and looks at <see cref="AdminSubscriptions"/> first, so a topic
/// without subscribers builds nothing (<see cref="PayloadsBuilt"/> stays put). Sends go through each connection's <see cref="ClientPump{TClient}"/>:
/// a slow browser loses frames or events, never delays another browser, the event bus or the session actor.
/// </summary>
public sealed partial class AdminBroadcaster : BackgroundService
{
    private readonly IEventBus _bus;
    private readonly AdminSubscriptions _subs;
    private readonly SessionActor _actor;
    private readonly AdminSessions _sessions;
    private readonly DashboardBuilder _dashboard;
    private readonly WorldMirror _mirror;
    private readonly InterestManager _interest;
    private readonly SaveService _saves;
    private readonly RingBufferSink _ring;
    private readonly SettingsService _settings;
    private readonly ActiveAlerts _alerts;
    private readonly EconomyModule _economy;
    private readonly EconomyViews _economyViews;
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AdminHubOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AdminBroadcaster> _logger;

    private readonly ConcurrentDictionary<string, long> _built = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, bool> _dirtyPlayers = new(); // value true = the player left
    private readonly Channel<bool> _dirtySignal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<uint, TransferProgressDto> _seenTransfers = [];
    private readonly Dictionary<ushort, (long Stamp, List<PersistentRecord> Items)> _persistentCache = [];
    private long _payloadsBuilt;
    private long _chatId;
    private int _sessionDirty;
    private long _deniedWindowStart;
    private int _deniedInWindow;

    internal AdminBroadcaster(
        IEventBus bus,
        AdminSubscriptions subs,
        SessionActor actor,
        AdminSessions sessions,
        DashboardBuilder dashboard,
        WorldMirror mirror,
        InterestManager interest,
        SaveService saves,
        RingBufferSink ring,
        SettingsService settings,
        ActiveAlerts alerts,
        EconomyModule economy,
        EconomyViews economyViews,
        IServiceProvider services,
        IOptionsMonitor<AdminHubOptions> options,
        TimeProvider time,
        ILogger<AdminBroadcaster> logger)
    {
        _bus = bus;
        _subs = subs;
        _actor = actor;
        _sessions = sessions;
        _dashboard = dashboard;
        _mirror = mirror;
        _interest = interest;
        _saves = saves;
        _ring = ring;
        _settings = settings;
        _alerts = alerts;
        _economy = economy;
        _economyViews = economyViews;
        _services = services;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Payload objects built for pushes since start (the spy of the "empty topics cost nothing" rule).</summary>
    public long PayloadsBuilt => Interlocked.Read(ref _payloadsBuilt);

    /// <summary>Payloads built per push kind (<c>dashboard</c>, <c>sector</c>, ...).</summary>
    public IReadOnlyDictionary<string, long> PayloadsByKind => _built;

    /// <summary>The topics' subscriber counts and connections.</summary>
    public AdminSubscriptions Subscriptions => _subs;

    private void Built(string kind)
    {
        Interlocked.Increment(ref _payloadsBuilt);
        _built.AddOrUpdate(kind, 1, static (_, n) => n + 1);
    }

    // ------------------------------------------------------------------ lifecycle

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;
        await using var subscription = _bus.Subscribe(
            "admin-hub", OnEvent, new SubscriberOptions { Capacity = 1024, DropPolicy = EventDropPolicy.DropOldest });
        AttachEconomy();
        Action<IReadOnlyList<string>> onSettings = OnSettingsChanged;
        _settings.Changed += onSettings;
        try
        {
            await Task.WhenAll(
                Loop(options.DashboardIntervalMs, DashboardTickAsync, stoppingToken),
                Loop(options.GalaxyIntervalMs, GalaxyTickAsync, stoppingToken),
                Loop(options.SectorIntervalMs, SectorTickAsync, stoppingToken),
                Loop(options.DiagnosticsIntervalMs, DiagnosticsTickAsync, stoppingToken),
                Loop(options.LogBatchIntervalMs, LogsTickAsync, stoppingToken),
                Loop(options.TransferIntervalMs, TransfersTickAsync, stoppingToken),
                Loop(options.EconomyWalletIntervalMs, EconomyFlushAsync, stoppingToken),
                Loop(options.EconomySummaryIntervalMs, EconomySummaryTickAsync, stoppingToken),
                DirtyLoop(stoppingToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        finally
        {
            _settings.Changed -= onSettings;
            DetachEconomy();
        }
    }

    private async Task Loop(int intervalMs, Func<Task> tick, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(10, intervalMs)), _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await tick().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogTickFailed(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    // ------------------------------------------------------------------ helpers

    private static void PostTo(IEnumerable<HubClient> clients, Func<IAdminClient, Task> send, string? key = null)
    {
        foreach (var client in clients)
        {
            client.Pump.Post(send, key);
        }
    }

    // ------------------------------------------------------------------ periodic pushes

    private async Task DashboardTickAsync()
    {
        if (_subs.Count(HubTopic.Dashboard) == 0)
        {
            return;
        }

        var snapshot = await _dashboard.BuildAsync().ConfigureAwait(false);
        Built("dashboard");
        PostTo(_subs.In(HubTopic.Dashboard), c => c.Dashboard(snapshot), "dashboard");
    }

    private async Task DiagnosticsTickAsync()
    {
        if (_subs.Count(HubTopic.Diagnostics) == 0)
        {
            return;
        }

        var stats = await ConnectionStatsBuilder.BuildAsync(_sessions, _services).ConfigureAwait(false);
        Built("diagnostics");
        PostTo(_subs.In(HubTopic.Diagnostics), c => c.Diagnostics(stats), "diagnostics");
    }

    private async Task GalaxyTickAsync()
    {
        if (_subs.Count(HubTopic.Galaxy) == 0)
        {
            return;
        }

        var frame = await _actor.CallAsync(BuildGalaxyFrame).ConfigureAwait(false);
        Built("galaxy");
        PostTo(_subs.In(HubTopic.Galaxy), c => c.GalaxyFrame(frame), "galaxy");
    }

    /// <summary>Runs on the actor thread: copies what the map needs out of the mirror and the interest manager.</summary>
    private GalaxyFrameDto BuildGalaxyFrame()
    {
        var snapshot = _actor.Snapshot;
        var names = snapshot.Nodes.ToDictionary(n => n.PlayerId, n => n.Name);
        var players = new List<GalaxyPlayerDto>();
        foreach (var ship in _mirror.PlayerShips)
        {
            double yawDeg = Quantize.RotationToRadians(ship.Yaw) * 180.0 / Math.PI;
            players.Add(new GalaxyPlayerDto(
                ship.PlayerId,
                names.GetValueOrDefault(ship.PlayerId) ?? ship.PlayerId.ToString(CultureInfo.InvariantCulture),
                ship.Sector,
                new Vec3Dto(Quantize.PositionToMetres(ship.Px), Quantize.PositionToMetres(ship.Py), Quantize.PositionToMetres(ship.Pz)),
                ((yawDeg % 360) + 360) % 360));
        }

        var agg = _mirror.Summary.Values
            .OrderBy(s => s.Sector)
            .Select(s => new SectorAggDto(s.Sector, s.ShipsXs + s.ShipsS + s.ShipsM + s.ShipsL + s.ShipsXl, s.Stations))
            .ToList();

        var interest = new List<InterestEntryDto>();
        foreach (var node in snapshot.Nodes)
        {
            foreach (var group in _interest.Subscriptions(node.PlayerId).GroupBy(s => s.Tier).OrderBy(g => g.Key))
            {
                interest.Add(new InterestEntryDto(node.PlayerId, [.. group.Select(s => (long)s.Sector).Order()], group.Key.ToString()));
            }
        }

        return new GalaxyFrameDto(_time.GetUtcNow(), players, agg, interest);
    }

    private async Task SectorTickAsync()
    {
        if (_subs.SectorViewCount == 0)
        {
            return;
        }

        var viewed = _subs.ViewedSectors();
        if (viewed.Count == 0)
        {
            return;
        }

        var maxEntities = _options.CurrentValue.MapMaxEntities;
        var frames = await _actor.CallAsync(() => viewed.Select(s => BuildSectorFrame(s, maxEntities)).ToList()).ConfigureAwait(false);
        foreach (var frame in frames)
        {
            Built("sector");
            ushort sector = (ushort)frame.SectorId;
            var clients = _subs.Clients.Where(c => c.Sectors().Contains(sector));
            PostTo(clients, c => c.SectorFrame(frame), "sector:" + frame.SectorId.ToString(CultureInfo.InvariantCulture));
        }
    }

    private readonly record struct PersistentRecord(uint NetId, byte Kind, int Px, int Pz, short Yaw, ushort Flags, ushort Controller);

    /// <summary>
    /// Runs on the actor thread. Players first, then persistent entities (stations, gates: cached for a second, the scan is O(mirror)),
    /// then the transient ones, up to <paramref name="maxEntities"/>.
    /// </summary>
    private SectorFrameDto BuildSectorFrame(ushort sector, int maxEntities)
    {
        var ids = new List<long>();
        var xs = new List<int>();
        var zs = new List<int>();
        var yaws = new List<int>();
        var classes = new List<int>();
        var flags = new List<int>();
        var playerIds = new List<long?>();
        var seen = new HashSet<uint>();

        void Add(uint netId, byte kind, int px, int pz, short yaw, ushort flag, long? player)
        {
            seen.Add(netId);
            ids.Add(netId);
            xs.Add(Round10(Quantize.PositionToMetres(px)));
            zs.Add(Round10(Quantize.PositionToMetres(pz)));
            double deg = Quantize.RotationToRadians(yaw) * 180.0 / Math.PI;
            yaws.Add((int)Math.Round(((deg % 360) + 360) % 360));
            classes.Add(kind);
            flags.Add(flag);
            playerIds.Add(player);
        }

        foreach (var ship in _mirror.PlayerShips)
        {
            if (ship.Sector == sector)
            {
                byte kind = _mirror.TryGet(ship.NetId, out var entity) ? (byte)entity.Kind : (byte)0;
                Add(ship.NetId, kind, ship.Px, ship.Pz, ship.Yaw, ship.Flags, ship.PlayerId);
            }
        }

        long now = _time.GetTimestamp();
        if (!_persistentCache.TryGetValue(sector, out var cached) || _time.GetElapsedTime(cached.Stamp, now) > TimeSpan.FromSeconds(1))
        {
            var items = new List<PersistentRecord>();
            foreach (var e in _mirror.All)
            {
                if (e.IsPersistent && e.Sector == sector)
                {
                    items.Add(new PersistentRecord(e.NetId, (byte)e.Kind, e.Px, e.Pz, e.Yaw, e.Flags, e.ControllerPlayer));
                }
            }

            cached = (now, items);
            _persistentCache[sector] = cached;
        }

        foreach (var p in cached.Items)
        {
            if (!seen.Contains(p.NetId))
            {
                Add(p.NetId, p.Kind, p.Px, p.Pz, p.Yaw, p.Flags, p.Controller == 0 ? null : p.Controller);
            }
        }

        foreach (var e in _mirror.TransientIn(sector))
        {
            if (ids.Count >= maxEntities)
            {
                break;
            }

            if (!seen.Contains(e.NetId))
            {
                Add(e.NetId, (byte)e.Kind, e.Px, e.Pz, e.Yaw, e.Flags, e.ControllerPlayer == 0 ? null : e.ControllerPlayer);
            }
        }

        return new SectorFrameDto(sector, _mirror.AuthorityTick, ids, xs, zs, yaws, classes, flags, playerIds);
    }

    private static int Round10(double metres) => (int)(Math.Round(metres / 10.0) * 10);

    private Task LogsTickAsync()
    {
        if (_subs.Count(HubTopic.Logs) == 0)
        {
            return Task.CompletedTask;
        }

        var clients = _subs.In(HubTopic.Logs).ToList();
        long total = _ring.TotalWritten;
        if (clients.All(c => c.LogCursor >= total))
        {
            return Task.CompletedTask;
        }

        var (firstSeq, events) = _ring.SnapshotWithSequence();
        int max = Math.Max(1, _options.CurrentValue.LogBatchMax);
        foreach (var client in clients)
        {
            var (batch, cursor) = LogTail.Since(events, firstSeq, client.LogCursor, client.LogFilter, max);
            client.LogCursor = cursor;
            if (batch.Count > 0)
            {
                Built("logs");
                client.Pump.Post(c => c.LogBatch(batch));
            }
        }

        return Task.CompletedTask;
    }

    private Task TransfersTickAsync()
    {
        if (_subs.Count(HubTopic.Dashboard) == 0)
        {
            _seenTransfers.Clear();
            return Task.CompletedTask;
        }

        var current = _saves.Transfers;
        if (current.Count == 0 && _seenTransfers.Count == 0)
        {
            return Task.CompletedTask;
        }

        var clients = _subs.In(HubTopic.Dashboard).ToList();
        var now = new HashSet<uint>();
        foreach (var t in current)
        {
            now.Add(t.Id);
            var dto = ToDto(t, finished: false);
            _seenTransfers[t.Id] = dto;
            Built("transfer");
            PostTo(clients, c => c.SaveTransfer(dto), "transfer:" + t.Id.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var id in _seenTransfers.Keys.Where(id => !now.Contains(id)).ToList())
        {
            var final = _seenTransfers[id] with { Finished = true };
            _seenTransfers.Remove(id);
            Built("transfer");
            PostTo(clients, c => c.SaveTransfer(final), "transfer:" + id.ToString(CultureInfo.InvariantCulture));
        }

        return Task.CompletedTask;
    }

    private static TransferProgressDto ToDto(TransferSnapshot t, bool finished) => new(
        t.Id, t.IsUpload, t.PlayerId, t.PlayerName, t.Sha256, t.Kind.ToString(), t.Size, t.Done, t.StartOffset, t.StartedAt, finished);

    // ------------------------------------------------------------------ event pushes

    private ValueTask OnEvent(DomainEvent domainEvent, CancellationToken ct)
    {
        switch (domainEvent)
        {
            case AlertRaised or AlertCleared:
                var alert = _alerts.Apply(domainEvent);
                if (alert is not null && _subs.ConnectionCount > 0)
                {
                    Built("alert");
                    PostTo(_subs.Clients, c => c.Alert(alert));
                }

                if (alert is not null && alert.Code.StartsWith("economy", StringComparison.Ordinal) && _subs.Count(HubTopic.Economy) > 0)
                {
                    Built("economy-alert");
                    PostTo(_subs.In(HubTopic.Economy), c => c.EconomyAlert(alert));
                }

                break;

            case var economy when _subs.Count(HubTopic.Economy) > 0 && EconomyViews.IsEconomyEvent(economy):
                PushEconomyEvent(economy);
                break;

            case ChatPosted chat when _subs.Count(HubTopic.Chat) > 0:
                var message = ToDto(chat);
                Built("chat");
                PostTo(_subs.In(HubTopic.Chat), c => c.Chat(message));
                break;

            case PermissionDenied denied when _subs.Count(HubTopic.Dashboard) > 0 && AllowDenied():
                var dto = new PermissionDeniedDto(denied.At, denied.PlayerId, denied.EntityId, denied.Action, denied.Reason);
                Built("permission");
                PostTo(_subs.In(HubTopic.Dashboard), c => c.PermissionDenied(dto));
                break;

            case PlayerJoined e when _subs.Count(HubTopic.Dashboard) > 0:
                MarkPlayer(e.PlayerId, left: false);
                break;
            case PlayerResumed e when _subs.Count(HubTopic.Dashboard) > 0:
                MarkPlayer(e.PlayerId, left: false);
                break;
            case PlayerDetached e when _subs.Count(HubTopic.Dashboard) > 0:
                MarkPlayer(e.PlayerId, left: false);
                break;
            case NodePhaseChanged e when _subs.Count(HubTopic.Dashboard) > 0:
                MarkPlayer(e.PlayerId, left: false);
                break;
            case PlayerLeft e when _subs.Count(HubTopic.Dashboard) > 0:
                MarkPlayer(e.PlayerId, left: true);
                break;

            case SessionStateChanged or AuthorityChanged when _subs.Count(HubTopic.Dashboard) > 0:
                Interlocked.Exchange(ref _sessionDirty, 1);
                _dirtySignal.Writer.TryWrite(true);
                break;

            default:
                break;
        }

        return ValueTask.CompletedTask;
    }

    private void MarkPlayer(long playerId, bool left)
    {
        _dirtyPlayers[playerId] = left;
        _dirtySignal.Writer.TryWrite(true);
    }

    /// <summary>At most <c>PermissionDeniedPerSecond</c> pushes per second; the rest are dropped.</summary>
    private bool AllowDenied()
    {
        long now = _time.GetTimestamp();
        lock (_dirtySignal)
        {
            if (_deniedWindowStart == 0 || _time.GetElapsedTime(_deniedWindowStart, now) >= TimeSpan.FromSeconds(1))
            {
                _deniedWindowStart = now;
                _deniedInWindow = 0;
            }

            return ++_deniedInWindow <= _options.CurrentValue.PermissionDeniedPerSecond;
        }
    }

    private ChatMessageDto ToDto(ChatPosted chat)
    {
        string from = chat.FromAdmin
            ?? (chat.FromPlayerId is { } id
                ? _actor.Snapshot.Nodes.FirstOrDefault(n => n.PlayerId == id)?.Name ?? "Player " + id.ToString(CultureInfo.InvariantCulture)
                : "System");
        // Live pushes have no database id yet: they carry a negative local number (history rows from GET /chat are positive).
        return new ChatMessageDto(-Interlocked.Increment(ref _chatId), chat.At, from, chat.FromAdmin is not null, chat.Channel, chat.Text);
    }

    private void OnSettingsChanged(IReadOnlyList<string> keys)
    {
        _ = keys;
        if (_subs.ConnectionCount == 0)
        {
            return;
        }

        var values = _settings.GetValues();
        Built("settings");
        PostTo(_subs.Clients, c => c.SettingsChanged(values));
    }

    // ------------------------------------------------------------------ player and session changes

    private async Task DirtyLoop(CancellationToken ct)
    {
        try
        {
            while (await _dirtySignal.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                _dirtySignal.Reader.TryRead(out _);
                await Task.Delay(TimeSpan.FromMilliseconds(50), _time, ct).ConfigureAwait(false); // let a burst of changes become one snapshot
                try
                {
                    await FlushDirtyAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogTickFailed(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task FlushDirtyAsync()
    {
        bool sessionDirty = Interlocked.Exchange(ref _sessionDirty, 0) == 1;
        var players = new List<KeyValuePair<long, bool>>();
        foreach (var key in _dirtyPlayers.Keys)
        {
            if (_dirtyPlayers.TryRemove(key, out var left))
            {
                players.Add(new(key, left));
            }
        }

        if ((players.Count == 0 && !sessionDirty) || _subs.Count(HubTopic.Dashboard) == 0)
        {
            return;
        }

        var live = await _sessions.GetLiveAsync().ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var clients = _subs.In(HubTopic.Dashboard).ToList();
        foreach (var (playerId, left) in players)
        {
            var node = live.Snapshot.Nodes.FirstOrDefault(n => n.PlayerId == playerId);
            if (left || node is null)
            {
                Built("player-removed");
                PostTo(clients, c => c.PlayerRemoved(playerId));
            }
            else
            {
                var dto = AdminMapping.ToLive(node, now, live.Muted);
                Built("player");
                PostTo(clients, c => c.PlayerChanged(dto), "player:" + playerId.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (sessionDirty)
        {
            var summary = _sessions.Summary(live);
            Built("session");
            PostTo(clients, c => c.SessionChanged(summary), "session");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "admin hub push failed")]
    private partial void LogTickFailed(Exception ex);
}
