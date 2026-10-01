using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Metrics;
using X4MP.Core.Net;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Server.Auth;
using X4MP.Server.Tests.Net;

namespace X4MP.Server.Tests;

public sealed class MeasurementCollector : IDisposable
{
    public sealed record Sample(string Instrument, double Value, Dictionary<string, string?> Tags);

    private readonly MeterListener _listener = new();

    public ConcurrentBag<Sample> Samples { get; } = [];

    public ConcurrentBag<string> Instruments { get; } = [];

    public MeasurementCollector()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ServerMetrics).TypeHandle);
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == ServerMetrics.MeterName)
            {
                Instruments.Add(instrument.Name);
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
        _listener.SetMeasurementEventCallback<int>((i, v, t, _) => Record(i, v, t));
        _listener.Start();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var map = new Dictionary<string, string?>();
        foreach (var tag in tags)
        {
            map[tag.Key] = tag.Value?.ToString();
        }

        Samples.Add(new Sample(instrument.Name, value, map));
    }

    /// <summary>Asks the observable gauges for a reading.</summary>
    public void Observe() => _listener.RecordObservableInstruments();

    public double Sum(string instrument, Func<Dictionary<string, string?>, bool>? where = null) =>
        Samples.Where(s => s.Instrument == instrument && (where?.Invoke(s.Tags) ?? true)).Sum(s => s.Value);

    public void Dispose() => _listener.Dispose();
}

[Collection("net")]
public class MetricsTests
{
    [Fact]
    public void AllInstrumentsArePublishedOnTheServerMeter()
    {
        using var collector = new MeasurementCollector();
        Assert.Equal(
            [
                "x4mp.events.dropped", "x4mp.events.published", "x4mp.net.bytes", "x4mp.net.coalesced", "x4mp.net.connections",
                "x4mp.net.disconnects", "x4mp.net.dropped", "x4mp.net.frames", "x4mp.net.handshakes", "x4mp.net.send_queue_bytes",
                "x4mp.net.violations",
            ],
            collector.Instruments.Distinct().Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ConnectionsBytesFramesAndDisconnectsAreVisibleToMeterListener()
    {
        using var collector = new MeasurementCollector();
        var before = (In: ServerMetrics.BytesIn(Lane.Control), Out: ServerMetrics.BytesOut(Lane.Control), Frames: ServerMetrics.FramesIn);
        await using var net = await NetHarness.CreateAsync("inproc");
        using var stop = new CancellationTokenSource();
        var serve = net.Serve(async connection =>
        {
            while (await connection.ReadAsync(CancellationToken.None) is { } inbound)
            {
                var frame = OutboundFrame.Create(inbound.Frame.Type, inbound.Frame.Lane, inbound.Frame.Payload);
                connection.TrySend(frame);
                frame.Release();
            }
        }, stop.Token);

        await using (var client = await net.ConnectAsync())
        {
            for (uint i = 1; i <= 10; i++)
            {
                await client.SendAsync(MsgType.Ping, TestFrames.Ping(i));
                _ = await client.ReadAsync<Ping>(MsgType.Ping);
            }

            Assert.True(ServerMetrics.ConnectionCount >= 1);
            collector.Observe();
            Assert.True(collector.Samples.Where(s => s.Instrument == "x4mp.net.connections").Max(s => s.Value) >= 1);
            Assert.Contains(collector.Samples, s => s.Instrument == "x4mp.net.send_queue_bytes" && s.Tags["lane"] == "bulk");
        }

        // 10 pings in and 10 echoed out, each a header plus payload on the Control lane.
        Assert.True(ServerMetrics.BytesIn(Lane.Control) - before.In >= 10 * FrameCodec.HeaderSize);
        Assert.True(ServerMetrics.BytesOut(Lane.Control) - before.Out >= 10 * FrameCodec.HeaderSize);
        Assert.True(ServerMetrics.FramesIn - before.Frames >= 10);
        Assert.True(collector.Sum("x4mp.net.bytes", t => t["dir"] == "in" && t["lane"] == "control") >= 10 * FrameCodec.HeaderSize);
        Assert.True(collector.Sum("x4mp.net.bytes", t => t["dir"] == "out" && t["lane"] == "control") >= 10 * FrameCodec.HeaderSize);
        Assert.True(collector.Sum("x4mp.net.frames", t => t["dir"] == "in") >= 10);
        Assert.True(collector.Sum("x4mp.net.frames", t => t["dir"] == "out") >= 10);

        stop.Cancel();
        await serve;
    }

    [Fact]
    public void DropsCoalescesViolationsAndHandshakeResultsAreCounted()
    {
        using var collector = new MeasurementCollector();
        var droppedBefore = ServerMetrics.Dropped;
        var refusedBefore = ServerMetrics.HandshakesRefused;

        var queue = new SendQueue(new SendQueueOptions { RealtimeHighWatermarkBytes = 100, RealtimeLowWatermarkBytes = 50 });
        var a = OutboundFrame.Create(MsgType.PlayerState, Lane.Realtime, new byte[60], 7);
        var b = OutboundFrame.Create(MsgType.PlayerState, Lane.Realtime, new byte[60], 7);
        var c = OutboundFrame.Create(MsgType.PlayerState, Lane.Realtime, new byte[60], 8);
        Assert.Equal(SendResult.Queued, queue.TrySend(a));
        Assert.Equal(SendResult.Coalesced, queue.TrySend(b));
        Assert.Equal(SendResult.DroppedLane, queue.TrySend(c));

        queue.Stats.AddViolation();
        ServerMetrics.RecordHandshakeOk();
        ServerMetrics.RecordHandshakeRefused(DisconnectCode.AuthFailed);

        Assert.Equal(1, ServerMetrics.Dropped - droppedBefore);
        Assert.Equal(1, ServerMetrics.HandshakesRefused - refusedBefore);
        Assert.Equal(1, collector.Sum("x4mp.net.dropped", t => t["lane"] == "realtime" && t["reason"] == "high_watermark"));
        Assert.Equal(1, collector.Sum("x4mp.net.coalesced"));
        Assert.Equal(1, collector.Sum("x4mp.net.violations"));
        Assert.Equal(1, collector.Sum("x4mp.net.handshakes", t => t["result"] == "ok"));
        Assert.Equal(1, collector.Sum("x4mp.net.handshakes", t => t["result"] == "AuthFailed"));
        Assert.Equal(1, queue.Stats.Dropped(Lane.Realtime));
        Assert.Equal(1, queue.Stats.Violations);
    }

    [Fact]
    public void SamplerKeepsTheNewest600SamplesOldestFirst()
    {
        var clock = new FakeTimeProvider();
        var sampler = new MetricsSampler(clock);
        var counter = 0;
        double total = 0;
        sampler.AddProbe(new MetricProbe("test.gauge", "n", MetricKind.Gauge, () => ++counter));
        sampler.AddProbe(new MetricProbe("test.rate", "n/s", MetricKind.Rate, () => total));

        for (int i = 0; i < 700; i++)
        {
            total += 50; // 50 per 500 ms is 100 per second
            clock.Advance(TimeSpan.FromMilliseconds(500));
            sampler.Sample();
        }

        var gauge = sampler.GetSeries(["test.gauge"]).Single();
        Assert.Equal(600, gauge.Samples.Count);
        Assert.Equal(101, gauge.Samples[0]);
        Assert.Equal(700, gauge.Samples[^1]);
        var rate = sampler.GetSeries(["TEST.RATE"]).Single();
        Assert.All(rate.Samples, v => Assert.Equal(100, v, 6));
        Assert.Equal(10, sampler.GetSeries(["test.gauge"], window: 10).Single().Samples.Count);
        Assert.Equal(clock.GetUtcNow(), gauge.EndedAt);
    }

    [Fact]
    public async Task EndpointReturnsTheSeriesForViewersOnly()
    {
        var clock = new FakeTimeProvider();
        var sampler = MetricsSampler.CreateDefault(clock);
        for (int i = 0; i < 650; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            sampler.Sample();
        }

        await using var baseFactory = new AuthFactory();
        await using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton(sampler)));
        var store = factory.Services.GetRequiredService<AdminStore>();
        var viewer = store.CreateToken("v", AdminRoles.Viewer);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/diagnostics/metrics")).StatusCode);

        HttpRequestMessage Get(string path)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new("Bearer", viewer);
            return request;
        }

        using var response = await client.SendAsync(Get("/api/v1/diagnostics/metrics"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var series = doc.RootElement.EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!);
        Assert.Contains("net.connections", series.Keys);
        Assert.Contains("net.bytes_in.realtime", series.Keys);
        Assert.Contains("net.send_queue_bytes.control", series.Keys);
        Assert.All(series.Values, s =>
        {
            Assert.Equal(600, s.GetProperty("samples").GetArrayLength());
            Assert.Equal(1, s.GetProperty("intervalSeconds").GetInt32());
        });

        using var filtered = await client.SendAsync(Get("/api/v1/diagnostics/metrics?series=net.connections&window=30"));
        using var filteredDoc = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
        var only = Assert.Single(filteredDoc.RootElement.EnumerateArray());
        Assert.Equal("net.connections", only.GetProperty("name").GetString());
        Assert.Equal(30, only.GetProperty("samples").GetArrayLength());
    }
}
