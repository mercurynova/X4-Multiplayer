using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

/// <summary>
/// What a fake node knows about teams (M1-T3, the receiving half of protocol.md 14): the team table, the relation matrix and the
/// session policy, kept up to date from <c>Welcome</c>, <c>TeamTable</c>, <c>TeamRelations</c>, <c>SessionSettings</c> and
/// <c>TeamMemberChanged</c>. No I/O: feed it frames with <see cref="Handle"/>.
/// <para>
/// The fake NPC hostility follows the matrix: <see cref="IsHostile"/> says whether ships of one team shoot at the other, the way the
/// authority's AI would once the mod has set the faction relations. A fake authority also records the
/// <c>ReassignPlayerAssets</c> it was told to apply (<see cref="Reassigns"/>).
/// </para>
/// </summary>
public sealed class FakeTeamState
{
    private readonly Dictionary<int, TeamInfoT> _teams = [];
    private readonly Dictionary<(int A, int B), TeamRelation> _relations = [];
    private TeamRelation _default = TeamRelation.Neutral;

    public int PlayerId { get; private set; }

    /// <summary>The node's own team (0 until it has one); follows <c>TeamMemberChanged</c> for itself.</summary>
    public int OwnTeam { get; private set; }

    public TeamRole OwnRole { get; private set; }

    public IReadOnlyCollection<TeamInfoT> Teams => _teams.Values;

    public TeamInfoT? Team(int teamId) => _teams.GetValueOrDefault(teamId);

    public uint TableVersion { get; private set; }

    public uint RelationsVersion { get; private set; }

    public uint SettingsVersion { get; private set; }

    public TeamPolicyT? Policy { get; private set; }

    public EconomySettingsT? Economy { get; private set; }

    public TeamRelation DefaultRelation => _default;

    public long TableUpdates { get; private set; }

    public long RelationUpdates { get; private set; }

    public long SettingsUpdates { get; private set; }

    public List<TeamMemberChangedT> MemberChanges { get; } = [];

    public List<RelationProposalT> Proposals { get; } = [];

    public List<TeamRequestResultT> Results { get; } = [];

    /// <summary>The asset re-owns a fake authority was asked to do.</summary>
    public List<ReassignPlayerAssetsT> Reassigns { get; } = [];

    /// <summary>A version that went backwards (a message that should not exist): a protocol-level violation.</summary>
    public long VersionRegressions { get; private set; }

    /// <summary>Called after a relation pair changed (a, b, new relation).</summary>
    public event Action<int, int, TeamRelation>? RelationChanged;

    /// <summary>The relation between two teams as this node holds it (the same team is Allied).</summary>
    public TeamRelation Relation(int teamA, int teamB)
    {
        if (teamA == teamB)
            return TeamRelation.Allied;
        return _relations.TryGetValue(Key(teamA, teamB), out var relation) ? relation : _default;
    }

    /// <summary>Fake NPC behaviour: ships of <paramref name="teamA"/> open fire on <paramref name="teamB"/> (and the other way round) while the relation is Hostile.</summary>
    public bool IsHostile(int teamA, int teamB) => Relation(teamA, teamB) == TeamRelation.Hostile;

    private static (int A, int B) Key(int a, int b) => a < b ? (a, b) : (b, a);

    /// <summary>Takes the team state of the <c>Welcome</c> (full table, full relations, settings, own team).</summary>
    public void ApplyWelcome(WelcomeT welcome)
    {
        ArgumentNullException.ThrowIfNull(welcome);
        PlayerId = welcome.PlayerId;
        OwnTeam = welcome.TeamId;
        OwnRole = welcome.TeamRole;
        if (welcome.Teams is not null)
            ApplyTable(welcome.Teams);
        if (welcome.Relations is not null)
            ApplyRelations(welcome.Relations);
        if (welcome.Settings is not null)
            ApplySettings(welcome.Settings);
    }

    /// <summary>Processes one frame from the server (team messages only; anything else is ignored). Returns what the node sends back (nothing).</summary>
    public IReadOnlyList<OutMessage> Handle(Frame frame)
    {
        switch (frame.Type)
        {
            case MsgType.TeamTable:
                ApplyTable(MessageRegistry.Default.Decode<TeamTable>(frame).UnPack());
                break;
            case MsgType.TeamRelations:
                ApplyRelations(MessageRegistry.Default.Decode<TeamRelations>(frame).UnPack());
                break;
            case MsgType.SessionSettings:
                ApplySettings(MessageRegistry.Default.Decode<SessionSettings>(frame).UnPack());
                break;
            case MsgType.TeamMemberChanged:
                var change = MessageRegistry.Default.Decode<TeamMemberChanged>(frame).UnPack();
                MemberChanges.Add(change);
                if (change.PlayerId == PlayerId && PlayerId != 0)
                {
                    OwnTeam = change.ToTeam;
                    OwnRole = change.Role;
                }

                break;
            case MsgType.RelationProposal:
                Proposals.Add(MessageRegistry.Default.Decode<RelationProposal>(frame).UnPack());
                break;
            case MsgType.TeamRequestResult:
                Results.Add(MessageRegistry.Default.Decode<TeamRequestResult>(frame).UnPack());
                break;
            case MsgType.ReassignPlayerAssets:
                Reassigns.Add(MessageRegistry.Default.Decode<ReassignPlayerAssets>(frame).UnPack());
                break;
        }

        return [];
    }

    private void ApplyTable(TeamTableT table)
    {
        if (table.Version < TableVersion)
            VersionRegressions++;
        TableVersion = Math.Max(TableVersion, table.Version);
        TableUpdates++;
        if (table.Full)
            _teams.Clear();
        foreach (var team in table.Teams ?? [])
            _teams[team.TeamId] = team;
        foreach (ushort id in table.Removed ?? [])
            _teams.Remove(id);
    }

    private void ApplyRelations(TeamRelationsT relations)
    {
        if (relations.Version < RelationsVersion)
            VersionRegressions++;
        RelationsVersion = Math.Max(RelationsVersion, relations.Version);
        RelationUpdates++;

        var before = relations.Full ? SnapshotPairs() : null;
        if (relations.Full)
            _relations.Clear();
        _default = relations.DefaultRelation;
        foreach (var entry in relations.Entries ?? [])
        {
            var key = Key(entry.TeamA, entry.TeamB);
            _relations.TryGetValue(key, out var old);
            bool had = _relations.ContainsKey(key);
            _relations[key] = entry.Relation;
            if (!had || old != entry.Relation)
                RelationChanged?.Invoke(key.A, key.B, entry.Relation);
        }

        if (before is not null)
        {
            // A full replacement: report pairs that lost their explicit value.
            foreach (var pair in before.Keys.Where(k => !_relations.ContainsKey(k)))
                RelationChanged?.Invoke(pair.A, pair.B, _default);
        }
    }

    private Dictionary<(int A, int B), TeamRelation> SnapshotPairs() => new(_relations);

    private void ApplySettings(SessionSettingsT settings)
    {
        if (settings.Version < SettingsVersion)
            VersionRegressions++;
        SettingsVersion = Math.Max(SettingsVersion, settings.Version);
        SettingsUpdates++;
        if (settings.Team is not null)
            Policy = settings.Team;
        if (settings.Economy is not null)
            Economy = settings.Economy;
    }

    // ------------------------------------------------------------------ what a fake leader or player sends

    public static OutMessage BuildRelationChangeRequest(ulong requestKey, int otherTeam, TeamRelation relation)
    {
        var request = new RelationChangeRequestT { RequestKey = new Id128T { Lo = requestKey, Hi = 0 }, OtherTeam = (ushort)otherTeam, Relation = relation };
        return new OutMessage(MsgType.RelationChangeRequest, MessageEncoder.EncodePayload(b => RelationChangeRequest.Pack(b, request), 64));
    }

    public static OutMessage BuildTeamChangeRequest(ulong requestKey, int teamId, byte[]? password = null)
    {
        var request = new TeamChangeRequestT
        {
            RequestKey = new Id128T { Lo = requestKey, Hi = 0 },
            TeamId = (ushort)teamId,
            Password = password is null ? [] : [.. password],
        };
        return new OutMessage(MsgType.TeamChangeRequest, MessageEncoder.EncodePayload(b => TeamChangeRequest.Pack(b, request), 96));
    }
}
