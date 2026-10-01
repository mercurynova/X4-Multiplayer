using Microsoft.Extensions.Logging;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;
using WireRelation = X4MP.Proto.TeamRelation;

namespace X4MP.Core.Teams;

/// <summary>
/// The Teams module (M1-T1 domain, M1-T2 join-time assignment): owns the <see cref="TeamRegistry"/>, implements
/// <see cref="ITeamDirectory"/>, fills the team fields of <c>Welcome</c> and moves nodes through the
/// <see cref="NodePhase.AwaitingTeam"/> phase (server-design 2.13). It is an <see cref="ISessionModule"/>: every callback
/// and every admin operation runs on the actor thread, so the registry needs no locks. Other threads read the directory
/// through an immutable view that is replaced after each change.
/// <para>
/// Register it before other modules: while the authority has no team the module swallows its non-team frames (no save, no
/// replication before the team is known), which only works if it sees them first.
/// </para>
/// </summary>
public sealed partial class TeamModule : ISessionModule, ISessionActorBound, ITeamDirectory
{
    /// <summary>A node asking for a team this often in a row without success gets <c>RateLimited</c>.</summary>
    private const int MaxLobbyFailures = 10;

    private const int MaxRememberedRequests = 16;

    /// <summary>What the module remembers per node while it needs a team.</summary>
    private sealed class NodeState
    {
        /// <summary>Lobby join mode: <c>OnTick</c> falls back to Auto at this timestamp.</summary>
        public long LobbyDeadline { get; set; } = long.MaxValue;

        public bool Lobby { get; set; }

        public int Failures { get; set; }

        public Dictionary<(ulong Lo, ulong Hi), TeamRequestResultT> Results { get; } = [];
    }

    /// <summary>Immutable, replaced after every change: what <see cref="ITeamDirectory"/> reads from any thread.</summary>
    private sealed record View(
        int Version,
        IReadOnlyList<TeamInfo> Teams,
        IReadOnlyDictionary<int, int> TeamByPlayer,
        IReadOnlyDictionary<int, IReadOnlyList<int>> Members,
        IReadOnlyDictionary<int, int> Leaders,
        TeamRelationMatrix Matrix);

    private readonly Func<TeamOptions> _options;
    private readonly ITeamStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TeamRegistry _registry = new();
    private readonly Dictionary<int, SessionNode> _nodes = [];
    private readonly Dictionary<int, NodeState> _state = [];
    private readonly Dictionary<int, string> _names = [];
    private ISessionNodeDriver? _driver;
    private volatile View _view;
    private volatile bool _authorityAwaitingTeam;
    private bool _dirty;

    /// <param name="options">Supplies the live <see cref="TeamOptions"/> on every use (<c>IOptionsMonitor.CurrentValue</c>).</param>
    /// <param name="store">Where memberships survive a restart; the latest stored state is loaded now.</param>
    public TeamModule(Func<TeamOptions> options, ITeamStore? store = null, TimeProvider? time = null, ILogger<TeamModule>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _store = store ?? new NullTeamStore();
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        try
        {
            if (_store.LoadLatest() is { } stored)
            {
                _registry.Restore(stored);
            }
        }
        catch (Exception ex)
        {
            LogLoadFailed(ex);
        }

        _view = BuildView();
        CaptureBaseline();
    }

    /// <summary>Convenience for tests and simple hosts: fixed options.</summary>
    public TeamModule(TeamOptions? options = null, ITeamStore? store = null, TimeProvider? time = null, ILogger<TeamModule>? logger = null)
        : this(Constant(options ?? new TeamOptions()), store, time, logger)
    {
    }

    private static Func<TeamOptions> Constant(TeamOptions options) => () => options;

    private TeamOptions Opt => _options();

    public void Bind(ISessionNodeDriver driver) => _driver = driver;

    // ------------------------------------------------------------------ ITeamDirectory

    public IReadOnlyList<TeamInfo> Teams => _view.Teams;

    public int? TeamOf(int playerId) => _view.TeamByPlayer.TryGetValue(playerId, out int team) ? team : null;

    public IReadOnlyList<int> MembersOf(int teamId) => _view.Members.TryGetValue(teamId, out var members) ? members : [];

    /// <summary>The leader of a team (the first member, or whoever an admin made leader), or null for an empty team. Not part of <see cref="ITeamDirectory"/>; the economy can use it for its pool-withdraw and remainder rules.</summary>
    public int? LeaderOf(int teamId) => _view.Leaders.TryGetValue(teamId, out int leader) ? leader : null;

    public TeamRelation RelationBetween(int teamA, int teamB) => _view.Matrix.Get(teamA, teamB);

    public event Action<TeamDirectoryChanged>? Changed;

    /// <summary>
    /// True while the authority's player waits for a team (AdminAssign, or a Lobby choice not made yet): it gets no save
    /// and nothing it sends is processed. The GUI flags this as blocking.
    /// </summary>
    public bool AuthorityAwaitingTeam => _authorityAwaitingTeam;

    /// <summary>Read-only copy of the stored state (teams, memberships, relations), for the GUI and tests.</summary>
    public TeamStateSnapshot Snapshot() => _registry.Snapshot();

    /// <summary>The relation matrix (immutable, versioned).</summary>
    public TeamRelationMatrix Matrix => _view.Matrix;

    /// <summary>The full team record (password hash, limits, leader); <see cref="Teams"/> only has the directory fields.</summary>
    public IReadOnlyList<Team> TeamDetails => _registry.Teams;

    private View BuildView()
    {
        var teams = _registry.Teams;
        var members = new Dictionary<int, IReadOnlyList<int>>();
        foreach (var team in teams)
        {
            members[team.Id] = _registry.MembersOf(team.Id);
        }

        return new View(
            _registry.Version,
            [.. teams.Select(t => t.Info)],
            _registry.Members.ToDictionary(m => m.PlayerId, m => m.TeamId),
            members,
            teams.Where(t => t.LeaderPlayerId is not null).ToDictionary(t => t.Id, t => t.LeaderPlayerId!.Value),
            _registry.Matrix);
    }

    /// <summary>After any registry change: refresh the view, the nodes' team fields, persistence and tell the listeners.</summary>
    private void AfterChange()
    {
        SyncDefaultRelation();
        _view = BuildView();
        _dirty = true;
        SyncNodes();
        FanOut();
        Persist();
        var handler = Changed;
        if (handler is not null)
        {
            try
            {
                handler(new TeamDirectoryChanged(_registry.Version));
            }
            catch (Exception ex)
            {
                LogListenerFailed(ex);
            }
        }
    }

    private void SyncDefaultRelation()
    {
        var wanted = Opt.DefaultRelation;
        if (_registry.Matrix.DefaultRelation != wanted)
        {
            _registry.SetDefaultRelation(wanted);
            _view = BuildView();
        }
    }

    private void Persist()
    {
        // Until the session row exists (nobody joined yet) the change stays dirty and is saved on a later tick.
        if (_dirty && _driver?.StoreSessionId is { } sessionId and > 0)
        {
            try
            {
                _dirty = !_store.Save(sessionId, _registry.Snapshot());
            }
            catch (Exception ex)
            {
                LogSaveFailed(ex);
            }
        }
    }

    /// <summary>Copies membership onto the nodes (roster fields) and releases nodes that were waiting for exactly this.</summary>
    private void SyncNodes()
    {
        foreach (var node in _nodes.Values)
        {
            var membership = _registry.MembershipOf(node.PlayerId);
            node.TeamId = membership?.TeamId ?? 0;
            node.TeamRole = membership?.Role ?? TeamRole.Member;
            if (membership is not null && node.Phase == NodePhase.AwaitingTeam)
            {
                Fire(node.PlayerId, NodeTrigger.TeamAssigned);
            }
        }
    }

    private void Fire(int playerId, NodeTrigger trigger)
    {
        if (_driver is null)
        {
            return;
        }

        _ = _driver.ApplyNodeTriggerAsync(playerId, trigger).ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully && !t.Result.Applied)
                {
                    LogTriggerIgnored(playerId, trigger, t.Result.Error);
                }
            },
            TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ admission (M1-T2)

    public AdmissionVerdict OnNodeAdmitting(SessionNode node, WelcomeT welcome, bool resumed)
    {
        _admitting = node.PlayerId;
        try
        {
            return Admit(node, welcome, resumed);
        }
        finally
        {
            _admitting = -1;
        }
    }

    private AdmissionVerdict Admit(SessionNode node, WelcomeT welcome, bool resumed)
    {
        _nodes[node.PlayerId] = node;
        _names[node.PlayerId] = node.Name;
        var state = resumed && _state.TryGetValue(node.PlayerId, out var kept) ? kept : _state[node.PlayerId] = new NodeState();
        var opt = Opt;
        SyncDefaultRelation();

        bool needsTeam = (node.Roles & (Role.Authority | Role.Client)) != 0;
        var membership = _registry.MembershipOf(node.PlayerId);
        if (needsTeam)
        {
            if (membership is not null)
            {
                // Sticky first: a known player skips AwaitingTeam, also after leaving, a restart or a resume.
                if (node.Phase is NodePhase.Admitted or NodePhase.AwaitingTeam)
                {
                    Fire(node.PlayerId, NodeTrigger.TeamAssigned);
                }
            }
            else if (!resumed || node.Phase is NodePhase.Admitted)
            {
                var refusal = JoinWithoutTeam(node, state, opt, out membership);
                if (refusal is not null)
                {
                    return refusal.Value;
                }
            }
        }

        node.TeamId = membership?.TeamId ?? 0;
        node.TeamRole = membership?.Role ?? TeamRole.Member;
        FillWelcome(node, welcome, opt);
        _sent[node.PlayerId] = (_registry.Version, _registry.Matrix.Version, _settingsVersion);
        UpdateAuthorityFlag();
        return AdmissionVerdict.Accept;
    }

    /// <summary>The join modes for a player the registry does not know. Returns a refusal, or null (membership set or node waiting).</summary>
    private AdmissionVerdict? JoinWithoutTeam(SessionNode node, NodeState state, TeamOptions opt, out TeamMembership? membership)
    {
        membership = null;
        bool canChoose = (node.Roles & Role.Client) != 0; // team requests are a Client message (the policy table)
        var mode = opt.JoinMode;
        if (mode != TeamJoinMode.Auto && !(mode == TeamJoinMode.Lobby && !canChoose))
        {
            state.Lobby = mode == TeamJoinMode.Lobby;
            state.LobbyDeadline = state.Lobby
                ? After(_time.GetTimestamp(), Math.Max(1, opt.LobbyTimeoutSeconds))
                : long.MaxValue;
            Fire(node.PlayerId, NodeTrigger.RequireTeam);
            return null;
        }

        // Auto, or a Lobby node that cannot answer (an authority without the Client role): assign now.
        var result = AutoAssign(node.PlayerId, node.Name, opt);
        if (!result.Ok)
        {
            LogRefused(node.PlayerId, node.Name, result.Reason);
            _nodes.Remove(node.PlayerId);
            _state.Remove(node.PlayerId);
            return new AdmissionVerdict(
                result.Reason == TeamRejectReason.Full ? DisconnectCode.SessionFull : DisconnectCode.NoFactionSlot,
                result.Detail ?? "no team available");
        }

        membership = result.Value;
        Fire(node.PlayerId, NodeTrigger.TeamAssigned);
        LogAssigned(node.PlayerId, node.Name, membership!.TeamId, membership.AssignedBy);
        return null;
    }

    private TeamResult<TeamMembership> AutoAssign(int playerId, string name, TeamOptions opt, string assignedBy = "auto")
    {
        var result = _registry.AutoAssign(new TeamPlayer(playerId, name), opt.AutoAssign, _time.GetUtcNow(), opt.MaxTeams, assignedBy);
        if (result.Ok)
        {
            AfterChange();
        }

        return result;
    }

    private void FillWelcome(SessionNode node, WelcomeT welcome, TeamOptions opt)
    {
        var team = node.TeamId == 0 ? null : _registry.Find(node.TeamId);
        welcome.TeamId = (ushort)Math.Clamp(node.TeamId, 0, ushort.MaxValue);
        welcome.TeamRole = node.TeamRole;
        welcome.FactionSlot = (byte)(team?.FactionSlot ?? 0);
        welcome.Teams = BuildTable();
        welcome.Relations = BuildRelations();
        // Shared with the economy module: only the team half of the settings is ours.
        welcome.Settings ??= new SessionSettingsT();
        welcome.Settings.Version = _settingsVersion;
        welcome.Settings.Team = BuildPolicy(opt);
        welcome.Settings.Economy ??= EconomySettings;
    }

    private TeamTableT BuildTable()
    {
        var table = new TeamTableT { Version = (uint)_registry.Version, Full = true, Teams = [], Removed = [] };
        foreach (var team in _registry.Teams)
        {
            var info = new TeamInfoT
            {
                TeamId = (ushort)team.Id,
                Name = team.Name,
                ColorRgb = TeamRules.ColorToRgb(team.Color),
                FactionSlot = (byte)team.FactionSlot,
                LeaderPlayer = (ushort)(team.LeaderPlayerId ?? 0),
                Locked = team.Locked,
                MaxMembers = (ushort)(team.MaxMembers ?? 0),
                PasswordProtected = team.PasswordProtected,
                Members = [],
            };
            foreach (int player in _registry.MembersOf(team.Id))
            {
                info.Members.Add(new TeamMemberT
                {
                    PlayerId = (ushort)player,
                    Name = _names.GetValueOrDefault(player, string.Empty),
                    Role = _registry.MembershipOf(player)?.Role ?? TeamRole.Member,
                    Online = _nodes.TryGetValue(player, out var n) && n.IsAttached,
                });
            }

            table.Teams.Add(info);
        }

        return table;
    }

    private TeamRelationsT BuildRelations()
    {
        var matrix = _registry.Matrix;
        var relations = new TeamRelationsT
        {
            Version = (uint)matrix.Version,
            Full = true,
            DefaultRelation = ToWire(matrix.DefaultRelation),
            Entries = [],
        };
        foreach (var (a, b, relation) in matrix.Entries)
        {
            relations.Entries.Add(new TeamRelationEntryT { TeamA = (ushort)a, TeamB = (ushort)b, Relation = ToWire(relation) });
        }

        return relations;
    }

    private static TeamPolicyT BuildPolicy(TeamOptions opt) => new()
    {
        JoinMode = opt.JoinMode,
        AutoAssign = opt.AutoAssign,
        AllowCreateInLobby = opt.AllowCreateInLobby,
        AllowSelfTeamChange = opt.AllowSelfTeamChange,
        MaxTeams = (byte)Math.Clamp(opt.MaxTeams, 1, TeamOptions.MaxFactionSlots),
        AssetPolicy = opt.AssetPolicy,
        AllowFriendlyFire = opt.AllowFriendlyFire,
        AllowAssetTransfer = opt.AllowAssetTransfer,
        MoveAssetsWithPlayer = opt.MoveAssetsWithPlayer,
        RelationChangePolicy = opt.RelationChangePolicy,
    };

    internal static WireRelation ToWire(TeamRelation relation) => relation switch
    {
        TeamRelation.Allied => WireRelation.Allied,
        TeamRelation.Hostile => WireRelation.Hostile,
        _ => WireRelation.Neutral,
    };

    internal static TeamRelation FromWire(WireRelation relation) => relation switch
    {
        WireRelation.Allied => TeamRelation.Allied,
        WireRelation.Hostile => TeamRelation.Hostile,
        _ => TeamRelation.Neutral,
    };

    // ------------------------------------------------------------------ node lifecycle

    public void OnNodePhaseChanged(SessionNode node, NodePhase previous, NodePhase current)
    {
        if (current != NodePhase.AwaitingTeam && _state.TryGetValue(node.PlayerId, out var state))
        {
            state.LobbyDeadline = long.MaxValue;
        }

        if (current == NodePhase.InGame)
        {
            CatchUp(node);
            if (node.IsAuthority)
            {
                FlushReassign();
            }
        }

        UpdateAuthorityFlag();
    }

    public void OnNodeLeft(SessionNode node, string reason)
    {
        // The membership stays: it is sticky across leaving, reconnecting and restarts.
        if (_nodes.TryGetValue(node.PlayerId, out var known) && ReferenceEquals(known, node))
        {
            _nodes.Remove(node.PlayerId);
            _state.Remove(node.PlayerId);
            _sent.Remove(node.PlayerId);
        }

        UpdateAuthorityFlag();
    }

    public void OnSessionPhaseChanged(SessionPhase previous, SessionPhase current)
    {
        _sessionPhase = current;
        Persist();
    }

    public void OnSessionBegun(long sessionId)
    {
        // A new sessions row: write the complete current state under it, so the latest session always holds everything (sticky across restarts).
        _dirty = _dirty || _registry.Teams.Count > 0;
        Persist();
    }

    private void UpdateAuthorityFlag() =>
        _authorityAwaitingTeam = _nodes.Values.Any(n => n.IsAuthority && n.Phase == NodePhase.AwaitingTeam);

    public void OnTick(long timestamp)
    {
        if (_dirty)
        {
            Persist(); // a change made before the session row existed
        }

        ExpireProposals(timestamp);

        List<SessionNode>? expired = null;
        foreach (var (playerId, state) in _state)
        {
            if (state.LobbyDeadline <= timestamp && _nodes.TryGetValue(playerId, out var node) && node.Phase == NodePhase.AwaitingTeam)
            {
                (expired ??= []).Add(node);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var node in expired)
        {
            _state[node.PlayerId].LobbyDeadline = long.MaxValue;
            var opt = Opt;
            if (_registry.MembershipOf(node.PlayerId) is not null)
            {
                Fire(node.PlayerId, NodeTrigger.TeamAssigned);
                continue;
            }

            var result = AutoAssign(node.PlayerId, node.Name, opt, "lobby-timeout");
            if (result.Ok)
            {
                LogLobbyFallback(node.PlayerId, node.Name, result.Value!.TeamId);
            }
            else if (_driver is not null)
            {
                LogRefused(node.PlayerId, node.Name, result.Reason);
                _ = _driver.RemoveNodeAsync(node.PlayerId, DisconnectCode.NoFactionSlot, result.Detail ?? "no team available");
            }
        }
    }

    // ------------------------------------------------------------------ lobby requests

    public bool OnMessage(SessionNode node, InboundFrame frame)
    {
        switch (frame.Type)
        {
            case MsgType.TeamChoice:
                HandleChoice(node, frame);
                return true;
            case MsgType.TeamCreateRequest:
                HandleCreate(node, frame);
                return true;
        }

        if (HandleLiveRequest(node, frame))
        {
            return true;
        }

        // The authority-must-have-a-team gate: nothing else from an authority that has no team yet (the policy table lets
        // it send saves and world data in any attached phase).
        return node.IsAuthority && node.Phase == NodePhase.AwaitingTeam;
    }

    private void HandleChoice(SessionNode node, InboundFrame frame)
    {
        TeamChoiceT request;
        try
        {
            request = MessageRegistry.Default.Decode<TeamChoice>(frame.Frame).UnPack();
        }
        catch (ProtocolViolation)
        {
            return;
        }

        Resolve(node, request.RequestKey, state =>
        {
            var team = _registry.Find(request.TeamId);
            if (team is null)
            {
                return Reject(TeamRejectReason.UnknownTeam, "no such team");
            }

            var check = _registry.CheckJoin(team.Id, node.PlayerId);
            if (!check.Ok)
            {
                return Reject(check.Reason, check.Detail);
            }

            if (team.PasswordProtected && !PasswordMatches(node, team, request.Password))
            {
                return Reject(TeamRejectReason.BadPassword, "wrong team password");
            }

            var assigned = _registry.Assign(node.PlayerId, team.Id, _time.GetUtcNow(), "lobby");
            return assigned.Ok ? Accept(team.Id) : Reject(assigned.Reason, assigned.Detail);
        });
    }

    private void HandleCreate(SessionNode node, InboundFrame frame)
    {
        TeamCreateRequestT request;
        try
        {
            request = MessageRegistry.Default.Decode<TeamCreateRequest>(frame.Frame).UnPack();
        }
        catch (ProtocolViolation)
        {
            return;
        }

        Resolve(node, request.RequestKey, state =>
        {
            var opt = Opt;
            if (!opt.AllowCreateInLobby || !state.Lobby)
            {
                return Reject(TeamRejectReason.NotPermitted, "creating a team is not allowed");
            }

            string? color = TeamRules.TryParseColor(request.ColorRgb, out string parsed) ? parsed : null;
            var created = _registry.CreateTeam(request.Name ?? string.Empty, _time.GetUtcNow(), opt.MaxTeams, color);
            if (!created.Ok)
            {
                return Reject(created.Reason, created.Detail);
            }

            var assigned = _registry.Assign(node.PlayerId, created.Value!.Id, _time.GetUtcNow(), "lobby");
            return assigned.Ok ? Accept(created.Value.Id) : Reject(assigned.Reason, assigned.Detail);
        });
    }

    private static TeamRequestResultT Accept(int teamId) =>
        new() { Status = TeamRequestStatus.Ok, Reason = TeamRejectReason.None, Detail = string.Empty, TeamId = (ushort)teamId };

    private static TeamRequestResultT Reject(TeamRejectReason reason, string? detail) =>
        new() { Status = TeamRequestStatus.Rejected, Reason = reason, Detail = detail ?? string.Empty };

    /// <summary>
    /// Runs one lobby request: a duplicate <c>request_key</c> gets the original answer again, a node that keeps failing gets
    /// <c>RateLimited</c>; success moves the node on to <c>SyncingSave</c>. The answer is sent before the phase changes.
    /// </summary>
    private void Resolve(SessionNode node, Id128T? key, Func<NodeState, TeamRequestResultT> decide)
    {
        if (!_state.TryGetValue(node.PlayerId, out var state) || node.Phase != NodePhase.AwaitingTeam)
        {
            return;
        }

        var id = (key?.Lo ?? 0, key?.Hi ?? 0);
        if (state.Results.TryGetValue(id, out var previous))
        {
            Send(node, previous);
            return;
        }

        var result = state.Failures >= MaxLobbyFailures ? Reject(TeamRejectReason.RateLimited, "too many failed attempts") : decide(state);
        result.RequestKey = key ?? new Id128T();
        bool assigned = result.Status == TeamRequestStatus.Ok;
        if (assigned)
        {
            state.Failures = 0;
        }
        else if (result.Reason != TeamRejectReason.RateLimited)
        {
            state.Failures++;
        }

        if (state.Results.Count >= MaxRememberedRequests)
        {
            state.Results.Clear();
        }

        state.Results[id] = result;
        Send(node, result);
        if (assigned)
        {
            AfterChange(); // also fires TeamAssigned for the node (the answer goes out before the phase changes)
            LogAssigned(node.PlayerId, node.Name, result.TeamId, "lobby");
        }
    }

    private static bool PasswordMatches(SessionNode node, Team team, List<byte>? proof)
    {
        var attached = node.Attached;
        if (attached is null || proof is null || team.JoinPasswordHash is not { } hash)
        {
            return false;
        }

        var expected = GatewayState.ComputeProof(hash, attached.Nonce, attached.Hello.PlayerKey?.ToArray() ?? []);
        return HandshakeAuth.ProofsEqual(expected, proof.ToArray());
    }

    private static void Send(SessionNode node, TeamRequestResultT result)
    {
        var frame = ControlFrames.Encode(MsgType.TeamRequestResult, fbb => TeamRequestResult.Pack(fbb, result).Value, 96);
        node.Connection?.TrySend(frame);
        frame.Release();
    }

    private long After(long from, double seconds)
    {
        double ticks = seconds * _time.TimestampFrequency;
        return ticks >= long.MaxValue - from ? long.MaxValue : from + (long)ticks;
    }

    // ------------------------------------------------------------------ logging

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "teams: loading the stored teams failed; starting empty")]
    private partial void LogLoadFailed(Exception ex);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "teams: saving the teams failed")]
    private partial void LogSaveFailed(Exception ex);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "teams: a Changed listener threw")]
    private partial void LogListenerFailed(Exception ex);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "teams: player {PlayerId}: {Trigger} ignored ({Error})")]
    private partial void LogTriggerIgnored(int playerId, NodeTrigger trigger, string? error);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "teams: player {PlayerId} ({Name}) is in team {TeamId} ({By})")]
    private partial void LogAssigned(int playerId, string name, int teamId, string by);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "teams: player {PlayerId} ({Name}) got no team in time and joined team {TeamId} automatically")]
    private partial void LogLobbyFallback(int playerId, string name, int teamId);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "teams: player {PlayerId} ({Name}) refused: {Reason}")]
    private partial void LogRefused(int playerId, string name, TeamRejectReason reason);
}
