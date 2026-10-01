using X4MP.Core.Interest;
using X4MP.Core.World;
using X4MP.Proto;
using static X4MP.Core.Tests.World.WorldKit;

namespace X4MP.Core.Tests.World;

public sealed class InterestManagerTests
{
    // Line galaxy 1-2-3-4-5-6 joined by gates. Entity ids: sector s holds 100*s+1 ... 100*s+n.
    private static InterestRig NewRig(InterestOptions? options = null, int sectorShips = 5)
    {
        var rig = new InterestRig(6, options);
        for (ushort s = 1; s <= 6; s++)
        {
            rig.Ships(s, (uint)(100 * s) + 1, sectorShips);
        }

        return rig;
    }

    private static uint[] Ids(ushort sector, int count = 5) => [.. Enumerable.Range(1, count).Select(i => (uint)(100 * sector) + (uint)i)];

    // ---------------------------------------------------------------- tiers and delivery

    [Fact]
    public void FirstPositionSendsAFullInterestUpdateWithSectorAndAdjacentTiers()
    {
        var rig = NewRig();
        rig.Activate(InterestRig.Alice);
        Assert.Empty(rig.Transport.ToClients);   // no position yet: nothing to follow

        rig.Move(InterestRig.Alice, 3);

        var update = Assert.Single(rig.Updates(InterestRig.Alice));
        Assert.True(update.Full);
        Assert.Equal(1u, update.Epoch);
        Assert.Equal(
            new Dictionary<ushort, InterestTier> { [3] = InterestTier.Sector, [2] = InterestTier.Adjacent, [4] = InterestTier.Adjacent },
            update.Tiers);
        Assert.Equal(InterestTier.Adjacent, rig.Manager.GetSectorTier(InterestRig.Alice, 2));
        Assert.Equal(InterestTier.None, rig.Manager.GetSectorTier(InterestRig.Alice, 5));
    }

    [Fact]
    public void NothingIsForwardedUntilTheAuthorityCompletesTheSector()
    {
        var rig = NewRig();
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 3);

        // The sectors are asked for, but the authority has not completed them: no spawn, no marker.
        Assert.Empty(rig.Spawned(InterestRig.Alice));
        Assert.Empty(rig.Completes(InterestRig.Alice));
        Assert.All([2, 3, 4], s => Assert.Equal(SectorDelivery.Pending, rig.Manager.GetDelivery(InterestRig.Alice, (ushort)s)));

        var capture = Assert.Single(rig.CaptureSets());
        Assert.Equal(new Dictionary<ushort, int> { [2] = 1, [3] = 5, [4] = 1 }, InterestRig.Rates(capture));

        // The authority completes sector 3 only: just that one is forwarded, spawns first, marker last.
        rig.Complete(3);
        Assert.Equal(Ids(3), rig.Spawned(InterestRig.Alice).Order());
        Assert.Equal(((ushort)3, 5u), rig.Completes(InterestRig.Alice).Single());
        Assert.Equal(SectorDelivery.Delivered, rig.Manager.GetDelivery(InterestRig.Alice, 3));
        Assert.Equal(SectorDelivery.Pending, rig.Manager.GetDelivery(InterestRig.Alice, 2));
        Assert.Equal(MsgType.SectorComplete, rig.Types(InterestRig.Alice)[^1]);
        Assert.DoesNotContain(MsgType.SectorComplete, rig.Types(InterestRig.Alice).Take(rig.Types(InterestRig.Alice).Count - 1));
    }

    [Fact]
    public void AStaleCompletionForAnOlderCaptureEpochIsIgnored()
    {
        var rig = NewRig();
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 3);          // epoch 1 adds 2,3,4
        rig.Advance(TimeSpan.FromSeconds(1));
        rig.Move(InterestRig.Alice, 4);          // epoch 2 adds 5, drops 2 from the set only after the grace

        rig.Complete(3, epoch: 0);               // from before the sector was asked for
        Assert.Equal(1, rig.Manager.Stats.StaleSectorCompletes);
        Assert.Empty(rig.Spawned(InterestRig.Alice));

        rig.Complete(5);                          // sector 5 entered the set at epoch 2
        Assert.Equal(Ids(5), rig.Spawned(InterestRig.Alice).Order());
        rig.Complete(99);                         // never captured
        Assert.Equal(2, rig.Manager.Stats.StaleSectorCompletes);
    }

    [Fact]
    public void ACapturedAndCompleteSectorIsForwardedImmediatelyToANewSubscriber()
    {
        var rig = NewRig();
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 3);
        rig.CompleteAllCaptured();
        rig.ClearSent();

        // Bob arrives next to Alice: sectors 2..4 are already captured and complete, so nothing waits for the authority.
        rig.Activate(InterestRig.Bob);
        rig.Move(InterestRig.Bob, 3);

        Assert.Equal(Ids(3).Concat(Ids(2)).Concat(Ids(4)).Order(), rig.Spawned(InterestRig.Bob).Order());
        Assert.Equal(3, rig.Completes(InterestRig.Bob).Count);
        Assert.Empty(rig.CaptureSets());           // the union did not change
    }

    [Fact]
    public void NearestEntitiesOfTheCurrentSectorAreSentFirst()
    {
        var rig = new InterestRig(3);
        // Ships at increasing distance along x, ids deliberately not in distance order.
        rig.Mirror.Spawn(Rec(10, EntityKind.ShipS, 2, 900_000), Rec(11, EntityKind.ShipS, 2, 64), Rec(12, EntityKind.ShipS, 2, 300_000));
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 2, 0);
        rig.Complete(2);

        Assert.Equal([11u, 12, 10], rig.Spawned(InterestRig.Alice));
    }

    [Fact]
    public void GateCrossingSpawnsAndDespawnsExactlyTheDifferenceAndNeverTheSectorItPromotes()
    {
        var rig = NewRig();
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 2);          // follows 1,2,3
        rig.CompleteAllCaptured();
        Assert.Equal(Ids(1).Concat(Ids(2)).Concat(Ids(3)).Order(), rig.Spawned(InterestRig.Alice).Order());
        rig.ClearSent();

        rig.Move(InterestRig.Alice, 3);          // jumps through the gate: now follows 2,3,4

        // The capture set follows within the 500 ms debounce.
        Assert.Empty(rig.CaptureSets());
        rig.Settle();
        Assert.Equal(new Dictionary<ushort, int> { [1] = 1, [2] = 1, [3] = 5, [4] = 1 }, InterestRig.Rates(Assert.Single(rig.CaptureSets())));

        // Sector 3 was Adjacent and is promoted to Sector: not one spawn or despawn for it.
        Assert.Equal(Ids(1).Order(), rig.Despawned(InterestRig.Alice, DespawnReason.OutOfInterest).Order());
        Assert.Empty(rig.Spawned(InterestRig.Alice));        // sector 4 is not complete yet
        var update = Assert.Single(rig.Updates(InterestRig.Alice));
        Assert.False(update.Full);
        Assert.Equal(
            new Dictionary<ushort, InterestTier> { [3] = InterestTier.Sector, [2] = InterestTier.Adjacent, [4] = InterestTier.Adjacent, [1] = InterestTier.None },
            update.Tiers);
        Assert.Equal(2u, update.Epoch);

        // The authority completes the newly captured sector 4: only its entities are spawned.
        rig.Complete(4);
        Assert.Equal(Ids(4).Order(), rig.Spawned(InterestRig.Alice).Order());
        Assert.DoesNotContain(rig.Despawned(InterestRig.Alice), id => id is > 200 and < 400);
        Assert.Equal(SectorDelivery.Delivered, rig.Manager.GetDelivery(InterestRig.Alice, 3));
        Assert.True(Ids(3).All(id => rig.Manager.IsHeld(InterestRig.Alice, id)));
    }

    [Fact]
    public void InterestUpdateComesBeforeTheSpawnsAndTheMarkerBeforeLaterDespawns()
    {
        var rig = NewRig();
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 3);
        rig.Complete(3);

        var types = rig.Types(InterestRig.Alice);
        Assert.Equal([MsgType.InterestUpdate, MsgType.EntitySpawn, MsgType.SectorComplete], types);
    }

    // ---------------------------------------------------------------- linger

    [Fact]
    public void LingerHoldsAFormerSectorForTwentySecondsOnAFakeClock()
    {
        // One-way highway 2 -> 3: sector 3 does not lead back to 2, so 2 is only held by the linger tier.
        var galaxy = GalaxyPayload(
            Enumerable.Range(0, 32).Select(i => (byte)(i + 50)).ToArray(),
            [(1, "a"), (2, "b"), (3, "c"), (4, "d")],
            [(1, 2, LinkKind.Gate), (2, 3, LinkKind.Highway), (3, 4, LinkKind.Gate)]);
        var rig = new InterestRig(options: null, galaxy: galaxy);
        for (ushort s = 1; s <= 4; s++)
        {
            rig.Ships(s, (uint)(100 * s) + 1, 3);
        }

        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 2);
        rig.CompleteAllCaptured();
        Assert.True(Ids(2, 3).All(id => rig.Manager.IsHeld(InterestRig.Alice, id)));
        rig.ClearSent();

        rig.Move(InterestRig.Alice, 3);          // takes the highway
        rig.Settle();
        rig.CompleteAllCaptured();
        rig.ClearSent();
        Assert.Equal(InterestTier.Linger, rig.Manager.GetSectorTier(InterestRig.Alice, 2));

        rig.Advance(TimeSpan.FromSeconds(19));
        Assert.Equal(InterestTier.Linger, rig.Manager.GetSectorTier(InterestRig.Alice, 2));
        Assert.True(Ids(2, 3).All(id => rig.Manager.IsHeld(InterestRig.Alice, id)));
        Assert.Empty(rig.Despawned(InterestRig.Alice));

        rig.Advance(TimeSpan.FromSeconds(2));    // 21 s after leaving
        Assert.Equal(InterestTier.None, rig.Manager.GetSectorTier(InterestRig.Alice, 2));
        Assert.Equal(Ids(2, 3).Order(), rig.Despawned(InterestRig.Alice, DespawnReason.OutOfInterest).Order());
        Assert.DoesNotContain(Ids(2, 3), id => rig.Manager.IsHeld(InterestRig.Alice, id));
        Assert.Equal(InterestTier.None, rig.Updates(InterestRig.Alice)[^1].Tiers[2]);
    }

    [Fact]
    public void ReturningToALingeringSectorKeepsItsGhostsAndCancelsTheTimer()
    {
        var rig = NewRig(new InterestOptions { PrefetchDepth = 0 });
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 2);
        rig.CompleteAllCaptured();
        rig.Move(InterestRig.Alice, 3);
        rig.CompleteAllCaptured();
        rig.ClearSent();
        Assert.Equal(InterestTier.Linger, rig.Manager.GetSectorTier(InterestRig.Alice, 2));

        rig.Advance(TimeSpan.FromSeconds(10));
        rig.Move(InterestRig.Alice, 2);          // ping-pong back
        rig.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(InterestTier.Sector, rig.Manager.GetSectorTier(InterestRig.Alice, 2));
        Assert.DoesNotContain(rig.Despawned(InterestRig.Alice, DespawnReason.OutOfInterest), id => id is > 200 and < 300);
        Assert.True(Ids(2).All(id => rig.Manager.IsHeld(InterestRig.Alice, id)));
    }

    [Fact]
    public void PrefetchDepthZeroFollowsOnlyTheCurrentSector()
    {
        var rig = NewRig(new InterestOptions { PrefetchDepth = 0 });
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 3);
        Assert.Equal([(ushort)3], rig.Manager.Subscriptions(InterestRig.Alice).Select(s => s.Sector));
    }

    [Fact]
    public void PrefetchDepthTwoFollowsTwoHops()
    {
        var rig = NewRig(new InterestOptions { PrefetchDepth = 2 });
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 3);
        Assert.Equal([1, 2, 3, 4, 5], rig.Manager.Subscriptions(InterestRig.Alice).Select(s => (int)s.Sector).Order());
    }

    // ---------------------------------------------------------------- live changes

    [Fact]
    public void LiveSpawnsDespawnsAndMovesInsideDeliveredSectorsAreForwarded()
    {
        var rig = NewRig(sectorShips: 2);
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 3);
        rig.CompleteAllCaptured();
        rig.ClearSent();

        rig.Mirror.Spawn(Rec(900, EntityKind.ShipS, 3));                 // flies in or is built in the current sector
        rig.Mirror.Spawn(Rec(901, EntityKind.ShipS, 6));                 // far away: not followed
        Assert.Equal([900u], rig.Spawned(InterestRig.Alice));

        rig.Mirror.Update(State(900, 4, 0));                              // moves to adjacent sector 4: still held
        rig.Mirror.Update(State(900, 6, 0));                              // leaves to sector 6: out of interest
        Assert.Equal([900u], rig.Despawned(InterestRig.Alice, DespawnReason.OutOfInterest));
        Assert.False(rig.Manager.IsHeld(InterestRig.Alice, 900));

        rig.Mirror.Update(State(901, 3, 0));                              // a stranger arrives from 6 into our sector
        Assert.Equal([900u, 901], rig.Spawned(InterestRig.Alice));

        rig.Mirror.Despawn(DespawnReason.Destroyed, 901);                 // killed: the client hears the real reason
        Assert.Equal([901u], rig.Despawned(InterestRig.Alice, DespawnReason.Destroyed));
    }

    [Fact]
    public void ASectorStillPendingGetsNoLiveTraffic()
    {
        var rig = NewRig(sectorShips: 1);
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 3);
        rig.ClearSent();

        rig.Mirror.Spawn(Rec(950, EntityKind.ShipS, 3));
        rig.Mirror.Despawn(DespawnReason.Removed, 950);
        Assert.Empty(rig.Transport.ToClients);
    }

    [Fact]
    public void PersistentEntitiesGoToEveryClientWithTheJournalSequenceStamped()
    {
        var rig = NewRig(sectorShips: 1);
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 1);
        rig.Activate(InterestRig.Bob);
        rig.Move(InterestRig.Bob, 6);
        rig.ClearSent();

        rig.Mirror.Spawn(Rec(7000, EntityKind.Station, 4, ownerTeam: 1));      // sector 4: neither client follows it
        rig.Mirror.ApplyChange(Decode<EntityChange>(MsgType.EntityChange, ChangePayload(7000, ChangeField.OwnerTeam, ownerTeam: 2)));

        foreach (int player in new[] { InterestRig.Alice, InterestRig.Bob })
        {
            var spawn = rig.Transport.Decode<EntitySpawn>(player, MsgType.EntitySpawn).Single();
            Assert.Equal(1UL, spawn.JournalSeq);
            Assert.Equal(7000u, spawn.Entities(0)!.Value.NetId);
            var change = rig.Transport.Decode<EntityChange>(player, MsgType.EntityChange).Single();
            Assert.Equal((2UL, (ushort)2), (change.JournalSeq, change.OwnerTeam));
        }

        Assert.False(rig.Manager.IsHeld(InterestRig.Alice, 7000));    // never a ghost
    }

    [Fact]
    public void ChangesToTransientEntitiesReachOnlyTheClientsHoldingThem()
    {
        var rig = NewRig(sectorShips: 1);
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 3);
        rig.CompleteAllCaptured();
        rig.Activate(InterestRig.Bob);
        rig.Move(InterestRig.Bob, 6);
        rig.CompleteAllCaptured();
        rig.ClearSent();

        rig.Mirror.ApplyChange(Decode<EntityChange>(MsgType.EntityChange, ChangePayload(301, ChangeField.OwnerTeam, ownerTeam: 4)));

        Assert.Single(rig.Transport.Decode<EntityChange>(InterestRig.Alice, MsgType.EntityChange));
        Assert.Empty(rig.Transport.Decode<EntityChange>(InterestRig.Bob, MsgType.EntityChange));
    }

    [Fact]
    public void PlayerShipsAreVisibleGalaxyWideRegardlessOfSectors()
    {
        var rig = NewRig(sectorShips: 1);
        rig.Mirror.Spawn(Rec(5000, EntityKind.ShipM, 6, 0, controller: 2, origin: EntityOrigin.PlayerShip));
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 1);

        Assert.Equal([5000u], rig.Spawned(InterestRig.Alice));          // sector 6 is nowhere near, still listed
        Assert.Equal(0, rig.Manager.GhostCount(InterestRig.Alice));     // and outside the budget

        rig.Mirror.Spawn(Rec(5001, EntityKind.ShipM, 5, 0, controller: 3, origin: EntityOrigin.PlayerShip));
        Assert.Equal([5000u, 5001], rig.Spawned(InterestRig.Alice));
        Assert.Equal(InterestTier.Adjacent, rig.Manager.TierOf(InterestRig.Alice, rig.Mirror.TryGet(5001, out var e) ? e : null!));
    }

    // ---------------------------------------------------------------- the authority's own player

    [Fact]
    public void TheAuthorityPlayerContributesToTheCaptureSetButIsNeverSentSpawns()
    {
        var rig = NewRig();
        rig.Activate(InterestRig.Alice, authority: true);
        rig.Move(InterestRig.Alice, 4);

        Assert.Empty(rig.Updates(InterestRig.Alice));
        rig.Complete(4);
        Assert.Empty(rig.Spawned(InterestRig.Alice));

        var set = Assert.Single(rig.CaptureSets());
        Assert.Equal(new Dictionary<ushort, int> { [4] = 5 }, InterestRig.Rates(set));   // its own sector only, no prefetch
        Assert.Equal(1, set.FocusLength);
    }

    // ---------------------------------------------------------------- resume

    [Fact]
    public void ADeactivatedClientStartsFreshWithAFullUpdateAndAllSpawnsAgain()
    {
        var rig = NewRig(sectorShips: 2);
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 3);
        rig.CompleteAllCaptured();
        rig.Manager.ClientDeactivated(InterestRig.Alice);
        Assert.False(rig.Manager.IsActive(InterestRig.Alice));
        rig.ClearSent();

        rig.Activate(InterestRig.Alice);          // resumed: position known from the mirror

        Assert.True(rig.Updates(InterestRig.Alice).Single().Full);
        Assert.Equal(6, rig.Spawned(InterestRig.Alice).Count);    // 2 ships in each of 2,3,4
        Assert.Equal(3, rig.Completes(InterestRig.Alice).Count);
    }
}
