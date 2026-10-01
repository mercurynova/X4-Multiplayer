using X4MP.Proto;

namespace X4MP.Core.Teams;

/// <summary>
/// The team domain: teams, sticky memberships, the relation matrix and the faction-slot allocator (server-design 2.13).
/// Pure and synchronous, no I/O. Not thread-safe: the team module owns one instance on the actor thread. Every method
/// that changes something bumps <see cref="Version"/>; operations are all-or-nothing (a failed one changes nothing).
/// </summary>
public sealed class TeamRegistry
{
    private readonly SortedDictionary<int, Team> _teams = [];
    private readonly Dictionary<int, TeamMembership> _members = [];
    private readonly FactionSlotAllocator _slots = new();
    private int _nextTeamId = 1;

    public TeamRegistry(TeamRelation defaultRelation = TeamRelation.Neutral)
    {
        Matrix = new TeamRelationMatrix(defaultRelation);
    }

    /// <summary>Counts every change (team, membership, relation). The <see cref="ITeamDirectory.Changed"/> version.</summary>
    public int Version { get; private set; }

    public TeamRelationMatrix Matrix { get; private set; }

    public IReadOnlyList<Team> Teams => [.. _teams.Values];

    public IReadOnlyCollection<TeamMembership> Members => _members.Values;

    public Team? Find(int teamId) => _teams.GetValueOrDefault(teamId);

    public TeamMembership? MembershipOf(int playerId) => _members.GetValueOrDefault(playerId);

    public int? TeamOf(int playerId) => _members.TryGetValue(playerId, out var m) ? m.TeamId : null;

    public IReadOnlyList<int> MembersOf(int teamId) => [.. _members.Values.Where(m => m.TeamId == teamId).Select(m => m.PlayerId).Order()];

    public int MemberCount(int teamId) => _members.Values.Count(m => m.TeamId == teamId);

    public int FreeSlots => _slots.FreeCount;

    // ------------------------------------------------------------------ teams

    /// <summary>Creates a team in the lowest free slot (or <paramref name="slot"/>). <paramref name="maxTeams"/> is <c>TeamOptions.MaxTeams</c>.</summary>
    public TeamResult<Team> CreateTeam(
        string name, DateTimeOffset now, int maxTeams = TeamOptions.MaxFactionSlots, string? color = null, int? slot = null,
        bool locked = false, int? maxMembers = null, byte[]? passwordHash = null)
    {
        if (TeamRules.NormalizeName(name) is not { } clean)
        {
            return TeamResults.Fail<Team>(TeamRejectReason.NotPermitted, $"a team name is 1 to {TeamRules.MaxNameLength} characters");
        }

        if (NameTaken(clean, except: 0))
        {
            return TeamResults.Fail<Team>(TeamRejectReason.NameTaken, $"the name '{clean}' is taken");
        }

        if (_teams.Count >= Math.Clamp(maxTeams, 1, TeamOptions.MaxFactionSlots))
        {
            return TeamResults.Fail<Team>(TeamRejectReason.NoFactionSlot, "no team slot is free");
        }

        int chosen;
        if (slot is { } wanted)
        {
            if (!_slots.TryReserve(wanted))
            {
                return TeamResults.Fail<Team>(TeamRejectReason.NoFactionSlot, $"faction slot {wanted} is not available");
            }

            chosen = wanted;
        }
        else if (!_slots.TryAllocate(out chosen))
        {
            return TeamResults.Fail<Team>(TeamRejectReason.NoFactionSlot, "all faction slots are in use");
        }

        var team = new Team(_nextTeamId++, clean, color ?? TeamRules.ColorForSlot(chosen), chosen, null, locked,
            maxMembers is > 0 ? maxMembers : null, passwordHash is { Length: > 0 } ? passwordHash : null, now);
        _teams[team.Id] = team;
        Version++;
        return TeamResults.Success(team);
    }

    /// <summary>The fields of a team an admin may change; null leaves a field as it is.</summary>
    public sealed record TeamPatch(
        string? Name = null, string? Color = null, bool? Locked = null, int? MaxMembers = null, bool ClearMaxMembers = false,
        int? LeaderPlayerId = null, byte[]? PasswordHash = null, bool ClearPassword = false, int? FactionSlot = null);

    public TeamResult<Team> UpdateTeam(int teamId, TeamPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (!_teams.TryGetValue(teamId, out var team))
        {
            return TeamResults.Fail<Team>(TeamRejectReason.UnknownTeam, $"no team {teamId}");
        }

        string name = team.Name;
        if (patch.Name is not null)
        {
            if (TeamRules.NormalizeName(patch.Name) is not { } clean)
            {
                return TeamResults.Fail<Team>(TeamRejectReason.NotPermitted, $"a team name is 1 to {TeamRules.MaxNameLength} characters");
            }

            if (NameTaken(clean, except: teamId))
            {
                return TeamResults.Fail<Team>(TeamRejectReason.NameTaken, $"the name '{clean}' is taken");
            }

            name = clean;
        }

        int? leader = team.LeaderPlayerId;
        if (patch.LeaderPlayerId is { } newLeader)
        {
            if (_members.GetValueOrDefault(newLeader)?.TeamId != teamId)
            {
                return TeamResults.Fail<Team>(TeamRejectReason.NotPermitted, "the leader must be a member of the team");
            }

            leader = newLeader;
        }

        int slot = team.FactionSlot;
        if (patch.FactionSlot is { } wanted && wanted != slot)
        {
            if (!_slots.TryReserve(wanted))
            {
                return TeamResults.Fail<Team>(TeamRejectReason.NoFactionSlot, $"faction slot {wanted} is not available");
            }

            _slots.Release(slot);
            slot = wanted;
        }

        int? maxMembers = patch.ClearMaxMembers ? null : patch.MaxMembers is > 0 ? patch.MaxMembers : team.MaxMembers;
        byte[]? hash = patch.ClearPassword ? null : patch.PasswordHash is { Length: > 0 } ? patch.PasswordHash : team.JoinPasswordHash;
        var updated = team with
        {
            Name = name,
            Color = patch.Color ?? team.Color,
            FactionSlot = slot,
            LeaderPlayerId = leader,
            Locked = patch.Locked ?? team.Locked,
            MaxMembers = maxMembers,
            JoinPasswordHash = hash,
        };
        _teams[teamId] = updated;
        if (patch.LeaderPlayerId is { } l)
        {
            SetRoles(teamId, l);
        }

        Version++;
        return TeamResults.Success(updated);
    }

    /// <summary>
    /// Deletes a team. Its members move to <paramref name="moveMembersTo"/> (when given and valid) or become unassigned;
    /// the slot is freed and every relation of the team dropped.
    /// </summary>
    public TeamResult DeleteTeam(int teamId, int? moveMembersTo = null)
    {
        if (!_teams.TryGetValue(teamId, out var doomed))
        {
            return new TeamResult(TeamRejectReason.UnknownTeam, $"no team {teamId}");
        }

        if (moveMembersTo is { } target && (target == teamId || !_teams.ContainsKey(target)))
        {
            return new TeamResult(TeamRejectReason.UnknownTeam, $"cannot move the members to team {target}");
        }

        foreach (int player in MembersOf(teamId))
        {
            if (moveMembersTo is { } to)
            {
                var old = _members[player];
                _members[player] = old with { TeamId = to, Role = TeamRole.Member };
            }
            else
            {
                _members.Remove(player);
            }
        }

        _slots.Release(doomed.FactionSlot);
        _teams.Remove(teamId);
        Matrix = Matrix.Without(teamId);
        if (moveMembersTo is { } dest)
        {
            EnsureLeader(dest);
        }

        Version++;
        return TeamResult.Success;
    }

    private bool NameTaken(string name, int except) =>
        _teams.Values.Any(t => t.Id != except && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ membership

    /// <summary>
    /// Puts a player in a team (a move when they were in another). Does not check locks, limits or passwords: joining is
    /// validated by <see cref="CheckJoin"/>, an admin may assign anywhere. The first member of a team without a leader
    /// becomes its leader.
    /// </summary>
    public TeamResult<TeamMembership> Assign(int playerId, int teamId, DateTimeOffset now, string assignedBy, TeamRole role = TeamRole.Member)
    {
        if (!_teams.TryGetValue(teamId, out var team))
        {
            return TeamResults.Fail<TeamMembership>(TeamRejectReason.UnknownTeam, $"no team {teamId}");
        }

        var previous = _members.GetValueOrDefault(playerId);
        if (previous is not null && previous.TeamId == teamId && (role == TeamRole.Member || previous.Role == role))
        {
            return TeamResults.Success(previous);
        }

        var membership = new TeamMembership(playerId, teamId, role, now, assignedBy);
        _members[playerId] = membership;
        if (previous is not null && previous.TeamId != teamId)
        {
            EnsureLeader(previous.TeamId);
        }

        if (role == TeamRole.Leader)
        {
            SetRoles(teamId, playerId);
        }
        else if (team.LeaderPlayerId is null)
        {
            SetRoles(teamId, playerId);
        }

        Version++;
        return TeamResults.Success(_members[playerId]);
    }

    /// <summary>Removes the membership (admin "unassign"). Returns false when the player had none.</summary>
    public bool Unassign(int playerId)
    {
        if (!_members.Remove(playerId, out var old))
        {
            return false;
        }

        EnsureLeader(old.TeamId);
        Version++;
        return true;
    }

    /// <summary>Lobby rules for choosing a team: locked and full (the password is checked by the caller).</summary>
    public TeamResult CheckJoin(int teamId, int playerId)
    {
        if (!_teams.TryGetValue(teamId, out var team))
        {
            return new TeamResult(TeamRejectReason.UnknownTeam, $"no team {teamId}");
        }

        if (_members.GetValueOrDefault(playerId)?.TeamId == teamId)
        {
            return new TeamResult(TeamRejectReason.SameTeam, "already in that team");
        }

        if (team.Locked)
        {
            return new TeamResult(TeamRejectReason.Locked, "the team is locked");
        }

        if (team.MaxMembers is { } max && MemberCount(teamId) >= max)
        {
            return new TeamResult(TeamRejectReason.Full, "the team is full");
        }

        return TeamResult.Success;
    }

    /// <summary>Makes sure the team has a leader among its members (the lowest player id), or none when it is empty.</summary>
    private void EnsureLeader(int teamId)
    {
        if (!_teams.TryGetValue(teamId, out var team))
        {
            return;
        }

        var members = MembersOf(teamId);
        if (team.LeaderPlayerId is { } current && members.Contains(current))
        {
            return;
        }

        SetRoles(teamId, members.Count > 0 ? members[0] : null);
    }

    private void SetRoles(int teamId, int? leader)
    {
        _teams[teamId] = _teams[teamId] with { LeaderPlayerId = leader };
        foreach (int player in MembersOf(teamId))
        {
            var m = _members[player];
            var role = player == leader ? TeamRole.Leader : TeamRole.Member;
            if (m.Role != role)
            {
                _members[player] = m with { Role = role };
            }
        }
    }

    // ------------------------------------------------------------------ relations

    public TeamResult SetRelation(int teamA, int teamB, TeamRelation relation)
    {
        if (!_teams.ContainsKey(teamA) || !_teams.ContainsKey(teamB))
        {
            return new TeamResult(TeamRejectReason.UnknownTeam, "unknown team");
        }

        if (teamA == teamB)
        {
            return new TeamResult(TeamRejectReason.SameTeam, "a team is always allied with itself");
        }

        var next = Matrix.With(teamA, teamB, relation);
        if (!ReferenceEquals(next, Matrix))
        {
            Matrix = next;
            Version++;
        }

        return TeamResult.Success;
    }

    public TeamRelation Relation(int teamA, int teamB) => Matrix.Get(teamA, teamB);

    /// <summary>Changes what an unset pair means (<c>TeamOptions.DefaultRelation</c>).</summary>
    public void SetDefaultRelation(TeamRelation relation)
    {
        var next = Matrix.WithDefault(relation);
        if (!ReferenceEquals(next, Matrix))
        {
            Matrix = next;
            Version++;
        }
    }

    // ------------------------------------------------------------------ auto-assign

    /// <summary>
    /// Auto join (server-design 2.13): <c>SingleTeam</c> the first team, <c>Balance</c> the unlocked, not full team with the
    /// fewest members (lowest id on ties), <c>NewTeamPerPlayer</c> a new team named after the player. A session without a
    /// team gets a default one first. <c>NoFactionSlot</c> when a new team is needed and none is free.
    /// </summary>
    public TeamResult<TeamMembership> AutoAssign(
        TeamPlayer player, AutoAssignStrategy strategy, DateTimeOffset now, int maxTeams = TeamOptions.MaxFactionSlots, string assignedBy = "auto")
    {
        Team? target = null;
        switch (strategy)
        {
            case AutoAssignStrategy.NewTeamPerPlayer:
                {
                    var created = CreateTeam(UniqueName(player.Name), now, maxTeams);
                    if (!created.Ok)
                    {
                        return TeamResults.Fail<TeamMembership>(created.Reason, created.Detail);
                    }

                    target = created.Value;
                    break;
                }

            case AutoAssignStrategy.Balance:
                {
                    target = _teams.Values
                        .Where(t => !t.Locked && (t.MaxMembers is not { } max || MemberCount(t.Id) < max))
                        .OrderBy(t => MemberCount(t.Id)).ThenBy(t => t.Id)
                        .FirstOrDefault();
                    break;
                }

            default:
                target = _teams.Values.FirstOrDefault();
                break;
        }

        if (target is null)
        {
            if (_teams.Count > 0 && strategy == AutoAssignStrategy.Balance && _teams.Count >= Math.Clamp(maxTeams, 1, TeamOptions.MaxFactionSlots))
            {
                return TeamResults.Fail<TeamMembership>(TeamRejectReason.Full, "every team is locked or full");
            }

            var created = CreateTeam(strategy == AutoAssignStrategy.SingleTeam ? "Everyone" : UniqueName("Team " + (_teams.Count + 1)), now, maxTeams);
            if (!created.Ok)
            {
                return TeamResults.Fail<TeamMembership>(created.Reason, created.Detail);
            }

            target = created.Value;
        }

        return Assign(player.PlayerId, target!.Id, now, assignedBy);
    }

    private string UniqueName(string wanted)
    {
        string baseName = TeamRules.NormalizeName(wanted) ?? "Team";
        if (baseName.Length > TeamRules.MaxNameLength)
        {
            baseName = baseName[..TeamRules.MaxNameLength];
        }

        string name = baseName;
        for (int i = 2; NameTaken(name, except: 0); i++)
        {
            string suffix = " " + i;
            name = baseName[..Math.Min(baseName.Length, TeamRules.MaxNameLength - suffix.Length)] + suffix;
        }

        return name;
    }

    // ------------------------------------------------------------------ presets

    /// <summary>
    /// Replaces the team table, the memberships and the matrix at once (server-design 2.13 presets). <paramref name="players"/>
    /// are the players to place (everyone known to the session). All-or-nothing: with more players than slots for the
    /// per-player presets nothing changes and the result is <c>NoFactionSlot</c>. Returns the plan (the settings the preset goes with).
    /// </summary>
    public TeamResult<TeamPresetPlan> ApplyPreset(TeamPreset preset, IReadOnlyCollection<TeamPlayer> players, DateTimeOffset now, string assignedBy = "preset")
    {
        ArgumentNullException.ThrowIfNull(players);
        var plan = TeamPresetPlan.For(preset);
        var ordered = players.OrderBy(p => p.PlayerId).ToList();
        if (plan.FixedTeamCount is null && ordered.Count > TeamOptions.MaxFactionSlots)
        {
            return TeamResults.Fail<TeamPresetPlan>(TeamRejectReason.NoFactionSlot, $"{ordered.Count} players need more than {TeamOptions.MaxFactionSlots} faction slots");
        }

        var fresh = new TeamRegistry(Matrix.DefaultRelation);
        switch (preset)
        {
            case TeamPreset.CoOp:
                {
                    var team = fresh.CreateTeam("Everyone", now).Value!;
                    foreach (var p in ordered)
                    {
                        fresh.Assign(p.PlayerId, team.Id, now, assignedBy);
                    }

                    break;
                }

            case TeamPreset.TwoTeams:
                {
                    fresh.CreateTeam("Team 1", now);
                    fresh.CreateTeam("Team 2", now);
                    foreach (var p in ordered)
                    {
                        fresh.AutoAssign(p, AutoAssignStrategy.Balance, now, 2, assignedBy);
                    }

                    break;
                }

            default:
                foreach (var p in ordered)
                {
                    fresh.AutoAssign(p, AutoAssignStrategy.NewTeamPerPlayer, now, assignedBy: assignedBy);
                }

                break;
        }

        fresh.Matrix = fresh.Matrix.Reset(fresh._teams.Keys, plan.Relation, Matrix.DefaultRelation);

        // Atomic swap, keeping the version counters monotonic.
        int version = Version + 1;
        int matrixVersion = Matrix.Version + 1;
        _teams.Clear();
        foreach (var t in fresh._teams)
        {
            _teams[t.Key] = t.Value;
        }

        _members.Clear();
        foreach (var m in fresh._members)
        {
            _members[m.Key] = m.Value;
        }

        _slots.Clear();
        foreach (var t in _teams.Values)
        {
            _slots.TryReserve(t.FactionSlot);
        }

        _nextTeamId = fresh._nextTeamId;
        Matrix = TeamRelationMatrix.Restore(Matrix.DefaultRelation, matrixVersion, fresh.Matrix.Entries);
        Version = version;
        return TeamResults.Success(plan);
    }

    // ------------------------------------------------------------------ persistence

    public TeamStateSnapshot Snapshot() => new(
        Matrix.DefaultRelation,
        Teams,
        [.. _members.Values.OrderBy(m => m.PlayerId)],
        Matrix.Entries);

    /// <summary>Replaces the state with a stored one (server start). Invalid rows (a bad slot, a member of a missing team) are skipped.</summary>
    public void Restore(TeamStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _teams.Clear();
        _members.Clear();
        _slots.Clear();
        foreach (var team in snapshot.Teams.OrderBy(t => t.Id))
        {
            if (team.Id > 0 && _slots.TryReserve(team.FactionSlot) && !_teams.ContainsKey(team.Id) && !NameTaken(team.Name, 0))
            {
                _teams[team.Id] = team;
            }
        }

        foreach (var m in snapshot.Members)
        {
            if (_teams.ContainsKey(m.TeamId))
            {
                _members[m.PlayerId] = m;
            }
        }

        foreach (int id in _teams.Keys.ToList())
        {
            EnsureLeader(id);
        }

        _nextTeamId = _teams.Count == 0 ? 1 : _teams.Keys.Max() + 1;
        Matrix = TeamRelationMatrix.Restore(
            snapshot.DefaultRelation, snapshot.Relations.Count == 0 ? 0 : 1,
            snapshot.Relations.Where(r => _teams.ContainsKey(r.TeamA) && _teams.ContainsKey(r.TeamB)));
        Version = 1;
    }
}
