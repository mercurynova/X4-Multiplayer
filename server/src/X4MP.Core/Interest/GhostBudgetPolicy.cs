using X4MP.Core.World;
using X4MP.Proto;

namespace X4MP.Core.Interest;

/// <summary>How far the ghost budget policy had to cut (each level includes the cuts of the ones before).</summary>
public enum BudgetLevel
{
    /// <summary>Everything in every tier fits.</summary>
    Full = 0,

    /// <summary>XS, S and M ships (and drones, lockboxes, crates) are dropped from Adjacent and Linger; L, XL and player ships stay.</summary>
    SmallsDroppedFromPrefetch = 1,

    /// <summary>Adjacent is limited to sectors on the predicted route (hinted jumps); Linger keeps only L and XL.</summary>
    PrefetchOnlyPredicted = 2,

    /// <summary>No Adjacent and no Linger at all.</summary>
    NoPrefetch = 3,

    /// <summary>Even the current sector exceeds the budget: it is capped nearest-first.</summary>
    CurrentSectorCapped = 4,
}

/// <summary>A sector a client wants, before the budget is applied.</summary>
public readonly record struct SectorDemand(ushort Sector, InterestTier Tier, SectorCounts Counts, bool Predicted);

/// <summary>What the budget lets a client have of one sector.</summary>
public enum SectorAdmission
{
    /// <summary>Not at all.</summary>
    None = 0,

    /// <summary>Only L, XL and player ships.</summary>
    LargeOnly = 1,

    /// <summary>Every entity.</summary>
    All = 2,
}

/// <summary>
/// The ghost budget policy (ADR-042: <c>max_ghosts</c> default 250; protocol.md 12.3). A pure function of the demanded sectors
/// and their per-class counts: it picks the lowest <see cref="BudgetLevel"/> whose total fits, so the order of cuts is always
/// the same, XS/S/M from Adjacent first, then the unpredicted Adjacent sectors, then the rest of the prefetch, and only then the
/// current sector. The current sector (tier Sector or Near) is never cut before the last level.
/// </summary>
public static class GhostBudgetPolicy
{
    public static SectorAdmission Admission(BudgetLevel level, in SectorDemand demand)
    {
        switch (demand.Tier)
        {
            case InterestTier.Sector:
            case InterestTier.Near:
                return SectorAdmission.All;
            case InterestTier.Adjacent:
                return level switch
                {
                    BudgetLevel.Full => SectorAdmission.All,
                    BudgetLevel.SmallsDroppedFromPrefetch => SectorAdmission.LargeOnly,
                    BudgetLevel.PrefetchOnlyPredicted => demand.Predicted ? SectorAdmission.LargeOnly : SectorAdmission.None,
                    _ => SectorAdmission.None,
                };
            case InterestTier.Linger:
                return level switch
                {
                    BudgetLevel.Full => SectorAdmission.All,
                    BudgetLevel.SmallsDroppedFromPrefetch or BudgetLevel.PrefetchOnlyPredicted => SectorAdmission.LargeOnly,
                    _ => SectorAdmission.None,
                };
            default:
                return SectorAdmission.None;
        }
    }

    /// <summary>Ghosts the demands would materialise at <paramref name="level"/>.</summary>
    public static int Total(BudgetLevel level, ReadOnlySpan<SectorDemand> demands)
    {
        int total = 0;
        foreach (var d in demands)
        {
            total += Admission(level, d) switch
            {
                SectorAdmission.All => d.Counts.Total,
                SectorAdmission.LargeOnly => d.Counts.Large,
                _ => 0,
            };
        }

        return total;
    }

    /// <summary>The lowest level whose total is within <paramref name="budget"/>; <see cref="BudgetLevel.CurrentSectorCapped"/> when none is.</summary>
    public static BudgetLevel Choose(ReadOnlySpan<SectorDemand> demands, int budget)
    {
        for (var level = BudgetLevel.Full; level < BudgetLevel.CurrentSectorCapped; level++)
        {
            if (Total(level, demands) <= budget)
            {
                return level;
            }
        }

        return BudgetLevel.CurrentSectorCapped;
    }

    /// <summary>
    /// <see cref="Choose(ReadOnlySpan{SectorDemand}, int)"/> with hysteresis: a client only moves back to a lower (richer) level
    /// when the result uses at most 90% of the budget, so a sector hovering around the limit does not flap between spawning and
    /// despawning its small ships.
    /// </summary>
    public static BudgetLevel Choose(ReadOnlySpan<SectorDemand> demands, int budget, BudgetLevel current)
    {
        var wanted = Choose(demands, budget);
        if (wanted >= current)
        {
            return wanted;
        }

        int relaxed = (int)((long)budget * 9 / 10);
        for (var level = wanted; level < current; level++)
        {
            if (Total(level, demands) <= relaxed)
            {
                return level;
            }
        }

        return current;
    }
}

/// <summary>
/// Per-team visibility filter hook (ADR-038): fog of war is off in v1, but the interest manager asks this before it shows an
/// entity to a player, so hostile-team ships and stations can later be hidden outside radar range without a protocol change.
/// The default <see cref="AllVisible"/> shows everything.
/// </summary>
public interface IVisibilityFilter
{
    /// <summary>True when <paramref name="viewerPlayerId"/> may see <paramref name="entity"/>.</summary>
    bool IsVisible(int viewerPlayerId, MirrorEntity entity);
}

/// <summary>The v1 filter: everything is visible to everyone.</summary>
public sealed class AllVisible : IVisibilityFilter
{
    public static AllVisible Instance { get; } = new();

    public bool IsVisible(int viewerPlayerId, MirrorEntity entity) => true;
}
