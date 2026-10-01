using Serilog;
using Serilog.Events;
using X4MP.Server.Logging;

namespace X4MP.Server.Tests;

public class LoggingTests
{
    private static LogEvent Event(string message) =>
        new(DateTimeOffset.UtcNow, LogEventLevel.Information, null,
            new MessageTemplate(message, []), []);

    private static (ILogger Logger, RingBufferSink Ring) Pipeline(int capacity = 100)
    {
        var ring = new RingBufferSink(capacity);
        var logger = new LoggerConfiguration()
            .Enrich.With<RedactionEnricher>()
            .WriteTo.Sink(ring)
            .CreateLogger();
        return (logger, ring);
    }

    [Fact]
    public void RingBufferKeepsOnlyLastNInOrder()
    {
        var ring = new RingBufferSink(5);
        for (var i = 0; i < 12; i++)
        {
            ring.Emit(Event($"m{i}"));
        }

        var texts = ring.Snapshot().Select(e => e.MessageTemplate.Text).ToArray();
        Assert.Equal(["m7", "m8", "m9", "m10", "m11"], texts);
        Assert.Equal(5, ring.Count);
        Assert.Equal(12, ring.TotalWritten);
    }

    [Fact]
    public void RingBufferBelowCapacityReturnsAllOldestFirst()
    {
        var ring = new RingBufferSink(5);
        ring.Emit(Event("a"));
        ring.Emit(Event("b"));
        Assert.Equal(["a", "b"], ring.Snapshot().Select(e => e.MessageTemplate.Text).ToArray());
    }

    [Fact]
    public async Task RingBufferSurvivesConcurrentWrites()
    {
        var ring = new RingBufferSink(1000);
        const int threads = 8, perThread = 5000;

        await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++)
            {
                ring.Emit(Event($"{t}:{i}"));
                if (i % 500 == 0)
                {
                    _ = ring.Snapshot();
                }
            }
        })));

        Assert.Equal(threads * perThread, ring.TotalWritten);
        Assert.Equal(1000, ring.Count);
        var snapshot = ring.Snapshot();
        Assert.Equal(1000, snapshot.Count);
        Assert.DoesNotContain(null, snapshot);

        // Per-thread order must be preserved within the retained window.
        foreach (var group in snapshot.Select(e => e.MessageTemplate.Text.Split(':')).GroupBy(p => p[0]))
        {
            var indexes = group.Select(p => int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(indexes.Order().ToArray(), indexes);
        }
    }

    [Fact]
    public void AuthorizationHeaderPropertyIsRedacted()
    {
        var (logger, ring) = Pipeline();
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer abc.def.ghi-secret",
            ["Accept"] = "application/json",
        };

        logger.Information("Request {Headers}", headers);
        logger.Information("Header {Authorization}", "Bearer abc.def.ghi-secret");
        logger.Information("Sent {Line} to server", "Authorization: Bearer abc.def.ghi-secret");

        var rendered = ring.Snapshot().Select(e => e.RenderMessage()).ToArray();
        Assert.All(rendered, r => Assert.DoesNotContain("abc.def.ghi-secret", r));
        Assert.Contains("application/json", rendered[0]);
        Assert.Contains(RedactionEnricher.Mask, rendered[1]);
        Assert.Contains(RedactionEnricher.Mask, rendered[2]);
    }

    [Fact]
    public void PasswordAndTokenPropertiesAreRedactedIncludingNested()
    {
        var (logger, ring) = Pipeline();

        logger.Information("Login {User} {Password}", "alice", "hunter2");
        logger.Information("Refresh {session_token} {SessionSecret}", "tok-123", "sec-456");
        logger.Information("Config {@Settings}", new { Name = "srv", JoinPassword = "pw-789", Nested = new { ApiToken = "t-1" } });
        logger.Information("Connect {Url}", "x?password=hunter2&mode=a");

        var all = string.Join("\n", ring.Snapshot().Select(e =>
            e.RenderMessage() + string.Join(",", e.Properties.Select(p => p.Value.ToString()))));

        foreach (var secret in new[] { "hunter2", "tok-123", "sec-456", "pw-789", "t-1\"" })
        {
            Assert.DoesNotContain(secret, all);
        }
        Assert.Contains("alice", all);
        Assert.Contains("srv", all);
        Assert.Contains("mode=a", all);
    }
}
