using X4MP.Core.Permissions;
using X4MP.Core.Relay;
using X4MP.Core.Teams;
using X4MP.Core.Tests.Relay;
using X4MP.Core.Tests.Session;
using X4MP.Core.Tests.World;
using X4MP.Proto;
using TeamRelation = X4MP.Core.Teams.TeamRelation;

namespace X4MP.Core.Tests.Permissions;

/// <summary>The permission gate on the real actor: what the relay forwards to the authority, what it rejects, and what the mirror says about owners.</summary>
public class AssetPermissionRelayTests
{
    private sealed class Scene : IAsyncDisposable
    {
        private readonly RelayRig _rig;

        public Scene(TeamOptions? teams = null, RelayOptions? relay = null)
        {
            Teams = teams ?? new TeamOptions();
            _rig = new RelayRig(relay, teams: true, teamOptions: Teams);
        }

        public TeamOptions Teams { get; }

        public RelayRig Rig => _rig;

        /// <summary>Entities an open trade holds (M1-E5); the gate rejects commands on them.</summary>
        public HashSet<uint> Locked { get; } = [];

        public JoinedNode Boss { get; private set; } = null!;

        public JoinedNode Alice { get; private set; } = null!;

        public JoinedNode Bob { get; private set; } = null!;

        public async Task StartAsync(int? leader = null)
        {
            Rig.Relay.AssetPermissions = new AssetPermissionGate(
                Rig.Mirror, Rig.Teams!, () => Teams, () => Rig.Options.ClaimRangeMetres, _ => leader ?? Boss?.PlayerId, Locked.Contains);
            Boss = await Rig.JoinAuthorityAsync();
            Alice = await Rig.JoinInGameAsync("Alice");
            Bob = await Rig.JoinInGameAsync("Bob");
        }

        public int Team => Rig.Teams!.TeamOf(Alice.PlayerId)!.Value;

        public static List<IntentResultT> Results(JoinedNode node) =>
            [.. node.Connection.SentOf(MsgType.IntentResult).Select(f => f.Decode<IntentResult>().UnPack())];

        public List<IntentT> Forwarded() =>
            [.. Boss.Connection.SentOf(MsgType.Intent).Select(f => f.Decode<Intent>().UnPack())];

        public Task Send(JoinedNode node, ulong key, IntentBodyUnion body) =>
            Rig.SendAsync(node, MsgType.Intent, RelayFrames.Intent(key, (uint)key, body));

        public void Spawn(params EntityRecordT[] records) => Rig.Mirror.Spawn(records);

        public async Task Authority(MsgType type, byte[] payload) => await Rig.SendPayloadAsync(Boss, type, payload);

        public ValueTask DisposeAsync() => Rig.DisposeAsync();
    }

    private static IntentBodyUnion Order(uint asset, OrderKind kind = OrderKind.MoveTo, uint target = 0) =>
        IntentBodyUnion.FromAssetOrder(new AssetOrderT { Asset = asset, Order = kind, Target = target });

    [Fact]
    public async Task AnAssetInAnOpenTradeTakesNoOrdersRenamesGiftsOrStationTradesUntilItIsFree()
    {
        await using var scene = new Scene();
        await scene.StartAsync();
        scene.Spawn(WorldKit.Rec(100, EntityKind.ShipM, 1, ownerTeam: 1), WorldKit.Rec(310, EntityKind.Station, 1, ownerTeam: 0));
        scene.Locked.Add(100);

        await scene.Send(scene.Alice, 1, Order(100));
        await scene.Send(scene.Alice, 2, IntentBodyUnion.FromAssetRename(new AssetRenameT { Asset = 100, Name = "x" }));
        await scene.Send(scene.Alice, 3, IntentBodyUnion.FromAssetGift(new AssetGiftT { Asset = 100, ToTeam = 1 }));
        await scene.Send(scene.Alice, 4, IntentBodyUnion.FromTradeReport(new TradeReportT { Ship = 100, Station = 310, Amount = 10 }));

        Assert.Empty(scene.Forwarded());
        Assert.All(Scene.Results(scene.Alice), r => Assert.Equal(RejectReason.Conflict, r.Reason));
        Assert.Equal(4, Scene.Results(scene.Alice).Count);
        Assert.Equal(4, scene.Rig.Relay.Stats.IntentsPermissionDenied);

        scene.Locked.Clear(); // the trade ended
        await scene.Send(scene.Alice, 5, Order(100));

        Assert.Single(scene.Forwarded());
        Assert.Equal(4, Scene.Results(scene.Alice).Count);
    }

    [Fact]
    public async Task AForeignTeamsAssetIsNeverForwardedAndTheSenderGetsOneRejection()
    {
        await using var scene = new Scene();
        await scene.StartAsync();
        scene.Spawn(WorldKit.Rec(100, EntityKind.ShipM, 1, ownerTeam: 2));

        await scene.Send(scene.Alice, 1, Order(100));

        Assert.Empty(scene.Forwarded());
        var result = Assert.Single(Scene.Results(scene.Alice));
        Assert.Equal(IntentStatus.Rejected, result.Status);
        Assert.Equal(RejectReason.NotYourAsset, result.Reason);
        Assert.Equal(1u, result.RequestId);
        Assert.Equal(0, scene.Rig.Relay.PendingIntentCount);
        Assert.Equal(1, scene.Rig.Relay.Stats.IntentsPermissionDenied);
        Assert.Equal(0, scene.Rig.Relay.Stats.IntentsForwarded);
        var evt = Assert.Single(scene.Rig.Events.OfType<PermissionDenied>());
        Assert.Equal((long)scene.Alice.PlayerId, evt.PlayerId);
        Assert.Equal(100, evt.EntityId);
        Assert.Equal("Order", evt.Action);
        Assert.Equal("NotYourAsset", evt.Reason);
    }

    [Fact]
    public async Task TeamAssetsAreForwardedUnderSharedCommand()
    {
        await using var scene = new Scene();
        await scene.StartAsync();
        scene.Spawn(
            WorldKit.Rec(100, EntityKind.ShipM, 1, ownerTeam: (ushort)1, ownerPlayer: 0),
            WorldKit.Rec(101, EntityKind.ShipM, 1, ownerTeam: 1, ownerPlayer: (ushort)scene.Bob.PlayerId));

        await scene.Send(scene.Alice, 1, Order(100));
        await scene.Send(scene.Alice, 2, Order(101));
        await scene.Send(scene.Alice, 3, IntentBodyUnion.FromAssetRename(new AssetRenameT { Asset = 101, Name = "Mine now" }));

        Assert.Equal(3, scene.Forwarded().Count);
        Assert.Empty(Scene.Results(scene.Alice));
        Assert.Empty(scene.Rig.Events.OfType<PermissionDenied>());
    }

    [Fact]
    public async Task OwnerOnlyBlocksATeammatesShipButNotTheLeaderUnderOwnerAndLeader()
    {
        var teams = new TeamOptions { AssetPolicy = TeamAssetPolicy.OwnerOnly };
        await using var scene = new Scene(teams);
        await scene.StartAsync(); // Boss leads
        scene.Spawn(WorldKit.Rec(101, EntityKind.ShipM, 1, ownerTeam: 1, ownerPlayer: (ushort)scene.Bob.PlayerId));

        await scene.Send(scene.Alice, 1, Order(101));
        await scene.Send(scene.Boss, 2, Order(101));    // the leader, but OwnerOnly
        await scene.Send(scene.Bob, 3, Order(101));     // the owner

        Assert.Equal(RejectReason.NotYourAsset, Assert.Single(Scene.Results(scene.Alice)).Reason);
        Assert.Equal(RejectReason.NotYourAsset, Assert.Single(Scene.Results(scene.Boss)).Reason);
        Assert.Empty(Scene.Results(scene.Bob));
        Assert.Equal([(uint)scene.Bob.PlayerId], scene.Forwarded().Select(i => (uint)i.PlayerId));

        teams.AssetPolicy = TeamAssetPolicy.OwnerAndLeader; // hot setting
        await scene.Send(scene.Boss, 4, Order(101));
        await scene.Send(scene.Alice, 5, Order(101));

        Assert.Equal([(uint)scene.Bob.PlayerId, (uint)scene.Boss.PlayerId], scene.Forwarded().Select(i => (uint)i.PlayerId));
        Assert.Equal(2, Scene.Results(scene.Alice).Count);
    }

    [Fact]
    public async Task OwnershipFollowsEntityChangeAfterAGiftOrACapture()
    {
        await using var scene = new Scene();
        await scene.StartAsync();
        scene.Spawn(
            WorldKit.Rec(100, EntityKind.ShipM, 1, ownerTeam: 1),
            WorldKit.Rec(200, EntityKind.ShipM, 1, ownerTeam: 2));

        // gifted away: the authority says the owner is now team 2 (and the mirror shows it)
        await scene.Authority(MsgType.EntityChange, WorldKit.ChangePayload(100, ChangeField.OwnerTeam | ChangeField.OwnerPlayer, ownerTeam: 2, ownerPlayer: 0));
        // captured by us: now team 1, owned by Alice
        await scene.Authority(
            MsgType.EntityChange,
            WorldKit.ChangePayload(200, ChangeField.OwnerTeam | ChangeField.OwnerPlayer, ownerTeam: 1, ownerPlayer: (ushort)scene.Alice.PlayerId));

        Assert.True(scene.Rig.Mirror.TryGet(100, out var gifted));
        Assert.Equal((2, 0), (gifted.OwnerTeam, gifted.OwnerPlayer));
        Assert.True(scene.Rig.Mirror.TryGet(200, out var captured));
        Assert.Equal((1, scene.Alice.PlayerId), (captured.OwnerTeam, (int)captured.OwnerPlayer));

        await scene.Send(scene.Alice, 1, Order(100));
        await scene.Send(scene.Alice, 2, Order(200));

        Assert.Equal(RejectReason.NotYourAsset, Assert.Single(Scene.Results(scene.Alice)).Reason);
        Assert.Equal(200u, Assert.Single(scene.Forwarded()).Body.AsAssetOrder().Asset);
    }

    [Fact]
    public async Task UnknownEntitiesAreRejectedWithoutForwarding()
    {
        await using var scene = new Scene();
        await scene.StartAsync();

        await scene.Send(scene.Alice, 1, Order(999));

        Assert.Equal(RejectReason.UnknownEntity, Assert.Single(Scene.Results(scene.Alice)).Reason);
        Assert.Empty(scene.Forwarded());
    }

    [Fact]
    public async Task AttackOrdersAndCapturesFollowTheRelationMatrix()
    {
        var teams = new TeamOptions { DefaultRelation = TeamRelation.Neutral };
        await using var scene = new Scene(teams);
        await scene.StartAsync();
        scene.Spawn(
            WorldKit.Rec(100, EntityKind.ShipM, 1, ownerTeam: 1),
            WorldKit.Rec(300, EntityKind.ShipM, 1, ownerTeam: 2),
            WorldKit.Rec(400, EntityKind.ShipM, 1));                 // NPC

        await scene.Send(scene.Alice, 1, Order(100, OrderKind.Attack, 300));   // neutral team
        await scene.Send(scene.Alice, 2, Order(100, OrderKind.Attack, 400));   // NPC
        await scene.Send(scene.Alice, 3, IntentBodyUnion.FromCaptureReport(new CaptureReportT { Target = 300 }));
        await scene.Send(scene.Alice, 4, IntentBodyUnion.FromCaptureReport(new CaptureReportT { Target = 400 }));

        Assert.Equal([RejectReason.HostileRequired, RejectReason.HostileRequired], Scene.Results(scene.Alice).Select(r => r.Reason));
        Assert.Equal([2u, 4u], scene.Forwarded().Select(i => i.RequestId));

        teams.AllowFriendlyFire = true;
        await scene.Send(scene.Alice, 5, Order(100, OrderKind.Attack, 300));
        Assert.Equal(3, scene.Forwarded().Count); // friendly fire lets the attack through; capture still needs Hostile
    }

    [Fact]
    public async Task KillAndHitClaimsAreRangeCheckedAndHostilityCheckedAgainstTheSendersShip()
    {
        var teams = new TeamOptions();
        await using var scene = new Scene(teams);
        await scene.StartAsync();
        const int metre = 64;
        scene.Spawn(
            WorldKit.Rec(500, EntityKind.ShipM, 1, px: 10_000 * metre),            // NPC 10 km away
            WorldKit.Rec(501, EntityKind.ShipM, 1, px: 40_000 * metre),            // NPC 40 km away
            WorldKit.Rec(502, EntityKind.ShipM, 2, px: 0),                         // NPC in another sector
            WorldKit.Rec(503, EntityKind.ShipM, 1, px: 5_000 * metre, ownerTeam: 1)); // a teammate's ship nearby
        await scene.Rig.SendAsync(scene.Alice, MsgType.PlayerState, RelayFrames.State(1, 1000, px: 0, sector: 1));

        await scene.Send(scene.Alice, 1, IntentBodyUnion.FromKillClaim(new KillClaimT { Target = 500 }));
        await scene.Send(scene.Alice, 2, IntentBodyUnion.FromKillClaim(new KillClaimT { Target = 501 }));
        await scene.Send(scene.Alice, 3, IntentBodyUnion.FromKillClaim(new KillClaimT { Target = 502 }));
        await scene.Send(scene.Alice, 4, IntentBodyUnion.FromKillClaim(new KillClaimT { Target = 503 }));
        await scene.Send(
            scene.Alice,
            5,
            IntentBodyUnion.FromHitReport(new HitReportT { Hits = [new HitEntryT { Target = 500 }, new HitEntryT { Target = 501 }] }));

        Assert.Equal([1u], scene.Forwarded().Select(i => i.RequestId));
        Assert.Equal(
            [RejectReason.NotPermitted, RejectReason.NotPermitted, RejectReason.FriendlyFireDisabled, RejectReason.NotPermitted],
            Scene.Results(scene.Alice).Select(r => r.Reason));

        teams.AllowFriendlyFire = true;
        await scene.Send(scene.Alice, 6, IntentBodyUnion.FromKillClaim(new KillClaimT { Target = 503 }));
        Assert.Equal([1u, 6u], scene.Forwarded().Select(i => i.RequestId));
    }

    [Fact]
    public async Task HostileTeamsMayBeAttackedAndTradingAtTheirStationIsRefused()
    {
        var teams = new TeamOptions { DefaultRelation = TeamRelation.Hostile };
        await using var scene = new Scene(teams);
        await scene.StartAsync();
        scene.Spawn(
            WorldKit.Rec(100, EntityKind.ShipM, 1, ownerTeam: 1),
            WorldKit.Rec(300, EntityKind.ShipM, 1, ownerTeam: 2),
            WorldKit.Rec(310, EntityKind.Station, 1, ownerTeam: 2),
            WorldKit.Rec(410, EntityKind.Station, 1));                // NPC station

        await scene.Send(scene.Alice, 1, Order(100, OrderKind.Attack, 300));
        await scene.Send(scene.Alice, 2, IntentBodyUnion.FromCaptureReport(new CaptureReportT { Target = 300 }));
        await scene.Send(scene.Alice, 3, IntentBodyUnion.FromTradeReport(new TradeReportT { Ship = 100, Station = 310, Amount = 10 }));
        await scene.Send(scene.Alice, 4, IntentBodyUnion.FromTradeReport(new TradeReportT { Ship = 100, Station = 410, Amount = 10 }));

        Assert.Equal([1u, 2u, 4u], scene.Forwarded().Select(i => i.RequestId));
        Assert.Equal(RejectReason.PolicyDenied, Assert.Single(Scene.Results(scene.Alice)).Reason);
    }

    [Fact]
    public async Task GiftsNeedTransfersEnabledAndAnAlliedExistingDestination()
    {
        var teams = new TeamOptions();
        await using var scene = new Scene(teams);
        await scene.StartAsync();
        scene.Spawn(WorldKit.Rec(100, EntityKind.ShipM, 1, ownerTeam: 1));
        int team = scene.Team;

        IntentBodyUnion Gift(ushort toTeam, ushort toPlayer = 0) => IntentBodyUnion.FromAssetGift(new AssetGiftT { Asset = 100, ToTeam = toTeam, ToPlayer = toPlayer });

        await scene.Send(scene.Alice, 1, Gift((ushort)team, (ushort)scene.Bob.PlayerId)); // transfers off
        teams.AllowAssetTransfer = true;
        await scene.Send(scene.Alice, 2, Gift((ushort)team, (ushort)scene.Bob.PlayerId)); // a teammate
        await scene.Send(scene.Alice, 3, Gift(0, (ushort)scene.Bob.PlayerId));             // 0 = my own team
        await scene.Send(scene.Alice, 4, Gift(5));                                          // no such team
        await scene.Send(scene.Alice, 5, Gift((ushort)team, 4000));                         // player not on that team

        Assert.Equal([2u, 3u], scene.Forwarded().Select(i => i.RequestId));
        Assert.Equal([RejectReason.PolicyDenied, RejectReason.InvalidParameters, RejectReason.InvalidParameters], Scene.Results(scene.Alice).Select(r => r.Reason));
    }

    [Fact]
    public async Task StationBuildRequestsAndOtherIntentsFromTeamMembersPass()
    {
        await using var scene = new Scene();
        await scene.StartAsync();

        await scene.Send(scene.Alice, 1, IntentBodyUnion.FromStationBuildRequest(new StationBuildRequestT { Macro = "station_gen_factory_01_macro", Sector = 1 }));
        await scene.Send(scene.Alice, 2, IntentBodyUnion.FromPlayerDeath(new PlayerDeathT { Killer = 5 }));

        Assert.Equal([1u, 2u], scene.Forwarded().Select(i => i.RequestId));
    }

    [Fact]
    public async Task PermissionDeniedEventsAreRateLimitedButEveryRejectionIsAnswered()
    {
        var relay = new RelayOptions { PermissionDeniedEventsPerSecond = 3 };
        await using var scene = new Scene(relay: relay);
        await scene.StartAsync();
        scene.Spawn(WorldKit.Rec(100, EntityKind.ShipM, 1, ownerTeam: 2));

        for (ulong i = 1; i <= 10; i++)
        {
            await scene.Send(scene.Alice, i, Order(100));
        }

        Assert.Equal(10, Scene.Results(scene.Alice).Count);
        Assert.Equal(3, scene.Rig.Events.OfType<PermissionDenied>().Count);
        Assert.Equal(7, scene.Rig.Relay.Stats.PermissionDeniedEventsSuppressed);

        await scene.Rig.AdvanceAsync(1.5);
        await scene.Send(scene.Alice, 11, Order(100));

        Assert.Equal(4, scene.Rig.Events.OfType<PermissionDenied>().Count);
        Assert.Empty(scene.Forwarded());
    }

    [Fact]
    public async Task WithoutAGateNothingIsEnforced()
    {
        await using var rig = new RelayRig(teams: true);
        var boss = await rig.JoinAuthorityAsync();
        var alice = await rig.JoinInGameAsync("Alice");
        rig.Mirror.Spawn(WorldKit.Rec(100, EntityKind.ShipM, 1, ownerTeam: 2));

        await rig.SendAsync(alice, MsgType.Intent, RelayFrames.Intent(1, 1, Order(100)));

        Assert.Single(boss.Connection.SentOf(MsgType.Intent));
    }
}
