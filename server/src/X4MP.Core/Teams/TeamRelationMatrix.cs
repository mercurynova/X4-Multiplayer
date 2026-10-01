namespace X4MP.Core.Teams;

/// <summary>
/// Symmetric relation matrix between teams (server-design 2.13). Immutable and versioned: every change returns a new
/// matrix with a higher <see cref="Version"/>. A pair nobody set explicitly has <see cref="DefaultRelation"/>; a team
/// is always <see cref="TeamRelation.Allied"/> with itself. Only explicitly set pairs are stored, as they were set (a
/// later change of the default does not rewrite them).
/// </summary>
public sealed class TeamRelationMatrix
{
    private readonly Dictionary<(int A, int B), TeamRelation> _entries;

    public TeamRelationMatrix(TeamRelation defaultRelation = TeamRelation.Neutral)
        : this(defaultRelation, 0, [])
    {
    }

    private TeamRelationMatrix(TeamRelation defaultRelation, int version, Dictionary<(int A, int B), TeamRelation> entries)
    {
        DefaultRelation = defaultRelation;
        Version = version;
        _entries = entries;
    }

    /// <summary>Starts at 0 and goes up by one with every change that returns a new matrix.</summary>
    public int Version { get; }

    public TeamRelation DefaultRelation { get; }

    /// <summary>The explicitly set pairs, <c>(a, b)</c> with <c>a &lt; b</c>, ordered.</summary>
    public IReadOnlyList<(int TeamA, int TeamB, TeamRelation Relation)> Entries =>
        [.. _entries.OrderBy(e => e.Key.A).ThenBy(e => e.Key.B).Select(e => (e.Key.A, e.Key.B, e.Value))];

    private static (int A, int B) Key(int a, int b) => a < b ? (a, b) : (b, a);

    /// <summary>Symmetric: <c>Get(a, b) == Get(b, a)</c>. Same team: Allied.</summary>
    public TeamRelation Get(int teamA, int teamB)
    {
        if (teamA == teamB)
        {
            return TeamRelation.Allied;
        }

        return _entries.TryGetValue(Key(teamA, teamB), out var relation) ? relation : DefaultRelation;
    }

    /// <summary>
    /// A matrix with the pair set. Returns <c>this</c> (same version) when the pair already has that explicit value;
    /// a team cannot be related to itself.
    /// </summary>
    public TeamRelationMatrix With(int teamA, int teamB, TeamRelation relation)
    {
        if (teamA == teamB)
        {
            throw new ArgumentException("A team is always allied with itself.", nameof(teamB));
        }

        var key = Key(teamA, teamB);
        if (_entries.TryGetValue(key, out var current) && current == relation)
        {
            return this;
        }

        var next = new Dictionary<(int A, int B), TeamRelation>(_entries) { [key] = relation };
        return new TeamRelationMatrix(DefaultRelation, Version + 1, next);
    }

    /// <summary>A matrix without any pair involving <paramref name="teamId"/> (the team was deleted).</summary>
    public TeamRelationMatrix Without(int teamId)
    {
        var next = _entries.Where(e => e.Key.A != teamId && e.Key.B != teamId).ToDictionary(e => e.Key, e => e.Value);
        return next.Count == _entries.Count ? this : new TeamRelationMatrix(DefaultRelation, Version + 1, next);
    }

    /// <summary>A matrix with another default (the version goes up when it differs).</summary>
    public TeamRelationMatrix WithDefault(TeamRelation defaultRelation) =>
        defaultRelation == DefaultRelation ? this : new TeamRelationMatrix(defaultRelation, Version + 1, new(_entries));

    /// <summary>
    /// A matrix holding exactly <paramref name="relation"/> for every pair of <paramref name="teams"/>, replacing everything
    /// (what a preset does). The version goes up by one, so clients see it as one change.
    /// </summary>
    public TeamRelationMatrix Reset(IEnumerable<int> teams, TeamRelation? relation, TeamRelation? defaultRelation = null)
    {
        var ids = teams.Distinct().Order().ToArray();
        var entries = new Dictionary<(int A, int B), TeamRelation>();
        if (relation is { } r)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                for (int j = i + 1; j < ids.Length; j++)
                {
                    entries[(ids[i], ids[j])] = r;
                }
            }
        }

        return new TeamRelationMatrix(defaultRelation ?? DefaultRelation, Version + 1, entries);
    }

    /// <summary>Rebuilds a stored matrix (restart): explicit pairs and the version it had.</summary>
    public static TeamRelationMatrix Restore(TeamRelation defaultRelation, int version, IEnumerable<(int TeamA, int TeamB, TeamRelation Relation)> entries)
    {
        var map = new Dictionary<(int A, int B), TeamRelation>();
        foreach (var (a, b, relation) in entries)
        {
            if (a != b)
            {
                map[Key(a, b)] = relation;
            }
        }

        return new TeamRelationMatrix(defaultRelation, version, map);
    }
}
