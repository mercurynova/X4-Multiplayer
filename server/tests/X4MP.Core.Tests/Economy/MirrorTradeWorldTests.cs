using X4MP.Core.Economy;
using X4MP.Core.Teams;
using X4MP.Core.Tests.World;
using X4MP.Core.World;
using X4MP.Proto;

namespace X4MP.Core.Tests.Economy;

/// <summary>The trade view of the world mirror: ownership, positions, asset-policy rules, and the owner change after a settled ship trade.</summary>
public sealed class MirrorTradeWorldTests
{
    private static (WorldMirror Mirror, MirrorTradeWorld World, TeamOptions Options) Make(Func<int, int?>? leader = null)
    {
        var teams = new FakeTeamDirectory();
        teams.Set([1, 2], new Dictionary<int, int?> { [1] = 1, [2] = 1, [3] = 2 });
        var mirror = new WorldMirror();
        mirror.Spawn(
            WorldKit.Rec(100, EntityKind.ShipM, 5, ownerTeam: 1, ownerPlayer: 1),
            WorldKit.Rec(101, EntityKind.ShipM, 5, ownerTeam: 1, ownerPlayer: 0),
            WorldKit.Rec(200, EntityKind.ShipL, 6, ownerTeam: 2, ownerPlayer: 3),
            WorldKit.Rec(310, EntityKind.Station, 5, ownerTeam: 0));
        var options = new TeamOptions();
        return (mirror, new MirrorTradeWorld(mirror, teams, () => options, leader), options);
    }

    [Fact]
    public void AssetsAreReadFromTheMirror()
    {
        var (_, world, _) = Make();

        Assert.True(world.TryGetAsset(100, out var ship));
        Assert.Equal((EntityKind.ShipM, 1, 1, (ushort)5, true, false), (ship.Kind, ship.OwnerTeam, ship.OwnerPlayer, ship.Sector, ship.IsShip, ship.IsPlayerShip));
        Assert.True(world.TryGetAsset(310, out var station));
        Assert.False(station.IsShip);
        Assert.False(world.TryGetAsset(999, out _));
        Assert.False(world.TryGetPlayerShip(1, out _, out _));
        Assert.Null(world.CargoAmount(100, 7));
    }

    [Fact]
    public void GivingNeedsTheAssetPolicyAndCrossTeamShipsNeedAssetTransfer()
    {
        var (_, world, options) = Make();
        world.TryGetAsset(100, out var ownShip);
        world.TryGetAsset(101, out var common);
        world.TryGetAsset(200, out var foreign);

        Assert.Null(world.DenyGive(1, ownShip, crossTeam: false, shipTransfer: true));
        Assert.Null(world.DenyGive(2, ownShip, crossTeam: false, shipTransfer: true)); // SharedCommand: a teammate may sell it
        Assert.Null(world.DenyGive(1, common, crossTeam: false, shipTransfer: true));
        Assert.NotNull(world.DenyGive(1, foreign, crossTeam: false, shipTransfer: true)); // another team's

        Assert.Equal("ships cannot change teams (asset transfers are disabled)", world.DenyGive(1, ownShip, crossTeam: true, shipTransfer: true));
        Assert.Null(world.DenyGive(1, ownShip, crossTeam: true, shipTransfer: false)); // cargo may cross teams
        options.AllowAssetTransfer = true;
        Assert.Null(world.DenyGive(1, ownShip, crossTeam: true, shipTransfer: true));

        options.AssetPolicy = TeamAssetPolicy.OwnerOnly;
        Assert.NotNull(world.DenyGive(2, ownShip, crossTeam: false, shipTransfer: true)); // a teammate's ship under OwnerOnly
        Assert.Null(world.DenyGive(2, common, crossTeam: false, shipTransfer: true));      // team-common stays free
    }

    [Fact]
    public void AReceiverMayOnlyTakeWaresIntoItsOwnTeamsContainer()
    {
        var (_, world, _) = Make();
        world.TryGetAsset(100, out var own);
        world.TryGetAsset(200, out var foreign);

        Assert.Null(world.DenyReceive(2, own));
        Assert.NotNull(world.DenyReceive(2, foreign));
    }

    [Fact]
    public void ASettledShipTradeChangesTheMirrorOwnerAndCarriesTheTradeId()
    {
        var (mirror, world, _) = Make();

        world.SetOwner(100, team: 2, player: 3, new Id128T { Lo = 77 });

        Assert.True(mirror.TryGet(100, out var ship));
        Assert.Equal((2, 3), (ship.OwnerTeam, ship.OwnerPlayer));
        Assert.True(world.TryGetAsset(100, out var info));
        Assert.Equal(2, info.OwnerTeam);
    }
}
