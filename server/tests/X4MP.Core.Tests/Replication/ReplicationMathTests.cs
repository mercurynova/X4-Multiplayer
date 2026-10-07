using Google.FlatBuffers;
using X4MP.Core.Interest;
using X4MP.Core.Replication;
using X4MP.Core.Settings;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;
using static X4MP.Core.Tests.World.WorldKit;
using ReplicationMsg = X4MP.Proto.Replication;

namespace X4MP.Core.Tests.Replication;

/// <summary>The stateless parts of replication: delta masks, sizes, the priority accumulator, the keyframe schedule and the frame layout.</summary>
public sealed class ReplicationMathTests
{
    private static MirrorEntity Entity(Action<WorldMirror>? setup = null)
    {
        var mirror = new WorldMirror();
        mirror.Spawn(Rec(7, EntityKind.ShipS, 2, px: 640, py: 64, pz: -64));
        setup?.Invoke(mirror);
        Assert.True(mirror.TryGet(7, out var e));
        return e;
    }

    private static Baseline BaselineOf(MirrorEntity e) => new()
    {
        Sector = e.Sector, Px = e.Px, Py = e.Py, Pz = e.Pz, Yaw = e.Yaw, Pitch = e.Pitch, Roll = e.Roll,
        Vx = e.Vx, Vy = e.Vy, Vz = e.Vz, Flags = e.Flags, Hull = e.Hull, Shield = e.Shield,
    };

    [Fact]
    public void AnUnchangedEntityHasAnEmptyDelta()
    {
        var e = Entity();
        Assert.Equal(ReplicationMask.None, ReplicationMath.DeltaMask(BaselineOf(e), e));
    }

    [Theory]
    [InlineData("sector", ReplicationMask.Sector)]
    [InlineData("pos", ReplicationMask.Pos | ReplicationMask.Time)]
    [InlineData("rot", ReplicationMask.Rot | ReplicationMask.Time)]
    [InlineData("vel", ReplicationMask.Vel | ReplicationMask.Time)]
    [InlineData("flags", ReplicationMask.Flags)]
    [InlineData("status", ReplicationMask.Status)]
    public void EachChangedFieldSetsItsBitAndTimeOnlyComesWithMovingFields(string field, ReplicationMask expected)
    {
        var e = Entity();
        var b = BaselineOf(e);
        switch (field)
        {
            case "sector": b.Sector++; break;
            case "pos": b.Pz += 1; break;
            case "rot": b.Roll += 1; break;
            case "vel": b.Vy -= 1; break;
            case "flags": b.Flags ^= 4; break;
            default: b.Hull--; break;
        }

        Assert.Equal(expected, ReplicationMath.DeltaMask(b, e));
    }

    [Fact]
    public void SeveralChangedFieldsCombine()
    {
        var e = Entity();
        var b = BaselineOf(e);
        b.Px++;
        b.Shield--;
        b.Sector++;
        Assert.Equal(
            ReplicationMask.Sector | ReplicationMask.Pos | ReplicationMask.Status | ReplicationMask.Time,
            ReplicationMath.DeltaMask(b, e));
    }

    [Fact]
    public void EntrySizeMatchesTheCodecForEveryMaskAndAFullKeyframeIs37Bytes()
    {
        for (int bits = 0; bits < 256; bits++)
        {
            var mask = (ReplicationMask)bits & ~ReplicationMask.Ext;
            var entry = new ReplicationEntry { NetId = 1, Mask = mask };
            Assert.Equal(ReplicationCodec.GetSize(entry), ReplicationMath.EntrySize(mask));
        }

        Assert.Equal(37, ReplicationMath.EntrySize(ReplicationMath.FullMask));
        Assert.Equal(25, ReplicationMath.EntrySize(ReplicationMask.Pos | ReplicationMask.Vel | ReplicationMask.Time)); // a moving ship
        Assert.Equal(7, ReplicationMath.EntrySize(ReplicationMask.Status));
    }

    [Fact]
    public void ABaselineMergesOnlyTheFieldsAnEntryCarried()
    {
        var b = new Baseline { Sector = 3, Px = 10, Py = 11, Pz = 12, Yaw = 1, Vx = 5, Hull = 200, Shield = 100, Flags = 2 };
        b.Apply(new ReplicationEntry { NetId = 1, Mask = ReplicationMask.Pos | ReplicationMask.Status | ReplicationMask.Time, PosX = -1, PosY = -2, PosZ = -3, Hull = 9, Shield = 8, TimeMs = 40 });

        Assert.Equal((-1, -2, -3), (b.Px, b.Py, b.Pz));
        Assert.Equal((9, 8), (b.Hull, b.Shield));
        Assert.Equal((3, 1, 5, 2), (b.Sector, b.Yaw, b.Vx, b.Flags)); // untouched
    }

    [Fact]
    public void NearOutranksSectorOutranksAdjacentAtTheSameAge()
    {
        float near = ReplicationMath.Priority(InterestTier.Near, 0.2, 20, 0.5);
        float sector = ReplicationMath.Priority(InterestTier.Sector, 0.2, 5, 0);
        float adjacent = ReplicationMath.Priority(InterestTier.Adjacent, 1.0, 1, 0);
        Assert.True(near > sector && sector > adjacent);
        Assert.True(ReplicationMath.TierWeight(InterestTier.Linger) == ReplicationMath.TierWeight(InterestTier.Adjacent));
        Assert.Equal(0f, ReplicationMath.TierWeight(InterestTier.None));
    }

    [Fact]
    public void PriorityAccumulatesWithAgeAndFavoursTheCloserNearEntity()
    {
        float young = ReplicationMath.Priority(InterestTier.Near, 0.05, 20, 0.5);
        float old = ReplicationMath.Priority(InterestTier.Near, 0.15, 20, 0.5);
        Assert.Equal(young * 3, old, 3);

        float close = ReplicationMath.Priority(InterestTier.Near, 0.05, 20, 0.1);
        float far = ReplicationMath.Priority(InterestTier.Near, 0.05, 20, 1.0);
        Assert.True(close > far);
        Assert.Equal((2.0 - 0.1) / (2.0 - 1.0), close / far, 3); // distance factor 1 at the edge, 2 at the player

        // outside Near the distance does not matter
        Assert.Equal(
            ReplicationMath.Priority(InterestTier.Sector, 0.2, 5, 0.1),
            ReplicationMath.Priority(InterestTier.Sector, 0.2, 5, 0.9));
    }

    [Fact]
    public void AnEntityIsDueAtNinetyPercentOfItsInterval()
    {
        Assert.False(ReplicationMath.IsDue(0.04, 20));   // 80% of 50 ms
        Assert.True(ReplicationMath.IsDue(0.0451, 20));  // just past 90%
        Assert.True(ReplicationMath.IsDue(0.05, 20));
        Assert.False(ReplicationMath.IsDue(0.15, 5));    // 3 ticks of a 200 ms interval
        Assert.True(ReplicationMath.IsDue(0.2, 5));
        Assert.False(ReplicationMath.IsDue(100, 0));     // a tier with no rate never sends
    }

    [Fact]
    public void RatesFollowTheTiersAndPlayerShipsGoOutAtTheFullRateEverywhere()
    {
        var options = new InterestOptions();
        Assert.Equal(20, ReplicationMath.RateHz(options, InterestTier.Near, false));
        Assert.Equal(5, ReplicationMath.RateHz(options, InterestTier.Sector, false));
        Assert.Equal(1, ReplicationMath.RateHz(options, InterestTier.Adjacent, false));
        Assert.Equal(1, ReplicationMath.RateHz(options, InterestTier.Linger, false));
        Assert.Equal(20, ReplicationMath.RateHz(options, InterestTier.Adjacent, playerShip: true));   // M3-33: no far-tier floor for player ships
        Assert.Equal(20, ReplicationMath.RateHz(options, InterestTier.Linger, playerShip: true));
        Assert.Equal(20, ReplicationMath.RateHz(options, InterestTier.Sector, playerShip: true));   // other players' ships: Near rate in followed sectors
        Assert.Equal(20, ReplicationMath.RateHz(options, InterestTier.Near, playerShip: true));
    }

    [Fact]
    public void KeyframesAreDueEveryFiveSecondsForNearAndSectorAndFifteenForAdjacentAndLinger()
    {
        var options = new ReplicationOptions();
        Assert.Equal(5, ReplicationMath.KeyframeIntervalSeconds(options, InterestTier.Near));
        Assert.Equal(5, ReplicationMath.KeyframeIntervalSeconds(options, InterestTier.Sector));
        Assert.Equal(15, ReplicationMath.KeyframeIntervalSeconds(options, InterestTier.Adjacent));
        Assert.Equal(15, ReplicationMath.KeyframeIntervalSeconds(options, InterestTier.Linger));

        options.KeyframeNearSectorSeconds = 2;
        Assert.Equal(2, ReplicationMath.KeyframeIntervalSeconds(options, InterestTier.Sector));
    }

    [Fact]
    public void AKeyframeOnlyEntityWaitsBehindAChangedOneOfTheSameTier()
    {
        float changed = ReplicationMath.Priority(InterestTier.Sector, 0.2, 5, 0);
        float keyframe = ReplicationMath.KeyframePriority(InterestTier.Sector, 5.0, 5.0);
        Assert.True(changed > keyframe);
        Assert.True(keyframe > 0);
    }

    [Theory]
    [InlineData(0.0, 0.0, 0)]
    [InlineData(10.05, 10.0, 50)]
    [InlineData(9.95, 10.0, -50)]
    [InlineData(10.0004, 10.0, 0)]
    [InlineData(10.0006, 10.0, 1)]
    [InlineData(1000.0, 10.0, 32767)]
    [InlineData(0.0, 1000.0, -32768)]
    public void TheTimeOffsetIsWholeMillisecondsClampedToSixteenBits(double sample, double reference, int expected) =>
        Assert.Equal((short)expected, ReplicationMath.TimeOffsetMs(sample, reference));

    [Fact]
    public void MakeEntryCarriesTheMirrorValuesAndTheSampleOffset()
    {
        var e = Entity(m => m.IngestWorldUpdate(UpdatePayload(5, 12.25, [State(7, 2, 128, 64, -64, flags: 4, vx: 12)])));
        var entry = ReplicationMath.MakeEntry(e, ReplicationMask.Pos | ReplicationMask.Vel | ReplicationMask.Time, referenceGameTime: 12.3);

        Assert.Equal(7u, entry.NetId);
        Assert.Equal((128, 64, -64), (entry.PosX, entry.PosY, entry.PosZ));
        Assert.Equal(12, entry.VelX);
        Assert.Equal(-50, entry.TimeMs); // sampled 50 ms before the frame's reference time
        Assert.Equal(25, ReplicationCodec.GetSize(entry));
    }

    // ---- the frame layout ----

    [Fact]
    public void TheHandWrittenFrameIsAValidReplicationFlatBuffer()
    {
        ReplicationEntry[] entries =
        [
            new() { NetId = 41, Mask = ReplicationMath.FullMask, Sector = 5, PosX = 1, PosY = -2, PosZ = 3, Yaw = 4, Pitch = -5, Roll = 6, VelX = 7, VelY = 8, VelZ = -9, StateFlags = 10, Hull = 11, Shield = 12, TimeMs = -13 },
            new() { NetId = 42, Mask = ReplicationMask.Status, Hull = 1, Shield = 2 },
        ];
        byte[] buffer = new byte[ReplicationFrame.HeaderBytes + 128];
        ReplicationFrame.Begin(buffer, 77, 123_456_789, 98.765);
        int at = ReplicationFrame.HeaderBytes;
        foreach (var entry in entries)
        {
            at += ReplicationCodec.Write(buffer.AsSpan(at), entry);
        }

        int length = ReplicationFrame.Finish(buffer, entries.Length, at - ReplicationFrame.HeaderBytes);
        Assert.Equal(60 + 37 + 7, length);

        var message = ReplicationMsg.GetRootAsReplication(new ByteBuffer(buffer.AsSpan(0, length).ToArray()));
        Assert.Equal(77u, message.ServerTick);
        Assert.Equal(123_456_789UL, message.ServerTimeUs);
        Assert.Equal(98.765, message.AuthorityGameTime);
        Assert.Equal(2, message.EntryCount);
        Assert.Equal(44, message.EntriesLength);

        var decoded = ReplicationCodec.Decode(message.GetEntriesArray(), message.EntryCount);
        Assert.Equal(41u, decoded[0].NetId);
        Assert.Equal(ReplicationMath.FullMask, decoded[0].Mask);
        Assert.Equal((short)-13, decoded[0].TimeMs);
        Assert.Equal(42u, decoded[1].NetId);
        Assert.Equal((byte)2, decoded[1].Shield);

        // the generated reader and the object API agree
        var t = message.UnPack();
        Assert.Equal(44, t.Entries.Count);
    }

    [Fact]
    public void ScalarsAreAlignedRelativeToTheBufferStartLikeTheCppVerifierWantsThem()
    {
        byte[] buffer = new byte[ReplicationFrame.HeaderBytes + 8];
        ReplicationFrame.Begin(buffer, 1, 2, 3.0);
        ReplicationFrame.Finish(buffer, 0, 0);
        var rootOffset = BitConverter.ToUInt32(buffer, 0);
        Assert.Equal(0u, rootOffset % 4);
        // 8-byte fields at 40 and 48, 4-byte fields at 28, 36 and 56, 2-byte field at 32
        foreach (int at in new[] { 40, 48 })
        {
            Assert.Equal(0, at % 8);
        }

        foreach (int at in new[] { 24, 28, 36, 56 })
        {
            Assert.Equal(0, at % 4);
        }
    }
}
