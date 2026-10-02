using System.Globalization;
using Microsoft.Extensions.Options;
using X4MP.Core.Session;
using X4MP.Core.Teams;
using X4MP.Persistence;
using X4MP.Proto;
using X4MP.Server.Api;

namespace X4MP.Server.Teams;

/// <summary>
/// Maps the team module's state to the admin API DTOs (REST and hub), resolving player names. The module's live state is read on the
/// session actor's thread (<see cref="State"/> and the other builders must be called there); the name lookups only use short-lived
/// database connections.
/// </summary>
internal sealed class TeamViews(
    TeamModule module, SessionActor actor, IOptionsMonitor<TeamOptions> options, SqliteAdminQueries queries, TimeProvider time)
{
    public TeamModule Module => module;

    public TeamOptions Options => options.CurrentValue;

    private string NameOf(int playerId, Dictionary<int, NodeSnapshot> nodes)
    {
        if (nodes.TryGetValue(playerId, out var node))
        {
            return node.Name;
        }

        if (module.KnownName(playerId) is { Length: > 0 } known)
        {
            return known;
        }

        return queries.FindPlayer(playerId, time.GetUtcNow())?.Name ?? "Player " + playerId.ToString(CultureInfo.InvariantCulture);
    }

    public static TeamDto Team(Team team, int memberCount) => new(
        team.Id, team.Name, team.Color, team.FactionSlot, team.LeaderPlayerId, team.Locked, team.MaxMembers, team.PasswordProtected, memberCount);

    public static TeamRelationsDto Relations(TeamRelationMatrix matrix) => new(
        matrix.Version, [.. matrix.Entries.Select(e => new TeamRelationEntryDto(e.TeamA, e.TeamB, e.Relation.ToString()))], matrix.DefaultRelation.ToString());

    public static TeamPolicyDto Policy(TeamOptions o) => new(
        o.JoinMode.ToString(), o.AutoAssign.ToString(), o.AllowCreateInLobby, o.LobbyTimeoutSeconds, o.MaxTeams, TeamOptions.MaxFactionSlots,
        o.DefaultRelation.ToString(), o.AssetPolicy.ToString(), o.AllowFriendlyFire, o.AllowAssetTransfer, o.MoveAssetsWithPlayer.ToString(),
        o.AllowSelfTeamChange, o.RelationChangePolicy.ToString());

    /// <summary>The whole picture (call on the actor thread).</summary>
    public TeamsStateDto State()
    {
        var snapshot = actor.Snapshot;
        var nodes = snapshot.Nodes.ToDictionary(n => n.PlayerId);
        var state = module.Snapshot();
        var counts = state.Members.GroupBy(m => m.TeamId).ToDictionary(g => g.Key, g => g.Count());
        var teams = state.Teams.OrderBy(t => t.Id).Select(t => Team(t, counts.GetValueOrDefault(t.Id))).ToList();
        var members = state.Members.OrderBy(m => m.PlayerId).Select(m => Member(m, nodes)).ToList();
        var unassigned = module.Unassigned().Select(u => new TeamMemberDto(
            u.PlayerId, u.Name, null, nameof(X4MP.Proto.TeamRole.Member), u.Connected, u.IsAuthority, string.Empty, null)).ToList();
        return new TeamsStateDto(teams, members, unassigned, Relations(module.Matrix), Policy(options.CurrentValue), snapshot.Phase.ToString());
    }

    private TeamMemberDto Member(TeamMembership m, Dictionary<int, NodeSnapshot> nodes)
    {
        nodes.TryGetValue(m.PlayerId, out var node);
        return new TeamMemberDto(
            m.PlayerId, NameOf(m.PlayerId, nodes), m.TeamId, m.Role.ToString(), node?.Connected ?? false,
            node is not null && (node.Roles & Role.Authority) != 0, m.AssignedBy, m.Since);
    }

    /// <summary>One member as the page shows it (call on the actor thread).</summary>
    public TeamMemberDto MemberOf(int playerId)
    {
        var nodes = actor.Snapshot.Nodes.ToDictionary(n => n.PlayerId);
        var membership = module.Snapshot().Members.FirstOrDefault(m => m.PlayerId == playerId);
        if (membership is not null)
        {
            return Member(membership, nodes);
        }

        nodes.TryGetValue(playerId, out var node);
        return new TeamMemberDto(
            playerId, NameOf(playerId, nodes), null, nameof(X4MP.Proto.TeamRole.Member), node?.Connected ?? false,
            node is not null && (node.Roles & Role.Authority) != 0, string.Empty, null);
    }

    public TeamPresetPreviewDto Preview(TeamPresetPreview preview)
    {
        var snapshot = actor.Snapshot;
        var nodes = snapshot.Nodes.ToDictionary(n => n.PlayerId);
        var counts = preview.Members.GroupBy(m => m.TeamId).ToDictionary(g => g.Key, g => g.Count());
        bool running = snapshot.Phase == SessionPhase.Running;
        return new TeamPresetPreviewDto(
            preview.Preset.ToString(), running, running && preview.PlayersMoved + preview.TeamsRemoved > 0,
            preview.Blocked == X4MP.Proto.TeamRejectReason.None ? null : preview.Blocked.ToString(), preview.BlockedDetail,
            [.. preview.Teams.OrderBy(t => t.Id).Select(t => Team(t, counts.GetValueOrDefault(t.Id)))],
            [.. preview.Members.OrderBy(m => m.PlayerId).Select(m => Member(m, nodes))],
            preview.PlayersMoved, preview.TeamsRemoved, preview.Plan.AutoAssign.ToString(), preview.Plan.Relation?.ToString());
    }
}
