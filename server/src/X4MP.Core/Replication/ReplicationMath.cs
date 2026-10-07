using X4MP.Core.Interest;
using X4MP.Core.Settings;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Replication;

/// <summary>
/// The field values of one entity the client is known to hold (protocol.md 10.3 "acked baseline"): what a replication entry is a delta
/// against. It only ever changes when a frame that carried the fields was confirmed delivered (<see cref="Apply"/>).
/// </summary>
public struct Baseline
{
    public ushort Sector { get; set; }
    public int Px { get; set; }
    public int Py { get; set; }
    public int Pz { get; set; }
    public short Yaw { get; set; }
    public short Pitch { get; set; }
    public short Roll { get; set; }
    public short Vx { get; set; }
    public short Vy { get; set; }
    public short Vz { get; set; }
    public ushort Flags { get; set; }
    public byte Hull { get; set; }
    public byte Shield { get; set; }

    /// <summary>Folds the fields an entry carried into the baseline (an omitted field keeps its value).</summary>
    public void Apply(in ReplicationEntry e)
    {
        var m = e.Mask;
        if ((m & ReplicationMask.Sector) != 0)
        {
            Sector = e.Sector;
        }

        if ((m & ReplicationMask.Pos) != 0)
        {
            Px = e.PosX;
            Py = e.PosY;
            Pz = e.PosZ;
        }

        if ((m & ReplicationMask.Rot) != 0)
        {
            Yaw = e.Yaw;
            Pitch = e.Pitch;
            Roll = e.Roll;
        }

        if ((m & ReplicationMask.Vel) != 0)
        {
            Vx = e.VelX;
            Vy = e.VelY;
            Vz = e.VelZ;
        }

        if ((m & ReplicationMask.Flags) != 0)
        {
            Flags = e.StateFlags;
        }

        if ((m & ReplicationMask.Status) != 0)
        {
            Hull = e.Hull;
            Shield = e.Shield;
        }
    }
}

/// <summary>
/// The pure parts of replication: delta masks, entry sizes, the priority accumulator and the keyframe schedule. No state, so they are
/// unit-tested on their own (server-design 2.6, protocol.md 10.2 and 10.3).
/// </summary>
public static class ReplicationMath
{
    /// <summary>
    /// True when <paramref name="entity"/> is the ship <paramref name="playerId"/> pilots, or that player's own avatar while nobody pilots it
    /// (the authority has not set <c>controller_player</c> again after a rejoin). A client simulates its own ship: it never gets it in
    /// <c>Replication</c> (M3 plan 4.1). Other players' parked avatars are ordinary player ships and are replicated.
    /// </summary>
    public static bool IsOwnShip(MirrorEntity entity, int playerId) =>
        entity.ControllerPlayer == playerId
        || (entity.ControllerPlayer == 0 && entity.Origin == EntityOrigin.PlayerShip && entity.OwnerPlayer == playerId);

    /// <summary>Every state field plus TIME: a keyframe or the first entry of a ghost (37 bytes).</summary>
    public const ReplicationMask FullMask =
        ReplicationMask.Sector | ReplicationMask.Pos | ReplicationMask.Rot | ReplicationMask.Vel
        | ReplicationMask.Flags | ReplicationMask.Status | ReplicationMask.Time;

    /// <summary>Fields whose value depends on when it was sampled: they carry TIME.</summary>
    public const ReplicationMask Timed = ReplicationMask.Pos | ReplicationMask.Rot | ReplicationMask.Vel;

    /// <summary>An entity is due once this fraction of its tier's send interval has passed (a replication tick is rarely exactly the interval).</summary>
    public const double DueFraction = 0.9;

    /// <summary>Size of the frame around the entries: 8 byte header plus the 60 byte Replication table.</summary>
    public const int FrameOverheadBytes = FrameCodec.HeaderSize + ReplicationFrame.HeaderBytes;

    /// <summary>The fields of <paramref name="e"/> that differ from what the client holds, plus TIME when a time-dependent field is among them.</summary>
    public static ReplicationMask DeltaMask(in Baseline b, MirrorEntity e)
    {
        var mask = ReplicationMask.None;
        if (e.Sector != b.Sector)
        {
            mask |= ReplicationMask.Sector;
        }

        if (e.Px != b.Px || e.Py != b.Py || e.Pz != b.Pz)
        {
            mask |= ReplicationMask.Pos;
        }

        if (e.Yaw != b.Yaw || e.Pitch != b.Pitch || e.Roll != b.Roll)
        {
            mask |= ReplicationMask.Rot;
        }

        if (e.Vx != b.Vx || e.Vy != b.Vy || e.Vz != b.Vz)
        {
            mask |= ReplicationMask.Vel;
        }

        if (e.Flags != b.Flags)
        {
            mask |= ReplicationMask.Flags;
        }

        if (e.Hull != b.Hull || e.Shield != b.Shield)
        {
            mask |= ReplicationMask.Status;
        }

        if ((mask & Timed) != 0)
        {
            mask |= ReplicationMask.Time;
        }

        return mask;
    }

    /// <summary>Encoded size of an entry with this mask and no EXT block (<see cref="ReplicationCodec.GetSize"/> without building the entry).</summary>
    public static int EntrySize(ReplicationMask mask)
    {
        int n = ReplicationCodec.MinEntrySize;
        if ((mask & ReplicationMask.Sector) != 0)
        {
            n += 2;
        }

        if ((mask & ReplicationMask.Pos) != 0)
        {
            n += 12;
        }

        if ((mask & ReplicationMask.Rot) != 0)
        {
            n += 6;
        }

        if ((mask & ReplicationMask.Vel) != 0)
        {
            n += 6;
        }

        if ((mask & ReplicationMask.Flags) != 0)
        {
            n += 2;
        }

        if ((mask & ReplicationMask.Status) != 0)
        {
            n += 2;
        }

        if ((mask & ReplicationMask.Time) != 0)
        {
            n += 2;
        }

        return n;
    }

    /// <summary>The tier's weight in the accumulator: Near entities outrank Sector entities, which outrank Adjacent and Linger.</summary>
    public static float TierWeight(InterestTier tier) => tier switch
    {
        InterestTier.Near => 8f,
        InterestTier.Sector => 3f,
        InterestTier.Adjacent or InterestTier.Linger => 1f,
        _ => 0f,
    };

    /// <summary>
    /// The send rate of a tier in Hz (the capture rates of <see cref="InterestOptions"/>). Player ships are what other players look at and shoot
    /// at: M3-33 (Finding 21): at the full rate (the Near rate) EVERYWHERE, whatever the tier or the sector. With at most 8 players the
    /// worst case is 8 x 20 Hz of one small PlayerShip entry per client; a slow far-tier stream made the client extrapolate a 5 km/s
    /// superhighway flight (path error up to 1.2 km). Everything that is not a player ship keeps its tier rate.
    /// </summary>
    public static int RateHz(InterestOptions options, InterestTier tier, bool playerShip)
    {
        if (!playerShip)
        {
            return options.RateHz(tier);
        }

        return Math.Max(options.NearRateHz, options.SectorRateHz);
    }

    /// <summary>True once <paramref name="ageSeconds"/> since the last send reaches <see cref="DueFraction"/> of the tier interval.</summary>
    public static bool IsDue(double ageSeconds, int rateHz) => rateHz > 0 && ageSeconds * rateHz >= DueFraction;

    /// <summary>
    /// The accumulated priority of a changed entity: the time since its last send times its tier rate (how many intervals it is
    /// overdue), times the tier weight, times a distance factor for Near entities (1 at the Near edge, 2 at the player). The budget goes
    /// to the highest values first.
    /// </summary>
    /// <param name="nearFraction">Distance to the player over the Near radius, 0..1 (ignored outside Near).</param>
    public static float Priority(InterestTier tier, double ageSeconds, int rateHz, double nearFraction)
    {
        double distance = tier == InterestTier.Near ? 2.0 - Math.Clamp(nearFraction, 0.0, 1.0) : 1.0;
        return (float)(ageSeconds * rateHz * TierWeight(tier) * distance);
    }

    /// <summary>Priority of an unchanged entity that is only due for its keyframe: it waits behind every changed one of the same tier.</summary>
    public static float KeyframePriority(InterestTier tier, double sinceKeyframeSeconds, double intervalSeconds) =>
        (float)(sinceKeyframeSeconds / Math.Max(intervalSeconds, 1e-3) * TierWeight(tier) * 0.5);

    /// <summary>Keyframe interval of a tier: 5 s for Near and Sector, 15 s for Adjacent and Linger (protocol.md 10.3).</summary>
    public static double KeyframeIntervalSeconds(ReplicationOptions options, InterestTier tier) =>
        tier is InterestTier.Near or InterestTier.Sector ? options.KeyframeNearSectorSeconds : options.KeyframeAdjacentSeconds;

    /// <summary>Per-entry TIME: the sample time offset to the frame's reference game time in whole milliseconds, clamped to i16.</summary>
    public static short TimeOffsetMs(double sampleGameTime, double referenceGameTime)
    {
        double ms = Math.Round((sampleGameTime - referenceGameTime) * 1000.0, MidpointRounding.AwayFromZero);
        return (short)Math.Clamp(ms, short.MinValue, short.MaxValue);
    }

    /// <summary>Builds the entry for <paramref name="e"/> carrying the fields of <paramref name="mask"/> (absolute values).</summary>
    public static ReplicationEntry MakeEntry(MirrorEntity e, ReplicationMask mask, double referenceGameTime) => new()
    {
        NetId = e.NetId,
        Mask = mask,
        Sector = e.Sector,
        PosX = e.Px,
        PosY = e.Py,
        PosZ = e.Pz,
        Yaw = e.Yaw,
        Pitch = e.Pitch,
        Roll = e.Roll,
        VelX = e.Vx,
        VelY = e.Vy,
        VelZ = e.Vz,
        StateFlags = e.Flags,
        Hull = e.Hull,
        Shield = e.Shield,
        TimeMs = (mask & ReplicationMask.Time) != 0 ? TimeOffsetMs(e.SampleGameTime, referenceGameTime) : (short)0,
    };
}
