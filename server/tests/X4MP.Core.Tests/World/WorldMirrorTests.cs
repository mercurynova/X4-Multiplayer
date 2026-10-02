using X4MP.Core.World;
using X4MP.Proto;
using static X4MP.Core.Tests.World.WorldKit;

namespace X4MP.Core.Tests.World;

public sealed class WorldMirrorTests
{
    private sealed class Recorder : IWorldObserver
    {
        public List<string> Log { get; } = [];

        public void OnEntitySpawned(MirrorEntity entity, bool isNew, ushort previousSector, ulong journalSeq) =>
            Log.Add($"spawn:{entity.NetId}:{isNew}:{previousSector}:{journalSeq}");

        public void OnEntityStateChanged(MirrorEntity entity, ushort previousSector, StateChange change) =>
            Log.Add($"state:{entity.NetId}:{previousSector}->{entity.Sector}:{change}");

        public void OnEntityChanged(MirrorEntity entity, EntityChange change, ulong journalSeq) =>
            Log.Add($"change:{entity.NetId}:{change.Fields}:{journalSeq}");

        public void OnEntityDespawned(MirrorEntity entity, DespawnReason reason, uint killerNetId, ulong journalSeq) =>
            Log.Add($"despawn:{entity.NetId}:{reason}:{killerNetId}:{journalSeq}");

        public void OnSectorEvicted(ushort sector, int removed) => Log.Add($"evict:{sector}:{removed}");
    }

    [Fact]
    public void SpawnStoresRecordAndStateAndClassifiesPersistence()
    {
        var mirror = new WorldMirror();
        mirror.Spawn(
            Rec(1, EntityKind.ShipM, 5, 640, 128, -64, ownerTeam: 2, name: "Hauler"),
            Rec(2, EntityKind.Station, 5, 0, 0, 0, ownerTeam: 1),
            Rec(3, EntityKind.ShipXL, 6));

        Assert.Equal(3, mirror.Count);
        Assert.Equal(1, mirror.PersistentCount);
        Assert.Equal(2, mirror.HotCount);
        Assert.True(mirror.TryGet(1, out var ship));
        Assert.Equal("Hauler", ship.Name);
        Assert.Equal((ushort)2, ship.OwnerTeam);
        Assert.Equal((5, 640, 128, -64), ((int)ship.Sector, ship.Px, ship.Py, ship.Pz));
        Assert.False(ship.IsPersistent);
        Assert.True(mirror.TryGet(2, out var station));
        Assert.True(station.IsPersistent);

        // The sector index holds transient entities only and counts by size class.
        Assert.Equal([1u], mirror.TransientIn(5).ToArray().Select(e => e.NetId));
        Assert.Equal(new SectorCounts(1, 0), mirror.CountsIn(5));
        Assert.Equal(new SectorCounts(0, 1), mirror.CountsIn(6));
    }

    [Fact]
    public void InvalidNetIdsAreRejected()
    {
        var mirror = new WorldMirror();
        mirror.Spawn(Rec(0, EntityKind.ShipS, 1), Rec(uint.MaxValue, EntityKind.ShipS, 1));
        Assert.Equal(0, mirror.Count);
        Assert.Equal(2, mirror.MalformedFrames);
    }

    [Fact]
    public void WorldUpdateBumpsVersionOnlyWhenTheQuantisedStateChanged()
    {
        var mirror = new WorldMirror();
        mirror.Spawn(Rec(1, EntityKind.ShipS, 3, 100));
        Assert.True(mirror.TryGet(1, out var e));
        uint v0 = e.Version;

        var same = mirror.Update(State(1, 3, 100));
        Assert.Equal(new IngestResult(1, 0, 0, false), same);
        Assert.Equal(v0, e.Version);

        var moved = mirror.Update(State(1, 3, 164, vx: 12));
        Assert.Equal(1, moved.Changed);
        Assert.Equal(v0 + 1, e.Version);
        Assert.Equal((164, (short)12), (e.Px, e.Vx));
    }

    [Fact]
    public void UnknownNetIdsInAWorldUpdateAreCountedNotCreated()
    {
        var mirror = new WorldMirror();
        mirror.Spawn(Rec(1, EntityKind.ShipS, 3));
        var result = mirror.Update(State(1, 3, 1), State(99, 3, 1));
        Assert.Equal(new IngestResult(1, 1, 1, false), result);
        Assert.Equal(1, mirror.Count);
        Assert.Equal(1, mirror.UnknownStateUpdates);
    }

    [Fact]
    public void MalformedWorldUpdatesAreReportedNotThrown()
    {
        var mirror = new WorldMirror();
        Assert.True(mirror.IngestWorldUpdate(new byte[] { 1, 2, 3 }).Malformed);
        var good = UpdatePayload(1, 1.0, [State(1, 1, 1), State(2, 1, 1)]);
        Assert.True(mirror.IngestWorldUpdate(good.AsSpan(0, good.Length - 20)).Malformed);
        var garbage = new byte[64];
        garbage[0] = 0xFF;
        Assert.True(mirror.IngestWorldUpdate(garbage).Malformed);
    }

    [Fact]
    public void FastPathReadsTheSameValuesAsTheGeneratedAccessors()
    {
        var mirror = new WorldMirror();
        mirror.Spawn(Rec(7, EntityKind.ShipL, 9));
        var state = new EntityStateT
        {
            NetId = 7, Sector = 9, Flags = 0x0102, Px = -123456, Py = 7, Pz = 99999, Yaw = -32768, Pitch = 12345, Roll = -1, Vx = -300, Vy = 4, Vz = 32767,
        };
        mirror.IngestWorldUpdate(UpdatePayload(77, 123.5, [state]));
        Assert.True(mirror.TryGet(7, out var e));
        Assert.Equal((state.Flags, state.Px, state.Py, state.Pz), (e.Flags, e.Px, e.Py, e.Pz));
        Assert.Equal((state.Yaw, state.Pitch, state.Roll), (e.Yaw, e.Pitch, e.Roll));
        Assert.Equal((state.Vx, state.Vy, state.Vz), (e.Vx, e.Vy, e.Vz));
        Assert.Equal(77u, mirror.AuthorityTick);
        Assert.Equal(123.5, mirror.AuthorityGameTime);
    }

    [Fact]
    public void ASpawnCarriesItsOwnGameTimeAndFallsBackToTheLatestWorldUpdateWhenZero()
    {
        var mirror = new WorldMirror();
        mirror.IngestWorldUpdate(UpdatePayload(10, 500.0, []));
        mirror.ApplySpawn(Decode<EntitySpawn>(MsgType.EntitySpawn, SpawnPayloadAt(480.5, Rec(1, EntityKind.ShipL, 9))));
        mirror.ApplySpawn(Decode<EntitySpawn>(MsgType.EntitySpawn, SpawnPayloadAt(0, Rec(2, EntityKind.ShipL, 9))));
        Assert.True(mirror.TryGet(1, out var withTime));
        Assert.True(mirror.TryGet(2, out var legacy));
        Assert.Equal(480.5, withTime.SampleGameTime); // not the (newer, unrelated) clock of the last update
        Assert.Equal(500.0, legacy.SampleGameTime); // an old sender: as fresh as the latest world update
    }

    [Fact]
    public void AReorderedOlderWorldUpdateCannotRollTheClockBack()
    {
        var mirror = new WorldMirror();
        mirror.Spawn(Rec(7, EntityKind.ShipL, 9));
        mirror.IngestWorldUpdate(UpdatePayload(100, 50.0, [State(7, 9, 1000)]));
        var late = mirror.IngestWorldUpdate(UpdatePayload(90, 45.0, [State(7, 9, 5)]));
        Assert.Equal(0, late.Applied);
        Assert.False(late.Malformed);
        Assert.Equal(1, mirror.StaleWorldUpdates);
        Assert.Equal(100u, mirror.AuthorityTick);
        Assert.Equal(50.0, mirror.AuthorityGameTime);
        Assert.True(mirror.TryGet(7, out var e));
        Assert.Equal(1000, e.Px);
        // equal and newer ones still apply
        Assert.Equal(1, mirror.IngestWorldUpdate(UpdatePayload(100, 50.0, [State(7, 9, 1001)])).Applied);
        Assert.Equal(1, mirror.IngestWorldUpdate(UpdatePayload(101, 50.5, [State(7, 9, 1002)])).Applied);
        // a reloaded save (lower game time, tick continuing) is not a late datagram
        Assert.Equal(1, mirror.IngestWorldUpdate(UpdatePayload(102, 10.0, [State(7, 9, 1003)])).Applied);
        // a reattached authority restarts its sequence
        mirror.ResetClockGuard();
        Assert.Equal(1, mirror.IngestWorldUpdate(UpdatePayload(1, 1.0, [State(7, 9, 1004)])).Applied);
        Assert.Equal(1u, mirror.AuthorityTick);
    }

    [Fact]
    public void SectorChangeMovesTheEntityBetweenSectorIndexesAndNotifiesObservers()
    {
        var mirror = new WorldMirror();
        var observer = new Recorder();
        mirror.AddObserver(observer);
        mirror.Spawn(Rec(1, EntityKind.ShipS, 3), Rec(2, EntityKind.ShipL, 3));
        observer.Log.Clear();

        mirror.Update(State(1, 4, 5));

        Assert.Equal(1, mirror.TransientIn(3).Length);
        Assert.Equal([1u], mirror.TransientIn(4).ToArray().Select(e => e.NetId));
        Assert.Equal(new SectorCounts(0, 1), mirror.CountsIn(3));
        Assert.Equal(new SectorCounts(1, 0), mirror.CountsIn(4));
        Assert.Equal(["state:1:3->4:Position, Sector"], observer.Log);
    }

    [Fact]
    public void DespawnRemovesFromEverythingAndRecyclesTheRecord()
    {
        var mirror = new WorldMirror();
        var observer = new Recorder();
        mirror.AddObserver(observer);
        mirror.Spawn(Rec(1, EntityKind.ShipS, 3), Rec(2, EntityKind.ShipS, 3));
        observer.Log.Clear();

        mirror.Despawn(DespawnReason.Destroyed, 1);

        Assert.False(mirror.Contains(1));
        Assert.Equal([2u], mirror.TransientIn(3).ToArray().Select(e => e.NetId));
        Assert.Equal(1, mirror.HotCount);
        Assert.Equal(["despawn:1:Destroyed:0:0"], observer.Log);

        // The slot index of the entity that moved into the freed place stays consistent.
        mirror.Despawn(DespawnReason.Removed, 2);
        Assert.Equal(0, mirror.TransientIn(3).Length);
        Assert.Equal(0, mirror.Count);
    }

    [Fact]
    public void PersistentMutationsAreJournaledInOrderAndStampedForForwarding()
    {
        var mirror = new WorldMirror();
        var observer = new Recorder();
        mirror.AddObserver(observer);

        mirror.Spawn(Rec(10, EntityKind.Station, 2, ownerTeam: 1));
        mirror.ApplyChange(Decode<EntityChange>(MsgType.EntityChange, ChangePayload(10, ChangeField.OwnerTeam | ChangeField.OwnerPlayer, ownerTeam: 2, ownerPlayer: 5)));
        mirror.ApplyCargo(Decode<EntityCargo>(MsgType.EntityCargo, CargoPayload(10, (3u, 500))));
        mirror.Despawn(DespawnReason.Removed, 10);

        Assert.Equal(
            ["spawn:10:True:0:1", "change:10:OwnerTeam, OwnerPlayer:2", "despawn:10:Removed:0:4"],
            observer.Log.Where(l => !l.StartsWith("state", StringComparison.Ordinal)).ToArray());
        Assert.Equal(
            [MsgType.EntitySpawn, MsgType.EntityChange, MsgType.EntityCargo, MsgType.EntityDespawn],
            mirror.Journal.Entries.Select(r => r.Kind));
        Assert.Equal([1UL, 2, 3, 4], mirror.Journal.Entries.Select(r => r.Seq));
    }

    [Fact]
    public void TransientMutationsAreNotJournaled()
    {
        var mirror = new WorldMirror();
        mirror.Spawn(Rec(1, EntityKind.ShipM, 2));
        mirror.ApplyChange(Decode<EntityChange>(MsgType.EntityChange, ChangePayload(1, ChangeField.OwnerTeam, ownerTeam: 3)));
        mirror.Despawn(DespawnReason.Destroyed, 1);
        Assert.Equal(0, mirror.Journal.Count);
    }

    [Fact]
    public void EntityChangeUpdatesAttributesAndReclassifiesTheSizeClass()
    {
        var mirror = new WorldMirror();
        mirror.Spawn(Rec(1, EntityKind.ShipM, 4));
        Assert.Equal(new SectorCounts(1, 0), mirror.CountsIn(4));

        mirror.ApplyChange(Decode<EntityChange>(MsgType.EntityChange, ChangePayload(1, ChangeField.Name | ChangeField.Controller, name: "Pilot ship")));
        // Controller was left 0 in the payload, so the ship stays small; a player-controlled ship counts as large.
        Assert.True(mirror.TryGet(1, out var e));
        Assert.Equal("Pilot ship", e.Name);

        var payload = MessageEncoderHelper.Change(1, ChangeField.Controller, controller: 7);
        mirror.ApplyChange(Decode<EntityChange>(MsgType.EntityChange, payload));
        Assert.Equal((ushort)7, e.ControllerPlayer);
        Assert.Equal(new SectorCounts(0, 1), mirror.CountsIn(4));
    }

    [Fact]
    public void StatusBatchUpdatesHullAndShield()
    {
        var mirror = new WorldMirror();
        mirror.Spawn(Rec(1, EntityKind.ShipM, 4));
        var result = mirror.IngestStatusBatch(StatusPayload((1, 100, 50), (2, 1, 1)));
        Assert.Equal(new IngestResult(1, 1, 1, false), result);
        Assert.True(mirror.TryGet(1, out var e));
        Assert.Equal((100, 50), (e.Hull, e.Shield));
    }

    [Fact]
    public void RespawnOfAnExistingNetIdRefreshesInPlaceAndReportsThePreviousSector()
    {
        var mirror = new WorldMirror();
        var observer = new Recorder();
        mirror.AddObserver(observer);
        mirror.Spawn(Rec(1, EntityKind.ShipS, 3));
        observer.Log.Clear();

        mirror.Spawn(Rec(1, EntityKind.ShipS, 8, name: "again"));

        Assert.Equal(1, mirror.Count);
        Assert.Equal(["spawn:1:False:3:0"], observer.Log);
        Assert.Equal(0, mirror.TransientIn(3).Length);
        Assert.Equal(1, mirror.TransientIn(8).Length);
    }

    [Fact]
    public void PlayerStateDrivesTheBoundShipAndIgnoresOtherPlayers()
    {
        var mirror = new WorldMirror();
        mirror.Spawn(Rec(50, EntityKind.ShipM, 2, 10, controller: 4, origin: EntityOrigin.PlayerShip));

        mirror.ApplyPlayerState(4, Decode<PlayerState>(MsgType.PlayerState, PlayerStatePayload(1, 50, 2, 999)));
        Assert.True(mirror.TryGet(50, out var ship));
        Assert.Equal(999, ship.Px);
        Assert.Equal((200, 100), (ship.Hull, ship.Shield));
        Assert.True(mirror.TryGetPlayerShip(4, out var state));
        Assert.Equal(50u, state.NetId);

        // Player 9 claims someone else's ship: stored as its own state, the ship is untouched.
        mirror.ApplyPlayerState(9, Decode<PlayerState>(MsgType.PlayerState, PlayerStatePayload(1, 50, 2, -5)));
        Assert.Equal(999, ship.Px);

        // An older sequence number is dropped.
        mirror.ApplyPlayerState(4, Decode<PlayerState>(MsgType.PlayerState, PlayerStatePayload(0, 50, 2, 1)));
        Assert.Equal(999, ship.Px);
        Assert.Equal(2, mirror.PlayerShipCount);
    }

    [Fact]
    public void EvictingASectorDropsItsTransientsAndKeepsPersistentAndPlayerShips()
    {
        var mirror = new WorldMirror();
        var observer = new Recorder();
        mirror.AddObserver(observer);
        mirror.Spawn(
            Rec(1, EntityKind.ShipS, 3), Rec(2, EntityKind.ShipL, 3), Rec(3, EntityKind.Station, 3),
            Rec(4, EntityKind.ShipM, 3, controller: 2, origin: EntityOrigin.PlayerShip));
        observer.Log.Clear();

        Assert.Equal(2, mirror.EvictTransient(3));

        Assert.False(mirror.Contains(1));
        Assert.False(mirror.Contains(2));
        Assert.True(mirror.Contains(3));
        Assert.True(mirror.Contains(4));
        Assert.Equal(["evict:3:2"], observer.Log);
    }

    [Fact]
    public void SessionEndClearsEverything()
    {
        var mirror = new WorldMirror();
        mirror.Spawn(Rec(1, EntityKind.ShipS, 3), Rec(2, EntityKind.Station, 3));
        ((Core.Session.ISessionModule)mirror).OnSessionPhaseChanged(Proto.SessionPhase.Running, Proto.SessionPhase.Ended);
        Assert.Equal(0, mirror.Count);
        Assert.Equal(0, mirror.PersistentCount);
        Assert.Equal(0, mirror.Journal.Count);
        Assert.Equal(0, mirror.TransientIn(3).Length);
    }

    [Fact]
    public void PooledRecordsAreFullyResetOnReuse()
    {
        var mirror = new WorldMirror();
        mirror.Spawn(Rec(1, EntityKind.ShipL, 3, 5, name: "old", ownerTeam: 3));
        mirror.Despawn(DespawnReason.Removed, 1);
        mirror.Spawn(Rec(2, EntityKind.ShipS, 4));
        Assert.True(mirror.TryGet(2, out var e));
        Assert.Equal((ushort)0, e.OwnerTeam);
        Assert.Equal(0, e.Px);
        Assert.Null(e.Cargo);
    }
}

internal static class MessageEncoderHelper
{
    public static byte[] Change(uint netId, ChangeField fields, ushort controller = 0) =>
        X4MP.Protocol.MessageEncoder.EncodePayload(
            b => EntityChange.Pack(b, new EntityChangeT { NetId = netId, Fields = fields, ControllerPlayer = controller }), 96);
}
