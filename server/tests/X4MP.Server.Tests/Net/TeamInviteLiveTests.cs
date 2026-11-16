using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using X4MP.Core.Session;
using X4MP.Core.Teams;
using X4MP.FakeNode;
using X4MP.Persistence;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Server.Hosting;
using X4MP.Server.Tests.Saves;
using Xunit.Abstractions;

namespace X4MP.Server.Tests.Net;

/// <summary>
/// M5b over real sockets: a real host in lobby mode, an authority that starts the session and a leader that creates a team and
/// invites a player still waiting for one. The unit tests in <c>TeamInviteTests</c> drive the same module through an
/// <c>ActorRig</c> with a capturing publisher, so they cannot show that the frame is targeted on the wire or that
/// <c>TeamInviteHandled</c> reaches <c>session_events</c>; this does, against the server's own SQLite database.
/// </summary>
[Collection("net")]
public sealed class TeamInviteLiveTests(ITestOutputHelper output)
{
    private static readonly string[] Settings =
    [
        "--X4MP:Saves:AutosaveMinutes=0",
        "--X4MP:Teams:JoinMode=Lobby",
        "--X4MP:Teams:AllowCreateInLobby=true",
    ];

    private static Id128T Key(int playerId, uint n) => new() { Hi = 0xE000_0000u | (uint)playerId, Lo = n };

    private static Task<TeamRequestResultT> ResultAsync(Peer peer, Id128T key, int timeoutMs = 10_000) =>
        peer.WaitForAsync(
            p => p.All(MsgType.TeamRequestResult, f => MessageRegistry.Default.Decode<TeamRequestResult>(f).UnPack()),
            r => r.RequestKey.Lo == key.Lo && r.RequestKey.Hi == key.Hi,
            $"the TeamRequestResult of request {key.Lo}",
            timeoutMs);

    private static List<TeamInviteT> Invites(Peer peer) =>
        peer.All(MsgType.TeamInvite, f => MessageRegistry.Default.Decode<TeamInvite>(f).UnPack());

    /// <summary>Rows of one <c>session_events</c> type, oldest first, as (player_id, data_json).</summary>
    private static List<(long? Player, string Data)> SessionEvents(string dataDir, string type)
    {
        using var db = new SqliteConnectionFactory(new PersistenceOptions { DataDir = dataDir }).Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT player_id, data_json FROM session_events WHERE type = @type ORDER BY id";
        cmd.Parameters.AddWithValue("@type", type);
        using var reader = cmd.ExecuteReader();
        var rows = new List<(long?, string)>();
        while (reader.Read())
        {
            rows.Add((reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.GetString(1)));
        }

        return rows;
    }

    private static string Action(string dataJson)
    {
        using var data = JsonDocument.Parse(dataJson);
        return data.RootElement.GetProperty("action").GetString() ?? "";
    }

    private static async Task<T> Eventually<T>(Func<T> read, Func<T, bool> ready, int timeoutMs = 10_000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var value = read();
            if (ready(value))
            {
                return value;
            }

            Assert.True(Environment.TickCount64 < until, "the session_events rows did not appear in time");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task ALeaderInvitesALobbyPlayerAndTheAuditRowsReachSessionEvents()
    {
        await using var server = await SaveServer.StartAsync(Settings);
        await using var authority = await AuthorityRig.StartAsync(server, new FakeAuthoritySaveOptions
        {
            SaveBytes = 1 << 20,
            Directory = Path.Combine(server.Dir, "fake-authority"),
        });
        await server.WaitForAsync(s => s.Phase == SessionPhase.Running, 60_000, "the session to start running");

        await using var alice = Peer.Attach(await server.ConnectAsync("Alice", Role.Client));
        await using var bob = Peer.Attach(await server.ConnectAsync("Bob", Role.Client));
        await using var cleo = Peer.Attach(await server.ConnectAsync("Cleo", Role.Client));

        // Lobby mode: every client without a team waits here, which is the only state a player can be invited out of.
        await server.WaitForAsync(
            s => s.Nodes.Count(n => n.Phase == NodePhase.AwaitingTeam) >= 3,
            20_000,
            "the three clients to wait for a team");

        var createKey = Key(alice.PlayerId, 1);
        await alice.SendAsync(MsgType.TeamCreateRequest, b => TeamCreateRequest.Pack(b, new TeamCreateRequestT
        {
            RequestKey = createKey,
            Name = "Wolves",
            ColorRgb = 0x00_44_88,
        }));
        var created = await ResultAsync(alice, createKey);
        Assert.Equal(TeamRequestStatus.Ok, created.Status);
        Assert.NotEqual(0, created.TeamId);

        // Creating the team hands Alice her own save phase; the world is where a team request is answered from.
        await alice.BringInGameAsync(s => server.WaitForAsync(s));

        var inviteKey = Key(alice.PlayerId, 2);
        await alice.SendAsync(MsgType.TeamInviteRequest, b => TeamInviteRequest.Pack(b, new TeamInviteRequestT
        {
            RequestKey = inviteKey,
            ToPlayer = (ushort)bob.PlayerId,
        }));
        var sent = await ResultAsync(alice, inviteKey);
        Assert.Equal(TeamRequestStatus.Ok, sent.Status);

        var invite = await bob.WaitForAsync(p => Invites(p), i => i.FromPlayer == alice.PlayerId, "the invite at Bob");
        Assert.Equal(created.TeamId, invite.FromTeam);
        Assert.Equal("Wolves", invite.TeamName);
        Assert.Equal((ushort)TeamModule.InviteSeconds, invite.ExpiresInS);

        var teams = server.Service<TeamModule>();
        Assert.Equal(1, teams.OpenInvites);

        // Only the invitee is told: the notice is a targeted frame, not a fan-out to the team or the session.
        Assert.Empty(Invites(alice));
        Assert.Empty(Invites(cleo));

        var respondKey = Key(bob.PlayerId, 3);
        await bob.SendAsync(MsgType.TeamInviteRespond, b => TeamInviteRespond.Pack(b, new TeamInviteRespondT
        {
            RequestKey = respondKey,
            InviteId = invite.InviteId,
            Accept = true,
        }));
        var accepted = await ResultAsync(bob, respondKey);
        Assert.Equal(TeamRequestStatus.Ok, accepted.Status);

        Assert.Equal(teams.TeamOf(alice.PlayerId), teams.TeamOf(bob.PlayerId));
        Assert.Null(teams.TeamOf(cleo.PlayerId));
        Assert.Equal(0, teams.OpenInvites);

        // A member may not invite in turn: the same check the lobby path makes, now from a real InGame node.
        await bob.BringInGameAsync(s => server.WaitForAsync(s));
        var memberKey = Key(bob.PlayerId, 4);
        await bob.SendAsync(MsgType.TeamInviteRequest, b => TeamInviteRequest.Pack(b, new TeamInviteRequestT
        {
            RequestKey = memberKey,
            ToPlayer = (ushort)cleo.PlayerId,
        }));
        var byMember = await ResultAsync(bob, memberKey);
        Assert.Equal(TeamRequestStatus.Rejected, byMember.Status);
        Assert.Equal(TeamRejectReason.NotLeader, byMember.Reason);
        Assert.Empty(Invites(cleo));

        // The audit trail the feature added: both steps left a row in the real session_events table.
        var rows = await Eventually(
            () => SessionEvents(server.Dir, nameof(X4MP.Core.Events.TeamInviteHandled)),
            r => r.Count(x => Action(x.Data) == "accepted") == 1);
        var sentRow = Assert.Single(rows, x => Action(x.Data) == "sent");
        var acceptedRow = Assert.Single(rows, x => Action(x.Data) == "accepted");
        Assert.Equal(alice.PlayerId, sentRow.Player);
        Assert.Equal(bob.PlayerId, acceptedRow.Player);

        using (var data = JsonDocument.Parse(sentRow.Data))
        {
            Assert.Equal(alice.PlayerId, data.RootElement.GetProperty("fromPlayerId").GetInt32());
            Assert.Equal(bob.PlayerId, data.RootElement.GetProperty("toPlayerId").GetInt32());
            Assert.Equal(created.TeamId, data.RootElement.GetProperty("teamId").GetInt32());
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"team invite over tcp: team={created.TeamId} sent-row={sentRow.Player} accepted-row={acceptedRow.Player} open-invites=0"));
    }
}
