using X4MP.Core.Interest;
using X4MP.Core.World;
using X4MP.Proto;
using static X4MP.Core.Tests.World.WorldKit;

namespace X4MP.Core.Tests.World;

/// <summary>The capture set union, the ghost budget, hints, the near grid and the visibility hook.</summary>
public sealed class InterestCaptureBudgetTests
{
    private static uint[] Ids(ushort sector, uint first, int count) => [.. Enumerable.Range(0, count).Select(i => first + (uint)i)];

    // ---------------------------------------------------------------- capture set

    [Fact]
    public void CaptureSetIsTheUnionOfAllClientsAtTheHighestRateAndSendsAtMostEvery500Ms()
    {
        var rig = new InterestRig(8);
        rig.Activate(InterestRig.Alice);
        rig.Activate(InterestRig.Bob);

        rig.Move(InterestRig.Alice, 2);          // 1,2,3
        var first = Assert.Single(rig.CaptureSets());
        Assert.Equal(1u, first.Epoch);
        Assert.Equal(new Dictionary<ushort, int> { [1] = 1, [2] = 5, [3] = 1 }, InterestRig.Rates(first));

        rig.Move(InterestRig.Bob, 4);            // 3,4,5: 3 is Adjacent for both
        rig.Advance(TimeSpan.FromMilliseconds(100));
        rig.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Single(rig.CaptureSets());       // still inside the 500 ms window

        rig.Advance(TimeSpan.FromMilliseconds(400));
        var sets = rig.CaptureSets();
        Assert.Equal(2, sets.Count);
        Assert.Equal(2u, sets[1].Epoch);
        Assert.Equal(new Dictionary<ushort, int> { [1] = 1, [2] = 5, [3] = 1, [4] = 5, [5] = 1 }, InterestRig.Rates(sets[1]));

        // Bob moves into Alice's sector 3: the shared sector is asked for at the Sector rate, not twice.
        rig.Move(InterestRig.Bob, 3);
        rig.Settle();
        var third = rig.CaptureSets()[^1];
        Assert.Equal(5, InterestRig.Rates(third)[3]);
        Assert.Equal(1, Enumerable.Range(0, third.SectorsLength).Count(i => third.Sectors(i)!.Value.Sector == 3));
    }

    [Fact]
    public void NoNewSetIsSentWhenNothingChanged()
    {
        var rig = new InterestRig(4);
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 2);
        rig.Settle();
        rig.Settle();
        Assert.Single(rig.CaptureSets());

        // A step within the same focus cell does not resend either.
        rig.Move(InterestRig.Alice, 2, px: 64 * 100);
        rig.Settle();
        Assert.Single(rig.CaptureSets());
    }

    [Fact]
    public void FocusSphereFollowsThePlayerInSteps()
    {
        var rig = new InterestRig(4, new InterestOptions { NearRadiusM = 1000 });
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 2, px: 64 * 100);
        var set = Assert.Single(rig.CaptureSets());
        var focus = set.Focus(0)!.Value;
        Assert.Equal((ushort)2, focus.Sector);
        Assert.Equal(1250f, focus.RadiusM);       // NearRadius plus one quantum (a quarter radius)
        Assert.Equal((byte)20, focus.RateHz);

        rig.Move(InterestRig.Alice, 2, px: 64 * 400);   // far enough to land in another cell
        rig.Settle();
        Assert.Equal(2, rig.CaptureSets().Count);
        Assert.NotEqual(0f, rig.CaptureSets()[1].Focus(0)!.Value.Center!.Value.X);
    }

    [Fact]
    public void AdminMapViewsAreAddedAtTheAdjacentRateAndRemovedWhenEmptied()
    {
        var rig = new InterestRig(6);
        rig.Manager.SetAdminView("map-1", [5, 6]);
        rig.Tick();                               // no client at all: the admin view alone asks for sectors
        var set = Assert.Single(rig.CaptureSets());
        Assert.Equal(new Dictionary<ushort, int> { [5] = 1, [6] = 1 }, InterestRig.Rates(set));
        Assert.Equal(0, set.FocusLength);
        Assert.Equal(1, rig.Manager.AdminViewCount);

        rig.Manager.SetAdminView("map-1", []);
        Assert.Equal(0, rig.Manager.AdminViewCount);
    }

    [Fact]
    public void AnUnneededSectorStaysCapturedFor60SecondsThenIsDroppedAndEvictedFromTheMirror()
    {
        var rig = new InterestRig(6, new InterestOptions { PrefetchDepth = 0, LingerSeconds = 0 });
        rig.Ships(2, 201, 3);
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 2);
        rig.Complete(2);
        rig.Settle();

        rig.Move(InterestRig.Alice, 3);           // 2 is no longer needed by anybody
        rig.Settle();
        Assert.Equal(new Dictionary<ushort, int> { [2] = 1, [3] = 5 }, InterestRig.Rates(rig.CaptureSets()[^1]));   // kept warm
        Assert.True(rig.Mirror.Contains(201));
        Assert.True(rig.Manager.GetCaptureState(2)!.Complete);

        rig.Advance(TimeSpan.FromSeconds(58));
        Assert.True(rig.Mirror.Contains(201));
        rig.Advance(TimeSpan.FromSeconds(3));     // 61 s after it was last needed
        Assert.Equal(new Dictionary<ushort, int> { [3] = 5 }, InterestRig.Rates(rig.CaptureSets()[^1]));
        Assert.False(rig.Mirror.Contains(201));
        Assert.Null(rig.Manager.GetCaptureState(2));

        // Coming back asks again and starts incomplete: the new epoch must be completed before anything is forwarded.
        rig.ClearSent();
        rig.Ships(2, 301, 2);
        rig.Move(InterestRig.Alice, 2);
        rig.Settle();
        Assert.Empty(rig.Spawned(InterestRig.Alice));
        rig.Complete(2);
        Assert.Equal(Ids(2, 301, 2), rig.Spawned(InterestRig.Alice));
    }

    [Fact]
    public void NoCaptureSetIsSentWhileNoAuthorityIsReady()
    {
        var rig = new InterestRig(4);
        rig.Transport.AuthorityReady = false;
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 2);
        rig.Settle();
        Assert.Empty(rig.CaptureSets());

        rig.Transport.AuthorityReady = true;
        rig.Settle();
        Assert.Single(rig.CaptureSets());
    }

    // ---------------------------------------------------------------- ghost budget

    [Fact]
    public void PolicyDropsSmallsFromAdjacentFirstThenUnpredictedThenAllPrefetchThenCapsTheCurrentSector()
    {
        SectorDemand Current(int small, int large) => new(3, InterestTier.Sector, new SectorCounts(small, large), false);
        SectorDemand Adjacent(ushort s, int small, int large, bool predicted = false) => new(s, InterestTier.Adjacent, new SectorCounts(small, large), predicted);
        SectorDemand[] demands = [Current(20, 5), Adjacent(2, 40, 4, predicted: true), Adjacent(4, 40, 4), new(1, InterestTier.Linger, new SectorCounts(30, 2), false)];
        // Totals: full 25 + 44 + 44 + 32 = 145; smalls dropped from prefetch 25 + 4 + 4 + 2 = 35;
        // only predicted adjacent + large linger: 25 + 4 + 0 + 2 = 31; no prefetch: 25.

        Assert.Equal(BudgetLevel.Full, GhostBudgetPolicy.Choose(demands, 145));
        Assert.Equal(BudgetLevel.SmallsDroppedFromPrefetch, GhostBudgetPolicy.Choose(demands, 144));
        Assert.Equal(BudgetLevel.SmallsDroppedFromPrefetch, GhostBudgetPolicy.Choose(demands, 35));
        Assert.Equal(BudgetLevel.PrefetchOnlyPredicted, GhostBudgetPolicy.Choose(demands, 34));
        Assert.Equal(BudgetLevel.PrefetchOnlyPredicted, GhostBudgetPolicy.Choose(demands, 31));
        Assert.Equal(BudgetLevel.NoPrefetch, GhostBudgetPolicy.Choose(demands, 30));
        Assert.Equal(BudgetLevel.NoPrefetch, GhostBudgetPolicy.Choose(demands, 25));
        Assert.Equal(BudgetLevel.CurrentSectorCapped, GhostBudgetPolicy.Choose(demands, 24));

        Assert.Equal(SectorAdmission.LargeOnly, GhostBudgetPolicy.Admission(BudgetLevel.SmallsDroppedFromPrefetch, demands[2]));
        Assert.Equal(SectorAdmission.None, GhostBudgetPolicy.Admission(BudgetLevel.PrefetchOnlyPredicted, demands[2]));
        Assert.Equal(SectorAdmission.LargeOnly, GhostBudgetPolicy.Admission(BudgetLevel.PrefetchOnlyPredicted, demands[1]));
        Assert.Equal(SectorAdmission.All, GhostBudgetPolicy.Admission(BudgetLevel.CurrentSectorCapped, demands[0]));
    }

    [Fact]
    public void PolicyHysteresisKeepsACutUntilTheTotalFitsWithMargin()
    {
        SectorDemand[] demands = [new(3, InterestTier.Sector, new SectorCounts(10, 0), false), new(4, InterestTier.Adjacent, new SectorCounts(88, 2), false)];
        // Full = 100, smalls dropped = 12. Budget 100: fits exactly, but a client already cut stays cut until <= 90.
        Assert.Equal(BudgetLevel.Full, GhostBudgetPolicy.Choose(demands, 100));
        Assert.Equal(BudgetLevel.SmallsDroppedFromPrefetch, GhostBudgetPolicy.Choose(demands, 100, BudgetLevel.SmallsDroppedFromPrefetch));
        Assert.Equal(BudgetLevel.Full, GhostBudgetPolicy.Choose(demands, 112, BudgetLevel.SmallsDroppedFromPrefetch));
    }

    [Fact]
    public void OverBudgetTheSmallShipsOfAdjacentSectorsAreDroppedBeforeAnythingElse()
    {
        // 30 + 30 + 30 ships; budget 90 fits everything at first.
        var rig = new InterestRig(5, new InterestOptions { MaxGhosts = 90 });
        rig.Mixed(2, 200, small: 20, large: 10);
        rig.Mixed(3, 300, small: 20, large: 10);
        rig.Mixed(4, 400, small: 20, large: 10);
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 3);
        rig.CompleteAllCaptured();

        Assert.Equal(BudgetLevel.Full, rig.Manager.LevelOf(InterestRig.Alice));
        Assert.Equal(90, rig.Manager.GhostCount(InterestRig.Alice));
        rig.ClearSent();

        // The admin lowers the budget: the sector itself (30) plus the L ships of the neighbours (20) = 50 fits in 60.
        rig.Options.MaxGhosts = 60;
        rig.Tick();

        Assert.Equal(BudgetLevel.SmallsDroppedFromPrefetch, rig.Manager.LevelOf(InterestRig.Alice));
        var dropped = rig.Despawned(InterestRig.Alice, DespawnReason.OutOfInterest);
        Assert.Equal(Ids(2, 200, 20).Concat(Ids(4, 400, 20)).Order(), dropped.Order());      // the XS/S ships of 2 and 4, exactly
        Assert.Equal(50, rig.Manager.GhostCount(InterestRig.Alice));
        Assert.True(Ids(3, 300, 30).All(id => rig.Manager.IsHeld(InterestRig.Alice, id)));    // the current sector is untouched
        Assert.True(Ids(2, 220, 10).All(id => rig.Manager.IsHeld(InterestRig.Alice, id)));    // L ships of the neighbours stay
        Assert.Equal(InterestTier.Adjacent, rig.Manager.GetSectorTier(InterestRig.Alice, 2)); // still reported as Adjacent

        // Lower still: the large neighbour ships go next (no prefetch), then the sector is capped.
        rig.ClearSent();
        rig.Options.MaxGhosts = 30;
        rig.Tick();
        Assert.Equal(BudgetLevel.PrefetchOnlyPredicted, rig.Manager.LevelOf(InterestRig.Alice));
        Assert.Equal(Ids(2, 220, 10).Concat(Ids(4, 420, 10)).Order(), rig.Despawned(InterestRig.Alice).Order());
        Assert.Equal(InterestTier.None, rig.Manager.GetSectorTier(InterestRig.Alice, 2));

        rig.ClearSent();
        rig.Options.MaxGhosts = 12;
        rig.Tick();
        Assert.Equal(BudgetLevel.CurrentSectorCapped, rig.Manager.LevelOf(InterestRig.Alice));
        Assert.Equal(12, rig.Manager.GhostCount(InterestRig.Alice));
        Assert.Equal(18, rig.Despawned(InterestRig.Alice).Count);
    }

    [Fact]
    public void RaisingTheBudgetBringsBackWhatWasCut()
    {
        var rig = new InterestRig(4, new InterestOptions { MaxGhosts = 20 });
        rig.Mixed(2, 200, small: 10, large: 2);
        rig.Mixed(3, 300, small: 10, large: 2);
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 2);
        rig.CompleteAllCaptured();
        Assert.Equal(BudgetLevel.SmallsDroppedFromPrefetch, rig.Manager.LevelOf(InterestRig.Alice));   // 12 + 12 = 24 > 20
        Assert.False(rig.Manager.IsHeld(InterestRig.Alice, 301));
        Assert.True(rig.Manager.IsHeld(InterestRig.Alice, 311));

        rig.ClearSent();
        rig.Options.MaxGhosts = 100;
        rig.Tick();

        Assert.Equal(BudgetLevel.Full, rig.Manager.LevelOf(InterestRig.Alice));
        Assert.True(Ids(3, 300, 10).All(id => rig.Manager.IsHeld(InterestRig.Alice, id)));
        Assert.Equal(Ids(3, 300, 10).Order(), rig.Spawned(InterestRig.Alice).Order());
        Assert.Empty(rig.Completes(InterestRig.Alice));    // the sector was complete already: no second marker
    }

    [Fact]
    public void ABigCurrentSectorIsCappedNearestFirstAndPlayerShipsStayOutsideTheBudget()
    {
        var rig = new InterestRig(3, new InterestOptions { MaxGhosts = 10, PrefetchDepth = 0 });
        // 25 ships at growing distance; the budget keeps the 10 nearest.
        rig.Mirror.Spawn([.. Enumerable.Range(0, 25).Select(i => Rec(500 + (uint)i, EntityKind.ShipS, 2, px: (24 - i) * 6400))]);
        rig.Mirror.Spawn(Rec(9000, EntityKind.ShipM, 2, 99_999_999, controller: 2, origin: EntityOrigin.PlayerShip));
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 2, px: 0);
        rig.Complete(2);

        Assert.Equal(BudgetLevel.CurrentSectorCapped, rig.Manager.LevelOf(InterestRig.Alice));
        Assert.Equal(10, rig.Manager.GhostCount(InterestRig.Alice));
        var held = rig.Manager.HeldBy(InterestRig.Alice).Where(id => id != 9000).Order().ToArray();
        Assert.Equal([.. Enumerable.Range(515, 10).Select(i => (uint)i)], held);          // the ten with the smallest px
        Assert.True(rig.Manager.IsHeld(InterestRig.Alice, 9000));

        // A new arrival beyond the cap is not admitted; a player ship always is.
        rig.Mirror.Spawn(Rec(600, EntityKind.ShipS, 2, 0));
        Assert.False(rig.Manager.IsHeld(InterestRig.Alice, 600));
        rig.Mirror.Spawn(Rec(9001, EntityKind.ShipM, 2, 0, controller: 3, origin: EntityOrigin.PlayerShip));
        Assert.True(rig.Manager.IsHeld(InterestRig.Alice, 9001));
        Assert.Equal(10, rig.Manager.GhostCount(InterestRig.Alice));
    }

    [Fact]
    public void MaxGhostsDefaultsTo250PerAdr042()
    {
        Assert.Equal(250, new InterestOptions().MaxGhosts);
        Assert.Equal(1, new InterestOptions().PrefetchDepth);
        Assert.Equal(20, new InterestOptions().LingerSeconds);
        Assert.Equal(60, new InterestOptions().CaptureEvictSeconds);
        Assert.Equal(500, new InterestOptions().CaptureSetMinIntervalMs);
    }

    // ---------------------------------------------------------------- hints

    private static readonly ulong HintCap = (ulong)Capability.InterestHint;

    private static InterestHint Hint(ushort target, uint etaMs = 5000)
    {
        var payload = X4MP.Protocol.MessageEncoder.EncodePayload(
            b => X4MP.Proto.InterestHint.Pack(b, new InterestHintT { TargetSector = target, EtaMs = etaMs, Reason = HintReason.GateApproach }), 32);
        return Decode<InterestHint>(MsgType.InterestHint, payload);
    }

    [Fact]
    public void AHintFromACapableClientPrefetchesANonAdjacentSectorUntilItExpires()
    {
        var rig = new InterestRig(8);
        rig.Activate(InterestRig.Alice, HintCap);
        rig.Move(InterestRig.Alice, 2);          // 1,2,3

        rig.Manager.HandleHint(InterestRig.Alice, Hint(6, etaMs: 4000));
        Assert.Equal(InterestTier.Adjacent, rig.Manager.GetSectorTier(InterestRig.Alice, 6));
        Assert.Equal(1, rig.Manager.Stats.HintsAccepted);
        Assert.Equal(InterestTier.Adjacent, rig.Updates(InterestRig.Alice)[^1].Tiers[6]);

        rig.Advance(TimeSpan.FromSeconds(13));   // eta 4 s + 10 s grace = 14 s
        Assert.Equal(InterestTier.Adjacent, rig.Manager.GetSectorTier(InterestRig.Alice, 6));
        rig.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(InterestTier.None, rig.Manager.GetSectorTier(InterestRig.Alice, 6));
    }

    [Fact]
    public void HintsAreIgnoredWithoutTheCapabilityForUnknownSectorsAndWhenDisabled()
    {
        var rig = new InterestRig(8);
        rig.Activate(InterestRig.Alice);                       // did not negotiate the capability
        rig.Move(InterestRig.Alice, 2);
        rig.Activate(InterestRig.Bob, HintCap);
        rig.Move(InterestRig.Bob, 2);

        rig.Manager.HandleHint(InterestRig.Alice, Hint(6));
        Assert.Equal(InterestTier.None, rig.Manager.GetSectorTier(InterestRig.Alice, 6));
        rig.Manager.HandleHint(InterestRig.Bob, Hint(999));    // not a sector
        rig.Manager.HandleHint(InterestRig.Bob, Hint(2));      // the sector it is in
        rig.Manager.HandleHint(99, Hint(6));                   // not a client
        Assert.Equal(InterestTier.None, rig.Manager.GetSectorTier(InterestRig.Bob, 6));

        rig.Options.HonorInterestHints = false;
        rig.Manager.HandleHint(InterestRig.Bob, Hint(6));
        Assert.Equal(InterestTier.None, rig.Manager.GetSectorTier(InterestRig.Bob, 6));
        Assert.Equal(5, rig.Manager.Stats.HintsIgnored);
        Assert.Equal(0, rig.Manager.Stats.HintsAccepted);

        rig.Options.HonorInterestHints = true;
        rig.Manager.HandleHint(InterestRig.Bob, Hint(6));
        Assert.Equal(InterestTier.Adjacent, rig.Manager.GetSectorTier(InterestRig.Bob, 6));
    }

    [Fact]
    public void AHintedSectorCountsAsThePredictedRouteWhenTheBudgetIsTight()
    {
        var rig = new InterestRig(8, new InterestOptions { MaxGhosts = 45 });
        rig.Mixed(2, 200, small: 10, large: 2);
        rig.Mixed(3, 300, small: 10, large: 2);
        rig.Mixed(1, 100, small: 10, large: 2);
        rig.Mixed(6, 600, small: 10, large: 2);
        rig.Activate(InterestRig.Alice, HintCap);
        rig.Move(InterestRig.Alice, 2);
        rig.Manager.HandleHint(InterestRig.Alice, Hint(6));
        rig.Settle();
        rig.CompleteAllCaptured();

        // Full = 48 > 45: smalls are dropped from the prefetch; 12 + 2 + 2 + 2 = 18 fits. Nothing else is cut.
        Assert.Equal(BudgetLevel.SmallsDroppedFromPrefetch, rig.Manager.LevelOf(InterestRig.Alice));
        Assert.True(Ids(6, 610, 2).All(id => rig.Manager.IsHeld(InterestRig.Alice, id)));
        Assert.False(rig.Manager.IsHeld(InterestRig.Alice, 600));

        // Tighter: the unpredicted neighbours (1 and 3) go, the hinted sector 6 keeps its L ships.
        rig.Options.MaxGhosts = 14;
        rig.Tick();
        Assert.Equal(BudgetLevel.PrefetchOnlyPredicted, rig.Manager.LevelOf(InterestRig.Alice));
        Assert.Equal(InterestTier.None, rig.Manager.GetSectorTier(InterestRig.Alice, 1));
        Assert.Equal(InterestTier.None, rig.Manager.GetSectorTier(InterestRig.Alice, 3));
        Assert.Equal(InterestTier.Adjacent, rig.Manager.GetSectorTier(InterestRig.Alice, 6));
    }

    // ---------------------------------------------------------------- near grid

    [Fact]
    public void NearTierIsEntityLevelAndServedByTheGridInTheCurrentSectorOnly()
    {
        var rig = new InterestRig(3, new InterestOptions { NearRadiusM = 15_000 });
        int m = 64;     // wire units per metre
        rig.Mirror.Spawn(
            Rec(1, EntityKind.ShipS, 2, 1000 * m),
            Rec(2, EntityKind.ShipS, 2, 14_900 * m),
            Rec(3, EntityKind.ShipS, 2, 15_100 * m),
            Rec(4, EntityKind.ShipS, 2, 0, 16_000 * m, 0),
            Rec(5, EntityKind.ShipS, 3, 100 * m));
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 2, 0, 0, 0);
        rig.CompleteAllCaptured();

        Assert.Equal(1, rig.Manager.ActiveGridSectors);
        var near = new List<MirrorEntity>();
        Assert.True(rig.Manager.QueryNear(InterestRig.Alice, near));
        Assert.Equal([1u, 2], near.Select(e => e.NetId).Order().ToArray());

        Assert.Equal(InterestTier.Near, rig.Manager.TierOf(InterestRig.Alice, rig.Mirror.TryGet(1, out var e1) ? e1 : null!));
        Assert.Equal(InterestTier.Sector, rig.Manager.TierOf(InterestRig.Alice, rig.Mirror.TryGet(3, out var e3) ? e3 : null!));
        Assert.Equal(InterestTier.Adjacent, rig.Manager.TierOf(InterestRig.Alice, rig.Mirror.TryGet(5, out var e5) ? e5 : null!));

        // The grid follows a ship that flies in: update through the mirror and the query sees it.
        rig.Mirror.Update(State(3, 2, 2000 * m));
        near.Clear();
        rig.Manager.QueryNear(InterestRig.Alice, near);
        Assert.Equal([1u, 2, 3], near.Select(e => e.NetId).Order().ToArray());

        // And one that flies out or leaves the sector.
        rig.Mirror.Update(State(1, 3, 0));
        rig.Mirror.Despawn(DespawnReason.Removed, 2);
        near.Clear();
        rig.Manager.QueryNear(InterestRig.Alice, near);
        Assert.Equal([3u], near.Select(e => e.NetId));
    }

    [Fact]
    public void GridsExistOnlyForSectorsWithAPlayerAndMoveWithHim()
    {
        var rig = new InterestRig(4);
        rig.Ships(2, 20, 3);
        rig.Ships(3, 30, 3);
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 2);
        Assert.Equal(1, rig.Manager.ActiveGridSectors);

        rig.Activate(InterestRig.Bob);
        rig.Move(InterestRig.Bob, 3);
        Assert.Equal(2, rig.Manager.ActiveGridSectors);

        rig.Move(InterestRig.Bob, 2);
        Assert.Equal(1, rig.Manager.ActiveGridSectors);

        rig.Manager.ClientDeactivated(InterestRig.Alice);
        rig.Manager.ClientDeactivated(InterestRig.Bob);
        Assert.Equal(0, rig.Manager.ActiveGridSectors);
    }

    [Fact]
    public void NearGridCellsQueryWithinTheRadiusAcrossCellBoundaries()
    {
        var grid = new NearGrid(100);
        var mirror = new WorldMirror();
        mirror.Spawn(
            Rec(1, EntityKind.ShipS, 1, -50 * 64),
            Rec(2, EntityKind.ShipS, 1, 99 * 64),
            Rec(3, EntityKind.ShipS, 1, 101 * 64),
            Rec(4, EntityKind.ShipS, 1, 260 * 64),
            Rec(5, EntityKind.ShipS, 1, -250 * 64, -250 * 64, -250 * 64));
        grid.Activate(1, mirror.TransientIn(1));

        var hits = new List<MirrorEntity>();
        Assert.True(grid.Query(1, 0, 0, 0, 100, hits));
        Assert.Equal([1u, 2], hits.Select(e => e.NetId).Order().ToArray());
        hits.Clear();
        grid.Query(1, 0, 0, 0, 300, hits);
        Assert.Equal([1u, 2, 3, 4], hits.Select(e => e.NetId).Order().ToArray());
        Assert.False(grid.Query(9, 0, 0, 0, 100, hits));
        Assert.True(grid.CellCount >= 4);

        grid.Deactivate(1);
        Assert.Equal(0, grid.CellCount);
    }

    // ---------------------------------------------------------------- visibility hook (ADR-038)

    private sealed class HideTeam(ushort team) : IVisibilityFilter
    {
        public bool IsVisible(int viewerPlayerId, MirrorEntity entity) => entity.OwnerTeam != team;
    }

    [Fact]
    public void TheVisibilityFilterHookIsANoOpByDefaultAndCanHideEntitiesPerViewer()
    {
        var rig = new InterestRig(4);
        Assert.Same(AllVisible.Instance, rig.Manager.VisibilityFilter);
        rig.Mirror.Spawn(Rec(1, EntityKind.ShipS, 2), Rec(2, EntityKind.ShipS, 2, ownerTeam: 3));
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 2);
        rig.Complete(2);
        Assert.Equal([1u, 2], rig.Spawned(InterestRig.Alice).Order().ToArray());     // everything visible in v1

        // Fog of war later: hide team 3 and the held ghost disappears, a later spawn is not shown.
        rig.ClearSent();
        rig.Manager.VisibilityFilter = new HideTeam(3);
        Assert.Equal([2u], rig.Despawned(InterestRig.Alice));
        rig.Mirror.Spawn(Rec(3, EntityKind.ShipS, 2, ownerTeam: 3), Rec(4, EntityKind.ShipS, 2, ownerTeam: 1));
        Assert.Equal([4u], rig.Spawned(InterestRig.Alice));
    }

    // ---------------------------------------------------------------- control lane pressure

    [Fact]
    public void SpawnBatchesPauseOverTheSoftCapAndSpreadOverTicks()
    {
        var rig = new InterestRig(3, new InterestOptions { SpawnBatchEntities = 10, SpawnFramesPerTick = 2, MaxGhosts = 1000, PrefetchDepth = 0 });
        rig.Ships(2, 1000, 95);
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 2);
        rig.Complete(2);

        // Two frames of 10 now, the rest waits for later ticks; no marker until everything is out.
        Assert.Equal(20, rig.Spawned(InterestRig.Alice).Count);
        Assert.Empty(rig.Completes(InterestRig.Alice));
        Assert.Equal(SectorDelivery.Delivering, rig.Manager.GetDelivery(InterestRig.Alice, 2));

        rig.Transport.OverSoftCap.Add(InterestRig.Alice);
        rig.Tick();
        Assert.Equal(20, rig.Spawned(InterestRig.Alice).Count);            // paused while the control lane is backed up

        rig.Transport.OverSoftCap.Clear();
        for (int i = 0; i < 4; i++)
        {
            rig.Tick();
        }

        Assert.Equal(Ids(2, 1000, 95).Order(), rig.Spawned(InterestRig.Alice).Order());
        Assert.Equal(((ushort)2, 95u), rig.Completes(InterestRig.Alice).Single());
        Assert.Equal(SectorDelivery.Delivered, rig.Manager.GetDelivery(InterestRig.Alice, 2));
    }

    [Fact]
    public void AnEntityRemovedWhileItsSectorIsStillBeingDeliveredIsNeverSpawnedAndOnesAlreadySentAreDespawned()
    {
        var rig = new InterestRig(3, new InterestOptions { SpawnBatchEntities = 5, SpawnFramesPerTick = 1, PrefetchDepth = 0 });
        rig.Ships(2, 10, 12);
        rig.Activate(InterestRig.Alice);
        rig.Move(InterestRig.Alice, 2);
        rig.Complete(2);
        Assert.Equal(5, rig.Spawned(InterestRig.Alice).Count);

        var sent = rig.Spawned(InterestRig.Alice);
        rig.Mirror.Despawn(DespawnReason.Destroyed, sent[0], 22);            // already sent: the client hears it
        rig.Mirror.Despawn(DespawnReason.Destroyed, 21);                      // still queued: nobody ever hears of it
        for (int i = 0; i < 4; i++)
        {
            rig.Tick();
        }

        Assert.Equal([sent[0]], rig.Despawned(InterestRig.Alice, DespawnReason.Destroyed));
        Assert.DoesNotContain(21u, rig.Spawned(InterestRig.Alice));
        Assert.Equal(10, rig.Manager.GhostCount(InterestRig.Alice));
        Assert.Equal(((ushort)2, 11u), rig.Completes(InterestRig.Alice).Single());   // spawns sent, including the one destroyed afterwards
    }
}
