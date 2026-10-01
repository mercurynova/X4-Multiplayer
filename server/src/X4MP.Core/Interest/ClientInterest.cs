using X4MP.Proto;

namespace X4MP.Core.Interest;

/// <summary>Where a subscribed sector stands for one client.</summary>
public enum SectorDelivery
{
    /// <summary>Subscribed, but the authority has not completed the sector yet (it is not captured, or its spawns are still coming): nothing is forwarded.</summary>
    Pending = 0,

    /// <summary>The sector is complete on the server and its entities are being sent (spread over ticks).</summary>
    Delivering,

    /// <summary>Every entity was sent and <c>SectorComplete</c> followed; live changes are forwarded.</summary>
    Delivered,
}

/// <summary>One sector in a client's interest.</summary>
public sealed class SectorSubscription
{
    internal SectorSubscription(ushort sector) => Sector = sector;

    public ushort Sector { get; }

    /// <summary>The tier reported in <c>InterestUpdate</c> (Sector, Adjacent or Linger; Near is entity-level, see <see cref="InterestManager.TierOf"/>).</summary>
    public InterestTier Tier { get; internal set; }

    /// <summary>What the ghost budget admits of this sector.</summary>
    public SectorAdmission Admission { get; internal set; }

    public SectorDelivery Delivery { get; internal set; }
}

/// <summary>A sector's entities waiting to be sent to a client in batches.</summary>
internal sealed class SpawnJob(ushort sector, List<uint> ids, bool sendComplete)
{
    /// <summary>0 for the galaxy-wide player ships.</summary>
    public ushort Sector { get; } = sector;

    public List<uint> Ids { get; } = ids;

    public int Next { get; set; }

    /// <summary>Send <c>SectorComplete</c> after the last batch (a first delivery; not for catch-up after the budget relaxed).</summary>
    public bool SendComplete { get; } = sendComplete;

    public int Sent { get; set; }
}

/// <summary>Interest state of one client node. Created at activation, dropped at deactivation. Actor-thread only.</summary>
internal sealed class ClientInterest(int playerId, ulong caps, bool isAuthority)
{
    public int PlayerId { get; } = playerId;

    public ulong Caps { get; } = caps;

    /// <summary>The authority's own player: it contributes to the capture set but is never sent spawns (it is the source).</summary>
    public bool IsAuthority { get; } = isAuthority;

    public bool ReceivesSpawns => !IsAuthority;

    public ushort Sector { get; set; }

    public int Px { get; set; }

    public int Py { get; set; }

    public int Pz { get; set; }

    /// <summary>Epoch of the last <c>InterestUpdate</c> sent.</summary>
    public uint Epoch { get; set; }

    public bool SentFull { get; set; }

    public Dictionary<ushort, SectorSubscription> Subs { get; } = [];

    /// <summary>Sectors the player left, with the timestamp their linger ends.</summary>
    public Dictionary<ushort, long> Linger { get; } = [];

    /// <summary>Hinted sectors with the timestamp the prefetch ends.</summary>
    public Dictionary<ushort, long> Hints { get; } = [];

    /// <summary>Tiers the client was last told.</summary>
    public Dictionary<ushort, InterestTier> LastSent { get; } = [];

    /// <summary>Entities the client holds as ghosts; the value says whether the entity was a player ship when sent (those are exempt from the budget).</summary>
    public Dictionary<uint, bool> Held { get; } = [];

    public int Ghosts { get; set; }

    public List<SpawnJob> Jobs { get; } = [];

    public BudgetLevel Level { get; set; }

    /// <summary>Budget level 4: the current sector is capped, nearest first.</summary>
    public bool HardCap => Level == BudgetLevel.CurrentSectorCapped;

    /// <summary>The sector whose near grid this client keeps alive (0 = none).</summary>
    public ushort GridSector { get; set; }

    /// <summary>Spawn frames this client may still get in the current tick (reset by the tick; event-driven sends draw on it too).</summary>
    public int FramesLeft { get; set; } = int.MaxValue;
}
