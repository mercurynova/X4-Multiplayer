using X4MP.Core.Events;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Teams;

/// <summary>
/// M5b: a player invites another player to the inviter's team from the Talk menu (ADR-046 3.1). Before this the only
/// ways into a team were the lobby (<c>TeamChoice</c>), a self move (<c>TeamChangeRequest</c>) and the admin API, so a
/// player could not ask anyone. The invite is leader-only and answered by the invitee; the accepted path reuses
/// <see cref="TeamRegistry.Assign"/> plus the normal <c>AfterChange</c> fan-out, so an invited player joins exactly like a
/// lobby joiner. Every step publishes <see cref="TeamInviteHandled"/>, which the session sink turns into a
/// <c>session_events</c> row: player-initiated team changes had no audit trail at all before this file.
/// </summary>
public sealed partial class TeamModule
{
    /// <summary>How long an invitation stays open. The inviter can send another one after it expires.</summary>
    public const int InviteSeconds = 120;

    private sealed record Invite(uint Id, int FromPlayer, int ToPlayer, int TeamId, long ExpiresAt);

    private readonly Dictionary<uint, Invite> _invites = [];

    private uint _nextInviteId = 1;

    /// <summary>The event bus, set by the composition root (like <see cref="ResyncPlayer"/>). Null in tests that do not care.</summary>
    public IEventPublisher? Events { get; set; }

    /// <summary>The <c>sessions.id</c> the audit rows belong to, or null before the session row exists.</summary>
    private long? AuditSession => _driver is { StoreSessionId: > 0 } driver ? driver.StoreSessionId : null;

    /// <summary>Number of invitations still waiting for an answer (diagnostics and tests).</summary>
    public int OpenInvites => _invites.Count;

    private bool HandleInviteRequest(SessionNode node, InboundFrame frame)
    {
        TeamInviteRequestT request;
        try
        {
            request = MessageRegistry.Default.Decode<TeamInviteRequest>(frame.Frame).UnPack();
        }
        catch (ProtocolViolation)
        {
            return true;
        }

        ResolveLive(node, request.RequestKey, lobbyToo: true, decide: () =>
        {
            if (_registry.MembershipOf(node.PlayerId) is not { } membership)
            {
                return new Decision(Reject(TeamRejectReason.NotPermitted, "you are not in a team"));
            }

            if (membership.Role != TeamRole.Leader)
            {
                return new Decision(Reject(TeamRejectReason.NotLeader, "only a team leader can invite"));
            }

            var toPlayer = (int)request.ToPlayer;
            if (toPlayer == node.PlayerId)
            {
                return new Decision(Reject(TeamRejectReason.SameTeam, "you are already in that team"));
            }

            // The target must exist before anything is said about it; whether it is reachable is checked last, so a
            // member who is still catching up is told "already in your team" rather than "not in the session".
            if (!_nodes.TryGetValue(toPlayer, out var target))
            {
                return new Decision(Reject(TeamRejectReason.NotPermitted, "that player is not in the session"));
            }

            var current = _registry.TeamOf(toPlayer);
            if (current == membership.TeamId)
            {
                return new Decision(Reject(TeamRejectReason.SameTeam, "that player is already in your team"));
            }

            if (current is not null)
            {
                return new Decision(Reject(TeamRejectReason.NotPermitted, "that player is in another team"));
            }

            if (!IsLive(target))
            {
                return new Decision(Reject(TeamRejectReason.NotPermitted, "that player is not in the session"));
            }

            var check = _registry.CheckJoin(membership.TeamId, toPlayer);
            if (!check.Ok)
            {
                return new Decision(Reject(check.Reason, check.Detail));
            }

            if (_registry.Find(membership.TeamId) is not { } team)
            {
                return new Decision(Reject(TeamRejectReason.UnknownTeam, "no such team"));
            }

            // One open invitation per pair: a second request replaces the first, so the invitee never sees two.
            foreach (var (id, stale) in _invites.ToArray())
            {
                if (stale.FromPlayer == node.PlayerId && stale.ToPlayer == toPlayer)
                {
                    _invites.Remove(id);
                }
            }

            var invite = new Invite(_nextInviteId++, node.PlayerId, toPlayer, team.Id, After(_time.GetTimestamp(), InviteSeconds));
            _invites[invite.Id] = invite;

            return new Decision(Accept(team.Id), () =>
            {
                SendInvite(target, invite, team.Name);
                Publish(invite, "sent");
            });
        });

        return true;
    }

    private bool HandleInviteRespond(SessionNode node, InboundFrame frame)
    {
        TeamInviteRespondT request;
        try
        {
            request = MessageRegistry.Default.Decode<TeamInviteRespond>(frame.Frame).UnPack();
        }
        catch (ProtocolViolation)
        {
            return true;
        }

        ResolveLive(node, request.RequestKey, lobbyToo: true, decide: () =>
        {
            if (!_invites.TryGetValue(request.InviteId, out var invite))
            {
                return new Decision(Reject(TeamRejectReason.NotPermitted, "that invitation is no longer open"));
            }

            if (invite.ToPlayer != node.PlayerId)
            {
                return new Decision(Reject(TeamRejectReason.NotPermitted, "that invitation is not yours"));
            }

            if (invite.ExpiresAt <= _time.GetTimestamp())
            {
                _invites.Remove(invite.Id);
                return new Decision(Reject(TeamRejectReason.NotPermitted, "that invitation expired"));
            }

            if (!request.Accept)
            {
                _invites.Remove(invite.Id);
                return new Decision(Accept(invite.TeamId), () => Publish(invite, "declined"));
            }

            var check = _registry.CheckJoin(invite.TeamId, node.PlayerId);
            if (!check.Ok)
            {
                _invites.Remove(invite.Id);
                return new Decision(Reject(check.Reason, check.Detail));
            }

            var assigned = _registry.Assign(node.PlayerId, invite.TeamId, _time.GetUtcNow(), "invite");
            if (!assigned.Ok)
            {
                _invites.Remove(invite.Id);
                return new Decision(Reject(assigned.Reason, assigned.Detail));
            }

            _invites.Remove(invite.Id);

            return new Decision(Accept(invite.TeamId), () =>
            {
                // also fires TeamAssigned for the node (the answer goes out before the phase changes)
                AfterChange();
                Publish(invite, "accepted");
            });
        });

        return true;
    }

    /// <summary>Drops invitations nobody answered in time and puts them in the session record.</summary>
    private void ExpireInvites(long timestamp)
    {
        if (_invites.Count == 0)
        {
            return;
        }

        foreach (var (id, invite) in _invites.ToArray())
        {
            if (invite.ExpiresAt <= timestamp)
            {
                _invites.Remove(id);
                Publish(invite, "expired");
            }
        }
    }

    private static void SendInvite(SessionNode invitee, Invite invite, string teamName)
    {
        var message = new TeamInviteT
        {
            InviteId = invite.Id,
            FromPlayer = (ushort)invite.FromPlayer,
            FromTeam = (ushort)invite.TeamId,
            TeamName = teamName,
            ExpiresInS = InviteSeconds,
        };
        var frame = ControlFrames.Encode(MsgType.TeamInvite, fbb => TeamInvite.Pack(fbb, message).Value, 96);
        try
        {
            invitee.Connection?.TrySend(frame);
        }
        finally
        {
            frame.Release();
        }
    }

    private void Publish(Invite invite, string action) =>
        Events?.Publish(new TeamInviteHandled(
            _time.GetUtcNow(),
            AuditSession,
            action == "accepted" ? invite.ToPlayer : invite.FromPlayer,
            invite.FromPlayer,
            invite.ToPlayer,
            invite.TeamId,
            action));
}
