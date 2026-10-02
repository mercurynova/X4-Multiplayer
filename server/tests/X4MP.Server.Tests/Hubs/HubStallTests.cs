using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using X4MP.Core.Events;
using X4MP.Server.Api;
using X4MP.Server.Hubs;
using X4MP.Server.Tests.Saves;

namespace X4MP.Server.Tests.Hubs;

/// <summary>
/// A hand-written SignalR client (negotiate, JSON handshake, one invocation) that then never reads from its socket, like a browser tab
/// that froze. The .NET client cannot play this part: it keeps reading into memory even when its handlers block.
/// </summary>
internal sealed class FrozenHubClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();

    public static async Task<FrozenHubClient> ConnectAsync(HubRig rig, string topicMethod)
    {
        string token = rig.Server.AdminToken();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{rig.Server.HttpPort}") };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var negotiate = await http.PostAsync(X4MP.Server.Api.AdminHubMethods.Route + "/negotiate?negotiateVersion=1", null);
        negotiate.EnsureSuccessStatusCode();
        string connectionToken = (await negotiate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("connectionToken").GetString()!;

        var client = new FrozenHubClient();
        await client._socket.ConnectAsync(
            new Uri($"ws://127.0.0.1:{rig.Server.HttpPort}{X4MP.Server.Api.AdminHubMethods.Route}?id={Uri.EscapeDataString(connectionToken)}&access_token={Uri.EscapeDataString(token)}"),
            CancellationToken.None);
        await client.SendAsync("""{"protocol":"json","version":1}""");
        var buffer = new byte[1024];
        await client._socket.ReceiveAsync(buffer, CancellationToken.None); // the handshake response
        await client.SendAsync($$"""{"type":1,"invocationId":"1","target":"{{topicMethod}}","arguments":[]}""");
        await client._socket.ReceiveAsync(buffer, CancellationToken.None); // the completion; from here on we never read again
        return client;
    }

    private Task SendAsync(string json) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(json + "\u001e"), WebSocketMessageType.Text, true, CancellationToken.None);

    public ValueTask DisposeAsync()
    {
        _socket.Abort();
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A browser that stops reading must not slow the event bus, the session actor or another browser (acceptance 5).</summary>
public sealed class HubStallTests
{
    [Fact]
    public async Task AFrozenBrowserDoesNotBlockTheBusTheActorOrAHealthyBrowserAndIsClosedAfterTheStallTimeout()
    {
        await using var rig = await HubRig.StartAsync("--X4MP:AdminHub:StallSeconds=2");

        await using var frozen = await FrozenHubClient.ConnectAsync(rig, AdminHubMethods.SubscribeChat);
        var (healthy, recorder) = await rig.ConnectAsync();
        await healthy.InvokeAsync(AdminHubMethods.SubscribeChat);
        Assert.Equal(2, rig.Subscriptions.Count(HubTopic.Chat));

        var events = rig.Server.Service<IEventPublisher>();
        string big = new('x', 300_000);
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            events.Publish(new ChatPosted(DateTimeOffset.UtcNow, null, null, "admin", "Admin", i.ToString("D3", CultureInfo.InvariantCulture) + big));
        }

        clock.Stop();
        Assert.True(clock.ElapsedMilliseconds < 2000, $"publishing 100 events took {clock.ElapsedMilliseconds} ms");

        // the actor still answers promptly while the frozen client holds up its own pump
        clock.Restart();
        await rig.Server.Actor.GetSnapshotAsync();
        Assert.True(clock.ElapsedMilliseconds < 1000, $"actor round trip took {clock.ElapsedMilliseconds} ms");

        // the healthy browser gets every message, in order
        await recorder.WaitAsync<ChatMessageDto>("Chat", m => m.Text.StartsWith("099", StringComparison.Ordinal), timeoutMs: 60_000);
        var texts = recorder.All<ChatMessageDto>("Chat").Select(m => m.Text[..3]).ToList();
        Assert.Equal(Enumerable.Range(0, 100).Select(i => i.ToString("D3", CultureInfo.InvariantCulture)), texts);

        // the frozen one is dropped by the server after the stall timeout; its subscription goes with it, the healthy one stays
        await SaveServer.WaitUntilAsync(() => rig.Subscriptions.ConnectionCount == 1, 30_000, "frozen connection removed");
        Assert.Equal(1, rig.Subscriptions.Count(HubTopic.Chat));
        Assert.Equal(HubConnectionState.Connected, healthy.State);
        var warnings = rig.Server.Service<X4MP.Server.Logging.RingBufferSink>().Snapshot().Where(e => e.Level >= Serilog.Events.LogEventLevel.Warning);
        Assert.Contains(warnings, e => e.RenderMessage(CultureInfo.InvariantCulture).Contains("stopped reading", StringComparison.Ordinal));

        // the bus subscriber of the hub never dropped an event
        Assert.Equal(0, rig.Server.Service<IEventBus>().Subscriptions.First(s => s.Name == "admin-hub").Dropped);
    }
}
