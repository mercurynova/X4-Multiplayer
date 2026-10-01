using System.Diagnostics;
using Google.FlatBuffers;
using X4MP.Core.Interest;
using X4MP.Core.Session;
using X4MP.Core.Tests.Session;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;
using Xunit.Abstractions;
using static X4MP.Core.Tests.World.WorldKit;

namespace X4MP.Core.Tests.World;

/// <summary>The world mirror and the interest manager attached to a real <see cref="SessionActor"/> over fake connections.</summary>
public sealed class InterestModuleTests(ITestOutputHelper output)
{
    private static FlatBufferBuilder Builder(Func<FlatBufferBuilder, int> pack)
    {
        var fbb = new FlatBufferBuilder(512);
        fbb.Finish(pack(fbb));
        return fbb;
    }

    [Fact]
    public async Task ClientAndAuthorityExchangeInterestCaptureAndSectorsThroughTheActor()
    {
        var mirror = new WorldMirror();
        var interest = new InterestManager(mirror, new InterestOptions());
        await using var rig = new ActorRig(modules: [mirror, interest]);
        var boss = await rig.JoinAuthorityAsync();
        await rig.BringInGameAsync(boss);
        var alice = await rig.JoinAsync("Alice");
        await rig.BringInGameAsync(alice);

        // The authority announces the galaxy (a line of 5 gate-linked sectors) and some ships.
        var sha = Enumerable.Range(0, 32).Select(i => (byte)(i + 9)).ToArray();
        await rig.SendAsync(boss, MsgType.GalaxyMetadata, Builder(b => GalaxyMetadata.Pack(b, Decode<GalaxyMetadata>(MsgType.GalaxyMetadata, LineGalaxy(sha, 5)).UnPack()).Value));
        await rig.SendAsync(boss, MsgType.EntitySpawn, Builder(b => EntitySpawn.Pack(b, new EntitySpawnT
        {
            Entities = [Rec(21, EntityKind.ShipS, 2), Rec(22, EntityKind.ShipM, 2), Rec(31, EntityKind.ShipL, 3)],
        }).Value));

        // The client reports its position: it gets its tiers, the authority gets the capture set.
        var state = Builder(b => PlayerState.Pack(b, new PlayerStateT { Seq = 1, Sector = 2, Px = 640 }).Value);
        await rig.SendAsync(alice, MsgType.PlayerState, state);

        var update = Assert.Single(alice.Connection.SentOf(MsgType.InterestUpdate)).Decode<InterestUpdate>();
        Assert.True(update.Full);
        Assert.Equal(InterestTier.Sector, Enumerable.Range(0, update.SectorsLength).Select(i => update.Sectors(i)!.Value).Single(s => s.Sector == 2).Tier);

        var capture = Assert.Single(boss.Connection.SentOf(MsgType.CaptureSet)).Decode<CaptureSet>();
        Assert.Equal([1, 2, 3], Enumerable.Range(0, capture.SectorsLength).Select(i => (int)capture.Sectors(i)!.Value.Sector).Order().ToArray());
        Assert.Empty(alice.Connection.SentOf(MsgType.EntitySpawn));   // nothing before the authority's SectorComplete

        // The authority completes sector 2 for the epoch it was told: the client gets the ships, then the marker.
        await rig.SendAsync(boss, MsgType.SectorComplete, Builder(b => SectorComplete.Pack(b, new SectorCompleteT { Sector = 2, Epoch = capture.Epoch, EntityCount = 2 }).Value));
        Assert.Equal(2, Assert.Single(alice.Connection.SentOf(MsgType.EntitySpawn)).Decode<EntitySpawn>().EntitiesLength);
        Assert.Equal((ushort)2, Assert.Single(alice.Connection.SentOf(MsgType.SectorComplete)).Decode<SectorComplete>().Sector);
        Assert.Empty(boss.Connection.SentOf(MsgType.EntitySpawn));    // the authority is never sent its own world
        Assert.Empty(boss.Connection.SentOf(MsgType.InterestUpdate));

        // A hint without the negotiated capability is ignored.
        var hint = Builder(b => InterestHint.Pack(b, new InterestHintT { TargetSector = 5, EtaMs = 1000 }).Value);
        await rig.SendAsync(alice, MsgType.InterestHint, hint);
        Assert.Equal(1, await rig.Actor.CallAsync(() => interest.Stats.HintsIgnored));

        // New strings reach a node that is in game.
        await rig.SendAsync(boss, MsgType.StringTableAdd, Builder(b => StringTableAdd.Pack(b, new StringTableAddT
        {
            Entries = [new StringEntryT { Index = 1, Kind = StringKind.Macro, Value = "ship_arg_s_fighter_01_a_macro" }],
        }).Value));
        Assert.Single(alice.Connection.SentOf(MsgType.StringTableAdd));
        Assert.Empty(boss.Connection.SentOf(MsgType.StringTableAdd));
    }

    [Fact]
    public async Task ADetachedClientIsForgottenAndStartsFreshWhenItResumes()
    {
        var mirror = new WorldMirror();
        var interest = new InterestManager(mirror, new InterestOptions());
        await using var rig = new ActorRig(modules: [mirror, interest]);
        var boss = await rig.JoinAuthorityAsync();
        await rig.BringInGameAsync(boss);
        var alice = await rig.JoinAsync("Alice");
        await rig.BringInGameAsync(alice);
        var sha = Enumerable.Range(0, 32).Select(i => (byte)(i + 3)).ToArray();
        await rig.SendAsync(boss, MsgType.GalaxyMetadata, Builder(b => GalaxyMetadata.Pack(b, Decode<GalaxyMetadata>(MsgType.GalaxyMetadata, LineGalaxy(sha, 3)).UnPack()).Value));
        await rig.SendAsync(alice, MsgType.PlayerState, Builder(b => PlayerState.Pack(b, new PlayerStateT { Seq = 1, Sector = 2 }).Value));
        Assert.True(await rig.Actor.CallAsync(() => interest.IsActive(alice.PlayerId)));

        alice.Connection.Drop();
        await Task.Delay(50);
        await rig.Actor.FlushAsync();
        Assert.False(await rig.Actor.CallAsync(() => interest.IsActive(alice.PlayerId)));

        var back = await rig.ResumeAsync("Alice", alice);
        await rig.Actor.FlushAsync();
        Assert.True(await rig.Actor.CallAsync(() => interest.IsActive(back.PlayerId)));
        Assert.True(back.Connection.SentOf(MsgType.InterestUpdate).Single().Decode<InterestUpdate>().Full);   // fresh state, full tiers
    }

    /// <summary>Hot path with the interest manager watching: observers and the near grid add nothing to the steady-state allocations.</summary>
    [Fact]
    public void IngestWithTheInterestManagerAttachedStillAllocatesNothing()
    {
        const int Entities = 20_000;
        const int Sectors = 100;
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var mirror = new WorldMirror(time, initialCapacity: Entities);
        var transport = new FakeTransport();
        var manager = new InterestManager(mirror, new InterestOptions { MaxGhosts = 100_000 }, time, transport);
        var sha = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        mirror.HandleForTest(new X4MP.Core.Net.InboundFrame(AsFrame(MsgType.GalaxyMetadata, LineGalaxy(sha, Sectors)), 0), authority: true);

        mirror.Spawn([.. Enumerable.Range(0, Entities).Select(i => Rec((uint)(i + 1), EntityKind.ShipS, (ushort)(1 + (i % Sectors))))]);

        // Four players in sectors 1..4 (all sectors complete, deliveries done), so those sectors have a near grid and subscribers.
        for (int p = 1; p <= 4; p++)
        {
            manager.ClientActivated(p, 0, isAuthority: false);
            mirror.ApplyPlayerState(p, Decode<PlayerState>(MsgType.PlayerState, PlayerStatePayload((uint)p, 0, (ushort)p, 0)));
        }

        time.Advance(TimeSpan.FromSeconds(1));
        manager.Tick(time.GetTimestamp());
        foreach (var rate in manager.LastCaptureSectors.ToArray())
        {
            manager.HandleSectorComplete(Decode<SectorComplete>(
                MsgType.SectorComplete,
                MessageEncoder.EncodePayload(b => SectorComplete.Pack(b, new SectorCompleteT { Sector = rate.Sector, Epoch = manager.CaptureEpoch }), 32)));
        }

        for (int i = 0; i < 400; i++)
        {
            manager.Tick(time.GetTimestamp());
        }

        // 20 ticks of positional updates that stay inside their sector but sweep through the grid cells of the subscribed ones.
        var cycle = new byte[20][];
        for (int t = 0; t < cycle.Length; t++)
        {
            cycle[t] = UpdatePayload((uint)t, t, Enumerable.Range(0, Entities).Select(i =>
                State((uint)(i + 1), (ushort)(1 + (i % Sectors)), (t * 64 * 3000) + i, (i % 7) * 64 * 100, 0)));
        }

        for (int c = 0; c < 3; c++)
        {
            foreach (var payload in cycle)
            {
                mirror.IngestWorldUpdate(payload);
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int c = 0; c < 5; c++)
        {
            foreach (var payload in cycle)
            {
                mirror.IngestWorldUpdate(payload);
            }
        }

        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"with interest observer: {sw.Elapsed.TotalMilliseconds / 100:F3} ms per 20k-entity tick, allocated {allocated} B, grid sectors {manager.ActiveGridSectors}");

        Assert.Equal(4, manager.ActiveGridSectors);
        Assert.True(allocated <= 4096, $"allocated {allocated} bytes");
    }
}
