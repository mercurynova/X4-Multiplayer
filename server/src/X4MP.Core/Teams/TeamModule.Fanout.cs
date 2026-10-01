using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Core.Settings;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Teams;

public sealed partial class TeamModule
{
    // M1-T3: pushing team state to the nodes (TeamMemberChanged, TeamTable and TeamRelations deltas, SessionSettings),
    // the mid-session requests (RelationChangeRequest with the mutual-ally proposals, TeamChangeRequest) and the asset
    // re-owning when a player moves (ReassignPlayerAssets to the authority, a resync for the moved client).
    // Everything here runs on the actor thread.

    /// <summary>How long a raise toward a better relation waits for the other leader's matching request (protocol.md 14.5).</summary>
    public const int ProposalSeconds = 120;

    /// <summary>A leader's open request to improve a relation, waiting for the other leader.</summary>
    private sealed record Proposal(
        int FromTeam, int ToTeam, TeamRelation Relation, int ProposerPlayer, Id128T RequestKey, long Deadline);

    /// <summary>The player a join is admitting right now: its Welcome carries the state, so nothing is pushed to it.</summary>
    private int _admitting = -1;

    private SessionPhase _sessionPhase = SessionPhase.Idle;
    private uint _settingsVersion = 1;
    private string _policyKey = string.Empty;

    // What the nodes were last told (the baseline of the next delta).
    private Dictionary<int, string> _lastTeamSignatures = [];
    private Dictionary<int, (int TeamId, TeamRole Role)> _lastMembers = [];
    private Dictionary<(int A, int B), TeamRelation> _lastRelations = [];
    private TeamRelation _lastDefault;

    /// <summary>Per node: the versions of the team table, the relations and the settings it holds (from its Welcome or the last push).</summary>
    private readonly Dictionary<int, (int Table, int Relations, uint Settings)> _sent = [];

    private readonly Dictionary<(int From, int To), Proposal> _proposals = [];
    private readonly List<ReassignPlayerAssetsT> _pendingReassign = [];

    /// <summary>
    /// The economy half of <c>SessionSettings</c> (the economy module owns it). Set it when the economy settings change; it
    /// goes into every later <c>Welcome</c> and <c>SessionSettings</c> push. Null until the economy supplies it.
    /// </summary>
    public EconomySettingsT? EconomySettings { get; set; }

    /// <summary>
    /// Asks replication to deliver a moved player's view again (the host points it at <c>ReplicationModule.Resync</c>). Called
    /// on the actor thread with the player id; the result is ignored.
    /// </summary>
    public Func<int, bool>? ResyncPlayer { get; set; }

    /// <summary>Teams waiting for the other leader: open proposals (for the GUI and tests).</summary>
    public int OpenProposals => _proposals.Count;

    // ------------------------------------------------------------------ baselines

    private void CaptureBaseline()
    {
        _lastTeamSignatures = SignaturesOf(BuildTable());
        _lastMembers = _registry.Members.ToDictionary(m => m.PlayerId, m => (m.TeamId, m.Role));
        _lastRelations = RelationsOf(_registry.Matrix);
        _lastDefault = _registry.Matrix.DefaultRelation;
        _policyKey = PolicyKey(BuildPolicy(Opt)) + "|";
    }

    private static Dictionary<(int A, int B), TeamRelation> RelationsOf(TeamRelationMatrix matrix) =>
        matrix.Entries.ToDictionary(e => (e.TeamA < e.TeamB ? e.TeamA : e.TeamB, e.TeamA < e.TeamB ? e.TeamB : e.TeamA), e => e.Relation);

    private static Dictionary<int, string> SignaturesOf(TeamTableT table) =>
        table.Teams.ToDictionary(
            t => (int)t.TeamId,
            // Presence (Online) is left out on purpose: the roster carries it, and a flapping connection must not churn the table.
            t => $"{t.Name}|{t.ColorRgb}|{t.FactionSlot}|{t.LeaderPlayer}|{t.Locked}|{t.MaxMembers}|{t.PasswordProtected}|" +
                 string.Join(',', t.Members.Select(m => $"{m.PlayerId}:{m.Name}:{(int)m.Role}")));

    private static string PolicyKey(TeamPolicyT p) =>
        $"{(int)p.JoinMode}|{(int)p.AutoAssign}|{p.AllowCreateInLobby}|{p.AllowSelfTeamChange}|{p.MaxTeams}|{(int)p.AssetPolicy}|" +
        $"{p.AllowFriendlyFire}|{p.AllowAssetTransfer}|{(int)p.MoveAssetsWithPlayer}|{(int)p.RelationChangePolicy}";

    private static bool IsLive(SessionNode node) =>
        node.IsAttached && node.Phase is NodePhase.InGame or NodePhase.AwaitingTeam;

    private SessionNode? AuthorityNode()
    {
        foreach (var node in _nodes.Values)
        {
            if (node.IsAuthority && node.IsAttached && node.Phase == NodePhase.InGame)
            {
                return node;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ the fan-out after a change

    /// <summary>
    /// Compares the registry with what the nodes were told and sends the difference: <c>TeamMemberChanged</c> per membership
    /// change, then the <c>TeamTable</c> delta, then the <c>TeamRelations</c> delta. Nodes that are not in game yet get a full
    /// copy of anything they missed when they get there (<see cref="CatchUp"/>).
    /// </summary>
    private void FanOut()
    {
        var table = BuildTable();
        var signatures = SignaturesOf(table);
        var members = _registry.Members.ToDictionary(m => m.PlayerId, m => m);
        var matrix = _registry.Matrix;
        var relations = RelationsOf(matrix);

        // Membership changes.
        var changes = new List<TeamMemberChangedT>();
        foreach (int player in members.Keys.Union(_lastMembers.Keys).Order())
        {
            bool had = _lastMembers.TryGetValue(player, out var before);
            bool has = members.TryGetValue(player, out var after);
            if (had && has && before.TeamId == after!.TeamId && before.Role == after.Role)
            {
                continue;
            }

            changes.Add(new TeamMemberChangedT
            {
                PlayerId = (ushort)player,
                FromTeam = (ushort)(had ? before.TeamId : 0),
                ToTeam = (ushort)(after?.TeamId ?? 0),
                Role = after?.Role ?? TeamRole.Member,
                ByAdmin = after is null || after.AssignedBy.StartsWith("admin", StringComparison.Ordinal),
                TableVersion = (uint)_registry.Version,
            });
        }

        // Table delta.
        var upserts = table.Teams.Where(t => !_lastTeamSignatures.TryGetValue(t.TeamId, out string? old) || old != signatures[t.TeamId]).ToList();
        var removed = _lastTeamSignatures.Keys.Where(id => !signatures.ContainsKey(id)).Select(id => (ushort)id).ToList();

        // Relation delta: every pair whose effective relation differs from what the nodes hold.
        bool defaultChanged = matrix.DefaultRelation != _lastDefault;
        var relationEntries = new List<TeamRelationEntryT>();
        var changedPairs = new HashSet<(int, int)>();
        if (!defaultChanged)
        {
            foreach (var pair in relations.Keys.Union(_lastRelations.Keys).Order())
            {
                var now = relations.TryGetValue(pair, out var n) ? n : matrix.DefaultRelation;
                var was = _lastRelations.TryGetValue(pair, out var w) ? w : _lastDefault;
                if (now != was)
                {
                    changedPairs.Add(pair);
                    relationEntries.Add(new TeamRelationEntryT { TeamA = (ushort)pair.A, TeamB = (ushort)pair.B, Relation = ToWire(now) });
                }
            }
        }

        bool relationsChanged = defaultChanged || relationEntries.Count > 0;

        _lastTeamSignatures = signatures;
        _lastMembers = members.ToDictionary(m => m.Key, m => (m.Value.TeamId, m.Value.Role));
        _lastRelations = relations;
        _lastDefault = matrix.DefaultRelation;
        DropStaleProposals(changedPairs, defaultChanged);

        var live = _nodes.Values.Where(n => n.PlayerId != _admitting && IsLive(n)).ToList();

        foreach (var change in changes)
        {
            var frame = ControlFrames.Encode(MsgType.TeamMemberChanged, fbb => TeamMemberChanged.Pack(fbb, change).Value, 64);
            foreach (var node in live)
            {
                TrySend(node, frame);
            }

            // The subject hears of its own move even when it is not live yet (an admin released it from the lobby).
            if (_nodes.TryGetValue(change.PlayerId, out var subject) && subject.PlayerId != _admitting && subject.IsAttached && !live.Contains(subject))
            {
                TrySend(subject, frame);
            }

            frame.Release();
        }

        if (upserts.Count > 0 || removed.Count > 0)
        {
            var delta = new TeamTableT { Version = (uint)_registry.Version, Full = false, Teams = upserts, Removed = removed };
            var frame = ControlFrames.Encode(MsgType.TeamTable, fbb => TeamTable.Pack(fbb, delta).Value, 512);
            foreach (var node in live)
            {
                TrySend(node, frame);
                MarkSent(node, table: _registry.Version);
            }

            frame.Release();
        }

        if (relationsChanged)
        {
            var delta = defaultChanged
                ? BuildRelations()
                : new TeamRelationsT
                {
                    Version = (uint)matrix.Version,
                    Full = false,
                    DefaultRelation = ToWire(matrix.DefaultRelation),
                    Entries = relationEntries,
                };
            var frame = ControlFrames.Encode(MsgType.TeamRelations, fbb => TeamRelations.Pack(fbb, delta).Value, 256);
            foreach (var node in live)
            {
                TrySend(node, frame);
                MarkSent(node, relations: matrix.Version);
            }

            frame.Release();
        }

        if (changes.Count > 0)
        {
            AnnounceRoster(changes);
            foreach (var change in changes)
            {
                OnMoved(change);
            }

            FlushReassign();
        }
    }

    private void MarkSent(SessionNode node, int? table = null, int? relations = null, uint? settings = null)
    {
        _sent.TryGetValue(node.PlayerId, out var held);
        _sent[node.PlayerId] = (table ?? held.Table, relations ?? held.Relations, settings ?? held.Settings);
    }

    private static void TrySend(SessionNode node, OutboundFrame frame) => node.Connection?.TrySend(frame);

    /// <summary>The roster carries each player's team, so a membership change is also a roster upsert.</summary>
    private void AnnounceRoster(List<TeamMemberChangedT> changes)
    {
        var players = new List<PlayerInfoT>();
        foreach (var change in changes)
        {
            if (_nodes.TryGetValue(change.PlayerId, out var node) && node.PlayerId != _admitting)
            {
                players.Add(SessionActor.PlayerInfo(node));
            }
        }

        if (players.Count == 0)
        {
            return;
        }

        var frame = ControlFrames.Encode(
            MsgType.RosterUpdate,
            fbb => RosterUpdate.Pack(fbb, new RosterUpdateT { Full = false, Players = players, Removed = [] }).Value,
            256);
        foreach (var node in _nodes.Values)
        {
            if (node.Announced && node.IsAttached)
            {
                TrySend(node, frame);
            }
        }

        frame.Release();
    }

    /// <summary>A player moved from one team to another: its assets are re-owned (authority) and its view is delivered again.</summary>
    private void OnMoved(TeamMemberChangedT change)
    {
        if (change.FromTeam == 0 || change.ToTeam == 0 || change.FromTeam == change.ToTeam)
        {
            return;
        }

        var scope = Opt.MoveAssetsWithPlayer;
        if (scope != MoveAssetsScope.None)
        {
            _pendingReassign.Add(new ReassignPlayerAssetsT
            {
                PlayerId = change.PlayerId,
                FromTeam = change.FromTeam,
                ToTeam = change.ToTeam,
                Scope = scope,
            });
        }

        try
        {
            ResyncPlayer?.Invoke(change.PlayerId);
        }
        catch (Exception ex)
        {
            LogListenerFailed(ex);
        }
    }

    /// <summary>Sends the queued <c>ReassignPlayerAssets</c> to the authority once it is in game (held until then).</summary>
    private void FlushReassign()
    {
        if (_pendingReassign.Count == 0 || AuthorityNode() is not { } authority)
        {
            return;
        }

        foreach (var move in _pendingReassign)
        {
            var frame = ControlFrames.Encode(MsgType.ReassignPlayerAssets, fbb => ReassignPlayerAssets.Pack(fbb, move).Value, 64);
            TrySend(authority, frame);
            frame.Release();
        }

        _pendingReassign.Clear();
    }

    /// <summary>
    /// A node reached <see cref="NodePhase.InGame"/> (or came back): whatever changed since its <c>Welcome</c> or its last push is
    /// sent in full, so a node that was loading while the team state moved on is not left behind.
    /// </summary>
    private void CatchUp(SessionNode node)
    {
        if (!node.IsAttached || !_sent.TryGetValue(node.PlayerId, out var held))
        {
            return;
        }

        if (held.Table != _registry.Version)
        {
            var frame = ControlFrames.Encode(MsgType.TeamTable, fbb => TeamTable.Pack(fbb, BuildTable()).Value, 512);
            TrySend(node, frame);
            frame.Release();
            MarkSent(node, table: _registry.Version);
        }

        if (held.Relations != _registry.Matrix.Version)
        {
            var frame = ControlFrames.Encode(MsgType.TeamRelations, fbb => TeamRelations.Pack(fbb, BuildRelations()).Value, 256);
            TrySend(node, frame);
            frame.Release();
            MarkSent(node, relations: _registry.Matrix.Version);
        }

        if (held.Settings != _settingsVersion)
        {
            SendSettings(node);
        }

        // Proposals waiting for this node's team leader.
        long now = _time.GetTimestamp();
        foreach (var proposal in _proposals.Values)
        {
            if (_registry.Find(proposal.ToTeam)?.LeaderPlayerId == node.PlayerId)
            {
                SendProposal(node, proposal, now);
            }
        }
    }

    // ------------------------------------------------------------------ SessionSettings

    private SessionSettingsT BuildSettings() => new()
    {
        Version = _settingsVersion,
        Team = BuildPolicy(Opt),
        Economy = EconomySettings,
    };

    private void SendSettings(SessionNode node)
    {
        var frame = ControlFrames.Encode(MsgType.SessionSettings, fbb => SessionSettings.Pack(fbb, BuildSettings()).Value, 256);
        TrySend(node, frame);
        frame.Release();
        MarkSent(node, settings: _settingsVersion);
    }

    /// <summary>
    /// The pushed settings changed (<see cref="ISessionModule.OnSettingsChanged"/>): a changed team policy goes to every live node
    /// in a <c>SessionSettings</c> with a new version, and a changed default relation updates the matrix.
    /// </summary>
    public void OnSettingsChanged(SessionSettingsSnapshot settings)
    {
        var wanted = Opt.DefaultRelation;
        if (_registry.Matrix.DefaultRelation != wanted)
        {
            AfterChange(); // syncs the default, then fans the relations out
        }

        PushSettings();
    }

    /// <summary>Sends <c>SessionSettings</c> to every live node when the team policy (or <see cref="EconomySettings"/>) changed since the last push.</summary>
    public void PushSettings()
    {
        string key = PolicyKey(BuildPolicy(Opt)) + "|" + (EconomySettings is null ? string.Empty : EconomyKey(EconomySettings));
        string baseline = _policyKey;
        _policyKey = key;
        if (key == baseline)
        {
            return;
        }

        _settingsVersion++;
        foreach (var node in _nodes.Values)
        {
            if (node.PlayerId != _admitting && IsLive(node))
            {
                SendSettings(node);
            }
        }
    }

    private static string EconomyKey(EconomySettingsT e) =>
        $"{(int)e.CreditMode}|{(int)e.EffectiveMode}|{e.TeamPoolEnabled}|{(int)e.PoolWithdrawPolicy}|{e.PoolWithdrawDailyLimit}|{e.MaxTransferAmount}|" +
        $"{e.AllowAlliedTransfers}|{(int)e.DonateScope}|{(int)e.LoanScope}|{(int)e.TradeScope}|{e.TradeShipsEnabled}|{e.TradeStationsEnabled}|" +
        $"{e.MaxOpenTradesPerPlayer}|{e.MaxOpenLoansPerPlayer}";

    // ------------------------------------------------------------------ the authority's own player

    /// <summary>
    /// The authority's player may not change team while the session is Running (ADR-017: the world is live and the authority's own
    /// faction would change under it). Returns the refusal, or null.
    /// </summary>
    private TeamRejectReason? AuthorityMoveBlocked(int playerId, int? newTeam)
    {
        if (_sessionPhase != SessionPhase.Running
            || !_nodes.TryGetValue(playerId, out var node) || !node.IsAuthority
            || _registry.MembershipOf(playerId) is not { } current || current.TeamId == newTeam)
        {
            return null;
        }

        return TeamRejectReason.SessionRunningRestricted;
    }

    private const string AuthorityMoveDetail = "the authority's player cannot change team while the session is running";

    // ------------------------------------------------------------------ requests (mid-session)

    private bool HandleLiveRequest(SessionNode node, InboundFrame frame)
    {
        switch (frame.Type)
        {
            case MsgType.RelationChangeRequest:
                HandleRelationChange(node, frame);
                return true;
            case MsgType.TeamChangeRequest:
                HandleTeamChange(node, frame);
                return true;
            default:
                return false;
        }
    }

    /// <summary>What a request decided: the answer, and what to do after the answer is sent (the change and its fan-out).</summary>
    private readonly record struct Decision(TeamRequestResultT Result, Action? After = null);

    private void ResolveLive(SessionNode node, Id128T? key, Func<Decision> decide)
    {
        if (!_state.TryGetValue(node.PlayerId, out var state) || node.Phase != NodePhase.InGame)
        {
            return;
        }

        var id = (key?.Lo ?? 0, key?.Hi ?? 0);
        if (state.Results.TryGetValue(id, out var previous))
        {
            Send(node, previous);
            return;
        }

        Decision decision = state.Failures >= MaxLobbyFailures
            ? new Decision(Reject(TeamRejectReason.RateLimited, "too many failed attempts"))
            : decide();
        var result = decision.Result;
        result.RequestKey = key ?? new Id128T();
        if (result.Status == TeamRequestStatus.Ok)
        {
            state.Failures = 0;
        }
        else if (result.Status == TeamRequestStatus.Rejected && result.Reason != TeamRejectReason.RateLimited)
        {
            state.Failures++;
        }

        if (state.Results.Count >= MaxRememberedRequests)
        {
            state.Results.Clear();
        }

        state.Results[id] = result;
        Send(node, result); // the answer first, then the broadcasts it causes
        decision.After?.Invoke();
    }

    private static int Rank(TeamRelation relation) => relation switch
    {
        TeamRelation.Hostile => 0,
        TeamRelation.Neutral => 1,
        _ => 2,
    };

    private void HandleRelationChange(SessionNode node, InboundFrame frame)
    {
        RelationChangeRequestT request;
        try
        {
            request = MessageRegistry.Default.Decode<RelationChangeRequest>(frame.Frame).UnPack();
        }
        catch (ProtocolViolation)
        {
            return;
        }

        ResolveLive(node, request.RequestKey, () =>
        {
            if (_registry.MembershipOf(node.PlayerId) is not { } membership)
            {
                return new Decision(Reject(TeamRejectReason.NotPermitted, "you are not in a team"));
            }

            var policy = Opt.RelationChangePolicy;
            var team = _registry.Find(membership.TeamId);
            if (team is null || team.LeaderPlayerId != node.PlayerId)
            {
                return new Decision(Reject(TeamRejectReason.NotLeader, "only a team leader can change relations"));
            }

            if (policy == RelationChangePolicy.AdminOnly)
            {
                return new Decision(Reject(TeamRejectReason.NotPermitted, "relations are changed by admins"));
            }

            var other = _registry.Find(request.OtherTeam);
            if (other is null)
            {
                return new Decision(Reject(TeamRejectReason.UnknownTeam, "no such team"));
            }

            if (other.Id == team.Id)
            {
                return new Decision(Reject(TeamRejectReason.SameTeam, "a team is always allied with itself"));
            }

            var wanted = FromWire(request.Relation);
            var current = _registry.Matrix.Get(team.Id, other.Id);
            if (wanted == current)
            {
                return new Decision(Accept(0));
            }

            bool raise = Rank(wanted) > Rank(current);
            if (policy == RelationChangePolicy.LeadersUnilateral || !raise)
            {
                return new Decision(Accept(0), () => ApplyRelation(team.Id, other.Id, wanted));
            }

            // LeadersMutualAlly, an improvement: the other leader has to ask for the same thing.
            long now = _time.GetTimestamp();
            if (_proposals.TryGetValue((other.Id, team.Id), out var waiting)
                && waiting.Relation == wanted && waiting.Deadline > now
                && other.LeaderPlayerId == waiting.ProposerPlayer)
            {
                _proposals.Remove((other.Id, team.Id));
                return new Decision(Accept(0), () =>
                {
                    ApplyRelation(team.Id, other.Id, wanted);
                    NotifyProposer(waiting, TeamRequestStatus.Ok, TeamRejectReason.None, "the other leader agreed");
                });
            }

            var proposal = new Proposal(team.Id, other.Id, wanted, node.PlayerId, request.RequestKey ?? new Id128T(), After(now, ProposalSeconds));
            return new Decision(
                new TeamRequestResultT
                {
                    Status = TeamRequestStatus.Pending,
                    Reason = TeamRejectReason.None,
                    Detail = "waiting for the other team's leader",
                    TeamId = (ushort)other.Id,
                },
                () =>
                {
                    _proposals[(team.Id, other.Id)] = proposal;
                    if (other.LeaderPlayerId is { } leader && _nodes.TryGetValue(leader, out var leaderNode) && IsLive(leaderNode))
                    {
                        SendProposal(leaderNode, proposal, now);
                    }
                });
        });
    }

    private void ApplyRelation(int teamA, int teamB, TeamRelation relation)
    {
        if (_registry.SetRelation(teamA, teamB, relation).Ok)
        {
            AfterChange();
        }
    }

    private void SendProposal(SessionNode leader, Proposal proposal, long now)
    {
        double left = Math.Max(1, (proposal.Deadline - now) / (double)_time.TimestampFrequency);
        var message = new RelationProposalT
        {
            FromTeam = (ushort)proposal.FromTeam,
            ToTeam = (ushort)proposal.ToTeam,
            Relation = ToWire(proposal.Relation),
            ExpiresInS = (ushort)Math.Min(ProposalSeconds, Math.Ceiling(left)),
        };
        var frame = ControlFrames.Encode(MsgType.RelationProposal, fbb => RelationProposal.Pack(fbb, message).Value, 64);
        TrySend(leader, frame);
        frame.Release();
    }

    /// <summary>Tells the leader whose request had been answered <c>Pending</c> how it ended (the same request key).</summary>
    private void NotifyProposer(Proposal proposal, TeamRequestStatus status, TeamRejectReason reason, string detail)
    {
        if (_nodes.TryGetValue(proposal.ProposerPlayer, out var node) && node.IsAttached)
        {
            Send(node, new TeamRequestResultT
            {
                RequestKey = proposal.RequestKey,
                Status = status,
                Reason = reason,
                Detail = detail,
                TeamId = (ushort)proposal.ToTeam,
            });
        }
    }

    /// <summary>Removes proposals that can no longer be answered: a team is gone, or the pair's relation was changed meanwhile (an admin decided).</summary>
    private void DropStaleProposals(HashSet<(int, int)> changedPairs, bool defaultChanged)
    {
        if (_proposals.Count == 0)
        {
            return;
        }

        foreach (var (key, proposal) in _proposals.ToArray())
        {
            var pair = (Math.Min(key.From, key.To), Math.Max(key.From, key.To));
            if (_registry.Find(proposal.FromTeam) is null || _registry.Find(proposal.ToTeam) is null
                || changedPairs.Contains(pair) || defaultChanged
                || _registry.Matrix.Get(proposal.FromTeam, proposal.ToTeam) == proposal.Relation)
            {
                _proposals.Remove(key);
            }
        }
    }

    private void ExpireProposals(long timestamp)
    {
        if (_proposals.Count == 0)
        {
            return;
        }

        foreach (var (key, proposal) in _proposals.ToArray())
        {
            if (proposal.Deadline <= timestamp)
            {
                _proposals.Remove(key);
                NotifyProposer(proposal, TeamRequestStatus.Rejected, TeamRejectReason.NotPermitted, "the other leader did not answer in time");
            }
        }
    }

    private void HandleTeamChange(SessionNode node, InboundFrame frame)
    {
        TeamChangeRequestT request;
        try
        {
            request = MessageRegistry.Default.Decode<TeamChangeRequest>(frame.Frame).UnPack();
        }
        catch (ProtocolViolation)
        {
            return;
        }

        ResolveLive(node, request.RequestKey, () =>
        {
            if (!Opt.AllowSelfTeamChange)
            {
                return new Decision(Reject(TeamRejectReason.NotPermitted, "changing team yourself is not allowed"));
            }

            if (_registry.MembershipOf(node.PlayerId) is not { } membership)
            {
                return new Decision(Reject(TeamRejectReason.NotPermitted, "you are not in a team"));
            }

            var team = _registry.Find(request.TeamId);
            if (team is null)
            {
                return new Decision(Reject(TeamRejectReason.UnknownTeam, "no such team"));
            }

            if (team.Id == membership.TeamId)
            {
                return new Decision(Reject(TeamRejectReason.SameTeam, "already in that team"));
            }

            var check = _registry.CheckJoin(team.Id, node.PlayerId);
            if (!check.Ok)
            {
                return new Decision(Reject(check.Reason, check.Detail));
            }

            if (team.PasswordProtected && !PasswordMatches(node, team, request.Password))
            {
                return new Decision(Reject(TeamRejectReason.BadPassword, "wrong team password"));
            }

            if (AuthorityMoveBlocked(node.PlayerId, team.Id) is { } blocked)
            {
                return new Decision(Reject(blocked, AuthorityMoveDetail));
            }

            var assigned = _registry.Assign(node.PlayerId, team.Id, _time.GetUtcNow(), "self");
            if (!assigned.Ok)
            {
                return new Decision(Reject(assigned.Reason, assigned.Detail));
            }

            return new Decision(Accept(team.Id), () =>
            {
                LogAssigned(node.PlayerId, node.Name, team.Id, "self");
                AfterChange();
            });
        });
    }
}
