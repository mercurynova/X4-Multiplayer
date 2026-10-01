using System.Security.Cryptography;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Core.Teams;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;

namespace X4MP.Server.Tests.Net;

/// <summary>
/// M1-T2 acceptance with the real gateway, the real <see cref="SessionActor"/>, the <see cref="TeamModule"/> and six real
/// <see cref="TcpNodeClient"/>s, over the in-process transport and over TCP: each join mode places six clients as expected.
/// </summary>
[Collection("net")]
public class TeamJoinIntegrationTests
{
    public static TheoryData<string> Kinds => ["inproc", "tcp"];

    private static NetOptions Fast() => new() { AuthFailureDelayMs = 0, HandshakeTimeoutSeconds = 2, MaxConnectionsPerIp = 50, MaxPlayers = 16 };

    private sealed class Setup : IAsyncDisposable
    {
        private readonly List<TcpNodeClient> _clients = [];

        public required ActorFixture Fixture { get; init; }

        public required NetHarness Net { get; init; }

        public required TeamModule Teams { get; init; }

        public static async Task<Setup> CreateAsync(string kind, TeamOptions options)
        {
            var fixture = new ActorFixture(Fast());
            var teams = new TeamModule(options);
            fixture.Modules.Add(teams);
            var net = await NetHarness.CreateAsync(kind, fixture.Net, withGateway: true, handler: fixture.Handler);
            return new Setup { Fixture = fixture, Net = net, Teams = teams };
        }

        public async Task<TcpNodeClient> JoinAsync(string name, byte[]? key = null, Role roles = Role.Client)
        {
            var handle = await Net.ConnectAsync();
            try
            {
                var client = await TcpNodeClient.ConnectAsync(
                    handle.Stream, new NodeClientOptions { PlayerName = name, PlayerKey = key ?? RandomNumberGenerator.GetBytes(32), RequestedRoles = roles });
                _clients.Add(client);
                return client;
            }
            catch
            {
                await handle.DisposeAsync();
                throw;
            }
        }

        public async Task<List<TcpNodeClient>> JoinManyAsync(int count)
        {
            var list = new List<TcpNodeClient>();
            for (int i = 1; i <= count; i++)
            {
                list.Add(await JoinAsync("Pilot" + i));
            }

            return list;
        }

        public Task<SessionSnapshot> WaitPhasesAsync(NodePhase phase, int count) =>
            Fixture.WaitForAsync(s => s.Nodes.Count(n => n.Phase == phase) == count);

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients)
            {
                await client.DisposeAsync();
            }

            await Net.DisposeAsync();
            await Fixture.DisposeAsync();
        }
    }

    private static async Task<TeamRequestResult> ChooseAsync(TcpNodeClient client, int teamId, byte[]? proof = null, ulong key = 1)
    {
        await client.SendAsync(MsgType.TeamChoice, b => TeamChoice.Pack(b, new TeamChoiceT
        {
            RequestKey = new Id128T { Lo = key, Hi = 9 },
            TeamId = (ushort)teamId,
            Password = proof is null ? [] : [.. proof],
        }));
        return await ReadResultAsync(client);
    }

    private static async Task<TeamRequestResult> ReadResultAsync(TcpNodeClient client)
    {
        using var cts = new CancellationTokenSource(10_000);
        while (await client.ReceiveAsync(cts.Token) is { } frame)
        {
            if (frame.Type == MsgType.TeamRequestResult)
            {
                return MessageRegistry.Default.Decode<TeamRequestResult>(frame);
            }
        }

        throw new EndOfStreamException("connection closed before a TeamRequestResult");
    }

    private static byte[] Proof(string password, TcpNodeClient client) =>
        HandshakeAuth.ComputeProof(password, client.ServerHello.Nonce.ToArray(), client.PlayerKey);

    // ------------------------------------------------------------------ Auto

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AutoSingleTeamPutsSixClientsInOneTeam(string kind)
    {
        await using var s = await Setup.CreateAsync(kind, new TeamOptions());

        var clients = await s.JoinManyAsync(6);
        await s.WaitPhasesAsync(NodePhase.SyncingSave, 6);

        Assert.All(clients, c =>
        {
            Assert.Equal(1, c.Welcome.TeamId);
            Assert.Equal(1, c.Welcome.FactionSlot);
        });
        Assert.Equal(6, s.Teams.MembersOf(1).Count);
        Assert.Single(s.Teams.Teams);
        Assert.Equal(6, clients[5].Welcome.Teams.Teams[0].Members.Count); // the last one sees everybody in its Welcome
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AutoBalanceSplitsSixClientsAcrossTwoTeams(string kind)
    {
        await using var s = await Setup.CreateAsync(kind, new TeamOptions { AutoAssign = AutoAssignStrategy.Balance });
        Assert.True((await s.Teams.ApplyPresetAsync(TeamPreset.TwoTeams)).Ok);

        var clients = await s.JoinManyAsync(6);
        await s.WaitPhasesAsync(NodePhase.SyncingSave, 6);

        Assert.Equal([3, 3], s.Teams.Teams.Select(t => s.Teams.MembersOf(t.TeamId).Count));
        Assert.Equal([1, 2, 1, 2, 1, 2], clients.Select(c => (int)c.Welcome.TeamId));
        Assert.Equal(X4MP.Proto.TeamRelation.Hostile, clients[0].Welcome.Relations.Entries[0].Relation);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AutoNewTeamPerPlayerGivesEachOfSixClientsItsOwnTeam(string kind)
    {
        await using var s = await Setup.CreateAsync(kind, new TeamOptions { AutoAssign = AutoAssignStrategy.NewTeamPerPlayer });

        var clients = await s.JoinManyAsync(6);
        await s.WaitPhasesAsync(NodePhase.SyncingSave, 6);

        Assert.Equal([1, 2, 3, 4, 5, 6], clients.Select(c => (int)c.Welcome.TeamId));
        Assert.Equal([1, 2, 3, 4, 5, 6], clients.Select(c => (int)c.Welcome.FactionSlot));
        Assert.Equal(6, s.Teams.Teams.Count);
    }

    [Fact]
    public async Task TheNinthPlayerUnderNewTeamPerPlayerIsRefusedWithNoFactionSlot()
    {
        await using var s = await Setup.CreateAsync("inproc", new TeamOptions { AutoAssign = AutoAssignStrategy.NewTeamPerPlayer });
        for (int i = 1; i <= 8; i++)
        {
            await s.JoinAsync("Pilot" + i);
        }

        var rejected = await Assert.ThrowsAsync<HandshakeRejectedException>(() => s.JoinAsync("Pilot9"));

        Assert.Equal(DisconnectCode.NoFactionSlot, rejected.Code);
        Assert.Equal(8, s.Teams.Teams.Count);
    }

    // ------------------------------------------------------------------ Lobby

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task LobbyPlacesSixClientsWhereTheyChooseAndChecksPasswordsAndLocks(string kind)
    {
        await using var s = await Setup.CreateAsync(kind, new TeamOptions { JoinMode = TeamJoinMode.Lobby, AllowCreateInLobby = true });
        int red = (await s.Teams.CreateTeamAsync("Red")).Value!.Id;
        int secret = (await s.Teams.CreateTeamAsync("Secret", password: "swordfish")).Value!.Id;
        int closed = (await s.Teams.CreateTeamAsync("Closed", locked: true)).Value!.Id;

        var clients = await s.JoinManyAsync(6);
        await s.WaitPhasesAsync(NodePhase.AwaitingTeam, 6);
        Assert.All(clients, c =>
        {
            Assert.Equal(0, c.Welcome.TeamId);
            Assert.Equal(["Red", "Secret", "Closed"], c.Welcome.Teams.Teams.Select(t => t.Name));
        });

        // a wrong proof and a locked team are rejected, and the client stays in the lobby
        var wrong = await ChooseAsync(clients[0], secret, Proof("nope", clients[0]));
        var locked = await ChooseAsync(clients[0], closed, key: 2);
        Assert.Equal(TeamRejectReason.BadPassword, wrong.Reason);
        Assert.Equal(TeamRejectReason.Locked, locked.Reason);
        Assert.Equal(TeamRequestStatus.Rejected, locked.Status);
        await s.WaitPhasesAsync(NodePhase.AwaitingTeam, 6);

        // the right proof, an open team and a new team
        Assert.Equal(TeamRequestStatus.Ok, (await ChooseAsync(clients[0], secret, Proof("swordfish", clients[0]), key: 3)).Status);
        foreach (var c in clients.Skip(1).Take(3))
        {
            var ok = await ChooseAsync(c, red);
            Assert.Equal(TeamRequestStatus.Ok, ok.Status);
            Assert.Equal(red, ok.TeamId);
        }

        await clients[4].SendAsync(MsgType.TeamCreateRequest, b => TeamCreateRequest.Pack(b, new TeamCreateRequestT
        {
            RequestKey = new Id128T { Lo = 5, Hi = 9 },
            Name = "Mine",
            ColorRgb = 0x112233,
        }));
        var created = await ReadResultAsync(clients[4]);
        Assert.Equal(TeamRequestStatus.Ok, created.Status);

        // one client has not chosen yet: it is the only one still waiting
        var snapshot = await s.WaitPhasesAsync(NodePhase.SyncingSave, 5);
        Assert.Single(snapshot.Nodes, n => n.Phase == NodePhase.AwaitingTeam);
        Assert.Single(s.Teams.MembersOf(secret));
        Assert.Equal(3, s.Teams.MembersOf(red).Count);
        Assert.Empty(s.Teams.MembersOf(closed));
        Assert.Single(s.Teams.MembersOf(created.TeamId));
        Assert.Equal(0x112233u, TeamRules.ColorToRgb(s.Teams.TeamDetails.Single(t => t.Id == created.TeamId).Color));
    }

    // ------------------------------------------------------------------ AdminAssign

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AdminAssignClientsWaitWithoutASaveUntilAnAdminAssignsThem(string kind)
    {
        await using var s = await Setup.CreateAsync(kind, new TeamOptions { JoinMode = TeamJoinMode.AdminAssign });
        int red = (await s.Teams.CreateTeamAsync("Red")).Value!.Id;
        int blue = (await s.Teams.CreateTeamAsync("Blue")).Value!.Id;

        var clients = await s.JoinManyAsync(6);
        await s.WaitPhasesAsync(NodePhase.AwaitingTeam, 6);

        // no save step before a team: the node does not advance on its own report, and the policy refuses a save request here
        await clients[0].SendAsync(MsgType.LoadStatus, b => LoadStatus.Pack(b, new LoadStatusT { Phase = NodePhase.SyncingSave, Detail = "", Error = DisconnectCode.None }));
        await Task.Delay(150);
        var snapshot = await s.Fixture.Actor.GetSnapshotAsync();
        Assert.All(snapshot.Nodes, n => Assert.Equal(NodePhase.AwaitingTeam, n.Phase));
        Assert.Equal(6, (await s.Teams.GetUnassignedAsync()).Count);

        for (int i = 0; i < 6; i++)
        {
            Assert.True((await s.Teams.AssignPlayerAsync(clients[i].Welcome.PlayerId, i < 3 ? red : blue, assignedBy: "admin:root")).Ok);
        }

        await s.WaitPhasesAsync(NodePhase.SyncingSave, 6);
        Assert.Equal(3, s.Teams.MembersOf(red).Count);
        Assert.Equal(3, s.Teams.MembersOf(blue).Count);
        Assert.Empty(await s.Teams.GetUnassignedAsync());
    }

    [Fact]
    public async Task AnAuthorityInAdminAssignWaitsForItsTeamAndTheFlagShowsIt()
    {
        await using var s = await Setup.CreateAsync("inproc", new TeamOptions { JoinMode = TeamJoinMode.AdminAssign });
        int red = (await s.Teams.CreateTeamAsync("Red")).Value!.Id;

        var authority = await s.JoinAsync("Boss", roles: Role.Authority | Role.Client);
        await s.WaitPhasesAsync(NodePhase.AwaitingTeam, 1);
        Assert.True(s.Teams.AuthorityAwaitingTeam);

        await s.Teams.AssignPlayerAsync(authority.Welcome.PlayerId, red);
        await s.WaitPhasesAsync(NodePhase.SyncingSave, 1);
        Assert.False(s.Teams.AuthorityAwaitingTeam);
    }

    // ------------------------------------------------------------------ sticky

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AClientThatLeavesAndComesBackKeepsItsTeamWithoutAwaitingTeam(string kind)
    {
        await using var s = await Setup.CreateAsync(kind, new TeamOptions { JoinMode = TeamJoinMode.Lobby });
        await s.Teams.CreateTeamAsync("Red");
        int blue = (await s.Teams.CreateTeamAsync("Blue")).Value!.Id;
        byte[] key = RandomNumberGenerator.GetBytes(32);

        var first = await s.JoinAsync("Pilot", key);
        await s.WaitPhasesAsync(NodePhase.AwaitingTeam, 1);
        Assert.Equal(TeamRequestStatus.Ok, (await ChooseAsync(first, blue)).Status);
        await first.SendAsync(MsgType.Disconnect, b => Disconnect.Pack(b, new DisconnectT { Code = DisconnectCode.ClientQuit, Message = "", Expected = "" }));
        await s.Fixture.WaitForAsync(snap => snap.Nodes.Count == 0);

        var again = await s.JoinAsync("Pilot", key);

        Assert.Equal(blue, again.Welcome.TeamId);
        Assert.Equal(2, again.Welcome.FactionSlot);
        var snapshot = await s.WaitPhasesAsync(NodePhase.SyncingSave, 1);
        Assert.Equal(NodePhase.SyncingSave, Assert.Single(snapshot.Nodes).Phase);
        Assert.Equal(1, s.Fixture.Events.OfType<X4MP.Core.Events.NodePhaseChanged>().Count(e => e.To == nameof(NodePhase.AwaitingTeam))); // only the first join waited
    }
}
