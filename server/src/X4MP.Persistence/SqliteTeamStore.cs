using System.Globalization;
using Dapper;
using X4MP.Core.Teams;
using TeamRole = X4MP.Proto.TeamRole;

namespace X4MP.Persistence;

/// <summary>
/// SQLite implementation of <see cref="ITeamStore"/> over the tables of migration 0003 (<c>teams</c>, <c>team_members</c>,
/// <c>team_relations</c>). <see cref="Save"/> queues one transaction through the write-behind <see cref="PersistenceWriter"/>
/// that replaces everything stored for the session, so the writes stay in order with the session and player rows. A full
/// queue drops the write (the next change saves a complete snapshot again). <see cref="LoadLatest"/> reads synchronously at start.
/// </summary>
public sealed class SqliteTeamStore(SqliteConnectionFactory factory, PersistenceWriter writer) : ITeamStore
{
    private static string Stamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public bool Save(long sessionId, TeamStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var now = Stamp(DateTimeOffset.UtcNow);
        var teams = snapshot.Teams.ToArray();
        var members = snapshot.Members.ToArray();
        var relations = snapshot.Relations.ToArray();
        return writer.TryEnqueue(
            (connection, transaction) =>
            {
                connection.Execute("DELETE FROM team_relations WHERE session_id = @sessionId", new { sessionId }, transaction);
                connection.Execute("DELETE FROM team_members WHERE session_id = @sessionId", new { sessionId }, transaction);
                connection.Execute("DELETE FROM teams WHERE session_id = @sessionId", new { sessionId }, transaction);
                foreach (var t in teams)
                {
                    connection.Execute(
                        """
                        INSERT INTO teams (session_id, id, name, color, faction_slot, leader_player_id, locked, max_members, join_pw_hash, created_at)
                        VALUES (@sessionId, @id, @name, @color, @slot, @leader, @locked, @max, @hash, @created)
                        """,
                        new { sessionId, id = t.Id, name = t.Name, color = t.Color, slot = t.FactionSlot, leader = t.LeaderPlayerId, locked = t.Locked ? 1 : 0, max = t.MaxMembers, hash = t.JoinPasswordHash, created = Stamp(t.CreatedAt) },
                        transaction);
                }

                foreach (var m in members)
                {
                    connection.Execute(
                        "INSERT INTO team_members (session_id, player_id, team_id, role, since, assigned_by) VALUES (@sessionId, @player, @team, @role, @since, @by)",
                        new { sessionId, player = m.PlayerId, team = m.TeamId, role = m.Role == TeamRole.Leader ? "leader" : "member", since = Stamp(m.Since), by = m.AssignedBy },
                        transaction);
                }

                foreach (var (a, b, relation) in relations)
                {
                    connection.Execute(
                        "INSERT INTO team_relations (session_id, team_a, team_b, relation, updated_at) VALUES (@sessionId, @a, @b, @relation, @now)",
                        new { sessionId, a, b, relation = relation switch { TeamRelation.Allied => 1, TeamRelation.Hostile => -1, _ => 0 }, now },
                        transaction);
                }
            });
    }

    public TeamStateSnapshot? LoadLatest()
    {
        using var connection = factory.Open();
        long? session = connection.ExecuteScalar<long?>("SELECT MAX(session_id) FROM teams");
        if (session is not { } id)
        {
            return null;
        }

        var teams = connection.Query<TeamRow>(
            "SELECT id AS Id, name AS Name, color AS Color, faction_slot AS FactionSlot, leader_player_id AS Leader, locked AS Locked, max_members AS MaxMembers, join_pw_hash AS Hash, created_at AS CreatedAt FROM teams WHERE session_id = @id AND deleted_at IS NULL ORDER BY id",
            new { id })
            .Select(r => new Team(
                (int)r.Id, r.Name, r.Color, (int)r.FactionSlot, r.Leader is { } l ? (int)l : null, r.Locked != 0,
                r.MaxMembers is { } m ? (int)m : null, r.Hash, Parse(r.CreatedAt)))
            .ToList();
        var members = connection.Query<MemberRow>(
            "SELECT player_id AS PlayerId, team_id AS TeamId, role AS Role, since AS Since, assigned_by AS AssignedBy FROM team_members WHERE session_id = @id ORDER BY player_id",
            new { id })
            .Select(r => new TeamMembership((int)r.PlayerId, (int)r.TeamId, r.Role == "leader" ? TeamRole.Leader : TeamRole.Member, Parse(r.Since), r.AssignedBy))
            .ToList();
        var relations = connection.Query<RelationRow>(
            "SELECT team_a AS A, team_b AS B, relation AS Relation FROM team_relations WHERE session_id = @id ORDER BY team_a, team_b",
            new { id })
            .Select(r => ((int)r.A, (int)r.B, r.Relation > 0 ? TeamRelation.Allied : r.Relation < 0 ? TeamRelation.Hostile : TeamRelation.Neutral))
            .ToList();
        return new TeamStateSnapshot(TeamRelation.Neutral, teams, members, relations);
    }

    private sealed class TeamRow
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Color { get; set; } = string.Empty;

        public long FactionSlot { get; set; }

        public long? Leader { get; set; }

        public long Locked { get; set; }

        public long? MaxMembers { get; set; }

        public byte[]? Hash { get; set; }

        public string CreatedAt { get; set; } = string.Empty;
    }

    private sealed class MemberRow
    {
        public long PlayerId { get; set; }

        public long TeamId { get; set; }

        public string Role { get; set; } = string.Empty;

        public string Since { get; set; } = string.Empty;

        public string AssignedBy { get; set; } = string.Empty;
    }

    private sealed class RelationRow
    {
        public long A { get; set; }

        public long B { get; set; }

        public long Relation { get; set; }
    }
}
