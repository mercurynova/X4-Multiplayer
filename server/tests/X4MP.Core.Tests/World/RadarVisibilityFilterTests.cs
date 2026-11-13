using X4MP.Core.Interest;
using X4MP.Core.World;
using X4MP.Proto;
using static X4MP.Core.Tests.World.WorldKit;

namespace X4MP.Core.Tests.World;

/// <summary>
/// M17 radar fog: the filter decides what a viewer's map may draw. Positions are wire units (1/64 m), so the test constants are
/// written as <c>64 * metres</c>.
/// </summary>
public sealed class RadarVisibilityFilterTests
{
    private const int PerMetre = 64;

    private static readonly RadarVisibilityFilter Filter = new();

    private static MirrorEntity Entity(uint netId, EntityKind kind, ushort sector, int px, ushort ownerTeam = 0,
        ushort ownerPlayer = 0, ushort controller = 0, EntityOrigin origin = EntityOrigin.AuthorityRuntime)
    {
        var mirror = new WorldMirror();
        mirror.Spawn(Rec(netId, kind, sector, px: px, ownerTeam: ownerTeam, ownerPlayer: ownerPlayer,
            controller: controller, origin: origin));
        Assert.True(mirror.TryGet(netId, out var entity));
        return entity;
    }

    private static VisibilityViewer Viewer(ushort sector = 3, int px = 0, int team = 1, int range = 30000) =>
        new(PlayerId: 9, TeamId: team, Sector: sector, Px: px, Py: 0, Pz: 0, RadarRangeMeters: range);

    [Fact]
    public void ANpcInsideTheRadarRangeIsVisibleAndOneBeyondItIsNot()
    {
        var near = Entity(1, EntityKind.ShipM, 3, 20_000 * PerMetre);
        var far = Entity(2, EntityKind.ShipM, 3, 40_000 * PerMetre);

        Assert.True(Filter.IsVisible(Viewer(), near));
        Assert.False(Filter.IsVisible(Viewer(), far));
    }

    [Fact]
    public void TheRangeIsMeasuredInThreeDimensions()
    {
        // 30 km down the x axis is on the boundary, 30 km diagonally is 42 km away.
        var onAxis = Entity(1, EntityKind.ShipM, 3, 30_000 * PerMetre);
        var diagonal = Entity(2, EntityKind.ShipM, 3, 22_000 * PerMetre);
        var viewer = Viewer();

        Assert.True(Filter.IsVisible(viewer, onAxis));
        Assert.False(Filter.IsVisible(viewer with { Py = 22_000 * PerMetre }, diagonal));
    }

    [Fact]
    public void PlayerShipsAreGalaxyWideSoTheMapNeverLosesAnAlly()
    {
        var farAvatar = Entity(1, EntityKind.ShipS, 3, 900_000 * PerMetre, controller: 2, origin: EntityOrigin.PlayerShip);

        Assert.True(Filter.IsVisible(Viewer(), farAvatar));
    }

    [Fact]
    public void TheViewersOwnShipAndTeamAlwaysStayVisible()
    {
        var own = Entity(1, EntityKind.ShipM, 3, 900_000 * PerMetre, ownerTeam: 1);
        var ownByPlayer = Entity(2, EntityKind.ShipM, 3, 900_000 * PerMetre, ownerPlayer: 9);
        var enemy = Entity(3, EntityKind.ShipM, 3, 900_000 * PerMetre, ownerTeam: 2);

        Assert.True(Filter.IsVisible(Viewer(), own));
        Assert.True(Filter.IsVisible(Viewer(), ownByPlayer));
        Assert.False(Filter.IsVisible(Viewer(), enemy));
    }

    [Fact]
    public void StationsAndOtherNonShipKindsAreNeverHidden()
    {
        var farStation = Entity(1, EntityKind.Station, 3, 900_000 * PerMetre, ownerTeam: 2);
        var farGate = Entity(2, EntityKind.Gate, 3, 900_000 * PerMetre);

        Assert.True(Filter.IsVisible(Viewer(), farStation));
        Assert.True(Filter.IsVisible(Viewer(), farGate));
    }

    [Fact]
    public void AnotherSectorIsUntouchedSoPrefetchKeepsWorking()
    {
        var farOtherSector = Entity(1, EntityKind.ShipM, 4, 900_000 * PerMetre, ownerTeam: 2);

        Assert.True(Filter.IsVisible(Viewer(), farOtherSector));
    }

    [Fact]
    public void ARangeOfZeroIsTheOldNoFogBehaviour()
    {
        var far = Entity(1, EntityKind.ShipM, 3, 900_000 * PerMetre, ownerTeam: 2);

        Assert.True(Filter.IsVisible(Viewer(range: 0), far));
    }

    [Fact]
    public void TheIdOnlyOverloadHidesNothingBecauseItHasNoViewerPosition()
    {
        var far = Entity(1, EntityKind.ShipM, 3, 900_000 * PerMetre, ownerTeam: 2);

        // The default interface method forwards to this overload, so an old filter that only implements this one keeps working -
        // and this one cannot measure a distance, so it must not treat the far ship as invisible.
        Assert.True(Filter.IsVisible(9, far));
    }
}
