using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using X4MP.Core.Events;
using X4MP.Core.Interest;
using X4MP.Core.Relay;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;
using X4MP.Server.Api;
using X4MP.Server.Auth;
using X4MP.Server.Hubs;
using AdminApi = X4MP.Server.Tests.Admin.Api;
using X4MP.Server.Tests.Admin;

namespace X4MP.Server.Tests.Hubs;

internal static partial class TestLog
{
    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "other-line-not-wanted")]
    public static partial void Other(ILogger logger);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "marker-wanted {Value}")]
    public static partial void Marker(ILogger logger, int value);
}

/// <summary>The admin hub over a real server and real SignalR clients (M1-S3).</summary>
public sealed class AdminHubTests
{
    private static async Task<T> InvokeAsync<T>(HubConnection connection, string method, params object?[] args) =>
        await connection.InvokeCoreAsync<T>(method, args);

    // ------------------------------------------------------------------ acceptance 2: authentication and roles

    [Fact]
    public async Task ConnectingWithoutCredentialsOrWithABadTokenIsRefused()
    {
        await using var rig = await HubRig.StartAsync();

        var anonymous = rig.Create(role: null);
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => anonymous.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        var bad = rig.Create(token: "x4mp_not-a-real-token");
        var refusedToo = await Assert.ThrowsAsync<HttpRequestException>(() => bad.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, refusedToo.StatusCode);

        Assert.Equal(0, rig.Subscriptions.ConnectionCount);
    }

    [Fact]
    public async Task AViewerCanSubscribeButOnlyAnAdminCanSendChatAndTheSendIsAudited()
    {
        await using var rig = await HubRig.StartAsync();
        var (viewer, _) = await rig.ConnectAsync(AdminRoles.Viewer);
        var (admin, adminRecorder) = await rig.ConnectAsync();

        var snapshot = await InvokeAsync<DashboardSnapshotDto>(viewer, AdminHubMethods.SubscribeDashboard);
        Assert.NotNull(snapshot.Traffic);
        await viewer.InvokeAsync(AdminHubMethods.SubscribeChat);

        var denied = await Assert.ThrowsAsync<HubException>(() => viewer.InvokeAsync(AdminHubMethods.SendChat, new SendChatRequest("hello", "all", null, false)));
        Assert.Contains("unauthorized", denied.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, adminRecorder.Count("Chat"));

        await admin.InvokeAsync(AdminHubMethods.SubscribeChat);
        await admin.InvokeAsync(AdminHubMethods.SendChat, new SendChatRequest("hello from the hub", "all", null, false));
        var chat = await adminRecorder.WaitAsync<ChatMessageDto>("Chat", m => m.Text == "hello from the hub");
        Assert.True(chat.FromAdmin);
        await AdminApi.WaitForAuditAsync(rig.Server, "chat.send", "all");

        var invalid = await Assert.ThrowsAsync<HubException>(() => admin.InvokeAsync(AdminHubMethods.SendChat, new SendChatRequest("", "all", null, false)));
        Assert.Contains("ValidationFailed", invalid.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ acceptance 1: every push type

    [Fact]
    public async Task DashboardPlayersLogsDiagnosticsChatAlertsSettingsAndPermissionPushesArrive()
    {
        await using var rig = await HubRig.StartAsync();
        var (admin, rec) = await rig.ConnectAsync();
        using var http = rig.Server.Http(rig.Server.AdminToken());

        // dashboard: the subscribe call returns the snapshot, then 1 Hz pushes follow (fast here)
        var first = await InvokeAsync<DashboardSnapshotDto>(admin, AdminHubMethods.SubscribeDashboard);
        Assert.Equal(32, first.MaxPlayers);
        await rec.WaitAsync<DashboardSnapshotDto>("Dashboard");
        await admin.InvokeAsync(AdminHubMethods.SubscribeDiagnostics);
        await admin.InvokeAsync(AdminHubMethods.SubscribeChat);
        var backfill = await InvokeAsync<List<LogEntryDto>>(admin, AdminHubMethods.SubscribeLogs, new LogFilterDto("Information", null, "marker-"));
        Assert.NotNull(backfill);

        // players: join, PlayerChanged; the dashboard and diagnostics list the node
        var (node, id) = await JoinAsync(rig, "HubJoin");
        await using var joined = node;
        var changed = await rec.WaitAsync<PlayerLiveDto>("PlayerChanged", p => p.PlayerId == id);
        Assert.Equal("HubJoin", changed.Name);
        await rec.WaitAsync<DashboardSnapshotDto>("Dashboard", d => d.Players.Any(p => p.PlayerId == id) && d.PlayersOnline >= 1);
        var diag = await rec.WaitAsync<List<ConnectionStatsDto>>("Diagnostics", c => c.Any(s => s.Player == "HubJoin"));
        Assert.True(diag.Count >= 1);

        // logs: the line that matches the filter arrives, the one that does not never does
        var logger = rig.Server.Service<ILoggerFactory>().CreateLogger("HubTest");
        TestLog.Other(logger);
        TestLog.Marker(logger, 42);
        var batch = await rec.WaitAsync<List<LogEntryDto>>("LogBatch", b => b.Any(e => e.Message.Contains("marker-wanted", StringComparison.Ordinal)));
        Assert.DoesNotContain(rec.All<List<LogEntryDto>>("LogBatch").SelectMany(b => b), e => e.Message.Contains("other-line-not-wanted", StringComparison.Ordinal));
        Assert.Equal("Information", batch.First(e => e.Message.Contains("marker-wanted", StringComparison.Ordinal)).Level);

        // alerts: raised and cleared notices reach every connected client (no subscription needed)
        var events = rig.Server.Service<IEventPublisher>();
        events.Publish(new AlertRaised(DateTimeOffset.UtcNow, null, AlertSeverity.Warning, "test_alert", "something is off"));
        var raised = await rec.WaitAsync<AlertDto>("Alert", a => a.Code == "test_alert" && a.Active);
        Assert.Equal("Warning", raised.Severity);
        var withAlert = await rec.WaitAsync<DashboardSnapshotDto>("Dashboard", d => d.ActiveAlerts.Any(a => a.Code == "test_alert"));
        Assert.Contains(withAlert.ActiveAlerts, a => a.Text == "something is off");
        events.Publish(new AlertCleared(DateTimeOffset.UtcNow, null, "test_alert"));
        var cleared = await rec.WaitAsync<AlertDto>("Alert", a => a.Code == "test_alert" && !a.Active);
        Assert.Equal("something is off", cleared.Text);

        // settings
        using (var patch = await http.CallAsync(HttpMethod.Patch, "/api/v1/settings", """{"Replication.TickRateHz":33}"""))
        {
            Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());
        }

        var settings = await rec.WaitAsync<SettingsDto>("SettingsChanged");
        Assert.Contains("Replication", settings.Sections.Keys);

        // permission denials (dashboard topic), rate limited
        for (int i = 0; i < 40; i++)
        {
            events.Publish(new PermissionDenied(DateTimeOffset.UtcNow, null, id, 100 + i, "Command", "NotYours", null));
        }

        await rec.WaitAsync<PermissionDeniedDto>("PermissionDenied");
        await Task.Delay(300);
        Assert.InRange(rec.Count("PermissionDenied"), 1, 12); // 5 per second; a window edge may let a second batch in

        // chat from a node reaches the chat topic
        events.Publish(new ChatPosted(DateTimeOffset.UtcNow, null, id, null, "All", "hello from a player"));
        var chat = await rec.WaitAsync<ChatMessageDto>("Chat", m => m.Text == "hello from a player");
        Assert.False(chat.FromAdmin);
        Assert.Equal("HubJoin", chat.From);

        // kicking removes the player from the roster: PlayerRemoved
        using (var kick = await http.PostAsJsonAsync($"/api/v1/players/{id}/kick", new KickRequest("test")))
        {
            Assert.True(kick.IsSuccessStatusCode, await kick.Content.ReadAsStringAsync());
        }

        await rec.WaitForValueAsync("PlayerRemoved", id);
    }

    [Fact]
    public async Task SessionChangeGalaxyFramesSectorFramesAndSaveTransfersArrive()
    {
        await using var rig = await HubRig.StartAsync();
        var (admin, rec) = await rig.ConnectAsync();
        await admin.InvokeAsync(AdminHubMethods.SubscribeDashboard);
        await admin.InvokeAsync(AdminHubMethods.SubscribeGalaxy);

        // the galaxy is not known before an authority sent its metadata
        Assert.Null(await InvokeAsync<GalaxyDto?>(admin, AdminHubMethods.SubscribeGalaxy));

        var authority = await rig.StartAuthorityAsync(megabytes: SaveCiMegabytes);
        _ = authority;

        // session: the phase changes while the authority joins and the first checkpoint lands
        await rec.WaitAsync<SessionSummaryDto>("SessionChanged", s => s.State == "Running");

        // save transfer: the authority's checkpoint upload ran while we were subscribed; its last push has Finished set
        var transfer = await rec.WaitAsync<TransferProgressDto>("SaveTransfer", t => !t.Finished && t.IsUpload);
        Assert.True(transfer.Size > 0);
        await rec.WaitAsync<TransferProgressDto>("SaveTransfer", t => t.Finished && t.Id == transfer.Id);

        // galaxy: the subscribe call now returns the galaxy and 1 Hz frames follow
        var galaxy = await InvokeAsync<GalaxyDto?>(admin, AdminHubMethods.SubscribeGalaxy);
        Assert.NotNull(galaxy);
        Assert.Equal(20, galaxy!.Sectors.Count);
        var frame = await rec.WaitAsync<GalaxyFrameDto>("GalaxyFrame");
        Assert.NotNull(frame.Players);

        // sector: a sector frame arrives for the viewed sector, with consistent parallel arrays
        long sectorId = galaxy.Sectors[0].Id;
        await admin.InvokeAsync(AdminHubMethods.SubscribeSector, (uint)sectorId);
        var sector = await rec.WaitAsync<SectorFrameDto>("SectorFrame", f => f.SectorId == sectorId);
        Assert.Equal(sector.Ids.Count, sector.X.Count);
        Assert.Equal(sector.Ids.Count, sector.Z.Count);
        Assert.Equal(sector.Ids.Count, sector.Yaw.Count);
        Assert.Equal(sector.Ids.Count, sector.Cls.Count);
        Assert.Equal(sector.Ids.Count, sector.Flags.Count);
        Assert.Equal(sector.Ids.Count, sector.PlayerIds.Count);
    }

    private static int SaveCiMegabytes => 12;

    // ------------------------------------------------------------------ acceptance 3: empty topics cost nothing

    [Fact]
    public async Task WithoutSubscribersNoPayloadIsBuilt()
    {
        await using var rig = await HubRig.StartAsync();
        var events = rig.Server.Service<IEventPublisher>();
        var broadcaster = rig.Broadcaster;

        // no connection at all: every kind of event and several timer periods
        PublishNoise(events, playerId: 1);
        await Task.Delay(600);
        Assert.Equal(0, broadcaster.PayloadsBuilt);

        // a connection that subscribed to nothing: still nothing is built for chat, players, permission denials
        var (admin, _) = await rig.ConnectAsync();
        PublishNoise(events, playerId: 1);
        await Task.Delay(600);
        Assert.Equal(0, broadcaster.PayloadsBuilt);
        Assert.Equal(1, rig.Subscriptions.ConnectionCount);

        // subscribing makes the payloads appear; unsubscribing makes them stop
        await admin.InvokeAsync(AdminHubMethods.SubscribeDashboard);
        await admin.InvokeAsync(AdminHubMethods.SubscribeChat);
        events.Publish(new ChatPosted(DateTimeOffset.UtcNow, null, null, "admin", "Admin", "now someone listens"));
        await SaveServerWait(() => broadcaster.PayloadsByKind.GetValueOrDefault("chat") >= 1 && broadcaster.PayloadsByKind.GetValueOrDefault("dashboard") >= 1);

        await admin.InvokeAsync(AdminHubMethods.UnsubscribeDashboard);
        await admin.InvokeAsync(AdminHubMethods.UnsubscribeChat);
        await Task.Delay(300); // a tick that was already running may still finish
        long before = broadcaster.PayloadsBuilt;
        PublishNoise(events, playerId: 1);
        await Task.Delay(600);
        Assert.Equal(before, broadcaster.PayloadsBuilt);
        Assert.Equal(0, rig.Subscriptions.Count(HubTopic.Dashboard));
    }

    private static void PublishNoise(IEventPublisher events, long playerId)
    {
        var now = DateTimeOffset.UtcNow;
        events.Publish(new ChatPosted(now, null, null, "admin", "Admin", "nobody listens"));
        events.Publish(new PermissionDenied(now, null, playerId, 1, "Command", "NotYours", null));
        events.Publish(new PlayerJoined(now, null, playerId, "Ghost", "Client"));
        events.Publish(new PlayerLeft(now, null, playerId, "Ghost", "test"));
        events.Publish(new SessionStateChanged(now, null, "Idle", "Lobby", null));
    }

    private static Task SaveServerWait(Func<bool> condition) =>
        Saves.SaveServer.WaitUntilAsync(condition, 10_000, "broadcaster payloads");


    /// <summary>Connects a fake client node and waits until the session lists it as connected; returns it with its player id.</summary>
    private static async Task<(TcpNodeClient Node, long PlayerId)> JoinAsync(HubRig rig, string name)
    {
        var node = await rig.Server.ConnectAsync(name, Role.Client);
        await rig.Server.WaitForAsync(s => s.Nodes.Any(n => n.Name == name && n.Connected), 10_000, "join " + name);
        return (node, node.Welcome.PlayerId);
    }
}
