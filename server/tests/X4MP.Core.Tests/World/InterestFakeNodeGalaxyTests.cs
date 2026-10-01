using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Interest;
using X4MP.Core.Net;
using X4MP.Core.World;
using X4MP.FakeNode;
using X4MP.Proto;
using static X4MP.Core.Tests.World.WorldKit;

namespace X4MP.Core.Tests.World;

/// <summary>
/// The mirror and interest manager against FakeNode's deterministic galaxy (152 sectors, gate graph, about 10k ships), with
/// FakeNode's authority answering the CaptureSets: realistic graph, realistic capture round trips and sector sizes.
/// </summary>
public sealed class InterestFakeNodeGalaxyTests
{
    private sealed class Harness
    {
        private int _captureSetsDelivered;
        private long _tick;

        public Harness(InterestOptions? options = null)
        {
            Galaxy = FakeGalaxy.Generate(42);
            Authority = new FakeAuthority(new FakeWorld(Galaxy));
            Options = options ?? new InterestOptions { MaxGhosts = 100_000 };
            Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
            Mirror = new WorldMirror(Time, initialCapacity: 16384);
            Transport = new FakeTransport();
            Manager = new InterestManager(Mirror, () => Options, Time, Transport);
            foreach (var message in Authority.StartupMessages())
            {
                Feed(message);
            }
        }

        public FakeGalaxy Galaxy { get; }

        public FakeAuthority Authority { get; }

        public InterestOptions Options { get; }

        public FakeTimeProvider Time { get; }

        public WorldMirror Mirror { get; }

        public FakeTransport Transport { get; }

        public InterestManager Manager { get; }

        /// <summary>Delivers a message from the authority to the server the way the actor would.</summary>
        private void Feed(OutMessage message)
        {
            var frame = new InboundFrame(AsFrame(message.Type, message.Payload), 0);
            if (message.Type == MsgType.SectorComplete)
            {
                Manager.HandleSectorComplete(Decode<SectorComplete>(MsgType.SectorComplete, message.Payload));
            }
            else
            {
                Mirror.HandleFrame(frame, 99, senderIsAuthority: true);
            }
        }

        /// <summary>One 50 ms step: the server ticks, the authority receives new capture sets and answers.</summary>
        public void Step()
        {
            Time.Advance(TimeSpan.FromMilliseconds(50));
            _tick++;
            Manager.Tick(Time.GetTimestamp());
            while (_captureSetsDelivered < Transport.ToAuthority.Count)
            {
                var sent = Transport.ToAuthority[_captureSetsDelivered++];
                if (sent.Type == MsgType.CaptureSet)
                {
                    Authority.OnCaptureSet(Decode<CaptureSet>(MsgType.CaptureSet, sent.Payload).UnPack(), _tick);
                }
            }

            foreach (var message in Authority.Tick(_tick))
            {
                Feed(message);
            }
        }

        public void Run(int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                Step();
            }
        }

        public void PlacePlayer(int player, ushort sector) =>
            Mirror.ApplyPlayerState(player, Decode<PlayerState>(MsgType.PlayerState, PlayerStatePayload((uint)(player * 1000) + (uint)_tick + 1, 0, sector, 0)));

        /// <summary>The ghosts a fully delivered client must hold: every transient entity of every subscribed sector.</summary>
        public HashSet<uint> ExpectedHeld(int player) =>
            [.. Manager.Subscriptions(player).SelectMany(s => Mirror.TransientIn(s.Sector).ToArray().Select(e => e.NetId))];
    }

    private static ushort BusySector(FakeGalaxy g) =>
        g.Sectors.Where(s => g.Neighbors(s.Index).Count >= 2).OrderBy(s => s.Index).First().Index;

    [Fact]
    public void TheMirrorBuildsTheRealGraphFromTheFakeAuthoritysGalaxyMetadata()
    {
        var h = new Harness();
        var graph = h.Mirror.Graph;

        Assert.Equal(152, graph.SectorCount);
        foreach (var sector in h.Galaxy.Sectors)
        {
            var expected = h.Galaxy.Neighbors(sector.Index).Select(n => n.Sector).Distinct().Order().ToArray();
            Assert.Equal(expected, graph.Neighbors(sector.Index).ToArray().Order());
        }

        // k = 2: everything within two gate hops, nothing twice, never the origin.
        ushort s0 = BusySector(h.Galaxy);
        var one = graph.WithinHops(s0, 1).ToArray();
        var two = graph.WithinHops(s0, 2).ToArray();
        Assert.True(two.Length >= one.Length);
        Assert.Equal(one.Order(), two.Take(one.Length).Order());
        Assert.Equal(two.Length, two.Distinct().Count());
        Assert.DoesNotContain(s0, two);
        Assert.Equal(h.Galaxy.Sectors.Count, graph.Sectors.Length);
        Assert.True(h.Mirror.Strings.Count > 0);                       // the startup replay filled the string table
    }

    [Fact]
    public void AClientFollowingTheFakeGalaxyHoldsExactlyTheEntitiesOfItsSectorsAndSurvivesAGateCrossing()
    {
        var h = new Harness();
        ushort start = BusySector(h.Galaxy);
        const int Alice = 1;
        h.Manager.ClientActivated(Alice, 0, isAuthority: false);
        h.PlacePlayer(Alice, start);

        var neighbours = h.Mirror.Graph.Neighbors(start).ToArray();
        Assert.Equal(1 + neighbours.Length, h.Manager.Subscriptions(Alice).Count);

        // Round trips: CaptureSet -> index passes -> spawns -> SectorComplete -> forwarded.
        h.Run(120);
        Assert.All(h.Manager.Subscriptions(Alice), s => Assert.Equal(SectorDelivery.Delivered, s.Delivery));
        Assert.Equal(h.ExpectedHeld(Alice), [.. h.Manager.HeldBy(Alice)]);
        Assert.True(h.Manager.GhostCount(Alice) > 0);
        Assert.Equal(h.Manager.Subscriptions(Alice).Count, h.Transport.Decode<SectorComplete>(Alice, MsgType.SectorComplete).Count());

        // Jump through the first gate: the sector it leads to is promoted, its entities are not despawned or respawned.
        ushort target = neighbours[0];
        var targetEntities = h.Mirror.TransientIn(target).ToArray().Select(e => e.NetId).ToHashSet();
        Assert.NotEmpty(targetEntities);
        int spawnedBefore = h.Transport.Decode<EntitySpawn>(Alice, MsgType.EntitySpawn).Sum(m => m.EntitiesLength);
        h.Transport.ToClients.Clear();

        h.PlacePlayer(Alice, target);
        h.Run(120);

        var despawned = h.Transport.Decode<EntityDespawn>(Alice, MsgType.EntityDespawn)
            .SelectMany(m => Enumerable.Range(0, m.EntriesLength).Select(i => m.Entries(i)!.Value.NetId)).ToHashSet();
        var respawned = h.Transport.Decode<EntitySpawn>(Alice, MsgType.EntitySpawn)
            .SelectMany(m => Enumerable.Range(0, m.EntitiesLength).Select(i => m.Entities(i)!.Value.NetId)).ToHashSet();
        Assert.False(despawned.Overlaps(targetEntities));      // the promoted sector never flickers
        Assert.False(respawned.Overlaps(targetEntities));
        Assert.True(spawnedBefore > 0);

        Assert.Equal(InterestTier.Sector, h.Manager.GetSectorTier(Alice, target));
        Assert.All(h.Manager.Subscriptions(Alice), s => Assert.Equal(SectorDelivery.Delivered, s.Delivery));
        Assert.Equal(h.ExpectedHeld(Alice), [.. h.Manager.HeldBy(Alice)]);

        // The authority was asked for the union, keeps the old sector warm, and nothing is evicted before the 60 s grace.
        Assert.Contains(start, h.Authority.CapturedSectors);
    }

    [Fact]
    public void TwoClientsInDifferentRegionsGetADisjointCaptureUnionAndEvictionFreesTheMirror()
    {
        var h = new Harness(new InterestOptions { MaxGhosts = 100_000, LingerSeconds = 0 });
        var sectors = h.Galaxy.Sectors.Select(s => s.Index).ToArray();
        ushort a = BusySector(h.Galaxy);
        ushort b = sectors.Last(s => h.Mirror.Graph.Neighbors(s).Length >= 1 && h.Mirror.Graph.HopDistance(a, s, 4) < 0);
        h.Manager.ClientActivated(1, 0, false);
        h.Manager.ClientActivated(2, 0, false);
        h.PlacePlayer(1, a);
        h.PlacePlayer(2, b);
        h.Run(100);

        var captured = h.Authority.CapturedSectors.Order().ToArray();
        var expected = h.Manager.Subscriptions(1).Concat(h.Manager.Subscriptions(2)).Select(s => s.Sector).Distinct().Order().ToArray();
        Assert.Equal(expected, captured);
        int hot = h.Mirror.HotCount;
        Assert.True(hot > 0);

        // Client 2 leaves: its sectors age out after 60 s and the mirror drops their transient entities.
        h.Manager.ClientDeactivated(2);
        h.Run(20 * 70);
        Assert.Equal(h.Manager.Subscriptions(1).Select(s => s.Sector).Order().ToArray(), h.Authority.CapturedSectors.Order().ToArray());
        Assert.True(h.Mirror.HotCount < hot);
    }
}
