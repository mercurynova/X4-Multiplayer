using System.Diagnostics;
using X4MP.Core.Net;
using X4MP.Core.Replication;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;
using Xunit.Abstractions;
using static X4MP.Core.Tests.World.WorldKit;

namespace X4MP.Core.Tests.Replication;

/// <summary>A transport that keeps nothing: it counts and confirms every frame at once (a writer that flushes instantly), so a test measures the module alone.</summary>
public sealed class CountingTransport : IReplicationTransport
{
    private readonly Dictionary<int, Action<OutboundFrame>> _observers = [];

    public long Frames { get; private set; }

    public long Bytes { get; private set; }

    public long Entries { get; private set; }

    public bool CanAcceptRealtime(int playerId) => true;

    public SendResult SendControl(int playerId, OutboundFrame frame) => SendResult.Queued;

    public SendResult SendRealtime(int playerId, OutboundFrame frame)
    {
        Frames++;
        Bytes += frame.Length;
        if (_observers.TryGetValue(playerId, out var observer))
        {
            observer(frame);
        }

        return SendResult.Queued;
    }

    public void SetDeliveryObserver(int playerId, INodeConnection? connection, Action<OutboundFrame>? observer)
    {
        if (observer is null)
        {
            _observers.Remove(playerId);
        }
        else
        {
            _observers[playerId] = observer;
        }
    }
}

/// <summary>The hot path of replication: steady-state allocations per tick, the cost of a tick, and the bandwidth the default settings produce.</summary>
public sealed class ReplicationBenchmarkTests(ITestOutputHelper output)
{
    private const int Clients = 8;
    private const int ShipsPerSector = 250;

    /// <summary>Eight clients, each in a sector of its own that holds 250 moving ships: every ship changes on every tick (the worst case).</summary>
    private static async Task<(ReplicationRig Rig, CountingTransport Transport)> SetupAsync()
    {
        var transport = new CountingTransport();
        var rig = await ReplicationRig.CreateAsync(
            replication: r => r.ChecksumIntervalSeconds = 0,
            interest: o => o.MaxGhosts = 100_000,
            fakeAuthority: false,
            lineSectors: Clients * 3,
            replicationTransport: transport,
            replicationTimer: false);
        for (int c = 0; c < Clients; c++)
        {
            ushort sector = (ushort)(1 + (c * 3));
            await rig.OnActorAsync(() => rig.Mirror.Spawn([.. Enumerable.Range(0, ShipsPerSector).Select(i =>
                Rec((uint)(10_000 + (c * 1000) + i), EntityKind.ShipS, sector, px: i * 640, py: (i % 7) * 64, pz: (i % 11) * 64))]));
            var client = await rig.AddClientAsync($"Bot{c}", verify: false);
            await rig.PlaceAsync(client, sector);
        }

        await rig.RunAsync(14);
        await rig.CompleteCapturedAsync();
        await rig.PumpAsync();
        return (rig, transport);
    }

    private static byte[][] MovementCycle() =>
    [.. Enumerable.Range(0, 20).Select(t => UpdatePayload((uint)t, t * 0.05, Enumerable.Range(0, Clients).SelectMany(c =>
        Enumerable.Range(0, ShipsPerSector).Select(i => State(
            (uint)(10_000 + (c * 1000) + i), (ushort)(1 + (c * 3)), (i * 640) + (t * 128), ((i % 7) * 64) + t, ((i % 11) * 64) - t, vx: (short)(2 + (t % 3))))))),
    ];

    [Fact]
    public async Task ASteadyStateReplicationTickAllocatesOnlyTheFrameObjects()
    {
        var (rig, transport) = await SetupAsync();
        await using var _ = rig;
        var cycle = MovementCycle();
        // Best of several rounds: a pooled buffer trimmed by a gen-2 GC (other tests run in parallel on CI) is re-allocated once, which
        // lands in exactly one round and would otherwise add its few KB to the average. Steady state = the smallest round.
        const int Rounds = 5;
        const int Ticks = 200;
        long allocated = long.MaxValue;
        long frames = 0;
        long entries = 0;
        double ms = 0;

        await rig.OnActorAsync(() =>
        {
            Assert.Equal(Clients, rig.Replication.ClientCount);
            // warm up (full keyframes, JIT, buffers, pools), then measure
            for (int i = 0; i < 80; i++)
            {
                rig.Time.Advance(TimeSpan.FromSeconds(0.05));
                rig.Mirror.IngestWorldUpdate(cycle[i % cycle.Length]);
                rig.Replication.Tick(rig.Time.GetTimestamp());
            }

            for (int round = 0; round < Rounds; round++)
            {
                long framesBefore = transport.Frames;
                long entriesBefore = rig.Replication.Stats.EntriesSent;
                long roundAllocated = 0;
                double roundMs = 0;
                for (int i = 0; i < Ticks; i++)
                {
                    rig.Time.Advance(TimeSpan.FromSeconds(0.05));
                    rig.Mirror.IngestWorldUpdate(cycle[i % cycle.Length]);
                    long now = rig.Time.GetTimestamp();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long started = Stopwatch.GetTimestamp();
                    rig.Replication.Tick(now);
                    roundMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    roundAllocated += GC.GetAllocatedBytesForCurrentThread() - before;
                }

                if (roundAllocated < allocated)
                {
                    allocated = roundAllocated;
                    frames = transport.Frames - framesBefore;
                    entries = rig.Replication.Stats.EntriesSent - entriesBefore;
                    ms = roundMs;
                }
            }
        });

        double perTick = allocated / (double)Ticks;
        double perFrame = allocated / (double)Math.Max(1, frames);
        output.WriteLine($"{Clients} clients x {ShipsPerSector} ghosts, every ghost moving: {entries / (double)Ticks:F0} entries/tick, {frames / (double)Ticks:F1} frames/tick, " +
                         $"{ms / Ticks:F3} ms/tick, {perTick:F0} B allocated/tick = {perFrame:F0} B/frame (best of {Rounds} rounds)");

        Assert.True(entries > 0 && frames > 0);
        Assert.True(perFrame <= 96, $"{perFrame:F0} bytes per frame: more than the OutboundFrame object itself is allocated");
        Assert.True(perTick <= (Clients * 12 * 96) + 512, $"{perTick:F0} bytes per tick");
    }

    [Fact]
    public async Task AnIdleTickAllocatesNothingAtAll()
    {
        var (rig, transport) = await SetupAsync();
        await using var _ = rig;
        rig.ReplicationOptions.KeyframeNearSectorSeconds = 600; // no keyframe falls into the window: a tick with nothing to send sends nothing
        rig.ReplicationOptions.KeyframeAdjacentSeconds = 600;
        long allocated = 0;
        await rig.OnActorAsync(() =>
        {
            for (int i = 0; i < 100; i++) // everything keyframed and confirmed, nothing moves
            {
                rig.Time.Advance(TimeSpan.FromSeconds(0.05));
                rig.Replication.Tick(rig.Time.GetTimestamp());
            }

            for (int i = 0; i < 20; i++)
            {
                rig.Time.Advance(TimeSpan.FromSeconds(0.05));
                long now = rig.Time.GetTimestamp();
                long before = GC.GetAllocatedBytesForCurrentThread();
                rig.Replication.Tick(now);
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            }
        });

        output.WriteLine($"idle: {allocated} bytes over 20 ticks of {Clients} clients x {ShipsPerSector} ghosts; frames sent in total {transport.Frames}, skips {rig.Replication.Stats.InFlightSkips}/{rig.Replication.Stats.LaneSkips}");
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task TheDefaultBudgetBoundsTheBandwidthOfAClientWhoseWholeSectorMoves()
    {
        var (rig, transport) = await SetupAsync();
        await using var _ = rig;
        var cycle = MovementCycle();
        long bytes0 = 0;
        const int Seconds = 10;
        await rig.OnActorAsync(() =>
        {
            for (int i = 0; i < 40; i++)
            {
                rig.Time.Advance(TimeSpan.FromSeconds(0.05));
                rig.Mirror.IngestWorldUpdate(cycle[i % cycle.Length]);
                rig.Replication.Tick(rig.Time.GetTimestamp());
            }

            bytes0 = transport.Bytes;
            for (int i = 0; i < Seconds * 20; i++)
            {
                rig.Time.Advance(TimeSpan.FromSeconds(0.05));
                rig.Mirror.IngestWorldUpdate(cycle[i % cycle.Length]);
                rig.Replication.Tick(rig.Time.GetTimestamp());
            }
        });

        double perClient = (transport.Bytes - bytes0) / (double)Clients / Seconds;
        output.WriteLine($"250 ghosts per client, all moving: {perClient / 1000:F1} KB/s per client (budget 256 KB/s)");
        Assert.InRange(perClient, 1, 256_000 * 1.05);
    }
}
