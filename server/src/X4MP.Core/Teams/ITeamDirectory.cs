namespace X4MP.Core.Teams;

/// <summary>Read-only view of team membership and relations, owned by the Teams module (M1-T1).</summary>
public interface ITeamDirectory
{
    /// <summary>All teams in the session, ordered by team id.</summary>
    IReadOnlyList<TeamInfo> Teams { get; }

    /// <summary>The team of a player, or null while unassigned.</summary>
    int? TeamOf(int playerId);

    /// <summary>Current members of a team (player ids).</summary>
    IReadOnlyList<int> MembersOf(int teamId);

    /// <summary>Relation between two teams; Allied for the same team.</summary>
    TeamRelation RelationBetween(int teamA, int teamB);

    /// <summary>Raised on the actor thread after any team, membership or relation change.</summary>
    event Action<TeamDirectoryChanged>? Changed;
}

/// <summary>A team and the X4 faction slot (1..8, faction x4mp_team_N) it maps to.</summary>
public sealed record TeamInfo(int TeamId, string Name, int FactionSlot);

public enum TeamRelation
{
    Allied,
    Neutral,
    Hostile,
}

/// <summary>Monotonic version of the team directory after a change.</summary>
public sealed record TeamDirectoryChanged(int Version);
