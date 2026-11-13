using X4MP.Core.World;
using X4MP.Proto;

namespace X4MP.Core.Interest;

/// <summary>
/// Radar-range fog (M17, ADR-038): a client is shown only the ships its own radar could see, so the second client's map stops
/// drawing ships it has no way of knowing about - while those ships keep existing on the authority, which simulates them and
/// still replicates them to whoever can see them.
/// <para>
/// What is never hidden, because the map would lie otherwise:
/// <list type="bullet">
/// <item>A <b>player ship</b> (an avatar): the request, the spawn and the changes of every player ship go out galaxy-wide
/// (ADR-011), and the human on the other PC must stay findable.</item>
/// <item>Anything that is not a ship: stations, gates, build storage and wrecks are map knowledge, not radar contacts.</item>
/// <item>Anything in <b>another sector</b>: the near grid only ever measures inside the player's own sector, and the adjacent
/// prefetch exists to make a gate crossing instant.</item>
/// <item>An entity whose pose the server does not know (<see cref="MirrorEntity.PoseKnown"/> false): hiding a ghost because of a
/// missing reading would be a bug, not fog.</item>
/// <item>The viewer's own team assets and the assets it owns (<see cref="MirrorEntity.OwnerTeam"/>,
/// <see cref="MirrorEntity.OwnerPlayer"/>): a wingman on the same team is not a radar contact either.</item>
/// </list>
/// </para>
/// <para>
/// With <see cref="InterestOptions.RadarRangeMeters"/> at 0 the filter shows everything, i.e. the v1 behaviour; that is also
/// what <see cref="AllVisible"/> does, so only the range makes the difference.
/// </para>
/// </summary>
public sealed class RadarVisibilityFilter : IVisibilityFilter
{
    /// <summary>
    /// True when the viewer's radar reaches the entity. The manager always passes a <see cref="VisibilityViewer"/>; without one
    /// there is no position to measure, so this filter hides nothing rather than guessing.
    /// </summary>
    public bool IsVisible(int viewerPlayerId, MirrorEntity entity)
    {
        _ = viewerPlayerId;
        _ = entity;
        return true;
    }

    public bool IsVisible(in VisibilityViewer viewer, MirrorEntity entity)
    {
        if (viewer.RadarRangeMeters <= 0)
        {
            return true; // fog off
        }

        if (entity.IsPlayerShip || !IsShip(entity.Kind) || !entity.PoseKnown)
        {
            return true;
        }

        if (entity.Sector != viewer.Sector)
        {
            return true; // not this viewer's sector: the near grid does not cover it
        }

        if (entity.OwnerTeam != 0 && entity.OwnerTeam == viewer.TeamId)
        {
            return true; // the viewer's team owns it
        }

        if (entity.OwnerPlayer != 0 && entity.OwnerPlayer == viewer.PlayerId)
        {
            return true; // the viewer owns it
        }

        // The wire's 1/64 m units; doubles so a wild coordinate cannot overflow the square.
        var dx = entity.Px - (long)viewer.Px;
        var dy = entity.Py - (long)viewer.Py;
        var dz = entity.Pz - (long)viewer.Pz;
        var metres = Math.Sqrt(((double)dx * dx) + ((double)dy * dy) + ((double)dz * dz)) / NearGrid.UnitsPerMetre;
        return metres <= viewer.RadarRangeMeters;
    }

    private static bool IsShip(EntityKind kind) =>
        kind is EntityKind.ShipS or EntityKind.ShipM or EntityKind.ShipL or EntityKind.ShipXL;
}
