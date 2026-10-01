using Google.FlatBuffers;
using X4MP.Core.Session;
using X4MP.Core.Tests.Session;
using X4MP.Core.World;
using X4MP.Proto;
using static X4MP.Core.Tests.World.WorldKit;

namespace X4MP.Core.Tests.World;

/// <summary>The mirror attached to a real <see cref="SessionActor"/>: frames arrive through the gateway rules and the actor loop.</summary>
public sealed class WorldMirrorModuleTests
{
    private static FlatBufferBuilder Spawn(params EntityRecordT[] records)
    {
        var fbb = new FlatBufferBuilder(512);
        fbb.Finish(EntitySpawn.Pack(fbb, new EntitySpawnT { Entities = [.. records] }).Value);
        return fbb;
    }

    private static FlatBufferBuilder Update(uint tick, params EntityStateT[] states)
    {
        var fbb = new FlatBufferBuilder(512);
        fbb.Finish(WorldUpdate.Pack(fbb, new WorldUpdateT { AuthorityTick = tick, GameTime = tick, States = [.. states] }).Value);
        return fbb;
    }

    private static FlatBufferBuilder PlayerState(uint seq, uint netId, ushort sector, int px)
    {
        var fbb = new FlatBufferBuilder(128);
        fbb.Finish(X4MP.Proto.PlayerState.Pack(fbb, new PlayerStateT { Seq = seq, NetId = netId, Sector = sector, Px = px }).Value);
        return fbb;
    }

    [Fact]
    public async Task TheAuthoritysWorldMessagesReachTheMirrorThroughTheActor()
    {
        var mirror = new WorldMirror();
        await using var rig = new ActorRig(modules: mirror);
        var boss = await rig.JoinAuthorityAsync();
        await rig.BringInGameAsync(boss);

        await rig.SendAsync(boss, MsgType.EntitySpawn, Spawn(Rec(1, EntityKind.ShipS, 3, 5), Rec(2, EntityKind.Station, 3)));
        await rig.SendAsync(boss, MsgType.WorldUpdate, Update(1, State(1, 3, 77)));

        var (count, px, persistent) = await rig.Actor.CallAsync(() => (mirror.Count, mirror.TryGet(1, out var e) ? e.Px : -1, mirror.PersistentCount));
        Assert.Equal((2, 77, 1), (count, px, persistent));
    }

    [Fact]
    public async Task ClientsCannotWriteTheWorldButTheirPlayerStateIsMirrored()
    {
        var mirror = new WorldMirror();
        await using var rig = new ActorRig(modules: mirror);
        var boss = await rig.JoinAuthorityAsync();
        await rig.BringInGameAsync(boss);
        var alice = await rig.JoinAsync("Alice");
        await rig.BringInGameAsync(alice);

        await rig.SendAsync(boss, MsgType.EntitySpawn, Spawn(Rec(50, EntityKind.ShipM, 2, 0, controller: (ushort)alice.PlayerId, origin: EntityOrigin.PlayerShip)));
        await rig.SendAsync(alice, MsgType.PlayerState, PlayerState(1, 50, 2, 4096));

        var (px, shipSector) = await rig.Actor.CallAsync(() =>
            (mirror.TryGet(50, out var e) ? e.Px : -1, mirror.TryGetPlayerShip(alice.PlayerId, out var s) ? s.Sector : 0));
        Assert.Equal((4096, (ushort)2), (px, shipSector));

        // The player's ship state is dropped when the player leaves for good.
        await rig.DisconnectAsync(alice, DisconnectCode.ClientQuit);
        Assert.Equal(0, await rig.Actor.CallAsync(() => mirror.PlayerShipCount));
    }

}
