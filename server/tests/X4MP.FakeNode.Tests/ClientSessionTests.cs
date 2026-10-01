using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode.Tests;

/// <summary>The receiving half of a fake client: ghost set, verification, tombstones, the stale-entry rule and the desync guard.</summary>
public sealed class ClientSessionTests
{
    private static readonly FakeWorld World = new(FakeGalaxy.Generate(42));

    private sealed class Clock
    {
        public double Now { get; set; }
    }

    private static FakeClientSession Session(Clock clock, bool verify = true) => new(new FakeWorld(World.Galaxy), verify, () => clock.Now);

    private static Frame AsFrame(MsgType type, byte[] payload) =>
        new(type, FrameOptions.None, MessageRegistry.Default.GetDescriptor(type).Lane, payload);

    private static int Ship => World.Galaxy.Entities.First(e => !e.IsStation).EntityId;

    private static int Station => World.Galaxy.Entities.First(e => e.IsStation).EntityId;

    private static Frame Spawn(params int[] entityIds) => AsFrame(MsgType.EntitySpawn, MessageEncoder.EncodePayload(
        b => EntitySpawn.Pack(b, new EntitySpawnT
        {
            Entities = [.. entityIds.Select(id => new EntityRecordT
            {
                NetId = FakeNetIds.ToNetId(id), Kind = World.Galaxy.Entities[id - 1].Kind, State = World.GetState(id, 0),
            })],
        }), 512));

    private static Frame Despawn(params int[] entityIds) => AsFrame(MsgType.EntityDespawn, MessageEncoder.EncodePayload(
        b => EntityDespawn.Pack(b, new EntityDespawnT
        {
            Entries = [.. entityIds.Select(id => new DespawnEntryT { NetId = FakeNetIds.ToNetId(id), Reason = DespawnReason.OutOfInterest })],
        }), 128));

    /// <summary>A Replication frame for an entity at <paramref name="timeSeconds"/>; <paramref name="mask"/> picks the fields (values from ground truth).</summary>
    private static Frame Entry(int entityId, double timeSeconds, ReplicationMask mask, int posError = 0)
    {
        var s = World.GetState(entityId, timeSeconds);
        var entry = new ReplicationEntry
        {
            NetId = s.NetId, Mask = mask, Sector = s.Sector, PosX = s.Px + posError, PosY = s.Py, PosZ = s.Pz,
            Yaw = s.Yaw, Pitch = s.Pitch, Roll = s.Roll, VelX = s.Vx, VelY = s.Vy, VelZ = s.Vz, StateFlags = s.Flags, Hull = 255, Shield = 255,
        };
        return AsFrame(MsgType.Replication, MessageEncoder.EncodePayload(
            b => Replication.Pack(b, new ReplicationT
            {
                ServerTick = 1, ServerTimeUs = 0, AuthorityGameTime = timeSeconds, EntryCount = 1, Entries = [.. ReplicationCodec.Encode([entry])],
            }), 128));
    }

    private static Frame Checksum(uint count, ulong hash) => AsFrame(MsgType.InterestChecksum, MessageEncoder.EncodePayload(
        b => InterestChecksum.CreateInterestChecksum(b, 1, count, hash), 48));

    [Fact]
    public void ASpawnMakesAGhostAndAFullEntryThatMatchesGroundTruthIsClean()
    {
        var clock = new Clock();
        var c = Session(clock);
        c.Handle(Spawn(Ship));
        Assert.True(c.IsGhost(FakeNetIds.ToNetId(Ship)));

        c.Handle(Entry(Ship, 3.0, ReplicationMath_Full));
        Assert.Equal(0, c.Errors);
        Assert.Equal(1, c.Verifier.EntriesChecked);
    }

    private const ReplicationMask ReplicationMath_Full =
        ReplicationMask.Sector | ReplicationMask.Pos | ReplicationMask.Rot | ReplicationMask.Vel | ReplicationMask.Flags | ReplicationMask.Status | ReplicationMask.Time;

    [Fact]
    public void AWrongPositionIsAPositionError()
    {
        var c = Session(new Clock());
        c.Handle(Spawn(Ship));
        c.Handle(Entry(Ship, 3.0, ReplicationMath_Full, posError: 640)); // 10 m off
        Assert.Equal(1, c.Errors);
        Assert.Contains(c.Violations, v => v.Kind == "position");
    }

    [Fact]
    public void StateBeforeSpawnIsAViolation()
    {
        var c = Session(new Clock());
        c.Handle(Entry(Ship, 3.0, ReplicationMath_Full));
        Assert.Equal(1, c.Errors);
        Assert.Contains(c.Violations, v => v.Kind == "state-before-spawn");
    }

    [Fact]
    public void APartialEntryBeforeTheFirstFullEntryIsStaleAndIgnored()
    {
        var c = Session(new Clock());
        c.Handle(Spawn(Ship));
        c.Handle(Entry(Ship, 3.0, ReplicationMask.Pos | ReplicationMask.Time)); // describes the old life: a frame queued before the respawn
        Assert.Equal(0, c.Errors);
        Assert.Equal(1, c.StaleEntries);
        Assert.Equal(0, c.Verifier.EntriesChecked);

        c.Handle(Entry(Ship, 3.05, ReplicationMath_Full));
        c.Handle(Entry(Ship, 3.1, ReplicationMask.Pos | ReplicationMask.Time)); // after the first full entry partial entries are normal
        Assert.Equal(0, c.Errors);
        Assert.Equal(2, c.Verifier.EntriesChecked);
        Assert.Equal(1, c.StaleEntries);
    }

    [Fact]
    public void ARespawnWithoutADespawnStartsTheStaleWindowAgain()
    {
        var c = Session(new Clock());
        c.Handle(Spawn(Ship));
        c.Handle(Entry(Ship, 3.0, ReplicationMath_Full));
        c.Handle(Spawn(Ship)); // a resync refreshes the ghost
        c.Handle(Entry(Ship, 4.0, ReplicationMask.Pos | ReplicationMask.Time));
        Assert.Equal(1, c.StaleEntries);
        Assert.Equal(0, c.Errors);
    }

    [Fact]
    public void ADespawnedIdIsTombstonedForFiveSecondsThenItsEntriesAreViolations()
    {
        var clock = new Clock();
        var c = Session(clock);
        c.Handle(Spawn(Ship));
        c.Handle(Entry(Ship, 1.0, ReplicationMath_Full));
        c.Handle(Despawn(Ship));
        Assert.False(c.IsGhost(FakeNetIds.ToNetId(Ship)));

        clock.Now = 4.0;
        c.Handle(Entry(Ship, 4.0, ReplicationMask.Pos | ReplicationMask.Time)); // a frame that was in flight
        Assert.Equal(0, c.Errors);
        Assert.Equal(1, c.TombstonedEntries);

        clock.Now = 6.0;
        c.Handle(Entry(Ship, 6.0, ReplicationMask.Pos | ReplicationMask.Time));
        Assert.Equal(1, c.Errors);
    }

    [Fact]
    public void PersistentEntitiesAreMatchedNotGhosted()
    {
        var c = Session(new Clock());
        c.Handle(Spawn(Station, Ship));
        Assert.Equal(1, c.Ghosts);
        Assert.False(c.IsGhost(FakeNetIds.ToNetId(Station)));
    }

    [Fact]
    public void AMatchingChecksumIsFineAndAMismatchAsksForAResync()
    {
        var c = Session(new Clock());
        c.Handle(Spawn(Ship));
        uint id = FakeNetIds.ToNetId(Ship);

        Assert.Empty(c.Handle(Checksum(1, InterestHash.Mix(id))));
        Assert.Equal(1, c.ChecksumsOk);

        var reply = Assert.Single(c.Handle(Checksum(2, InterestHash.Mix(id) ^ 5)));
        Assert.Equal(MsgType.ResyncRequest, reply.Type);
        var request = MessageRegistry.Default.Decode<ResyncRequest>(AsFrame(reply.Type, reply.Payload)).UnPack();
        Assert.Empty(request.Sectors);
        Assert.Contains("checksum mismatch", request.Reason);
        Assert.Equal(1, c.ChecksumMismatches);
        Assert.Equal(1, c.ResyncsRequested);
        Assert.Equal(0, c.Errors);
    }

    [Fact]
    public void AMismatchThatNeverHealsBecomesAViolationAfterThreeChecksums()
    {
        var c = Session(new Clock());
        c.Handle(Spawn(Ship));
        for (int i = 0; i < 3; i++)
        {
            c.Handle(Checksum(9, 1));
        }

        Assert.Equal(1, c.Errors);
        Assert.Contains(c.Violations, v => v.Kind == "checksum");

        c.Handle(Checksum(1, InterestHash.Mix(FakeNetIds.ToNetId(Ship)))); // healed: the streak ends
        Assert.Equal(1, c.Errors);
    }

    [Fact]
    public void TheTestHookSkewsTheNextChecksumOnly()
    {
        var c = Session(new Clock());
        c.Handle(Spawn(Ship));
        uint id = FakeNetIds.ToNetId(Ship);
        c.ChecksumCountSkew = 1;
        Assert.Single(c.Handle(Checksum(1, InterestHash.Mix(id)))); // the skewed view disagrees
        Assert.Empty(c.Handle(Checksum(1, InterestHash.Mix(id))));  // back to normal
    }

    [Fact]
    public void AGhostThatGetsNothingForFortyFiveSecondsIsStale()
    {
        var clock = new Clock();
        var c = Session(clock);
        c.Handle(Spawn(Ship));
        clock.Now = 30;
        c.CheckStale();
        Assert.Equal(0, c.Errors);
        clock.Now = 46;
        c.CheckStale();
        Assert.Equal(1, c.Errors);
        Assert.Contains(c.Violations, v => v.Kind == "stale-ghost");
        c.CheckStale();
        Assert.Equal(1, c.Errors); // reported once per interval
    }

    [Fact]
    public void WithoutVerifyEntriesAreCountedButNotCheckedAgainstGroundTruth()
    {
        var c = Session(new Clock(), verify: false);
        c.Handle(Spawn(Ship));
        c.Handle(Entry(Ship, 3.0, ReplicationMath_Full, posError: 6400));
        Assert.Equal(0, c.Errors);
        Assert.Equal(1, c.ReplicationEntries);
        Assert.Equal(0, c.Verifier.EntriesChecked);
    }

    [Fact]
    public void EntriesForNetIdsOutsideTheGalaxyAreForeignNotErrors()
    {
        var c = Session(new Clock());
        uint foreign = (uint)World.Galaxy.Entities.Count + 5;
        c.Handle(AsFrame(MsgType.EntitySpawn, MessageEncoder.EncodePayload(
            b => EntitySpawn.Pack(b, new EntitySpawnT { Entities = [new EntityRecordT { NetId = foreign, Kind = EntityKind.ShipM, State = new EntityStateT { NetId = foreign, Sector = 1 } }] }), 128)));
        var entry = new ReplicationEntry { NetId = foreign, Mask = ReplicationMath_Full };
        c.Handle(AsFrame(MsgType.Replication, MessageEncoder.EncodePayload(
            b => Replication.Pack(b, new ReplicationT { AuthorityGameTime = 1, EntryCount = 1, Entries = [.. ReplicationCodec.Encode([entry])] }), 128)));
        Assert.Equal(0, c.Errors);
        Assert.Equal(1, c.Verifier.ForeignEntries);
    }

    [Fact]
    public void TheInterestHashIsTheSplitmix64FinaliserAndOrderIndependent()
    {
        Assert.Equal(InterestHash.Of([1u, 2u, 3u]), InterestHash.Of([3u, 1u, 2u]));
        Assert.NotEqual(InterestHash.Of([1u, 2u]), InterestHash.Of([1u, 3u]));
        Assert.Equal(0UL, InterestHash.Of([]));
        // reference values of splitmix64 with the input as the state increment, for the C++ port to check against
        Assert.Equal(0xE220A8397B1DCDAFUL, InterestHash.Mix(0));
        Assert.Equal(InterestHash.Mix(7) ^ InterestHash.Mix(9), InterestHash.Of([7u, 9u]));
    }
}
