using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode.Tests;

/// <summary>M1-F3: the team options of the CLI, the lobby answer, the fake authority's asset re-owning and the fake NPC hostility (no sockets).</summary>
public sealed class TeamBehaviourTests
{
    private static Frame Frame<TObj, TTable>(MsgType type, Func<Google.FlatBuffers.FlatBufferBuilder, TObj, Google.FlatBuffers.Offset<TTable>> pack, TObj value) where TTable : struct =>
        new(type, FrameOptions.None, Lane.Control, MessageEncoder.EncodePayload(b => pack(b, value), 512));

    private static Frame ToFrame(OutMessage m) => new(m.Type, FrameOptions.None, MessageRegistry.Default.GetDescriptor(m.Type).Lane, m.Payload);

    private static TeamInfoT Team(int id, string name, params int[] members) =>
        new() { TeamId = (ushort)id, Name = name, Members = [.. members.Select(m => new TeamMemberT { PlayerId = (ushort)m, Name = "p" + m })] };

    private static FakeTeamState State(bool allowCreate, params TeamInfoT[] teams)
    {
        var state = new FakeTeamState();
        state.ApplyWelcome(new WelcomeT
        {
            PlayerId = 9,
            Teams = new TeamTableT { Version = 1, Full = true, Teams = [.. teams], Removed = [] },
            Relations = new TeamRelationsT { Version = 1, Full = true, DefaultRelation = TeamRelation.Neutral, Entries = [] },
            Settings = new SessionSettingsT { Version = 1, Team = new TeamPolicyT { AllowCreateInLobby = allowCreate } },
        });
        return state;
    }

    // ------------------------------------------------------------------ CLI

    [Fact]
    public void TeamOptionsParse()
    {
        var o = CliParser.Parse(["client", "--team", "Alpha"]).Options!;
        Assert.Equal("Alpha", o.Team);

        Assert.Equal(TeamPickMode.LobbyRandom, CliParser.Parse(["client", "--team-pick", "lobby-random"]).Options!.TeamPick);
        Assert.Equal(3, CliParser.Parse(["swarm", "--clients", "6", "--teams", "3"]).Options!.EffectiveTeams);
        Assert.Equal(RelationsPreset.Ffa, CliParser.Parse(["swarm", "--clients", "4", "--relations", "FFA"]).Options!.Relations);
    }

    [Theory]
    [InlineData("coop", 6, 1)]
    [InlineData("twoteams", 6, 2)]
    [InlineData("ffa", 5, 5)]
    [InlineData("allied", 4, 4)]
    public void RelationsImplyTheTeamCount(string preset, int clients, int expected) =>
        Assert.Equal(expected, CliParser.Parse(["swarm", "--clients", clients.ToString(), "--relations", preset]).Options!.EffectiveTeams);

    [Fact]
    public void TeamsOverridesTheCountOfARelationsPreset() =>
        Assert.Equal(3, CliParser.Parse(["swarm", "--clients", "6", "--relations", "ffa", "--teams", "3"]).Options!.EffectiveTeams);

    [Theory]
    [InlineData("client", "--team-pick", "random")]
    [InlineData("swarm", "--relations", "chaos")]
    [InlineData("swarm", "--teams", "9")]
    [InlineData("swarm", "--teams", "0")]
    public void BadTeamOptionsAreRefused(string command, string option, string value) =>
        Assert.False(CliParser.Parse([command, option, value]).Ok);

    [Fact]
    public void ConflictingTeamOptionsAreRefused()
    {
        Assert.False(CliParser.Parse(["client", "--team", "A", "--team-pick", "lobby-random"]).Ok);
        Assert.False(CliParser.Parse(["swarm", "--team", "A", "--teams", "2"]).Ok);
        Assert.False(CliParser.Parse(["client", "--relations", "ffa"]).Ok);
    }

    [Fact]
    public void WishesFollowTheOptionsAndSpreadTheClientsOverTheTeams()
    {
        var spread = CliParser.Parse(["swarm", "--clients", "5", "--teams", "2"]).Options!;
        Assert.Equal([1, 2, 1, 2, 1], Enumerable.Range(0, 5).Select(i => ((TeamWish.Slot)TeamWish.For(spread, i)).Number));
        Assert.Equal("Team 2", TeamWish.SlotName(2));

        Assert.Equal(new TeamWish.Named("Beta"), TeamWish.For(CliParser.Parse(["client", "--team", "Beta"]).Options!, 0));
        Assert.IsType<TeamWish.Random>(TeamWish.For(CliParser.Parse(["client", "--team-pick", "lobby-random"]).Options!, 0));
        Assert.IsType<TeamWish.None>(TeamWish.For(CliParser.Parse(["client"]).Options!, 0));
    }

    [Fact]
    public void EachLayoutNamesItsServerSettings()
    {
        Assert.Contains("--X4MP:Teams:DefaultRelation=Hostile", TeamLayout.ServerSettings(RelationsPreset.Ffa));
        Assert.Contains("--X4MP:Teams:DefaultRelation=Allied", TeamLayout.ServerSettings(RelationsPreset.Allied));
        Assert.Contains("--X4MP:Teams:JoinMode=Lobby", TeamLayout.ServerSettings(RelationsPreset.TwoTeams));
        Assert.Contains("--X4MP:Teams:AutoAssign=SingleTeam", TeamLayout.ServerSettings(RelationsPreset.Coop));
        Assert.Empty(TeamLayout.ServerSettings(RelationsPreset.None));
    }

    // ------------------------------------------------------------------ the lobby answer

    [Fact]
    public void ANamedTeamIsChosenByIdOrNameAndCreatedWhenTheLobbyAllowsIt()
    {
        var rng = new DetRandom(1);
        var withTeams = State(allowCreate: false, Team(1, "Alpha"), Team(2, "Beta"));

        var byName = ToFrame(FakeTeamJoin.Decide(new TeamWish.Named("beta"), withTeams, "Bot", 1, rng, out _)!);
        var byId = ToFrame(FakeTeamJoin.Decide(new TeamWish.Named("1"), withTeams, "Bot", 2, rng, out _)!);
        Assert.Equal(2, MessageRegistry.Default.Decode<TeamChoice>(byName).TeamId);
        Assert.Equal(1, MessageRegistry.Default.Decode<TeamChoice>(byId).TeamId);

        Assert.Null(FakeTeamJoin.Decide(new TeamWish.Named("Gamma"), withTeams, "Bot", 3, rng, out string note)); // cannot create
        Assert.Contains("does not let", note);

        var creating = State(allowCreate: true, Team(1, "Alpha"));
        var create = ToFrame(FakeTeamJoin.Decide(new TeamWish.Named("Gamma"), creating, "Bot", 4, rng, out _)!);
        Assert.Equal("Gamma", MessageRegistry.Default.Decode<TeamCreateRequest>(create).Name);
        Assert.Null(FakeTeamJoin.Decide(new TeamWish.Named("7"), creating, "Bot", 5, rng, out _)); // an unknown id is not a name to create
    }

    [Fact]
    public void ASlotFindsItsTeamCreatesItOrFallsBackToATableTeam()
    {
        var rng = new DetRandom(1);
        var table = State(allowCreate: false, Team(1, "Team 1"), Team(2, "Team 2"));
        Assert.Equal(2, MessageRegistry.Default.Decode<TeamChoice>(ToFrame(FakeTeamJoin.Decide(new TeamWish.Slot(2), table, "Bot", 1, rng, out _)!)).TeamId);

        var onlyFirst = State(allowCreate: true, Team(1, "Team 1"));
        Assert.Equal("Team 3", MessageRegistry.Default.Decode<TeamCreateRequest>(ToFrame(FakeTeamJoin.Decide(new TeamWish.Slot(3), onlyFirst, "Bot", 2, rng, out _)!)).Name);

        var noCreate = State(allowCreate: false, Team(1, "Everyone"), Team(2, "Others"));
        Assert.Equal(1, MessageRegistry.Default.Decode<TeamChoice>(ToFrame(FakeTeamJoin.Decide(new TeamWish.Slot(3), noCreate, "Bot", 3, rng, out _)!)).TeamId); // slot 3 of 2 teams wraps

        Assert.Null(FakeTeamJoin.Decide(new TeamWish.Slot(1), State(allowCreate: false), "Bot", 4, rng, out _));
    }

    [Fact]
    public void LobbyRandomPicksOnlyOpenTeamsAndSpreadsAcrossThem()
    {
        var table = State(
            allowCreate: false,
            Team(1, "Open1"),
            Team(2, "Open2"),
            new TeamInfoT { TeamId = 3, Name = "Locked", Locked = true, Members = [] },
            new TeamInfoT { TeamId = 4, Name = "Secret", PasswordProtected = true, Members = [] },
            new TeamInfoT { TeamId = 5, Name = "Full", MaxMembers = 1, Members = [new TeamMemberT { PlayerId = 1 }] });

        var picked = new HashSet<int>();
        for (ulong seed = 0; seed < 40; seed++)
        {
            var message = FakeTeamJoin.Decide(new TeamWish.Random(), table, "Bot", seed + 1, new DetRandom(seed), out _)!;
            picked.Add(MessageRegistry.Default.Decode<TeamChoice>(ToFrame(message)).TeamId);
        }

        Assert.Equal([1, 2], picked.Order());

        // a lobby that lets nodes create teams makes "a new team" one more random choice, up to the policy's team limit
        var kinds = new HashSet<MsgType>();
        for (ulong seed = 0; seed < 40; seed++)
            kinds.Add(FakeTeamJoin.Decide(new TeamWish.Random(), State(allowCreate: true, Team(1, "Open1")), "Bot", seed + 1, new DetRandom(seed), out _)!.Type);
        Assert.Equal([MsgType.TeamChoice, MsgType.TeamCreateRequest], kinds.Order());

        var atLimit = State(allowCreate: true, Team(1, "Open1"));
        atLimit.ApplyWelcome(new WelcomeT { Settings = new SessionSettingsT { Version = 2, Team = new TeamPolicyT { AllowCreateInLobby = true, MaxTeams = 1 } } });
        for (ulong seed = 0; seed < 20; seed++)
            Assert.Equal(MsgType.TeamChoice, FakeTeamJoin.Decide(new TeamWish.Random(), atLimit, "Bot", seed + 1, new DetRandom(seed), out _)!.Type);

        var closed = State(allowCreate: true, new TeamInfoT { TeamId = 3, Name = "Locked", Locked = true, Members = [] });
        var create = ToFrame(FakeTeamJoin.Decide(new TeamWish.Random(), closed, "Bot7", 1, new DetRandom(1), out _)!);
        Assert.Equal("Bot7", MessageRegistry.Default.Decode<TeamCreateRequest>(create).Name);
    }

    // ------------------------------------------------------------------ authority: owners and reassign

    private static FakeAuthority AuthorityWithMembers(int p1, int p2)
    {
        var authority = new FakeAuthority(new FakeWorld(FakeGalaxy.Generate(42)), new FakeAuthorityOptions { TeamAssets = true });
        authority.Teams.ApplyWelcome(new WelcomeT
        {
            PlayerId = 1,
            Teams = new TeamTableT { Version = 1, Full = true, Teams = [Team(1, "One", p1, p2), Team(2, "Two")], Removed = [] },
            Relations = new TeamRelationsT { Version = 1, Full = true, DefaultRelation = TeamRelation.Neutral, Entries = [] },
        });
        return authority;
    }

    [Fact]
    public void ShipsAreTaggedForEveryTeamAndRealMembersOwnSome()
    {
        var authority = AuthorityWithMembers(10, 11);
        var owners = authority.World.Galaxy.Entities.Where(e => !e.IsStation).Select(e => authority.TeamAssetOwner(e.EntityId, false)).ToList();

        Assert.Equal([1, 2], authority.TeamIdsInUse());
        Assert.Contains(((ushort)1, (ushort)10), owners);
        Assert.Contains(((ushort)1, (ushort)11), owners);
        Assert.Contains(((ushort)1, FakeAuthorityOptions.TeammatePlayerId), owners);
        Assert.Contains(((ushort)2, (ushort)0), owners);
        Assert.Contains(((ushort)0, (ushort)0), owners); // plain NPCs stay
    }

    [Fact]
    public void MoreThanTwoTeamsSpreadTheCommonShips()
    {
        var authority = new FakeAuthority(new FakeWorld(FakeGalaxy.Generate(42)), new FakeAuthorityOptions { TeamAssets = true, TeamIds = [1, 2, 3, 4] });
        var common = authority.World.Galaxy.Entities.Where(e => !e.IsStation).Select(e => authority.TeamAssetOwner(e.EntityId, false)).Where(o => o is { Team: > 0, Player: 0 });

        Assert.Equal([1, 2, 3, 4], common.Select(o => (int)o.Team).Distinct().Order());
    }

    [Fact]
    public void ReassignMovesThePlayersAssetsAndEmitsOneEntityChangeEach()
    {
        var authority = AuthorityWithMembers(10, 11);
        var entities = authority.World.Galaxy.Entities.Where(e => !e.IsStation).ToList();
        var ownedBy10 = entities.Where(e => authority.TeamAssetOwner(e.EntityId, false) == ((ushort)1, (ushort)10)).Select(e => FakeNetIds.ToNetId(e.EntityId)).ToList();
        var ownedBy11 = entities.Where(e => authority.TeamAssetOwner(e.EntityId, false) == ((ushort)1, (ushort)11)).Select(e => FakeNetIds.ToNetId(e.EntityId)).ToList();
        Assert.NotEmpty(ownedBy10);

        var messages = authority.Reassign(new ReassignPlayerAssetsT { PlayerId = 10, FromTeam = 1, ToTeam = 2, Scope = MoveAssetsScope.ShipOnly });

        Assert.Equal(ownedBy10.Count, messages.Count);
        Assert.All(messages, m => Assert.Equal(MsgType.EntityChange, m.Type));
        var decoded = messages.Select(m => MessageRegistry.Default.Decode<EntityChange>(ToFrame(m))).ToList();
        Assert.Equal(ownedBy10.Order(), decoded.Select(c => c.NetId).Order());
        Assert.All(decoded, c =>
        {
            Assert.Equal(ChangeField.OwnerTeam, c.Fields);
            Assert.Equal(2, c.OwnerTeam);
            Assert.Equal(10, c.OwnerPlayer);
        });
        Assert.All(ownedBy10, id => Assert.Equal(((ushort)2, (ushort)10), authority.TeamAssetOwner((int)id, false)));
        Assert.All(ownedBy11, id => Assert.Equal(((ushort)1, (ushort)11), authority.TeamAssetOwner((int)id, false))); // teammates keep theirs
        Assert.Equal(ownedBy10.Count, authority.OwnershipChanges.Count);

        // a second identical request finds nothing left to move; scope None and a wrong source team move nothing either
        Assert.Empty(authority.Reassign(new ReassignPlayerAssetsT { PlayerId = 10, FromTeam = 1, ToTeam = 2, Scope = MoveAssetsScope.ShipOnly }));
        Assert.Empty(authority.Reassign(new ReassignPlayerAssetsT { PlayerId = 11, FromTeam = 1, ToTeam = 2, Scope = MoveAssetsScope.None }));
        Assert.Empty(authority.Reassign(new ReassignPlayerAssetsT { PlayerId = 11, FromTeam = 2, ToTeam = 1, Scope = MoveAssetsScope.AllOwned }));
    }

    [Fact]
    public void WithoutTeamAssetsNothingIsReassigned()
    {
        var authority = new FakeAuthority(new FakeWorld(FakeGalaxy.Generate(42)));

        Assert.Empty(authority.Reassign(new ReassignPlayerAssetsT { PlayerId = 10, FromTeam = 1, ToTeam = 2, Scope = MoveAssetsScope.ShipOnly }));
    }

    [Fact]
    public void AReassignedAssetFollowsTheNewTeamInAClientsOrders()
    {
        var authority = AuthorityWithMembers(10, 11);
        authority.Teams.Handle(Frame(MsgType.TeamMemberChanged, TeamMemberChanged.Pack, new TeamMemberChangedT { PlayerId = 1, FromTeam = 1, ToTeam = 1 }));
        var client = new FakeClientSession(new FakeWorld(FakeGalaxy.Generate(42)), verify: false);
        ushort sector = (ushort)authority.World.Galaxy.Entities.Where(e => !e.IsStation).GroupBy(e => e.HomeSector).OrderByDescending(g => g.Count()).First().Key;
        authority.OnCaptureSet(new CaptureSetT { Epoch = 1, Sectors = [new CaptureSectorT { Sector = sector, RateHz = 5 }] }, 0);
        for (long tick = 0; tick < 100; tick++)
        {
            foreach (var m in authority.Tick(tick).Where(m => m.Type == MsgType.EntitySpawn))
                client.Handle(ToFrame(m));
        }

        // player 10 commands its own ship before and after the move; team 2 sees it as foreign before and own after
        var mine = client.GhostsOwnedBy(1, 10);
        Assert.NotEmpty(mine);
        var ownBefore = Enumerable.Range(0, 400).Select(n => client.PickAsset(CommanderMode.Own, 1, 10, n)!.Value.NetId).ToHashSet();
        Assert.All(mine, x => Assert.Contains(x.NetId, ownBefore));

        foreach (var m in authority.Reassign(new ReassignPlayerAssetsT { PlayerId = 10, FromTeam = 1, ToTeam = 2, Scope = MoveAssetsScope.ShipOnly }))
            client.Handle(ToFrame(m));

        Assert.Equal(mine.Count, client.OwnerChanges);
        Assert.Empty(client.GhostsOwnedBy(1, 10));
        Assert.Equal(mine.Select(x => x.NetId), client.GhostsOwnedBy(2, 10).Select(x => x.NetId));
        Assert.Equal(((ushort)2, (ushort)10), client.OwnerOf(mine[0].NetId));
        // player 10 in team 2 now picks its former ships as "own", and no longer as foreign
        var ownAfter = Enumerable.Range(0, 200).Select(n => client.PickAsset(CommanderMode.Own, 2, 10, n)!.Value.NetId).ToHashSet();
        Assert.All(mine, x => Assert.Contains(x.NetId, ownAfter));
        var foreignForTeam1 = Enumerable.Range(0, 400).Select(n => client.PickAsset(CommanderMode.Foreign, 1, 99, n)!.Value.NetId).ToHashSet();
        Assert.All(mine, x => Assert.Contains(x.NetId, foreignForTeam1));
    }

    // ------------------------------------------------------------------ NPC hostility

    [Fact]
    public void FakeNpcHostilityFollowsTheRelationMatrix()
    {
        var authority = AuthorityWithMembers(10, 11);
        authority.Tick(0);

        var calm = authority.Hostility();
        Assert.Empty(calm.HostileTeamPairs);
        Assert.Equal(0, calm.EngagedShipPairs);

        var hostile = new TeamRelationsT
        {
            Version = 2, Full = false, DefaultRelation = TeamRelation.Neutral,
            Entries = [new TeamRelationEntryT { TeamA = 1, TeamB = 2, Relation = TeamRelation.Hostile }],
        };
        authority.Teams.Handle(Frame(MsgType.TeamRelations, TeamRelations.Pack, hostile));
        var war = authority.Hostility();

        Assert.Equal([(1, 2)], war.HostileTeamPairs);
        Assert.True(war.EngagedShipPairs > 0);
        Assert.True(war.ShipsAtWar > 0);
        Assert.True(war.SectorsWithFights > 0);
        Assert.Equal(1, authority.RelationChangesApplied);

        var peace = new TeamRelationsT
        {
            Version = 3, Full = false, DefaultRelation = TeamRelation.Neutral,
            Entries = [new TeamRelationEntryT { TeamA = 1, TeamB = 2, Relation = TeamRelation.Allied }],
        };
        authority.Teams.Handle(Frame(MsgType.TeamRelations, TeamRelations.Pack, peace));

        Assert.Empty(authority.Hostility().HostileTeamPairs);
        Assert.Equal(0, authority.Hostility().EngagedShipPairs);
        Assert.Equal(2, authority.RelationChangesApplied);
    }

    [Fact]
    public void AHostileDefaultMakesEveryTeamPairHostile()
    {
        var authority = new FakeAuthority(new FakeWorld(FakeGalaxy.Generate(42)), new FakeAuthorityOptions { TeamAssets = true, TeamIds = [1, 2, 3] });
        authority.Teams.Handle(Frame(MsgType.TeamRelations, TeamRelations.Pack, new TeamRelationsT { Version = 1, Full = true, DefaultRelation = TeamRelation.Hostile, Entries = [] }));

        Assert.Equal([(1, 2), (1, 3), (2, 3)], authority.Hostility().HostileTeamPairs);
    }
}
