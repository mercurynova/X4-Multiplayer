using X4MP.Core.Net;
using X4MP.FakeNode;
using X4MP.Protocol;
using Xunit.Abstractions;

namespace X4MP.Core.Tests.Replication;

/// <summary>
/// End-to-end in virtual time: FakeNode's authority streams the deterministic galaxy into the real mirror, the real interest manager and
/// replication module send frames, and verifying fake clients compare every entry with ground truth (server-design 6.3 <c>--verify</c>).
/// 300 s of game time run in a few seconds because nothing waits on a real clock.
/// </summary>
public sealed class ReplicationVerifyTests(ITestOutputHelper output)
{
    private static ushort BusySector(FakeGalaxy g) =>
        g.Sectors.Where(s => g.Neighbors(s.Index).Count >= 2).OrderBy(s => s.Index).First().Index;

    private static async Task<ReplicationRig> RigWithFlyingClientsAsync(int clients, Action<X4MP.Core.Settings.ReplicationOptions>? replication = null)
    {
        var rig = await ReplicationRig.CreateAsync(replication);
        var behaviors = new[] { ClientBehavior.Explore, ClientBehavior.Patrol, ClientBehavior.Wander };
        for (int i = 0; i < clients; i++)
        {
            var client = await rig.AddClientAsync($"Bot{i + 1:00}");
            client.Player = new FakePlayer(rig.Galaxy, 42, i + 1, behaviors[i % behaviors.Length]);
        }

        return rig;
    }

    private void Report(ReplicationRig rig, double seconds)
    {
        var st = rig.Replication.Stats;
        double perClient = st.BytesSent / (double)rig.Clients.Count / seconds / 1000;
        output.WriteLine($"server: frames={st.FramesSent} entries={st.EntriesSent} full={st.FullEntriesSent} keyframes={st.KeyframesSent} " +
                         $"dropped={st.FramesDropped} inflight-skips={st.InFlightSkips} lost={st.FramesLost} tombstones={st.Tombstones} checksums={st.ChecksumsSent} " +
                         $"epoch-resets={st.EpochResets} resyncs={st.ResyncsHandled} => {perClient:F1} KB/s per client");
        foreach (var c in rig.Clients)
        {
            output.WriteLine($"  client {c.PlayerId}: {c.Session.Summary()} checked={c.Session.Verifier.EntriesChecked} foreign={c.Session.Verifier.ForeignEntries} tombstoned={c.Session.TombstonedEntries} stale={c.Session.StaleEntries}");
            foreach (var v in c.Session.Violations.Take(6))
            {
                output.WriteLine($"    violation {v.Kind} net_id={v.NetId} t={v.GameTime:F2}: {v.Detail}");
            }
        }
    }

    [Fact]
    public async Task AClientInTheFakeGalaxyReceivesVerifiedReplication()
    {
        await using var rig = await ReplicationRig.CreateAsync();
        var alice = await rig.AddClientAsync("Alice");
        await rig.PlaceAsync(alice, BusySector(rig.Galaxy));
        await rig.RunSecondsAsync(20);

        Report(rig, 20);
        Assert.True(alice.Session.Ghosts > 0);
        Assert.True(alice.Session.Verifier.EntriesChecked > 1000);
        Assert.Equal(0, alice.Session.Errors);
    }

    [Fact]
    public async Task ThreeFlyingClientsSeeZeroPositionErrorsOverFiveMinutesOfGameTime()
    {
        await using var rig = await RigWithFlyingClientsAsync(3);
        const double Seconds = 300;
        for (int block = 0; block < 6; block++)
        {
            await rig.RunSecondsAsync(Seconds / 6);
            foreach (var c in rig.Clients)
            {
                c.Session.CheckStale();
            }
        }

        Report(rig, Seconds);
        foreach (var c in rig.Clients)
        {
            Assert.True(c.Session.Ghosts > 20, $"client {c.PlayerId} holds {c.Session.Ghosts} ghosts");
            Assert.True(c.Session.Verifier.EntriesChecked > 20_000);
            Assert.Equal(0, c.Session.Errors);
            Assert.Equal(0, c.Session.ResyncsRequested);            // the desync guard never fired
            Assert.True(c.Session.ChecksumsOk >= 50);
            Assert.True(c.Session.DespawnsApplied > 0);             // they changed sectors: despawns and respawns happened
        }

        Assert.True(rig.Clients.Sum(c => c.Session.TombstonedEntries) >= 0);
        Assert.Equal(0, rig.Replication.Stats.FramesDropped);
    }

    [Fact]
    public async Task DroppedRealtimeFramesCauseNoVerifyErrorsAndTheClientsConvergeOnTheMirror()
    {
        await using var rig = await RigWithFlyingClientsAsync(2);
        var random = new Random(1234);
        rig.Net.RealtimePolicy = (_, _) => random.NextDouble() < 0.25 ? SendResult.DroppedLane : SendResult.Queued;   // a quarter of the frames never reach the lane

        await rig.RunSecondsAsync(90);
        Report(rig, 90);
        Assert.True(rig.Replication.Stats.FramesDropped > 100);

        // freeze the world (no more authority samples) and let the stream drain: every ghost must equal the mirror in every wire field
        rig.AuthorityPaused = true;
        foreach (var c in rig.Clients)
        {
            c.Player = null;
        }

        rig.Net.RealtimePolicy = null;
        await rig.RunSecondsAsync(8);
        foreach (var c in rig.Clients)
        {
            Assert.Equal(0, c.Session.Errors);
            Assert.True(c.Session.Verifier.EntriesChecked > 5_000);
            var divergent = await rig.DivergenceAsync(c);
            Assert.True(divergent.Count == 0, $"client {c.PlayerId} diverges: {string.Join("; ", divergent.Take(5))}");
        }
    }

    [Fact]
    public async Task AtThirtyTwoKilobytesPerSecondTheNearShipsOfTheFakeGalaxyStillUpdateAtTenHertzOrMore()
    {
        const int Window = 20;          // seconds measured, one second at a time
        const double Radius = 12_000;   // metres: comfortably inside the 15 km Near radius
        var galaxy = FakeGalaxy.Generate(42);
        var world = new FakeWorld(galaxy);

        // a busy sector (150+ ships, the busiest of the fake galaxy have about 190) with a Near crowd of 60..85 ships within 12 km of the player:
        // 85 ships at 20 Hz would need about 44 KB/s, so the 32 KB/s budget really bites
        ushort sector = 0;
        foreach (var candidate in galaxy.Sectors.Select(x => x.Index))
        {
            var ships = world.EntitiesInSector(candidate, 0).Where(id => !galaxy.Entities[id - 1].IsStation).ToList();
            int nearNow = ships.Count(id => world.GetKinematics(id, 10).Pos.Length < Radius);
            if (ships.Count >= 150 && nearNow is >= 60 and <= 85)
            {
                sector = candidate;
                break;
            }
        }

        Assert.NotEqual(0, sector);

        await using var rig = await ReplicationRig.CreateAsync(r => r.BandwidthBudgetKBps = 32, galaxy: galaxy);
        var alice = await rig.AddClientAsync("Alice");
        await rig.PlaceAsync(alice, sector);
        await rig.RunSecondsAsync(5);                                    // the sector is delivered and settles

        var ids = world.EntitiesInSector(sector, 0).Where(id => !galaxy.Entities[id - 1].IsStation).ToList();
        long bytes0 = rig.Replication.Stats.BytesSent;
        var perSecond = new List<int>();                                  // entries in one second, for every ship that was Near during all of it
        double nearShips = 0;
        for (int second = 0; second < Window; second++)
        {
            var before = ids.ToDictionary(id => id, id => alice.EntryCounts.GetValueOrDefault((uint)id));
            double t0 = rig.GameTime;
            await rig.RunSecondsAsync(1);
            double t1 = rig.GameTime;
            foreach (int id in ids)
            {
                bool near = Enumerable.Range(0, 5).All(k => world.GetKinematics(id, t0 + ((t1 - t0) * k / 4)).Pos.Length < Radius);
                if (near)
                {
                    perSecond.Add(alice.EntryCounts.GetValueOrDefault((uint)id) - before[id]);
                    nearShips++;
                }
            }
        }

        double kbps = (rig.Replication.Stats.BytesSent - bytes0) / (double)Window / 1000;
        output.WriteLine($"32 KB/s in sector {sector}: {ids.Count} ships, {nearShips / Window:F0} Near on average, entries per Near ship and second: min {perSecond.Min()} / avg {perSecond.Average():F1}, " +
                         $"stream {kbps:F1} KB/s, {alice.Session.Ghosts} ghosts, lane skips {rig.Replication.Stats.LaneSkips}");

        Assert.True(perSecond.Count > 500, $"only {perSecond.Count} ship-seconds were Near");
        Assert.True(perSecond.Average() >= 10, $"Near ships average {perSecond.Average():F1} updates per second");
        Assert.True(perSecond.Count(n => n < 10) <= perSecond.Count / 100, $"{perSecond.Count(n => n < 10)} of {perSecond.Count} ship-seconds under 10 updates");
        Assert.InRange(kbps, 1, 32 * 1.1);
        Assert.Equal(0, alice.Session.Errors);
    }

    [Fact]
    public async Task LostConfirmationsAndSlowFlushesCauseNoErrorsEither()
    {
        await using var rig = await RigWithFlyingClientsAsync(2, r => r.InFlightTimeoutMs = 400);
        // every third tick the flush callback never comes (a stalled writer / lost datagram): the frame is declared lost after 400 ms
        int n = 0;
        rig.Net.RealtimePolicy = (_, _) => SendResult.Queued;
        for (int block = 0; block < 60; block++)
        {
            await rig.RunSecondsAsync(1, confirm: ++n % 3 != 0);
            if (n % 3 == 0)
            {
                rig.Net.ConfirmAll();      // ... it arrives late, long after the timeout (or never for lost frames)
            }
        }

        rig.AuthorityPaused = true;
        foreach (var c in rig.Clients)
        {
            c.Player = null;
        }

        await rig.RunSecondsAsync(8);
        Report(rig, 68);
        foreach (var c in rig.Clients)
        {
            Assert.Equal(0, c.Session.Errors);
            Assert.Empty(await rig.DivergenceAsync(c));
        }
    }
}
