using X4MP.Core.Permissions;
using X4MP.Proto;
using TeamRelation = X4MP.Core.Teams.TeamRelation;

namespace X4MP.Core.Tests.Permissions;

/// <summary>
/// The pure policy, table-driven: every asset policy x relation x action cell with literal expected answers
/// (server-design 2.13, protocol.md 16.2). The sender is player 7 on team 1.
/// </summary>
public class AssetPermissionPolicyTests
{
    public enum Who
    {
        Self,      // team 1, player 7
        Common,    // team 1, player 0 (team-common)
        Teammate,  // team 1, player 8
        Allied,    // team 2, Allied
        Neutral,   // team 2, Neutral
        Hostile,   // team 2, Hostile
        Npc,       // team 0
    }

    private const int Me = 7;
    private static readonly PermissionActor Member = new(Me, 1, false);
    private static readonly PermissionActor Leader = new(Me, 1, true);

    private static AssetPermissionSettings Settings(
        TeamAssetPolicy policy = TeamAssetPolicy.SharedCommand, bool friendlyFire = false, bool transfer = false) =>
        new(policy, friendlyFire, transfer, 30_000);

    private static PermissionSubject Subject(Who who) => who switch
    {
        Who.Self => new(1, Me),
        Who.Common => new(1, 0),
        Who.Teammate => new(1, 8),
        Who.Allied or Who.Neutral or Who.Hostile => new(2, 0),
        _ => new(0, 0),
    };

    private static TeamRelation Relation(Who who) => who switch
    {
        Who.Neutral => TeamRelation.Neutral,
        Who.Hostile => TeamRelation.Hostile,
        _ => TeamRelation.Allied, // self, common, teammate, allied (an NPC's relation is ignored)
    };

    private static readonly Who[] All = Enum.GetValues<Who>();

    // ------------------------------------------------------------------ ownership: Order, Rename (and the ownership half of the rest)

    /// <summary>Who each policy lets the sender command (literal table): policy -> (leader?) -> allowed owners.</summary>
    private static readonly Dictionary<(TeamAssetPolicy, bool), Who[]> Commandable = new()
    {
        [(TeamAssetPolicy.SharedCommand, false)] = [Who.Self, Who.Common, Who.Teammate],
        [(TeamAssetPolicy.SharedCommand, true)] = [Who.Self, Who.Common, Who.Teammate],
        [(TeamAssetPolicy.OwnerOnly, false)] = [Who.Self, Who.Common],
        [(TeamAssetPolicy.OwnerOnly, true)] = [Who.Self, Who.Common],
        [(TeamAssetPolicy.OwnerAndLeader, false)] = [Who.Self, Who.Common],
        [(TeamAssetPolicy.OwnerAndLeader, true)] = [Who.Self, Who.Common, Who.Teammate],
    };

    public static TheoryData<TeamAssetPolicy, bool, Who, AssetAction, RejectReason> OwnershipCells()
    {
        var data = new TheoryData<TeamAssetPolicy, bool, Who, AssetAction, RejectReason>();
        foreach (var policy in Enum.GetValues<TeamAssetPolicy>())
        {
            foreach (bool leader in new[] { false, true })
            {
                foreach (var owner in All)
                {
                    foreach (var action in new[] { AssetAction.Order, AssetAction.Rename })
                    {
                        data.Add(policy, leader, owner, action, Commandable[(policy, leader)].Contains(owner) ? RejectReason.None : RejectReason.NotYourAsset);
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(OwnershipCells))]
    public void OrderAndRenameFollowTheAssetPolicy(TeamAssetPolicy policy, bool leader, Who owner, AssetAction action, RejectReason expected)
    {
        var verdict = AssetPermissionPolicy.Evaluate(Settings(policy), leader ? Leader : Member, action, Subject(owner), null, Relation(owner));

        Assert.Equal(expected, verdict.Reason);
        Assert.Equal(expected == RejectReason.None, verdict.Allowed);
    }

    // ------------------------------------------------------------------ attack orders and kill/hit claims: hostility and friendly fire

    private static RejectReason ExpectedHostility(Who target, bool friendlyFire) => target switch
    {
        Who.Hostile or Who.Npc => RejectReason.None,
        _ when friendlyFire => RejectReason.None,
        Who.Neutral => RejectReason.HostileRequired,
        _ => RejectReason.FriendlyFireDisabled, // teammate, allied
    };

    public static TheoryData<TeamAssetPolicy, Who, bool, RejectReason> AttackCells()
    {
        var data = new TheoryData<TeamAssetPolicy, Who, bool, RejectReason>();
        foreach (var policy in Enum.GetValues<TeamAssetPolicy>())
        {
            foreach (var target in All.Where(w => w is not Who.Common))
            {
                foreach (bool friendlyFire in new[] { false, true })
                {
                    data.Add(policy, target, friendlyFire, ExpectedHostility(target, friendlyFire));
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AttackCells))]
    public void AttackOrdersNeedAHostileOrNpcTargetUnlessFriendlyFire(TeamAssetPolicy policy, Who target, bool friendlyFire, RejectReason expected)
    {
        // the attacker is the sender's own ship, so only the target decides
        var verdict = AssetPermissionPolicy.Evaluate(
            Settings(policy, friendlyFire), Member, AssetAction.OrderAttack, Subject(Who.Self), Subject(target), Relation(target));

        Assert.Equal(expected, verdict.Reason);
    }

    [Fact]
    public void AnAttackOrderWithAForeignShipIsRejectedBeforeTheTargetIsLookedAt()
    {
        var verdict = AssetPermissionPolicy.Evaluate(
            Settings(), Member, AssetAction.OrderAttack, Subject(Who.Hostile), Subject(Who.Npc), TeamRelation.Hostile);

        Assert.Equal(RejectReason.NotYourAsset, verdict.Reason);
    }

    [Theory]
    [MemberData(nameof(AttackCells))]
    public void KillAndHitClaimsFollowHostilityAndFriendlyFireInRange(TeamAssetPolicy policy, Who target, bool friendlyFire, RejectReason expected)
    {
        var verdict = AssetPermissionPolicy.Evaluate(
            Settings(policy, friendlyFire), Member, AssetAction.KillOrHit, null, Subject(target), Relation(target), distanceMetres: 5_000);

        Assert.Equal(expected, verdict.Reason);
    }

    [Theory]
    [InlineData(0.0, RejectReason.None)]
    [InlineData(29_999.0, RejectReason.None)]
    [InlineData(30_000.0, RejectReason.None)]
    [InlineData(30_001.0, RejectReason.NotPermitted)]
    [InlineData(1_000_000.0, RejectReason.NotPermitted)]
    [InlineData(null, RejectReason.NotPermitted)] // different sector or no ship position
    public void KillAndHitClaimsAreRangeChecked(double? distance, RejectReason expected)
    {
        var verdict = AssetPermissionPolicy.Evaluate(
            Settings(), Member, AssetAction.KillOrHit, null, Subject(Who.Npc), TeamRelation.Neutral, distance);

        Assert.Equal(expected, verdict.Reason);
    }

    [Fact]
    public void RangeIsCheckedBeforeHostility()
    {
        var verdict = AssetPermissionPolicy.Evaluate(
            Settings(), Member, AssetAction.KillOrHit, null, Subject(Who.Allied), TeamRelation.Allied, distanceMetres: 90_000);

        Assert.Equal(RejectReason.NotPermitted, verdict.Reason);
    }

    // ------------------------------------------------------------------ capture

    [Theory]
    [InlineData(Who.Self, RejectReason.HostileRequired)]
    [InlineData(Who.Teammate, RejectReason.HostileRequired)]
    [InlineData(Who.Allied, RejectReason.HostileRequired)]
    [InlineData(Who.Neutral, RejectReason.HostileRequired)]
    [InlineData(Who.Hostile, RejectReason.None)]
    [InlineData(Who.Npc, RejectReason.None)]
    public void CaptureNeedsAnNpcOrHostileTargetEvenWithFriendlyFire(Who target, RejectReason expected)
    {
        foreach (bool friendlyFire in new[] { false, true })
        {
            var verdict = AssetPermissionPolicy.Evaluate(
                Settings(friendlyFire: friendlyFire), Member, AssetAction.Capture, null, Subject(target), Relation(target));

            Assert.Equal(expected, verdict.Reason);
        }
    }

    // ------------------------------------------------------------------ trade report

    [Theory]
    [InlineData(Who.Npc, RejectReason.None)]
    [InlineData(Who.Self, RejectReason.None)]
    [InlineData(Who.Teammate, RejectReason.None)]
    [InlineData(Who.Allied, RejectReason.None)]
    [InlineData(Who.Neutral, RejectReason.None)]
    [InlineData(Who.Hostile, RejectReason.PolicyDenied)]
    public void TradeReportsNeedAnOwnShipAndAStationThatIsNotHostile(Who station, RejectReason expected)
    {
        var verdict = AssetPermissionPolicy.Evaluate(
            Settings(), Member, AssetAction.TradeReport, Subject(Who.Self), Subject(station), Relation(station));

        Assert.Equal(expected, verdict.Reason);
    }

    [Theory]
    [InlineData(Who.Teammate, TeamAssetPolicy.OwnerOnly, RejectReason.NotYourAsset)]
    [InlineData(Who.Teammate, TeamAssetPolicy.SharedCommand, RejectReason.None)]
    [InlineData(Who.Allied, TeamAssetPolicy.SharedCommand, RejectReason.NotYourAsset)]
    [InlineData(Who.Hostile, TeamAssetPolicy.SharedCommand, RejectReason.NotYourAsset)]
    [InlineData(Who.Npc, TeamAssetPolicy.SharedCommand, RejectReason.NotYourAsset)]
    public void TradeReportsNeedACommandableShip(Who ship, TeamAssetPolicy policy, RejectReason expected)
    {
        var verdict = AssetPermissionPolicy.Evaluate(
            Settings(policy), Member, AssetAction.TradeReport, Subject(ship), Subject(Who.Npc), TeamRelation.Neutral);

        Assert.Equal(expected, verdict.Reason);
    }

    // ------------------------------------------------------------------ gift

    [Theory]
    [InlineData(Who.Teammate, false, RejectReason.PolicyDenied)]
    [InlineData(Who.Allied, false, RejectReason.PolicyDenied)]
    [InlineData(Who.Neutral, false, RejectReason.PolicyDenied)]
    [InlineData(Who.Hostile, false, RejectReason.PolicyDenied)]
    [InlineData(Who.Teammate, true, RejectReason.None)]
    [InlineData(Who.Allied, true, RejectReason.None)]
    [InlineData(Who.Neutral, true, RejectReason.NotAllied)]
    [InlineData(Who.Hostile, true, RejectReason.NotAllied)]
    public void GiftsNeedTransfersEnabledAndAnAlliedDestination(Who destination, bool transfer, RejectReason expected)
    {
        var verdict = AssetPermissionPolicy.Evaluate(
            Settings(transfer: transfer), Member, AssetAction.Gift, Subject(Who.Self), Subject(destination) with { Player = 0 }, Relation(destination));

        Assert.Equal(expected, verdict.Reason);
    }

    [Theory]
    [InlineData(Who.Teammate, RejectReason.NotYourAsset)]
    [InlineData(Who.Hostile, RejectReason.NotYourAsset)]
    [InlineData(Who.Npc, RejectReason.NotYourAsset)]
    public void GiftsOfSomebodyElsesAssetAreRejectedWhateverTheDestination(Who owner, RejectReason expected)
    {
        var verdict = AssetPermissionPolicy.Evaluate(
            Settings(TeamAssetPolicy.OwnerOnly, transfer: true), Member, AssetAction.Gift, Subject(owner), new PermissionSubject(1, 0), TeamRelation.Allied);

        Assert.Equal(expected, verdict.Reason);
    }

    // ------------------------------------------------------------------ station build and unassigned senders

    [Fact]
    public void AStationCanBeBuiltByAnyTeamMember()
    {
        foreach (var policy in Enum.GetValues<TeamAssetPolicy>())
        {
            Assert.True(AssetPermissionPolicy.Evaluate(Settings(policy), Member, AssetAction.StationBuild, null, null, TeamRelation.Allied).Allowed);
        }
    }

    [Theory]
    [InlineData(AssetAction.Order)]
    [InlineData(AssetAction.OrderAttack)]
    [InlineData(AssetAction.Rename)]
    [InlineData(AssetAction.Gift)]
    [InlineData(AssetAction.StationBuild)]
    [InlineData(AssetAction.TradeReport)]
    [InlineData(AssetAction.KillOrHit)]
    [InlineData(AssetAction.Capture)]
    public void ASenderWithoutATeamCanDoNothing(AssetAction action)
    {
        var verdict = AssetPermissionPolicy.Evaluate(
            Settings(friendlyFire: true, transfer: true), new PermissionActor(Me, 0, false), action, new PermissionSubject(0, 0), new PermissionSubject(0, 0), TeamRelation.Hostile, 100);

        Assert.Equal(RejectReason.PolicyDenied, verdict.Reason);
    }
}
