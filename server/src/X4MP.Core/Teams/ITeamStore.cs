namespace X4MP.Core.Teams;

/// <summary>
/// Persistence seam of the team module (the <c>teams</c>, <c>team_members</c> and <c>team_relations</c> tables). The
/// module keeps the truth in memory; <see cref="Save"/> must return without waiting for the database (write-behind).
/// </summary>
public interface ITeamStore
{
    /// <summary>
    /// The teams of the most recent session that has any (the previous run: sticky memberships survive a restart), or
    /// null when there are none. Called once at start, off the actor thread.
    /// </summary>
    TeamStateSnapshot? LoadLatest();

    /// <summary>
    /// Replaces what is stored for <paramref name="sessionId"/> (the <c>sessions</c> row) with this snapshot. False when the
    /// write could not be queued (the module then keeps the state dirty and tries again on a later tick).
    /// </summary>
    bool Save(long sessionId, TeamStateSnapshot snapshot);
}

/// <summary>Keeps nothing (running the server without persistence, tests).</summary>
public sealed class NullTeamStore : ITeamStore
{
    public TeamStateSnapshot? LoadLatest() => null;

    public bool Save(long sessionId, TeamStateSnapshot snapshot) => true;
}
