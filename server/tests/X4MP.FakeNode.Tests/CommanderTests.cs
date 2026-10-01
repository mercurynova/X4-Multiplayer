using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode.Tests;

/// <summary>M1-T4: <c>--commander</c> parsing, the authority's team tagging and how a client picks the assets it orders.</summary>
public sealed class CommanderTests
{
    [Theory]
    [InlineData("shared", CommanderMode.Shared)]
    [InlineData("own", CommanderMode.Own)]
    [InlineData("Foreign", CommanderMode.Foreign)]
    public void CommanderParses(string value, CommanderMode expected)
    {
        var r = CliParser.Parse(["swarm", "--clients", "2", "--commander", value]);

        Assert.True(r.Ok);
        Assert.Equal(expected, r.Options!.Commander);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("boss")]
    public void CommanderRejectsOtherValues(string value) =>
        Assert.False(CliParser.Parse(["client", "--commander", value]).Ok);

    [Fact]
    public void TeamAssetsIsAFlagAndOffByDefault()
    {
        Assert.False(CliParser.Parse(["authority"]).Options!.TeamAssets);
        Assert.True(CliParser.Parse(["authority", "--team-assets"]).Options!.TeamAssets);
        Assert.Equal(CommanderMode.None, CliParser.Parse(["client"]).Options!.Commander);
    }

    private static (FakeAuthority Authority, FakeClientSession Client, int Spawned) Setup(bool teamAssets)
    {
        var world = new FakeWorld(FakeGalaxy.Generate(42));
        var authority = new FakeAuthority(world, new FakeAuthorityOptions { TeamAssets = teamAssets });
        var client = new FakeClientSession(new FakeWorld(FakeGalaxy.Generate(42)), verify: false);
        ushort sector = (ushort)world.Galaxy.Entities.Where(e => !e.IsStation).GroupBy(e => e.HomeSector).OrderByDescending(g => g.Count()).First().Key;
        authority.OnCaptureSet(new CaptureSetT { Epoch = 1, Sectors = [new CaptureSectorT { Sector = sector, RateHz = 5 }] }, 0);
        int spawned = 0;
        for (long tick = 0; tick < 200 && spawned == 0; tick++)
        {
            foreach (var m in authority.Tick(tick).Where(m => m.Type == MsgType.EntitySpawn))
            {
                var frame = new Frame(m.Type, FrameOptions.None, MessageRegistry.Default.GetDescriptor(m.Type).Lane, m.Payload);
                spawned += MessageRegistry.Default.Decode<EntitySpawn>(frame).EntitiesLength;
                client.Handle(frame);
            }
        }

        return (authority, client, spawned);
    }

    [Fact]
    public void WithoutTeamAssetsEveryShipIsNpcAndNobodyHasAnythingToOrder()
    {
        var (_, client, spawned) = Setup(teamAssets: false);

        Assert.True(spawned > 0);
        foreach (var mode in new[] { CommanderMode.Own, CommanderMode.Shared, CommanderMode.Foreign })
            Assert.Null(client.PickAsset(mode, myTeam: 1, myPlayer: 5, n: 0));
    }

    [Fact]
    public void TheAuthorityTagsShipsAndEachModePicksItsKind()
    {
        var (authority, client, spawned) = Setup(teamAssets: true);

        Assert.True(spawned > 0);
        var own = client.PickAsset(CommanderMode.Own, 1, 5, 0);
        var shared = client.PickAsset(CommanderMode.Shared, 1, 5, 0);
        var foreign = client.PickAsset(CommanderMode.Foreign, 1, 5, 0);
        Assert.NotNull(own);
        Assert.NotNull(shared);
        Assert.NotNull(foreign);
        Assert.Equal(((ushort)1, (ushort)0), authority.TeamAssetOwner((int)own!.Value.NetId, false));
        Assert.Equal(((ushort)1, FakeAuthorityOptions.TeammatePlayerId), authority.TeamAssetOwner((int)shared!.Value.NetId, false));
        Assert.Equal((ushort)2, authority.TeamAssetOwner((int)foreign!.Value.NetId, false).Team); // any ship of another team

        // seen from team 2 the "foreign" ships are team 1, and an unassigned client commands nothing
        Assert.Equal((ushort)1, authority.TeamAssetOwner((int)client.PickAsset(CommanderMode.Foreign, 2, 5, 0)!.Value.NetId, false).Team);
        Assert.Null(client.PickAsset(CommanderMode.Own, 0, 5, 0));
        Assert.Null(client.PickAsset(CommanderMode.None, 1, 5, 0));
    }

    [Fact]
    public void PicksRotateOverTheCandidatesAndStationsStayNpc()
    {
        var (authority, client, _) = Setup(teamAssets: true);

        var picks = Enumerable.Range(0, 6).Select(n => client.PickAsset(CommanderMode.Foreign, 1, 5, n)!.Value.NetId).ToList();

        Assert.True(picks.Distinct().Count() > 1);
        Assert.Equal(((ushort)0, (ushort)0), authority.TeamAssetOwner(1, isStation: true));
    }
}
