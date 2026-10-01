using X4MP.Proto;

namespace X4MP.Core.World;

/// <summary>Size class used by the ghost budget (protocol.md 12.3).</summary>
public enum EntitySize : byte
{
    /// <summary>XS, S and M ships, drones, lockboxes, crates and anything unclassified: dropped first when over budget.</summary>
    Small = 0,

    /// <summary>L and XL ships and player-controlled ships: kept longest.</summary>
    Large = 1,
}

/// <summary>Kind helpers shared by the mirror and the interest manager.</summary>
public static class EntityKinds
{
    /// <summary>
    /// Persistent entities (protocol.md 2): tracked universe-wide, journaled and matched to the local copy through the
    /// manifest. Never ghosted and never removed by interest.
    /// </summary>
    public static bool IsPersistent(EntityKind kind) => kind is
        EntityKind.Station or EntityKind.Gate or EntityKind.Accelerator or EntityKind.HighwayEntry
        or EntityKind.Satellite or EntityKind.NavBeacon or EntityKind.ResourceProbe or EntityKind.Mine or EntityKind.LaserTower;

    public static bool IsLargeShip(EntityKind kind) => kind is EntityKind.ShipL or EntityKind.ShipXL;
}

/// <summary>
/// One entity of the server's mirror (server-design 2.6): the authority-assigned identity, the descriptive record and the
/// latest quantised state. Instances are pooled and mutated in place by the <see cref="WorldMirror"/> on the actor
/// thread; readers (the interest manager, replication) must not keep a reference across a despawn.
/// </summary>
public sealed class MirrorEntity
{
    // ---- identity and description (EntityRecord) ----

    public uint NetId { get; internal set; }

    public EntityKind Kind { get; internal set; }

    public EntityOrigin Origin { get; internal set; }

    public uint MacroRef { get; internal set; }

    public uint OwnerRef { get; internal set; }

    public ushort OwnerTeam { get; internal set; }

    public ushort OwnerPlayer { get; internal set; }

    public uint ParentNetId { get; internal set; }

    public ushort ControllerPlayer { get; internal set; }

    public string? Name { get; internal set; }

    public string? IdCode { get; internal set; }

    /// <summary>0..255 fraction of max.</summary>
    public byte Hull { get; internal set; } = 255;

    public byte Shield { get; internal set; } = 255;

    // ---- hot state (EntityState, protocol.md 11 quantisation) ----

    public ushort Sector { get; internal set; }

    public ushort Flags { get; internal set; }

    public int Px { get; internal set; }

    public int Py { get; internal set; }

    public int Pz { get; internal set; }

    public short Yaw { get; internal set; }

    public short Pitch { get; internal set; }

    public short Roll { get; internal set; }

    public short Vx { get; internal set; }

    public short Vy { get; internal set; }

    public short Vz { get; internal set; }

    /// <summary>Increments whenever a quantised state field changes (so equal wire values never bump it).</summary>
    public uint Version { get; internal set; }

    /// <summary>Latest <c>TimeProvider</c> timestamp the state was written.</summary>
    public long LastUpdateTick { get; internal set; }

    /// <summary>
    /// Authority game time (seconds) the current state was sampled at: the <c>game_time</c> of the <c>WorldUpdate</c> that last changed it,
    /// or the latest known authority game time for states that came with a spawn or a player state. Replication turns it into the
    /// per-entry TIME offset (protocol.md 10.2).
    /// </summary>
    public double SampleGameTime { get; internal set; }

    /// <summary>Latest cargo snapshot (<c>EntityCargo</c>); null until one arrived.</summary>
    public WareAmount[]? Cargo { get; internal set; }

    public bool IsPersistent { get; internal set; }

    public bool IsPlayerShip => ControllerPlayer != 0 || Origin == EntityOrigin.PlayerShip;

    public EntitySize Size => IsPlayerShip || EntityKinds.IsLargeShip(Kind) ? EntitySize.Large : EntitySize.Small;

    // ---- mirror bookkeeping ----

    /// <summary>Index in the owning sector bucket, or -1.</summary>
    internal int SectorSlot { get; set; } = -1;

    /// <summary>Near-grid bookkeeping owned by the interest manager (cell key and slot in the cell list).</summary>
    internal ulong GridKey { get; set; }

    internal int GridSlot { get; set; } = -1;

    /// <summary>The size class the entity was counted under in its sector bucket.</summary>
    internal bool BucketLarge { get; set; }

    internal void Reset()
    {
        NetId = 0;
        Kind = EntityKind.Unknown;
        Origin = EntityOrigin.Manifest;
        MacroRef = OwnerRef = ParentNetId = 0;
        OwnerTeam = OwnerPlayer = ControllerPlayer = 0;
        Name = IdCode = null;
        Hull = Shield = 255;
        Sector = Flags = 0;
        Px = Py = Pz = 0;
        Yaw = Pitch = Roll = Vx = Vy = Vz = 0;
        Version = 0;
        LastUpdateTick = 0;
        SampleGameTime = 0;
        Cargo = null;
        IsPersistent = false;
        SectorSlot = -1;
        GridKey = 0;
        GridSlot = -1;
        BucketLarge = false;
    }
}

/// <summary>What a state write changed.</summary>
[Flags]
public enum StateChange : byte
{
    None = 0,
    Position = 1,
    Rotation = 2,
    Velocity = 4,
    Flags = 8,
    Sector = 16,
    Status = 32,
}

/// <summary>Latest <c>PlayerState</c> of a player (the client is authoritative for its own ship, protocol.md 13).</summary>
public sealed class PlayerShipState
{
    internal PlayerShipState(int playerId) => PlayerId = playerId;

    public int PlayerId { get; }

    public uint NetId { get; internal set; }

    public uint Seq { get; internal set; }

    public ulong SampleTimeUs { get; internal set; }

    public ushort Sector { get; internal set; }

    public ushort Flags { get; internal set; }

    public int Px { get; internal set; }

    public int Py { get; internal set; }

    public int Pz { get; internal set; }

    public short Yaw { get; internal set; }

    public short Pitch { get; internal set; }

    public short Roll { get; internal set; }

    public byte Hull { get; internal set; } = 255;

    public byte Shield { get; internal set; } = 255;

    public uint TargetNetId { get; internal set; }

    public long UpdatedTick { get; internal set; }
}
