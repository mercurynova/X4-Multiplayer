using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode.Tests;

public sealed class PlayerAndCliTests
{
    private static readonly FakeGalaxy Galaxy = FakeGalaxy.Generate(42);

    private static List<PlayerStateT> Run(ClientBehavior b, int ticks, int index = 0)
    {
        var p = new FakePlayer(Galaxy, 42, index, b) { NetId = 777 };
        return Enumerable.Range(0, ticks).Select(t => p.Step(t)).ToList();
    }

    [Theory]
    [InlineData(ClientBehavior.Wander)]
    [InlineData(ClientBehavior.Patrol)]
    [InlineData(ClientBehavior.Explore)]
    public void PlayerStateIsDeterministicAndWellFormed(ClientBehavior b)
    {
        var a = Run(b, 3000);
        var c = Run(b, 3000);
        Assert.Equal(a.Select(s => (s.Seq, s.Sector, s.Px, s.Py, s.Pz, s.Yaw, s.Flags)), c.Select(s => (s.Seq, s.Sector, s.Px, s.Py, s.Pz, s.Yaw, s.Flags)));
        Assert.NotEqual(a.Select(s => s.Px), Run(b, 3000, index: 1).Select(s => s.Px));

        Assert.Equal(Enumerable.Range(1, 3000).Select(i => (uint)i), a.Select(s => s.Seq));
        // 20 Hz: 50 ms between samples
        Assert.All(a.Zip(a.Skip(1)), pair => Assert.Equal(50_000UL, pair.Second.SampleTimeUs - pair.First.SampleTimeUs));
        Assert.All(a, s => Assert.Equal(777u, s.NetId));

        // every sample encodes through the real catalog
        foreach (var s in a.Take(50))
            Assert.Equal(MsgType.PlayerState, FrameCodec.TryDecode(MessageEncoder.EncodeFrame(MsgType.PlayerState, bld => PlayerState.Pack(bld, s)), out var f, out _) ? f.Type : 0);
    }

    [Fact]
    public void WanderStaysInItsSector()
    {
        var states = Run(ClientBehavior.Wander, 4000);
        Assert.Single(states.Select(s => s.Sector).Distinct());
        Assert.True(states.Select(s => s.Px).Distinct().Count() > 1000);
    }

    [Fact]
    public void PatrolLoopsThroughThreeSectors()
    {
        var p = new FakePlayer(Galaxy, 42, 3, ClientBehavior.Patrol);
        Assert.True(p.Route.Count >= 3);
        var visited = new HashSet<ushort>();
        for (int t = 0; t < 20 * 600; t++)
            visited.Add(p.Step(t).Sector);
        Assert.True(visited.Count >= 3, $"visited {visited.Count} sectors");
        Assert.True(visited.IsSubsetOf(p.Route));
    }

    [Fact]
    public void ExploreRandomWalksAndFlagsTeleports()
    {
        var states = Run(ClientBehavior.Explore, 20 * 900);
        Assert.True(states.Select(s => s.Sector).Distinct().Count() >= 4);
        int changes = 0;
        for (int i = 1; i < states.Count; i++)
        {
            if (states[i].Sector == states[i - 1].Sector)
                continue;
            changes++;
            Assert.True((states[i].Flags & (ushort)StateFlags.Teleport) != 0);
            Assert.Contains(Galaxy.Neighbors(states[i - 1].Sector), n => n.Sector == states[i].Sector);
        }
        Assert.True(changes >= 3);
    }

    [Fact]
    public void StepsMustIncrease()
    {
        var p = new FakePlayer(Galaxy, 1, 0, ClientBehavior.Wander);
        p.Step(5);
        Assert.Throws<ArgumentException>(() => p.Step(5));
    }

    // ---------------- CLI ----------------

    [Fact]
    public void ParsesAuthorityWithServerSeedAndFlags()
    {
        var r = CliParser.Parse(["authority", "--server", "10.0.0.5:47790", "--seed=99", "--verify", "--fps", "30"]);
        Assert.True(r.Ok, r.Error);
        var o = r.Options!;
        Assert.Equal(FakeNodeCommand.Authority, o.Command);
        Assert.Equal("10.0.0.5", o.Host);
        Assert.Equal(47790, o.Port);
        Assert.Equal(99UL, o.Seed);
        Assert.True(o.Verify);
        Assert.Equal(30, o.Fps);
    }

    [Fact]
    public void ParsesSwarmClientInspect()
    {
        var swarm = CliParser.Parse(["swarm", "--server", "localhost:47780", "--clients", "8", "--verify", "--behavior", "explore", "--udp"]).Options!;
        Assert.Equal(FakeNodeCommand.Swarm, swarm.Command);
        Assert.Equal(8, swarm.Clients);
        Assert.Equal(ClientBehavior.Explore, swarm.Behavior);
        Assert.True(swarm.Udp);

        var client = CliParser.Parse(["client", "--name", "Bob", "--count", "3", "--name-prefix", "Bot"]).Options!;
        Assert.Equal(("Bob", 3, "Bot"), (client.Name, client.Clients, client.NamePrefix));

        var inspect = CliParser.Parse(["inspect", "--sector", "12"]).Options!;
        Assert.Equal((ushort)12, inspect.Sector);
        Assert.Equal(ProtocolConstants.DefaultTcpPort, inspect.Port);
    }

    [Fact]
    public void NoArgsOrHelpShowsUsage()
    {
        Assert.Equal(FakeNodeCommand.Help, CliParser.Parse([]).Options!.Command);
        Assert.Equal(FakeNodeCommand.Help, CliParser.Parse(["--help"]).Options!.Command);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("authority", "--server", "nohostport")]
    [InlineData("authority", "--server", "h:99999")]
    [InlineData("authority", "--seed", "abc")]
    [InlineData("authority", "--seed")]
    [InlineData("client", "--behavior", "dance")]
    [InlineData("client", "--nope", "1")]
    [InlineData("client", "stray")]
    [InlineData("swarm", "--clients", "0")]
    [InlineData("authority", "--verify=maybe")]
    public void RejectsBadArguments(params string[] args)
    {
        var r = CliParser.Parse(args);
        Assert.False(r.Ok);
        Assert.False(string.IsNullOrEmpty(r.Error));
    }

    [Fact]
    public void GalaxyStatsReportTheShape()
    {
        var s = GalaxyStats.Of(Galaxy);
        Assert.Equal(152, s.Sectors);
        Assert.Contains("sectors=152", s.ToString(), StringComparison.Ordinal);
    }
}
