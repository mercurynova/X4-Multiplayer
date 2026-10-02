using X4MP.Core.Interest;
using X4MP.Core.Net;
using X4MP.Core.Replication;
using X4MP.Core.Settings;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;
using Xunit.Abstractions;
using static X4MP.Core.Tests.World.WorldKit;
using ReplicationMsg = X4MP.Proto.Replication;

namespace X4MP.Core.Tests.Replication;

/// <summary>
/// The replication module on a synthetic world (a line galaxy of six sectors, a handful of static ships) driven in virtual time through a real
/// <see cref="X4MP.Core.Session.SessionActor"/>: baselines, deliveries, drops, tombstones, keyframes, resume, checksum and resync.
/// </summary>
public sealed class ReplicationModuleTests(ITestOutputHelper output)
{
    /// <summary>The authority joins first (player 1), so the first client is player 2.</summary>
    private const int Alice = 2;

    /// <summary>Alice sits in sector 2 at the origin; sector 2 holds ships 21..25 (Sector tier, within Near range), sector 3 holds 31..34 (Adjacent).</summary>
    private static async Task<(ReplicationRig Rig, RigClient Alice)> SetupAsync(
        Action<ReplicationOptions>? replication = null, Action<InterestOptions>? interest = null, bool deliverSpawns = true)
    {
        var rig = await ReplicationRig.CreateAsync(replication, interest ?? (o => o.MaxGhosts = 10_000), fakeAuthority: false);
        var alice = await rig.AddClientAsync("Alice", verify: false);
        Assert.Equal(Alice, alice.PlayerId);
        await rig.OnActorAsync(() =>
        {
            rig.Mirror.Spawn([.. Enumerable.Range(0, 5).Select(i => Rec((uint)(21 + i), EntityKind.ShipS, 2, px: 640 * (i + 1), py: 64 * i, pz: -640))]);
            rig.Mirror.Spawn([.. Enumerable.Range(0, 4).Select(i => Rec((uint)(31 + i), EntityKind.ShipM, 3, px: 6400 * (i + 1), pz: 640))]);
        });
        await rig.PlaceAsync(alice, 2);
        await rig.RunAsync(12);                       // the capture set goes out after its 500 ms debounce
        await rig.CompleteCapturedAsync();
        if (deliverSpawns)
        {
            await rig.PumpAsync();
        }

        return (rig, alice);
    }

    private static List<ReplicationEntry> Entries(ReplicationRig rig, int player, int fromFrame = 0) =>
        [.. rig.Net.RealtimeLog.Skip(fromFrame).Where(f => f.Player == player).SelectMany(f =>
        {
            var m = ReplicationMsg.GetRootAsReplication(new Google.FlatBuffers.ByteBuffer(f.Payload));
            return ReplicationCodec.Decode(m.GetEntriesArray(), m.EntryCount);
        })];

    private static async Task MoveAsync(ReplicationRig rig, uint id, ushort sector, int px, int py = 0, int pz = 0, short vx = 0, ushort flags = 0) =>
        await rig.OnActorAsync(() =>
        {
            rig.Mirror.IngestWorldUpdate(UpdatePayload((uint)rig.Tick, rig.GameTime, [State(id, sector, px, py, pz, flags, vx)]));
        });

    // ------------------------------------------------------------------ tick timing (M1-C2)

    [Fact]
    public async Task EveryTickRecordsItsDurationSoThePercentilesAreRealMeasurements()
    {
        var (rig, _) = await SetupAsync();
        await using var _r = rig;
        long before = X4MP.Core.Metrics.ServerMetrics.TickCount;
        await rig.RunAsync(8);
        Assert.True(X4MP.Core.Metrics.ServerMetrics.TickCount - before >= 8);
        double p99 = X4MP.Core.Metrics.ServerMetrics.TickPercentileMs(99);
        double p50 = X4MP.Core.Metrics.ServerMetrics.TickPercentileMs(50);
        Assert.True(p99 >= p50 && p50 >= 0);
        Assert.InRange(p99, 0, 5000);
    }

    [Fact]
    public void TickPercentilesComeFromTheRecordedDurations()
    {
        for (int i = 0; i < 3000; i++)
        {
            X4MP.Core.Metrics.ServerMetrics.RecordTick(i % 100 == 99 ? 40.0 : 1.0);
        }

        // The counters are process-wide and other tests tick too, so assert only what a concurrent tick cannot break.
        Assert.True(X4MP.Core.Metrics.ServerMetrics.TickPercentileMs(100) >= 40.0 - 0.001);
        Assert.True(X4MP.Core.Metrics.ServerMetrics.TickPercentileMs(50) <= X4MP.Core.Metrics.ServerMetrics.TickPercentileMs(99));
    }

    // ------------------------------------------------------------------ spawn before state

    [Fact]
    public async Task AGhostGetsNoStateUntilTheSpawnHoldPassedAndItsFirstEntryIsAFullKeyframe()
    {
        var (rig, alice) = await SetupAsync(r => r.SpawnHoldTicks = 3);
        await using var _ = rig;
        Assert.Equal(9, await rig.OnActorAsync(() => rig.Replication.GhostCount(Alice)));
        Assert.Empty(rig.Net.RealtimeLog);   // the spawns were queued this very moment, the hold is 3 ticks

        await rig.RunAsync(2);
        Assert.Empty(rig.Net.RealtimeLog);
        await rig.RunAsync(2);

        var first = Entries(rig, Alice);
        Assert.Equal(9, first.Count);
        Assert.All(first, e => Assert.Equal(ReplicationMath.FullMask, e.Mask));
        Assert.Equal(0, alice.Session.Errors);       // the fake client flags any state that comes before its spawn
        Assert.Equal(9, alice.Session.Ghosts);
        Assert.Equal(1, rig.Replication.Stats.FramesSent);
    }

    [Fact]
    public async Task OnlyHeldGhostsAreReplicatedNotTheRestOfTheSectorsEntities()
    {
        var (rig, alice) = await SetupAsync(deliverSpawns: false);
        await using var _ = rig;
        // the sector is delivered to the client only after SectorComplete: spawns were not sent yet, so nothing is replicated
        await rig.OnActorAsync(() => rig.Interest.Resync(Alice, null));
        Assert.Equal(0, alice.Session.Errors);
        await rig.RunAsync(4);
        Assert.Equal(0, alice.Session.Errors);
        Assert.All(Entries(rig, Alice), e => Assert.Contains(e.NetId, new uint[] { 21, 22, 23, 24, 25, 31, 32, 33, 34 }));
    }

    // ------------------------------------------------------------------ baselines

    [Fact]
    public async Task ABaselineAdvancesOnlyWhenTheFrameWasConfirmedDeliveredAndNothingIsResentWhileItIsInFlight()
    {
        var (rig, alice) = await SetupAsync();
        await using var _ = rig;

        await rig.RunAsync(3, confirm: false);        // the writer never flushed: no confirmation
        Assert.Equal(1, rig.Replication.Stats.FramesSent);
        Assert.True(rig.Replication.Stats.InFlightSkips >= 1);
        Assert.Equal(9, await rig.OnActorAsync(() => rig.Replication.PendingEntries(Alice)));
        Assert.Null(await rig.OnActorAsync(() => rig.Replication.BaselineOf(Alice, 21)));

        int mark = rig.Net.RealtimeLog.Count;
        await MoveAsync(rig, 21, 2, 1280);
        await rig.RunAsync(2, confirm: false);
        Assert.Equal(mark, rig.Net.RealtimeLog.Count); // still in flight: not a single further frame

        rig.Net.ConfirmAll();                          // the writer's flush callback arrives
        await rig.StepAsync();
        var baseline = await rig.OnActorAsync(() => rig.Replication.BaselineOf(Alice, 21));
        Assert.NotNull(baseline);
        Assert.Equal(0, await rig.OnActorAsync(() => rig.Replication.PendingEntries(Alice)) - (rig.Net.RealtimeLog.Count > mark ? 1 : 0));

        // the entry that follows is a delta against what was confirmed: position and TIME only
        var delta = Entries(rig, Alice, mark);
        var e21 = Assert.Single(delta, e => e.NetId == 21);
        Assert.Equal(ReplicationMask.Pos | ReplicationMask.Time, e21.Mask);
        Assert.Equal(1280, e21.PosX);
        Assert.Equal(0, alice.Session.Errors);
    }

    [Fact]
    public async Task ADeltaCarriesOnlyTheFieldsThatChangedAndAnIdleGhostSendsNothing()
    {
        var (rig, alice) = await SetupAsync();
        await using var _ = rig;
        await rig.RunAsync(4);                         // everything delivered and confirmed
        int mark = rig.Net.RealtimeLog.Count;

        await rig.RunAsync(6);                         // nothing changed, no keyframe due: silence
        Assert.Equal(mark, rig.Net.RealtimeLog.Count);

        await rig.OnActorAsync(() => rig.Mirror.IngestStatusBatch(StatusPayload((22, 100, 255))));
        await MoveAsync(rig, 23, 2, 6400, 0, -640, vx: 8, flags: (ushort)StateFlags.Teleport);
        await rig.RunAsync(2);

        var entries = Entries(rig, Alice, mark);
        var status = Assert.Single(entries, e => e.NetId == 22);
        Assert.Equal(ReplicationMask.Status, status.Mask);
        Assert.Equal((100, 255), (status.Hull, status.Shield));
        var moved = Assert.Single(entries, e => e.NetId == 23);
        Assert.Equal(ReplicationMask.Pos | ReplicationMask.Vel | ReplicationMask.Flags | ReplicationMask.Time, moved.Mask);
        Assert.Equal(2, entries.Count);
        Assert.Equal(0, alice.Session.Errors);
    }

    [Fact]
    public async Task ADroppedRealtimeFrameChangesNothingAndTheSameDeltaIsSentAgain()
    {
        var (rig, alice) = await SetupAsync();
        await using var _ = rig;
        await rig.RunAsync(4);

        int drops = 1;
        rig.Net.RealtimePolicy = (_, _) => drops-- > 0 ? SendResult.DroppedLane : SendResult.Queued;
        int mark = rig.Net.RealtimeLog.Count;
        await MoveAsync(rig, 21, 2, 3200);
        await rig.StepAsync();

        Assert.Equal(1, rig.Replication.Stats.FramesDropped);
        Assert.Equal(mark, rig.Net.RealtimeLog.Count);
        Assert.Equal(0, await rig.OnActorAsync(() => rig.Replication.PendingEntries(Alice)));   // rolled back
        Assert.Equal(0, alice.Session.Errors);

        await rig.StepAsync();                          // the lane takes the frame now
        var again = Entries(rig, Alice, mark);
        var e21 = Assert.Single(again, e => e.NetId == 21);
        Assert.Equal(ReplicationMask.Pos | ReplicationMask.Time, e21.Mask);   // same delta against the still-confirmed old baseline
        Assert.Equal(3200, e21.PosX);
        await rig.RunAsync(2);
        Assert.Empty(await rig.DivergenceAsync(alice));
    }

    [Fact]
    public async Task ARevertedValueIsNeverOmittedBecauseOnlyOneFrameIsInFlightAtATime()
    {
        // The "changed then reverted" hole of protocol.md 10.3: A to B (in flight), back to A. The baseline says A, so a naive server would
        // omit the field and the client would keep B. With one frame in flight the second frame waits for the confirmation of the first.
        var (rig, alice) = await SetupAsync();
        await using var _ = rig;
        await rig.RunAsync(4);

        await MoveAsync(rig, 21, 2, 9999);
        await rig.StepAsync(confirm: false);            // B goes out, unconfirmed
        await MoveAsync(rig, 21, 2, 640);               // back to the baseline value A
        await rig.StepAsync(confirm: false);
        rig.Net.ConfirmAll();
        await rig.RunAsync(3);

        var state = alice.States[21];
        Assert.Equal(640, state.Px);
        Assert.Empty(await rig.DivergenceAsync(alice));
    }

    [Fact]
    public async Task AFrameThatIsNeverConfirmedCountsAsLostAndItsGhostsRestartWithAFullEntry()
    {
        var (rig, alice) = await SetupAsync(r => r.InFlightTimeoutMs = 500);
        await using var _ = rig;
        await rig.RunAsync(4);
        int mark = rig.Net.RealtimeLog.Count;

        await MoveAsync(rig, 24, 2, 5555);
        await rig.RunAsync(4, confirm: false);          // sent, never confirmed
        await rig.RunAsync(14, confirm: false);         // 700 ms: past the timeout
        Assert.True(rig.Replication.Stats.FramesLost >= 1);

        await rig.RunAsync(2);
        var resent = Entries(rig, Alice, mark).Where(e => e.NetId == 24).ToList();
        Assert.Contains(resent, e => e.Mask == ReplicationMath.FullMask);  // never trust that baseline again
        Assert.Empty(await rig.DivergenceAsync(alice));
    }

    [Fact]
    public async Task ANodeWhoseRealtimeLaneIsBackedUpIsSkippedAndCatchesUpLater()
    {
        var (rig, alice) = await SetupAsync();
        await using var _ = rig;
        await rig.RunAsync(4);
        int mark = rig.Net.RealtimeLog.Count;

        rig.Net.CanAccept = false;
        await MoveAsync(rig, 21, 2, 1111);
        await rig.RunAsync(5);
        Assert.Equal(mark, rig.Net.RealtimeLog.Count);
        Assert.True(rig.Replication.Stats.LaneSkips >= 5);

        rig.Net.CanAccept = true;
        await rig.RunAsync(2);
        Assert.Contains(Entries(rig, Alice, mark), e => e.NetId == 21 && e.PosX == 1111);
        Assert.Equal(0, alice.Session.Errors);
    }

    // ------------------------------------------------------------------ tombstones

    [Fact]
    public async Task ADespawnTombstonesTheIdAndTheConfirmationOfAFrameThatWasInFlightDoesNotRebuildItsBaseline()
    {
        var (rig, alice) = await SetupAsync();
        await using var _ = rig;
        await rig.RunAsync(3, confirm: false);          // the first frame (all nine ghosts) is in flight

        await rig.OnActorAsync(() => rig.Mirror.Despawn(DespawnReason.Destroyed, 21));
        Assert.True(await rig.OnActorAsync(() => rig.Replication.IsTombstoned(Alice, 21)));
        Assert.Equal(8, await rig.OnActorAsync(() => rig.Replication.GhostCount(Alice)));

        rig.Net.ConfirmAll();
        await rig.RunAsync(2);
        Assert.Null(await rig.OnActorAsync(() => rig.Replication.BaselineOf(Alice, 21)));
        Assert.Equal(8, await rig.OnActorAsync(() => rig.Replication.GhostCount(Alice)));
        Assert.DoesNotContain(Entries(rig, Alice).Skip(9), e => e.NetId == 21);
        Assert.Equal(0, alice.Session.Errors);

        await rig.RunSecondsAsync(5.5);                  // the 5 s tombstone ends
        Assert.False(await rig.OnActorAsync(() => rig.Replication.IsTombstoned(Alice, 21)));
    }

    [Fact]
    public async Task ARespawnIsANewLifeThatStartsWithAFullEntryEvenIfAnOldFrameIsConfirmedLate()
    {
        var (rig, alice) = await SetupAsync();
        await using var _ = rig;
        await rig.RunAsync(3, confirm: false);

        await rig.OnActorAsync(() => rig.Mirror.Despawn(DespawnReason.OutOfInterest, 22));
        await rig.OnActorAsync(() => rig.Mirror.Spawn(Rec(22, EntityKind.ShipS, 2, px: 777)));
        Assert.False(await rig.OnActorAsync(() => rig.Replication.IsTombstoned(Alice, 22)));   // a spawn ends the tombstone

        int mark = rig.Net.RealtimeLog.Count;
        rig.Net.ConfirmAll();                            // the confirmation of the old life arrives now
        await rig.RunAsync(3);
        Assert.Contains(Entries(rig, Alice, mark), e => e.NetId == 22 && e.Mask == ReplicationMath.FullMask && e.PosX == 777);
        Assert.Empty(await rig.DivergenceAsync(alice));
    }

    // ------------------------------------------------------------------ keyframes

    [Fact]
    public async Task EveryGhostGetsAFullEntryEveryFiveSecondsInTheSectorTierAndEveryFifteenInTheAdjacentTier()
    {
        var (rig, alice) = await SetupAsync();
        await using var _ = rig;
        await rig.RunSecondsAsync(31);

        foreach (uint id in new uint[] { 21, 25 })
        {
            var times = alice.States[id].FullEntryTimes;
            Assert.True(times.Count >= 6, $"{id}: {times.Count} keyframes");
            for (int i = 1; i < times.Count; i++)
            {
                Assert.InRange(times[i] - times[i - 1], 5.0, 5.2);
            }
        }

        foreach (uint id in new uint[] { 31, 34 })
        {
            var times = alice.States[id].FullEntryTimes;
            Assert.True(times.Count >= 2, $"{id}: {times.Count} keyframes");
            for (int i = 1; i < times.Count; i++)
            {
                Assert.InRange(times[i] - times[i - 1], 15.0, 15.2);
            }
        }

        Assert.True(rig.Replication.Stats.KeyframesSent >= 9 * 2);
        Assert.Equal(0, alice.Session.Errors);
    }

    [Fact]
    public async Task TheKeyframeIntervalsAreSettingsAppliedOnTheNextTick()
    {
        var (rig, alice) = await SetupAsync(r =>
        {
            r.KeyframeNearSectorSeconds = 2;
            r.KeyframeAdjacentSeconds = 4;
        });
        await using var _ = rig;
        await rig.RunSecondsAsync(9);
        var near = alice.States[21].FullEntryTimes;
        var adjacent = alice.States[31].FullEntryTimes;
        Assert.True(near.Count >= 4);
        Assert.InRange(near[2] - near[1], 2.0, 2.2);
        Assert.InRange(adjacent[2] - adjacent[1], 4.0, 4.2);
    }

    // ------------------------------------------------------------------ resume

    [Fact]
    public async Task AResumedNodeStartsWithNewBaselinesAndGetsAKeyframeOfEverything()
    {
        var (rig, alice) = await SetupAsync();
        await using var _ = rig;
        await rig.RunAsync(6);
        Assert.Equal(0, await rig.OnActorAsync(() => rig.Replication.EpochOf(Alice)));
        Assert.NotNull(await rig.OnActorAsync(() => rig.Replication.BaselineOf(Alice, 21)));

        var back = await rig.ResumeClientAsync(alice);
        await rig.CompleteCapturedAsync();
        await rig.PumpAsync();
        int mark = rig.Net.RealtimeLog.Count;
        await rig.RunAsync(4);

        Assert.Equal(1, await rig.OnActorAsync(() => rig.Replication.EpochOf(Alice)));
        var entries = Entries(rig, Alice, mark);
        Assert.Equal(9, entries.Count);
        Assert.All(entries, e => Assert.Equal(ReplicationMath.FullMask, e.Mask));
        Assert.Equal(0, back.Session.Errors);
        Assert.Equal(9, back.Session.Ghosts);
        Assert.Empty(await rig.DivergenceAsync(back));
    }

    [Fact]
    public async Task ResumingWhileTheOldSocketIsStillAttachedAlsoResetsTheBaselines()
    {
        var (rig, alice) = await SetupAsync();
        await using var _ = rig;
        await rig.RunAsync(6);

        var back = await rig.ResumeClientAsync(alice, dropFirst: false);   // the old connection is superseded, never detached
        await rig.CompleteCapturedAsync();
        await rig.PumpAsync();
        await rig.RunAsync(4);

        Assert.Equal(1, await rig.OnActorAsync(() => rig.Replication.EpochOf(Alice)));
        Assert.True(rig.Replication.Stats.EpochResets >= 1);
        Assert.Equal(9, back.Session.Ghosts);
        Assert.Equal(0, back.Session.Errors);
        Assert.Empty(await rig.DivergenceAsync(back));
    }

    // ------------------------------------------------------------------ welcome

    [Fact]
    public async Task WelcomeMaxGhostsComesFromTheInterestOptions()
    {
        await using var rig = await ReplicationRig.CreateAsync(interest: o => o.MaxGhosts = 321, fakeAuthority: false);
        var node = await rig.Actor.JoinAsync("Alice");
        Assert.Equal(321u, node.Welcome.MaxGhosts);

        rig.InterestOptions.MaxGhosts = 77;
        var bob = await rig.Actor.JoinAsync("Bob");
        Assert.Equal(77u, bob.Welcome.MaxGhosts);
        Assert.Equal(250, new InterestOptions().MaxGhosts);   // the default budget of ADR-042
    }

    // ------------------------------------------------------------------ desync guard

    [Fact]
    public async Task EveryFiveSecondsTheClientGetsAChecksumOverItsGhostSetWhichItsOwnViewMatches()
    {
        var (rig, alice) = await SetupAsync();
        await using var _ = rig;
        await rig.RunSecondsAsync(16);

        var checksums = rig.Net.ControlLog.Where(f => f.Player == Alice && f.Type == MsgType.InterestChecksum)
            .Select(f => WorldKitDecode(f.Payload)).ToList();
        Assert.InRange(checksums.Count, 3, 4);
        var held = await rig.OnActorAsync(() => rig.Interest.HeldBy(Alice).ToArray());
        Assert.All(checksums, c =>
        {
            Assert.Equal((uint)held.Length, c.Count);
            Assert.Equal(InterestHash.Of(held), c.XorHash);
        });
        Assert.Equal(checksums.Count, alice.Session.ChecksumsOk);
        Assert.Equal(0, alice.Session.ChecksumMismatches);
    }

    private static InterestChecksum WorldKitDecode(byte[] payload) => Decode<InterestChecksum>(MsgType.InterestChecksum, payload);

    [Fact]
    public async Task AChecksumMismatchMakesTheClientAskForAResyncAndTheServerDeliversTheSectorsAgain()
    {
        var (rig, alice) = await SetupAsync();
        await using var _ = rig;
        await rig.RunSecondsAsync(2);
        int spawnsBefore = rig.Net.ControlLog.Count(f => f.Player == Alice && f.Type == MsgType.EntitySpawn);
        int completesBefore = rig.Net.ControlLog.Count(f => f.Player == Alice && f.Type == MsgType.SectorComplete);
        int mark = rig.Net.RealtimeLog.Count;

        alice.Session.ChecksumCountSkew = 1;             // the client's next view of itself is off by one ghost
        await rig.RunSecondsAsync(6);                     // the checksum arrives, the client answers with a ResyncRequest

        Assert.Equal(1, alice.Session.ResyncsRequested);
        Assert.Equal(1, rig.Replication.Stats.ResyncsHandled);
        Assert.True(rig.Net.ControlLog.Count(f => f.Player == Alice && f.Type == MsgType.EntitySpawn) > spawnsBefore);
        Assert.True(rig.Net.ControlLog.Count(f => f.Player == Alice && f.Type == MsgType.SectorComplete) >= completesBefore + 2);   // the markers follow the spawns again

        // baselines started over: the redelivered ghosts got full entries
        var entries = Entries(rig, Alice, mark);
        Assert.True(entries.Count(e => e.Mask == ReplicationMath.FullMask) >= 9);

        await rig.RunSecondsAsync(6);                     // the next checksum matches again
        Assert.True(alice.Session.ChecksumsOk >= 1);
        Assert.Equal(1, alice.Session.ChecksumMismatches);
        Assert.Equal(0, alice.Session.Errors);
        Assert.Empty(await rig.DivergenceAsync(alice));
    }

    [Fact]
    public async Task AResyncOfOneSectorLeavesTheOthersAloneAndAGreedyClientIsRateLimited()
    {
        var (rig, alice) = await SetupAsync(r => r.ResyncMinIntervalMs = 3000);
        await using var _ = rig;
        await rig.RunAsync(6);
        int spawnsBefore = rig.Net.ControlLog.Count(f => f.Player == Alice && f.Type == MsgType.EntitySpawn);

        Assert.True(await rig.OnActorAsync(() => rig.Replication.Resync(Alice, [3], "test")));
        await rig.PumpAsync();
        var spawned = rig.Net.ControlLog.Where(f => f.Player == Alice && f.Type == MsgType.EntitySpawn).Skip(spawnsBefore)
            .SelectMany(f => { var s = Decode<EntitySpawn>(f.Type, f.Payload); return Enumerable.Range(0, s.EntitiesLength).Select(i => s.Entities(i)!.Value.NetId); })
            .ToList();
        Assert.Equal([31u, 32u, 33u, 34u], spawned.Order().ToArray());     // sector 3 only

        Assert.False(await rig.OnActorAsync(() => rig.Replication.Resync(Alice, [3], "again")));   // inside the 3 s window
        Assert.Equal(1, rig.Replication.Stats.ResyncsRefused);
        await rig.RunSecondsAsync(3.2);
        Assert.True(await rig.OnActorAsync(() => rig.Replication.Resync(Alice, [], "later")));
        await rig.RunAsync(4);
        Assert.Equal(0, alice.Session.Errors);
        Assert.Equal(9, alice.Session.Ghosts);
    }

    // ------------------------------------------------------------------ player ships (M1-10 relay)

    [Fact]
    public async Task AnotherPlayersShipIsReplicatedLikeAnyMirroredEntityAtTheNearRateAndNeverBackToItsPilot()
    {
        var (rig, alice) = await SetupAsync();
        await using var _ = rig;
        var bob = await rig.AddClientAsync("Bob", verify: false);
        await rig.OnActorAsync(() => rig.Mirror.Spawn(Rec(900, EntityKind.ShipM, 2, px: 0, controller: (ushort)alice.PlayerId, origin: EntityOrigin.PlayerShip)));
        await rig.PlaceAsync(bob, 2);
        await rig.RunAsync(4);
        await rig.CompleteCapturedAsync();
        await rig.PumpAsync();
        await rig.RunAsync(4);
        Assert.True(bob.Session.IsGhost(900), "Bob holds Alice's ship as a ghost");

        // Alice flies for 3 s: her PlayerState drives the mirror entity, replication carries it to Bob at 20 Hz.
        int mark = rig.Net.RealtimeLog.Count;
        for (int step = 0; step < 60; step++)
        {
            int px = 1000 + (step * 640);
            await rig.OnActorAsync(() => rig.Mirror.ApplyPlayerState(alice.PlayerId, Decode<PlayerState>(
                MsgType.PlayerState, PlayerStatePayload((uint)(100 + step), 900, 2, px))));
            await rig.StepAsync();
        }

        var toBob = Entries(rig, bob.PlayerId, mark).Where(e => e.NetId == 900).ToList();
        var toAlice = Entries(rig, Alice, mark).Where(e => e.NetId == 900).ToList();
        output.WriteLine($"ship 900: {toBob.Count} entries to Bob in 3 s, {toAlice.Count} to its pilot");
        Assert.InRange(toBob.Count, 55, 61);                  // ~20 Hz
        Assert.Empty(toAlice);                                // never a client's own ship
        Assert.Equal(1000 + (59 * 640), bob.States[900].Px);   // Bob sees where Alice is
        Assert.Equal(0, bob.Session.Errors);
        Assert.Empty(await rig.DivergenceAsync(bob));
    }

    [Fact]
    public async Task APlayerShipInAFarSectorStillReachesEveryoneAtAtLeastTwoHertz()
    {
        var (rig, alice) = await SetupAsync();
        await using var _ = rig;
        // a ship of another player in sector 6, five hops away (no tier of Alice's covers it)
        await rig.OnActorAsync(() => rig.Mirror.Spawn(Rec(901, EntityKind.ShipM, 6, px: 640, controller: 9, origin: EntityOrigin.PlayerShip)));
        await rig.RunAsync(4);
        int mark = rig.Net.RealtimeLog.Count;
        for (int step = 0; step < 60; step++)
        {
            await MoveAsync(rig, 901, 6, 640 + (step * 640));
            await rig.StepAsync();
        }

        var entries = Entries(rig, Alice, mark).Where(e => e.NetId == 901).ToList();
        Assert.True(alice.Session.IsGhost(901));
        Assert.InRange(entries.Count, 5, 8);                  // 2 Hz for 3 s
        Assert.Equal(0, alice.Session.Errors);
    }

    // ------------------------------------------------------------------ budget

    [Fact]
    public async Task TheByteBudgetCapsWhatAClientIsSentAndTheClosestEntitiesGoFirst()
    {
        // 40 ships in range but only room for a handful per tick: 2 KB/s at 20 Hz is 100 bytes a tick.
        var rig = await ReplicationRig.CreateAsync(r => r.BandwidthBudgetKBps = 2, o => o.MaxGhosts = 10_000, fakeAuthority: false);
        await using var _ = rig;
        var alice = await rig.AddClientAsync("Alice", verify: false);
        await rig.OnActorAsync(() => rig.Mirror.Spawn([.. Enumerable.Range(0, 40).Select(i => Rec((uint)(100 + i), EntityKind.ShipS, 2, px: 6400 * (i + 1)))]));
        await rig.PlaceAsync(alice, 2);
        await rig.RunAsync(12);
        await rig.CompleteCapturedAsync();
        await rig.PumpAsync();
        int mark = rig.Net.RealtimeLog.Count;

        await rig.RunSecondsAsync(2);
        long bytes = rig.Net.RealtimeLog.Skip(mark).Where(f => f.Player == Alice).Sum(f => f.Payload.Length + FrameCodec.HeaderSize);
        Assert.InRange(bytes, 1, (2 * 1000 * 2) + 400);   // 2 s of 2 KB/s plus one frame of slack
        var first = Entries(rig, Alice, mark).Take(4).Select(e => e.NetId).ToList();
        Assert.All(first, id => Assert.InRange(id, 100u, 110u));   // nearest ships (ships 100.. are the closest to the origin) lead
        Assert.Equal(0, alice.Session.Errors);
    }

    [Fact]
    public async Task AtThirtyTwoKilobytesPerSecondTheNearEntitiesStillUpdateAtTenHertzOrMore()
    {
        output.WriteLine("(see ReplicationBudgetTests for the fake-galaxy variant)");
        var rig = await ReplicationRig.CreateAsync(r => r.BandwidthBudgetKBps = 32, o => o.MaxGhosts = 10_000, fakeAuthority: false);
        await using var _ = rig;
        var alice = await rig.AddClientAsync("Alice", verify: false);
        // 40 moving Near ships and 120 moving Sector ships far away; each ship moves every tick
        await rig.OnActorAsync(() => rig.Mirror.Spawn([.. Enumerable.Range(0, 160).Select(i => Rec((uint)(200 + i), EntityKind.ShipS, 2, px: i < 40 ? 640 * (i + 1) : 64 * 30_000 + (i * 640)))]));
        await rig.PlaceAsync(alice, 2);
        await rig.RunAsync(12);
        await rig.CompleteCapturedAsync();
        await rig.PumpAsync();
        await rig.RunAsync(40);

        int mark = rig.Net.RealtimeLog.Count;
        const int Seconds = 6;
        for (int step = 0; step < Seconds * 20; step++)
        {
            await rig.OnActorAsync(() => rig.Mirror.IngestWorldUpdate(UpdatePayload((uint)rig.Tick, rig.GameTime,
                Enumerable.Range(0, 160).Select(i => State((uint)(200 + i), 2, (i < 40 ? 640 * (i + 1) : 64 * 30_000 + (i * 640)) + (step * 3), vx: 4)))));
            await rig.StepAsync();
        }

        var entries = Entries(rig, Alice, mark);
        var perShip = entries.GroupBy(e => e.NetId).ToDictionary(g => g.Key, g => g.Count());
        double nearHz = Enumerable.Range(0, 40).Select(i => perShip.GetValueOrDefault((uint)(200 + i)) / (double)Seconds).Min();
        double farHz = Enumerable.Range(40, 120).Select(i => perShip.GetValueOrDefault((uint)(200 + i)) / (double)Seconds).Average();
        long bytes = rig.Net.RealtimeLog.Skip(mark).Where(f => f.Player == Alice).Sum(f => f.Payload.Length + FrameCodec.HeaderSize);
        output.WriteLine($"32 KB/s: near min {nearHz:F1} Hz, sector avg {farHz:F2} Hz, {bytes / (double)Seconds / 1000:F1} KB/s");

        Assert.True(nearHz >= 10, $"the slowest Near ship updates at {nearHz:F1} Hz");
        Assert.True(bytes / (double)Seconds <= 32_000 * 1.1, $"{bytes / (double)Seconds} B/s");
        Assert.True(farHz < nearHz);                         // the far tier pays for the budget, not the near one
        Assert.Equal(0, alice.Session.Errors);
    }
}
