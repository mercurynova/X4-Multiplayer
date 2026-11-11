using X4MP.Core.Events;
using X4MP.Core.Session;
using X4MP.Core.Teams;
using X4MP.Core.Tests.Session;
using X4MP.Proto;

namespace X4MP.Core.Tests.Teams;

/// <summary>
/// M5b: a team leader invites another player (the Talk-menu action behind ADR-046 3.1), the invitee accepts or
/// declines, and every step leaves a <see cref="TeamInviteHandled"/> on the bus so the action reaches
/// <c>session_events</c>. Player-initiated team changes had no audit row before this.
/// </summary>
public class TeamInviteTests
{
    private sealed class CapturingEvents : IEventPublisher
    {
        public List<DomainEvent> Published { get; } = [];

        public void Publish(DomainEvent domainEvent) => Published.Add(domainEvent);

        public List<TeamInviteHandled> Invites => [.. Published.OfType<TeamInviteHandled>()];
    }

    /// <summary>Two nodes in a session, the first one the leader of team 1.</summary>
    private static async Task<(TeamRig Rig, CapturingEvents Events, JoinedNode Leader, JoinedNode Guest)> TwoPlayersAsync(
        TeamOptions? options = null)
    {
        // Lobby mode with create allowed: the leader makes its own team and walks in, the guest stays in AwaitingTeam.
        // That is the only state in which a player has no team, and therefore the only state an invitation can lift.
        var rig = new TeamRig(options ?? new TeamOptions { JoinMode = TeamJoinMode.Lobby, AllowCreateInLobby = true });
        var events = new CapturingEvents();
        rig.Teams.Events = events;

        var leader = await rig.JoinAsync("Leader");
        var guest = await rig.JoinAsync("Guest");
        await rig.CreateAsync(leader, "Wolves");
        await rig.Rig.BringInGameAsync(leader);
        return (rig, events, leader, guest);
    }

    private static TeamRequestResultT LastResult(JoinedNode node, int before)
    {
        var sent = node.Connection.SentOf(MsgType.TeamRequestResult);
        Assert.True(sent.Count > before, "no TeamRequestResult was sent");
        return sent[^1].Decode<TeamRequestResult>().UnPack();
    }

    private static async Task<TeamRequestResultT> InviteAsync(TeamRig rig, JoinedNode from, int toPlayer, ulong key = 1)
    {
        int before = from.Connection.SentOf(MsgType.TeamRequestResult).Count;
        await rig.Rig.SendAsync(from, MsgType.TeamInviteRequest, TeamFrames.Invite(key, toPlayer));
        await rig.SettleAsync();
        return LastResult(from, before);
    }

    private static async Task<TeamRequestResultT> RespondAsync(TeamRig rig, JoinedNode by, uint inviteId, bool accept, ulong key = 2)
    {
        int before = by.Connection.SentOf(MsgType.TeamRequestResult).Count;
        await rig.Rig.SendAsync(by, MsgType.TeamInviteRespond, TeamFrames.InviteRespond(key, inviteId, accept));
        await rig.SettleAsync();
        return LastResult(by, before);
    }

    private static TeamInviteT NewestInvite(JoinedNode node)
    {
        var sent = node.Connection.SentOf(MsgType.TeamInvite);
        Assert.NotEmpty(sent);
        return sent[^1].Decode<TeamInvite>().UnPack();
    }

    [Fact]
    public async Task ALeaderInvitesAPlayerAndTheInviteeIsTold()
    {
        var (rig, events, leader, guest) = await TwoPlayersAsync();
        await using var _ = rig;

        var result = await InviteAsync(rig, leader, guest.PlayerId);

        Assert.Equal(TeamRequestStatus.Ok, result.Status);
        var invite = NewestInvite(guest);
        Assert.Equal(leader.PlayerId, (int)invite.FromPlayer);
        Assert.Equal(1, (int)invite.FromTeam);
        Assert.Equal("Wolves", invite.TeamName);
        Assert.Equal((ushort)TeamModule.InviteSeconds, invite.ExpiresInS);
        Assert.Equal(1, rig.Teams.OpenInvites);

        // Only the invitee hears about it: the notice is a targeted frame, like RelationProposal.
        Assert.Empty(leader.Connection.SentOf(MsgType.TeamInvite));

        var handled = Assert.Single(events.Invites);
        Assert.Equal("sent", handled.Action);
        Assert.Equal(leader.PlayerId, handled.FromPlayerId);
        Assert.Equal(guest.PlayerId, handled.ToPlayerId);
        Assert.Equal(1, handled.TeamId);
        // The inviter is the acting player, so that is the row's player_id.
        Assert.Equal(leader.PlayerId, handled.PlayerId);
    }

    [Fact]
    public async Task AnAcceptedInviteMovesThePlayerAndIsAudited()
    {
        var (rig, events, leader, guest) = await TwoPlayersAsync();
        await using var _ = rig;

        await InviteAsync(rig, leader, guest.PlayerId);
        var invite = NewestInvite(guest);
        int versionsBefore = rig.Versions.Count;

        var result = await RespondAsync(rig, guest, invite.InviteId, accept: true);

        Assert.Equal(TeamRequestStatus.Ok, result.Status);
        Assert.Equal(1, rig.Teams.TeamOf(guest.PlayerId));
        Assert.Equal(0, rig.Teams.OpenInvites);
        Assert.True(rig.Versions.Count > versionsBefore, "the membership change was not fanned out");

        var accepted = Assert.Single(events.Invites, e => e.Action == "accepted");
        Assert.Equal(guest.PlayerId, accepted.PlayerId);
        Assert.Equal(1, accepted.TeamId);

        // A second answer with the same invite id finds nothing left to act on. It has to come from a phase that
        // accepts team requests: accepting moved the guest through SyncingSave, so walk it into the world first.
        await rig.Rig.BringInGameAsync(guest);
        var again = await RespondAsync(rig, guest, invite.InviteId, accept: true, key: 3);
        Assert.Equal(TeamRequestStatus.Rejected, again.Status);
    }

    [Fact]
    public async Task ADeclinedInviteLeavesThePlayerWhereTheyWere()
    {
        var (rig, events, leader, guest) = await TwoPlayersAsync();
        await using var _ = rig;

        await InviteAsync(rig, leader, guest.PlayerId);
        var invite = NewestInvite(guest);

        var result = await RespondAsync(rig, guest, invite.InviteId, accept: false);

        Assert.Equal(TeamRequestStatus.Ok, result.Status);
        Assert.Null(rig.Teams.TeamOf(guest.PlayerId));
        Assert.Equal(0, rig.Teams.OpenInvites);
        Assert.Single(events.Invites, e => e.Action == "declined");
    }

    [Fact]
    public async Task AnInviteExpiresAfterItsWindow()
    {
        var (rig, events, leader, guest) = await TwoPlayersAsync();
        await using var _ = rig;

        await InviteAsync(rig, leader, guest.PlayerId);
        var invite = NewestInvite(guest);

        await rig.AdvanceSecondsAsync(TeamModule.InviteSeconds + 1);

        Assert.Equal(0, rig.Teams.OpenInvites);
        Assert.Single(events.Invites, e => e.Action == "expired");

        // The late answer is refused rather than silently moving the player.
        var late = await RespondAsync(rig, guest, invite.InviteId, accept: true);
        Assert.Equal(TeamRequestStatus.Rejected, late.Status);
        Assert.Null(rig.Teams.TeamOf(guest.PlayerId));
    }

    [Fact]
    public async Task OnlyALeaderCanInvite()
    {
        var (rig, events, leader, guest) = await TwoPlayersAsync();
        await using var _ = rig;

        // Accepting an invite makes the guest a plain Member, so it may not invite in turn.
        await InviteAsync(rig, leader, guest.PlayerId);
        await RespondAsync(rig, guest, NewestInvite(guest).InviteId, accept: true);
        Assert.Equal(1, rig.Teams.TeamOf(guest.PlayerId));
        await rig.Rig.BringInGameAsync(guest); // accepting moved it to SyncingSave; only in-game frames are team requests

        var third = await rig.JoinAsync("Third");

        var byMember = await InviteAsync(rig, guest, third.PlayerId, key: 21);
        Assert.Equal(TeamRequestStatus.Rejected, byMember.Status);
        Assert.Equal(TeamRejectReason.NotLeader, byMember.Reason);
        Assert.Empty(third.Connection.SentOf(MsgType.TeamInvite));

        // A player with no team at all gets the same refusal, not a crash.
        var byLoner = await InviteAsync(rig, third, leader.PlayerId, key: 22);
        Assert.Equal(TeamRequestStatus.Rejected, byLoner.Status);
        Assert.Equal(TeamRejectReason.NotPermitted, byLoner.Reason);
        Assert.DoesNotContain(events.Invites, e => e.Action == "sent" && e.FromPlayerId == third.PlayerId);
    }

    [Fact]
    public async Task ALeaderCannotInviteItsOwnMemberOrAPlayerInAnotherTeam()
    {
        var (rig, events, leader, guest) = await TwoPlayersAsync();
        await using var _ = rig;

        // Self is already in that team.
        var self = await InviteAsync(rig, leader, leader.PlayerId);
        Assert.Equal(TeamRejectReason.SameTeam, self.Reason);

        // The guest already accepted once, so a second invite is refused the same way.
        await InviteAsync(rig, leader, guest.PlayerId, key: 2); // a fresh key: key 1 above answered the self-invite
        Assert.Equal(TeamRequestStatus.Ok, (await RespondAsync(rig, guest, NewestInvite(guest).InviteId, accept: true)).Status);
        var member = await InviteAsync(rig, leader, guest.PlayerId, key: 9);
        Assert.Equal(TeamRejectReason.SameTeam, member.Reason);

        // A player the session does not know.
        var ghost = await InviteAsync(rig, leader, 4242, key: 10);
        Assert.Equal(TeamRejectReason.NotPermitted, ghost.Reason);
        Assert.Equal(1, events.Invites.Count(e => e.Action == "sent")); // only the one invite above went out
    }

    [Fact]
    public async Task ASecondInviteToTheSamePlayerReplacesTheFirst()
    {
        var (rig, events, leader, guest) = await TwoPlayersAsync();
        await using var _ = rig;

        await InviteAsync(rig, leader, guest.PlayerId, key: 11);
        var first = NewestInvite(guest);
        await InviteAsync(rig, leader, guest.PlayerId, key: 12);
        var second = NewestInvite(guest);

        Assert.NotEqual(first.InviteId, second.InviteId);
        Assert.Equal(1, rig.Teams.OpenInvites);

        // The stale id is no longer accepted.
        var stale = await RespondAsync(rig, guest, first.InviteId, accept: true, key: 13);
        Assert.Equal(TeamRequestStatus.Rejected, stale.Status);

        // The newest one still works.
        var fresh = await RespondAsync(rig, guest, second.InviteId, accept: true, key: 14);
        Assert.Equal(TeamRequestStatus.Ok, fresh.Status);
        Assert.Equal(1, rig.Teams.TeamOf(guest.PlayerId));
    }
}
