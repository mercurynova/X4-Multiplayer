using X4MP.Core.Session;
using X4MP.Proto;

namespace X4MP.Core.Teams;

/// <summary>A player who has no team yet and waits for one (AdminAssign, or a Lobby choice not made).</summary>
public sealed record UnassignedPlayer(int PlayerId, string Name, bool IsAuthority, bool Connected);

public sealed partial class TeamModule
{
    // The admin side of the module (M1-T1; REST and the GUI come with M1-T5). Every method runs on the actor thread, one
    // call at a time, so it sees and changes a consistent state.

    private Task<T> OnActor<T>(Func<T> work) =>
        _driver is { } driver ? driver.CallAsync(work) : Task.FromResult(work());

    /// <summary>Creates a team (lowest free slot unless <paramref name="slot"/> is given). <paramref name="password"/> makes it a protected lobby team.</summary>
    public Task<TeamResult<Team>> CreateTeamAsync(
        string name, string? color = null, int? slot = null, bool locked = false, int? maxMembers = null, string? password = null) =>
        OnActor(() =>
        {
            var result = _registry.CreateTeam(
                name, _time.GetUtcNow(), Opt.MaxTeams, color, slot, locked, maxMembers,
                string.IsNullOrEmpty(password) ? null : TeamRules.HashPassword(password));
            if (result.Ok)
            {
                AfterChange();
            }

            return result;
        });

    /// <summary>Changes name, colour, lock, member limit, leader or slot. <paramref name="password"/>: null keeps it, empty clears it.</summary>
    public Task<TeamResult<Team>> UpdateTeamAsync(int teamId, TeamRegistry.TeamPatch patch, string? password = null) =>
        OnActor(() =>
        {
            if (password is not null)
            {
                patch = patch with
                {
                    PasswordHash = password.Length == 0 ? null : TeamRules.HashPassword(password),
                    ClearPassword = password.Length == 0,
                };
            }

            var result = _registry.UpdateTeam(teamId, patch);
            if (result.Ok)
            {
                AfterChange();
            }

            return result;
        });

    public Task<TeamResult> DeleteTeamAsync(int teamId, int? moveMembersTo = null) =>
        OnActor(() =>
        {
            foreach (int member in _registry.MembersOf(teamId))
            {
                if (AuthorityMoveBlocked(member, moveMembersTo) is { } blocked)
                {
                    return new TeamResult(blocked, AuthorityMoveDetail);
                }
            }

            var result = _registry.DeleteTeam(teamId, moveMembersTo);
            if (result.Ok)
            {
                AfterChange();
            }

            return result;
        });

    /// <summary>
    /// Puts a player in a team now, whatever its lock, limit or password (an admin decides). A node waiting in
    /// <c>AwaitingTeam</c> moves on to <c>SyncingSave</c> (and a detached one does when it resumes). Moving a player who is
    /// already in the game fans out (TeamMemberChanged, table delta, ReassignPlayerAssets to the authority, a resync). The
    /// authority's own player cannot move while the session is Running (<c>SessionRunningRestricted</c>; the REST layer maps it to 409).
    /// </summary>
    public Task<TeamResult<TeamMembership>> AssignPlayerAsync(int playerId, int teamId, TeamRole role = TeamRole.Member, string assignedBy = "admin") =>
        OnActor(() =>
        {
            if (AuthorityMoveBlocked(playerId, teamId) is { } blocked)
            {
                return TeamResults.Fail<TeamMembership>(blocked, AuthorityMoveDetail);
            }

            var result = _registry.Assign(playerId, teamId, _time.GetUtcNow(), assignedBy, role);
            if (result.Ok)
            {
                string name = _names.GetValueOrDefault(playerId, string.Empty);
                LogAssigned(playerId, name, teamId, assignedBy);
                AfterChange();
            }

            return result;
        });

    /// <summary>Removes a player's membership (they become unassigned).</summary>
    public Task<bool> UnassignPlayerAsync(int playerId) =>
        OnActor(() =>
        {
            if (AuthorityMoveBlocked(playerId, null) is not null)
            {
                return false;
            }

            bool removed = _registry.Unassign(playerId);
            if (removed)
            {
                AfterChange();
            }

            return removed;
        });

    public Task<TeamResult> SetRelationAsync(int teamA, int teamB, TeamRelation relation) =>
        OnActor(() =>
        {
            var result = _registry.SetRelation(teamA, teamB, relation);
            if (result.Ok)
            {
                AfterChange();
            }

            return result;
        });

    /// <summary>
    /// Applies a preset atomically: replaces the team table, the memberships and the matrix. Everyone the session knows (all
    /// members and every connected player node) is placed. The result carries the settings the preset goes with
    /// (<see cref="TeamPresetPlan.AutoAssign"/>); the caller sets those through the settings service.
    /// </summary>
    public Task<TeamResult<TeamPresetPlan>> ApplyPresetAsync(TeamPreset preset, string assignedBy = "preset") =>
        OnActor(() =>
        {
            var players = new Dictionary<int, TeamPlayer>();
            foreach (var m in _registry.Members)
            {
                players[m.PlayerId] = new TeamPlayer(m.PlayerId, _names.GetValueOrDefault(m.PlayerId, $"Player {m.PlayerId}"));
            }

            foreach (var node in _nodes.Values.Where(n => (n.Roles & (Role.Authority | Role.Client)) != 0))
            {
                players[node.PlayerId] = new TeamPlayer(node.PlayerId, node.Name);
            }

            var result = _registry.ApplyPreset(preset, players.Values, _time.GetUtcNow(), assignedBy);
            if (result.Ok)
            {
                AfterChange();
            }

            return result;
        });

    /// <summary>The connected players still waiting for a team (the GUI's "Unassigned" list).</summary>
    public Task<IReadOnlyList<UnassignedPlayer>> GetUnassignedAsync() =>
        OnActor<IReadOnlyList<UnassignedPlayer>>(() =>
        [
            .. _nodes.Values
                .Where(n => (n.Roles & (Role.Authority | Role.Client)) != 0 && _registry.MembershipOf(n.PlayerId) is null)
                .OrderBy(n => n.PlayerId)
                .Select(n => new UnassignedPlayer(n.PlayerId, n.Name, n.IsAuthority, n.IsAttached)),
        ]);
}
