using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode.Tests;

/// <summary>M3-05: <c>--galaxy-file</c> reads the sitting-0 galaxy dump (synthetic fixture, no game data) and feeds GalaxyMetadata.</summary>
public sealed class GalaxyDumpTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "galaxy-dump-small.json");

    private static FakeGalaxy Build(ulong seed = 42) =>
        FakeGalaxy.FromDump(seed, new GalaxyOptions { ShipCount = 200 }, GalaxyDump.Load(Fixture).Dump!);

    [Fact]
    public void TheFixtureParsesAndCountsGateTargetsThatAreNotInTheFile()
    {
        var (dump, error) = GalaxyDump.Load(Fixture);
        Assert.Null(error);
        Assert.Equal(7, dump!.Sectors.Count);
        Assert.Equal(1, dump.UnknownGateTargets);
        Assert.Contains(dump.Sectors, s => s.Macro == "cluster_09_sector001_macro" && s.Gates.Count == 0);
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("{}", "no 'sectors' array")]
    [InlineData("{\"sectors\":[{\"macro\":\"a\"}]}", "at least 2 sectors")]
    [InlineData("{\"sectors\":[{\"macro\":\"a\"},{\"macro\":\"a\"}]}", "appears twice")]
    [InlineData("{\"sectors\":[{\"macro\":\"a\"},{\"name\":\"b\"}]}", "no 'macro'")]
    public void BrokenDumpsAreRefusedWithAReason(string json, string reason)
    {
        var (dump, error) = GalaxyDump.Parse(json);
        Assert.Null(dump);
        Assert.Contains(reason, error);
    }

    [Fact]
    public void ASmallHandMadeFileWithoutClustersOrGatesIsEnough()
    {
        var (dump, error) = GalaxyDump.Parse("{\"sectors\":[{\"macro\":\"b_macro\"},{\"macro\":\"a_macro\",\"gates\":[\"b_macro\"]}]}");
        Assert.Null(error);
        var g = FakeGalaxy.FromDump(1, new GalaxyOptions { ShipCount = 50 }, dump!);
        Assert.Equal(["a_macro", "b_macro"], g.Sectors.Select(s => s.Macro));
        Assert.Single(g.Links);
    }

    [Fact]
    public void SectorsGetTheirIndexFromTheOrdinalRankOfTheMacroAndTheGraphFollowsTheFile()
    {
        var g = Build();
        Assert.Equal(
            ["cluster_01_sector001_macro", "cluster_01_sector002_macro", "cluster_01_sector003_macro", "cluster_02_sector001_macro", "cluster_02_sector002_macro", "cluster_03_sector001_macro", "cluster_09_sector001_macro"],
            g.Sectors.Select(s => s.Macro));
        Assert.Equal(Enumerable.Range(1, 7).Select(i => (ushort)i), g.Sectors.Select(s => s.Index));
        Assert.Equal(["cluster_01_macro", "cluster_01_macro", "cluster_01_macro", "cluster_02_macro", "cluster_02_macro", "cluster_03_macro", "cluster_09_macro"], g.Sectors.Select(s => s.ClusterMacro));
        Assert.Equal([1, 1, 1, 2, 2, 3, 9], g.Sectors.Select(s => s.ClusterNumber));

        // undirected, every pair once: 1-2, 1-4, 2-3, 4-5, 5-6 (the gate to an unknown sector is ignored)
        Assert.Equal([(1, 2), (1, 4), (2, 3), (4, 5), (5, 6)], g.Links.Select(l => ((int)l.A, (int)l.B)));
        Assert.All(g.Links, l => Assert.Equal(LinkKind.Gate, l.Kind));
        Assert.Equal(1, g.IgnoredGateTargets);
        Assert.Equal([2, 4], g.Neighbors(1).Select(n => (int)n.Sector));
        Assert.Empty(g.Neighbors(7));
        Assert.Equal<ushort[]>([1, 2, 3, 4, 5, 6], [.. g.PlayableSectors]);
    }

    [Fact]
    public void TheGalaxyIsDeterministicAndStillHasShipsAndStations()
    {
        var a = Build();
        var b = Build();
        Assert.Equal(a.Entities.Select(e => (e.Macro, e.HomeSector, e.Name)), b.Entities.Select(e => (e.Macro, e.HomeSector, e.Name)));
        Assert.Equal(a.Links.Select(l => (l.PosInA, l.PosInB)), b.Links.Select(l => (l.PosInA, l.PosInB)));
        Assert.True(a.StationCount > 0);
        Assert.True(a.ShipCount > 0);
        Assert.NotEqual(a.Entities.Select(e => e.Name), Build(7).Entities.Select(e => e.Name));
        var world = new FakeWorld(a);
        Assert.True(world.EntitiesInSector(1, 0).Count > 0);
    }

    [Fact]
    public void GalaxyMetadataCarriesTheRealSectorMacrosAndBothDirectionsOfEveryLink()
    {
        var authority = new FakeAuthority(new FakeWorld(Build()));
        var message = authority.BuildGalaxyMetadata();
        Assert.Equal(MsgType.GalaxyMetadata, message.Type);
        var meta = MessageRegistry.Default.Decode<GalaxyMetadata>(
            new Frame(message.Type, FrameOptions.None, MessageRegistry.Default.GetDescriptor(message.Type).Lane, message.Payload)).UnPack();
        Assert.Equal(7, meta.Sectors.Count);
        Assert.Equal("cluster_01_sector001_macro", meta.Sectors[0].Macro);
        Assert.Equal("cluster_01_macro", meta.Sectors[0].ClusterMacro);
        Assert.Equal(10, meta.Links.Count);
        Assert.Contains(meta.Links, l => l.From == 4 && l.To == 1);
        Assert.Contains(meta.Links, l => l.From == 1 && l.To == 4);
    }

    [Fact]
    public void PlayersNeverStartInASectorWithoutAGate()
    {
        var g = Build();
        for (int i = 0; i < 40; i++)
        {
            var p = new FakePlayer(g, 42, i, ClientBehavior.Explore);
            Assert.NotEqual((ushort)7, p.Sector);
            for (int t = 0; t < 400; t++)
                p.Step(t);
        }
    }

    [Fact]
    public void TheCliReadsAndChecksTheGalaxyFile()
    {
        var ok = CliParser.Parse(["galaxy", "--galaxy-file", Fixture]);
        Assert.True(ok.Ok, ok.Error);
        var galaxy = ok.Options!.BuildGalaxy();
        Assert.Equal(7, galaxy.Sectors.Count);
        Assert.Equal(7, GalaxyStats.Of(galaxy).Sectors);

        var missing = CliParser.Parse(["galaxy", "--galaxy-file", Fixture + ".nope"]);
        Assert.False(missing.Ok);
        Assert.Contains("--galaxy-file", missing.Error);
    }

    [Fact]
    public void FakeStringTableAddsNewStringsOnceAndReportsThem()
    {
        var authority = new FakeAuthority(new FakeWorld(Build()));
        uint a = authority.Strings.Ensure("ship_arg_s_fighter_01_a_macro", StringKind.Macro, out var added);
        Assert.NotNull(added);
        Assert.Equal(a, added!.Index);
        uint again = authority.Strings.Ensure("ship_arg_s_fighter_01_a_macro", StringKind.Macro, out var second);
        Assert.Equal(a, again);
        Assert.Null(second);
        Assert.Equal(a, authority.Strings.Index("ship_arg_s_fighter_01_a_macro"));
    }
}
