using Google.FlatBuffers;
using X4MP.Core.Session;
using X4MP.Core.Tests.Session;
using X4MP.Core.World;
using X4MP.Proto;
using static X4MP.Core.Tests.World.WorldKit;

namespace X4MP.Core.Tests.World;

/// <summary>The strategic map module attached to a real <see cref="SessionActor"/> over fake connections.</summary>
public sealed class MapModuleTests
{
    private static FlatBufferBuilder Builder(Func<FlatBufferBuilder, int> pack)
    {
        var fbb = new FlatBufferBuilder(512);
        fbb.Finish(pack(fbb));
        return fbb;
    }

    private static FlatBufferBuilder GalaxyFrame(byte[] sha, int sectors)
    {
        var message = Decode<GalaxyMetadata>(MsgType.GalaxyMetadata, LineGalaxy(sha, sectors)).UnPack();
        return Builder(b => GalaxyMetadata.Pack(b, message).Value);
    }

    private static FlatBufferBuilder KnownSectorsFrame(
        ulong revision, MapSectorT[] sectors, params MapLinkT[] links) =>
        Builder(b => KnownSectors.Pack(b, new KnownSectorsT
        {
            Revision = revision,
            Sectors = [.. sectors],
            Links = [.. links],
        }).Value);

    private static MapSectorT Sector(ushort index, string macro, string owner = "", float x = 0) => new()
    {
        Index = index,
        Macro = macro,
        ClusterMacro = "cluster",
        Name = macro,
        Owner = owner,
        GalaxyPos = new Vec3fT { X = x },
    };

    private static MapLinkT Link(ushort from, ushort to) =>
        new() { From = from, To = to, Kind = LinkKind.Gate };

    [Fact]
    public async Task FirstMapIsSnapshottedAndALateJoinerGetsTheSnapshotAndGalaxy()
    {
        var mirror = new WorldMirror();
        var map = new MapModule(mirror);
        await using var rig = new ActorRig(modules: [mirror, map]);
        var boss = await rig.JoinAuthorityAsync();

        await rig.SendAsync(boss, MsgType.GalaxyMetadata, GalaxyFrame([.. Enumerable.Repeat((byte)7, 32)], 2));
        await rig.SendAsync(boss, MsgType.KnownSectors, KnownSectorsFrame(
            1,
            [Sector(1, "sector_001_macro", "argon", 1.5f), Sector(2, "sector_002_macro", "xenon", 2.5f)],
            Link(1, 2)));

        var bossSnapshot = Assert.Single(boss.Connection.SentOf(MsgType.MapSnapshot)).Decode<MapSnapshot>();
        Assert.Equal(1UL, bossSnapshot.Version);
        Assert.Equal(2, bossSnapshot.SectorsLength);
        Assert.Single(boss.Connection.SentOf(MsgType.GalaxyMetadata));
        Assert.Equal(2, await rig.Actor.CallAsync(() => map.SectorCount));

        // The map is not sent to the authority; it learns nothing from its own message.
        Assert.Empty(boss.Connection.SentOf(MsgType.MapDelta));

        // A late joiner receives the cached galaxy and the current map snapshot on attach.
        var alice = await rig.JoinAsync("Alice");
        Assert.Single(alice.Connection.SentOf(MsgType.GalaxyMetadata));
        var snapshot = Assert.Single(alice.Connection.SentOf(MsgType.MapSnapshot)).Decode<MapSnapshot>();
        Assert.Equal(1UL, snapshot.Version);
        Assert.Equal(2, snapshot.SectorsLength);
        Assert.Equal("argon", snapshot.Sectors(0)!.Value.Owner);
    }

    [Fact]
    public async Task ChangedMapBroadcastsADeltaWithUpsertsAndRemovals()
    {
        var mirror = new WorldMirror();
        var map = new MapModule(mirror);
        await using var rig = new ActorRig(modules: [mirror, map]);
        var boss = await rig.JoinAuthorityAsync();
        var alice = await rig.JoinAsync("Alice");

        await rig.SendAsync(boss, MsgType.KnownSectors, KnownSectorsFrame(
            1,
            [Sector(1, "a"), Sector(2, "b", "argon"), Sector(3, "c")],
            Link(1, 2), Link(2, 3)));
        Assert.Single(boss.Connection.SentOf(MsgType.MapSnapshot));
        Assert.Single(alice.Connection.SentOf(MsgType.MapSnapshot));

        // Sector 2 changes owner; sector 3 disappears with its link (a delta removes links incident to a removed
        // sector). The 1-2 link stays, so the whole update is expressible as a delta.
        await rig.SendAsync(boss, MsgType.KnownSectors, KnownSectorsFrame(
            2,
            [Sector(1, "a"), Sector(2, "b", "xenon")],
            Link(1, 2)));

        var delta = Assert.Single(boss.Connection.SentOf(MsgType.MapDelta)).Decode<MapDelta>();
        Assert.Equal(2UL, delta.Version);
        var upsert = Assert.Single(Enumerable.Range(0, delta.SectorsLength).Select(i => delta.Sectors(i)!.Value));
        Assert.Equal((ushort)2, upsert.Index);
        Assert.Equal("xenon", upsert.Owner);
        Assert.Equal(0, delta.LinksLength);
        Assert.Equal([3], Enumerable.Range(0, delta.RemovedSectorsLength).Select(i => (int)delta.RemovedSectors(i)).ToArray());
        Assert.Single(alice.Connection.SentOf(MsgType.MapDelta));
        // T1.7 parity: every node gets the same bytes, not merely a delta of the same version.
        Assert.Equal(boss.Connection.SentOf(MsgType.MapSnapshot)[0].Payload, alice.Connection.SentOf(MsgType.MapSnapshot)[0].Payload);
        Assert.Equal(boss.Connection.SentOf(MsgType.MapDelta)[0].Payload, alice.Connection.SentOf(MsgType.MapDelta)[0].Payload);
        Assert.Single(boss.Connection.SentOf(MsgType.MapSnapshot));
        Assert.Equal(2, await rig.Actor.CallAsync(() => map.SectorCount));
    }

    [Fact]
    public async Task RemovingALinkBetweenLiveSectorsForcesASnapshot()
    {
        var mirror = new WorldMirror();
        var map = new MapModule(mirror);
        await using var rig = new ActorRig(modules: [mirror, map]);
        var boss = await rig.JoinAuthorityAsync();

        await rig.SendAsync(boss, MsgType.KnownSectors, KnownSectorsFrame(
            1,
            [Sector(1, "a"), Sector(2, "b"), Sector(3, "c")],
            Link(1, 2), Link(2, 3)));
        Assert.Single(boss.Connection.SentOf(MsgType.MapSnapshot));

        // A delta cannot remove one link between two live sectors, so the module resends the whole map.
        await rig.SendAsync(boss, MsgType.KnownSectors, KnownSectorsFrame(
            2,
            [Sector(1, "a"), Sector(2, "b"), Sector(3, "c")],
            Link(1, 2)));

        var snapshots = boss.Connection.SentOf(MsgType.MapSnapshot);
        Assert.Equal(2, snapshots.Count);
        var latest = snapshots[^1].Decode<MapSnapshot>();
        Assert.Equal(2UL, latest.Version);
        Assert.Equal(3, latest.SectorsLength);
        Assert.Equal(1, latest.LinksLength);
        Assert.Empty(boss.Connection.SentOf(MsgType.MapDelta));
    }

    [Fact]
    public async Task StaleAndInvalidMapsAreIgnored()
    {
        var mirror = new WorldMirror();
        var map = new MapModule(mirror);
        await using var rig = new ActorRig(modules: [mirror, map]);
        var boss = await rig.JoinAuthorityAsync();

        await rig.SendAsync(boss, MsgType.KnownSectors, KnownSectorsFrame(
            5,
            [Sector(1, "a", "argon"), Sector(2, "b")],
            Link(1, 2)));
        Assert.Single(boss.Connection.SentOf(MsgType.MapSnapshot));

        // A delayed map from a previous authority connection.
        await rig.SendAsync(boss, MsgType.KnownSectors, KnownSectorsFrame(
            4,
            [Sector(1, "a", "xenon"), Sector(2, "b")],
            Link(1, 2)));

        // Zero/repeated sector indices, a link to an unknown sector, and an empty map are all rejected.
        await rig.SendAsync(boss, MsgType.KnownSectors, KnownSectorsFrame(
            6,
            [Sector(1, "a"), Sector(1, "b")]));
        await rig.SendAsync(boss, MsgType.KnownSectors, KnownSectorsFrame(
            7,
            [Sector(1, "a"), Sector(2, "b")],
            Link(1, 9)));
        await rig.SendAsync(boss, MsgType.KnownSectors, KnownSectorsFrame(8, []));

        Assert.Empty(boss.Connection.SentOf(MsgType.MapDelta));
        Assert.Single(boss.Connection.SentOf(MsgType.MapSnapshot));
        Assert.Equal(1UL, await rig.Actor.CallAsync(() => map.Version));
        Assert.Equal(2, await rig.Actor.CallAsync(() => map.SectorCount));

        // A valid newer map is applied after the rejected ones.
        await rig.SendAsync(boss, MsgType.KnownSectors, KnownSectorsFrame(
            9,
            [Sector(1, "a", "argon"), Sector(2, "b", "xenon")],
            Link(1, 2)));
        var delta = Assert.Single(boss.Connection.SentOf(MsgType.MapDelta)).Decode<MapDelta>();
        Assert.Equal(2UL, delta.Version);
        Assert.Single(Enumerable.Range(0, delta.SectorsLength));
        Assert.Equal(0, delta.RemovedSectorsLength);
        Assert.Equal(2UL, await rig.Actor.CallAsync(() => map.Version));
    }

    [Fact]
    public async Task GalaxyMetadataIsForwardedWhenItChanges()
    {
        var mirror = new WorldMirror();
        var map = new MapModule(mirror);
        await using var rig = new ActorRig(modules: [mirror, map]);
        var boss = await rig.JoinAuthorityAsync();

        await rig.SendAsync(boss, MsgType.GalaxyMetadata, GalaxyFrame([.. Enumerable.Repeat((byte)1, 32)], 2));
        await rig.SendAsync(boss, MsgType.GalaxyMetadata, GalaxyFrame([.. Enumerable.Repeat((byte)2, 32)], 3));

        var forwarded = boss.Connection.SentOf(MsgType.GalaxyMetadata);
        Assert.Equal(2, forwarded.Count);
        Assert.Equal(3, forwarded[^1].Decode<GalaxyMetadata>().SectorsLength);
    }
}
