using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode.Tests;

public sealed class FakeTeamStateTests
{
    private static Frame Frame<TObj, TTable>(MsgType type, Func<Google.FlatBuffers.FlatBufferBuilder, TObj, Google.FlatBuffers.Offset<TTable>> pack, TObj value) where TTable : struct =>
        new(type, FrameOptions.None, Lane.Control, MessageEncoder.EncodePayload(b => pack(b, value), 256));

    private static WelcomeT Welcome() => new()
    {
        PlayerId = 7,
        TeamId = 1,
        TeamRole = TeamRole.Leader,
        Teams = new TeamTableT { Version = 3, Full = true, Teams = [new TeamInfoT { TeamId = 1, Name = "A", Members = [] }, new TeamInfoT { TeamId = 2, Name = "B", Members = [] }], Removed = [] },
        Relations = new TeamRelationsT { Version = 1, Full = true, DefaultRelation = TeamRelation.Neutral, Entries = [] },
        Settings = new SessionSettingsT { Version = 1, Team = new TeamPolicyT { RelationChangePolicy = RelationChangePolicy.AdminOnly } },
    };

    [Fact]
    public void WelcomeSetsTheOwnTeamTheTableTheMatrixAndThePolicy()
    {
        var state = new FakeTeamState();
        state.ApplyWelcome(Welcome());

        Assert.Equal((7, 1, TeamRole.Leader), (state.PlayerId, state.OwnTeam, state.OwnRole));
        Assert.Equal(2, state.Teams.Count);
        Assert.False(state.IsHostile(1, 2));
        Assert.Equal(RelationChangePolicy.AdminOnly, state.Policy!.RelationChangePolicy);
    }

    [Fact]
    public void RelationDeltasUpdateTheMatrixAndNpcHostilityFollows()
    {
        var state = new FakeTeamState();
        state.ApplyWelcome(Welcome());
        var changes = new List<(int, int, TeamRelation)>();
        state.RelationChanged += (a, b, r) => changes.Add((a, b, r));

        var delta = new TeamRelationsT
        {
            Version = 2, Full = false, DefaultRelation = TeamRelation.Neutral,
            Entries = [new TeamRelationEntryT { TeamA = 2, TeamB = 1, Relation = TeamRelation.Hostile }],
        };
        state.Handle(Frame(MsgType.TeamRelations, TeamRelations.Pack, delta));

        Assert.True(state.IsHostile(1, 2));
        Assert.True(state.IsHostile(2, 1));
        Assert.False(state.IsHostile(1, 1));
        Assert.Equal([(1, 2, TeamRelation.Hostile)], changes);

        // A full copy with the default Hostile makes every other pair hostile; an Allied entry wins for its pair.
        var full = new TeamRelationsT
        {
            Version = 3, Full = true, DefaultRelation = TeamRelation.Hostile,
            Entries = [new TeamRelationEntryT { TeamA = 1, TeamB = 2, Relation = TeamRelation.Allied }],
        };
        state.Handle(Frame(MsgType.TeamRelations, TeamRelations.Pack, full));
        Assert.False(state.IsHostile(1, 2));
        Assert.True(state.IsHostile(1, 3));
        Assert.Equal(0, state.VersionRegressions);

        state.Handle(Frame(MsgType.TeamRelations, TeamRelations.Pack, delta)); // version 2 after 3
        Assert.Equal(1, state.VersionRegressions);
    }

    [Fact]
    public void MemberChangesMoveTheOwnTeamTableDeltasUpsertAndRemoveAndAuthorityRecordsReassigns()
    {
        var state = new FakeTeamState();
        state.ApplyWelcome(Welcome());

        state.Handle(Frame(MsgType.TeamMemberChanged, TeamMemberChanged.Pack, new TeamMemberChangedT { PlayerId = 7, FromTeam = 1, ToTeam = 2, Role = TeamRole.Member }));
        Assert.Equal(2, state.OwnTeam);

        state.Handle(Frame(MsgType.TeamTable, TeamTable.Pack, new TeamTableT
        {
            Version = 4, Full = false, Removed = [1],
            Teams = [new TeamInfoT { TeamId = 2, Name = "B2", Members = [new TeamMemberT { PlayerId = 7, Name = "me" }] }],
        }));
        Assert.Null(state.Team(1));
        Assert.Equal("B2", state.Team(2)!.Name);

        state.Handle(Frame(MsgType.ReassignPlayerAssets, ReassignPlayerAssets.Pack, new ReassignPlayerAssetsT { PlayerId = 7, FromTeam = 1, ToTeam = 2, Scope = MoveAssetsScope.ShipOnly }));
        Assert.Equal(7, Assert.Single(state.Reassigns).PlayerId);
    }

    [Fact]
    public void TheRequestBuildersProduceFramesTheCatalogDecodes()
    {
        var relation = FakeTeamState.BuildRelationChangeRequest(5, 3, TeamRelation.Allied);
        var decoded = MessageRegistry.Default.Decode<RelationChangeRequest>(new Frame(relation.Type, FrameOptions.None, Lane.Control, relation.Payload)).UnPack();
        Assert.Equal((5UL, (ushort)3, TeamRelation.Allied), (decoded.RequestKey.Lo, decoded.OtherTeam, decoded.Relation));

        var change = FakeTeamState.BuildTeamChangeRequest(6, 4);
        Assert.Equal((ushort)4, MessageRegistry.Default.Decode<TeamChangeRequest>(new Frame(change.Type, FrameOptions.None, Lane.Control, change.Payload)).TeamId);
    }
}
