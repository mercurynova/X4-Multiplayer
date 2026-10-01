using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Events;
using X4MP.Persistence;

namespace X4MP.Server.Tests;

public class EventBusIntegrationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static List<(long Session, string Type, long? Player, long? Sector, string Data)> SessionEvents(string dataDir)
    {
        using var db = new SqliteConnectionFactory(new PersistenceOptions { DataDir = dataDir }).Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT session_id, type, player_id, sector_id, data_json FROM session_events ORDER BY id";
        using var reader = cmd.ExecuteReader();
        var rows = new List<(long, string, long?, long?, string)>();
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetString(4)));
        }

        return rows;
    }

    private static async Task<T> Eventually<T>(Func<T> read, Func<T, bool> ready)
    {
        for (int i = 0; i < 400; i++)
        {
            var value = read();
            if (ready(value))
            {
                return value;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("not reached");
    }

    [Fact]
    public async Task PublishedEventsReachSessionEventsAndTheAuditLog()
    {
        await using var factory = new AuthFactory();
        _ = factory.NewClient();
        var publisher = factory.Services.GetRequiredService<IEventPublisher>();
        Assert.Same(factory.Services.GetRequiredService<IEventBus>(), publisher);

        publisher.Publish(new GameEventOccurred(T0, 3, "kill", 5, 99, """{"victim":"x"}"""));
        publisher.Publish(new ChatPosted(T0, 3, 5, null, "all", "hello"));
        publisher.Publish(new NodeStatsReported(T0, 3, 5, "p", false, 60)); // not persisted
        publisher.Publish(new AdminActionTaken(T0, 3, "alice", "player.kick", "bob", new Dictionary<string, string?> { ["reason"] = "afk" }, "10.0.0.1"));
        publisher.Publish(new SaveStored(T0, null, "abc123", 42, "authority"));

        var rows = await Eventually(() => SessionEvents(factory.DataDir), r => r.Count >= 4);
        Assert.Equal(["GameEventOccurred", "ChatPosted", "AdminActionTaken", "SaveStored"], rows.Select(r => r.Type).ToArray());
        var kill = rows[0];
        Assert.Equal(3, kill.Session);
        Assert.Equal(5, kill.Player);
        Assert.Equal(99, kill.Sector);
        using var data = JsonDocument.Parse(kill.Data);
        Assert.Equal("kill", data.RootElement.GetProperty("kind").GetString());
        Assert.Equal(0, rows[3].Session); // server-wide event

        var audit = await Eventually(() => factory.AuditRows(), r => r.Any(x => x.Action == "player.kick"));
        var kick = audit.Single(x => x.Action == "player.kick");
        Assert.Equal("alice", kick.Actor);
        Assert.Equal("bob", kick.Target);
        Assert.Contains("afk", kick.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthorityLowFpsAlertFlowsThroughTheHostOnAFakeClock()
    {
        var clock = new FakeTimeProvider(T0);
        await using var baseFactory = new AuthFactory();
        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<TimeProvider>(clock)));
        _ = factory.CreateClient();
        var publisher = factory.Services.GetRequiredService<IEventPublisher>();

        publisher.Publish(new NodeStatsReported(clock.GetUtcNow(), 3, 1, "host", true, 9));
        await Task.Delay(100);
        clock.Advance(TimeSpan.FromSeconds(31));

        var audit = await Eventually(() => baseFactory.AuditRows(), r => r.Any(x => x.Action == "alert.raised"));
        Assert.Equal("authority_fps_low", audit.Single(x => x.Action == "alert.raised").Target);
        var events = await Eventually(() => SessionEvents(baseFactory.DataDir), r => r.Any(x => x.Type == "AlertRaised"));
        Assert.Equal(3, events.Single(x => x.Type == "AlertRaised").Session);
    }
}
