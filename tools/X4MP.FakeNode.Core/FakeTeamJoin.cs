using System.Globalization;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

/// <summary>What team a fake client wants (M1-F3): a named or numbered team (<c>--team</c>), its slot in a swarm layout (<c>--teams</c>, <c>--relations</c>), a random open one, or nothing.</summary>
public abstract record TeamWish
{
    public sealed record None : TeamWish;

    /// <summary><c>--team &lt;id|name&gt;</c>.</summary>
    public sealed record Named(string IdOrName) : TeamWish;

    /// <summary>Slot k of N: the team called "Team k" (<c>--teams</c>, <c>--relations</c>).</summary>
    public sealed record Slot(int Number) : TeamWish;

    /// <summary><c>--team-pick lobby-random</c>.</summary>
    public sealed record Random : TeamWish;

    /// <summary>The wish of client number <paramref name="clientOrdinal"/> (0-based among the clients) under these options.</summary>
    public static TeamWish For(CliOptions o, int clientOrdinal)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (o.Team is { } team)
            return new Named(team);
        if (o.TeamPick == TeamPickMode.LobbyRandom)
            return new Random();
        int n = o.EffectiveTeams;
        return n > 0 ? new Slot((clientOrdinal % n) + 1) : new None();
    }

    /// <summary>The name a swarm team of slot <paramref name="number"/> gets.</summary>
    public static string SlotName(int number) => "Team " + number.ToString(CultureInfo.InvariantCulture);
}

/// <summary>What a node's team step did: the request it sent and the server's answer (counted for the swarm summary).</summary>
public sealed record TeamStepResult(bool Placed, int Requests, int Rejections, string Note);

/// <summary>
/// The lobby answer of a fake client (M1-F3, server-design 2.13 "Join-time behaviour"): in <c>AwaitingTeam</c> it replies <c>TeamChoice</c> (or
/// <c>TeamCreateRequest</c> when the lobby lets it create the team it wants), and retries when the server refuses (the team was created by another
/// node a moment ago, locked, full). With no lobby (Auto assigned the node at once) it can still ask for a move once in game.
/// </summary>
public static class FakeTeamJoin
{
    private const int MaxAttempts = 12;

    /// <summary>The request a wish turns into against the team table, or null when nothing can be asked for (no such team, creating not allowed).</summary>
    public static OutMessage? Decide(TeamWish wish, FakeTeamState teams, string playerName, ulong key, DetRandom rng, out string note)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(rng);
        bool canCreate = teams.Policy?.AllowCreateInLobby == true;
        var table = teams.OrderedTeams();
        switch (wish)
        {
            case TeamWish.Named named:
                if (teams.FindTeam(named.IdOrName) is { } found)
                {
                    note = $"choose '{found.Name}' (team {found.TeamId})";
                    return FakeTeamState.BuildTeamChoice(key, found.TeamId);
                }

                if (canCreate && !IsId(named.IdOrName))
                {
                    note = $"create '{named.IdOrName}'";
                    return FakeTeamState.BuildTeamCreateRequest(key, named.IdOrName);
                }

                note = $"no team '{named.IdOrName}' and the lobby does not let a node create one";
                return null;

            case TeamWish.Slot slot:
                string name = TeamWish.SlotName(slot.Number);
                if (teams.FindTeam(name) is { } existing)
                {
                    note = $"choose '{existing.Name}' (team {existing.TeamId})";
                    return FakeTeamState.BuildTeamChoice(key, existing.TeamId);
                }

                if (canCreate)
                {
                    note = $"create '{name}'";
                    return FakeTeamState.BuildTeamCreateRequest(key, name);
                }

                if (table.Count > 0)
                {
                    var fallback = table[(slot.Number - 1) % table.Count];
                    note = $"no team '{name}' and no creating: choose '{fallback.Name}' (team {fallback.TeamId})";
                    return FakeTeamState.BuildTeamChoice(key, fallback.TeamId);
                }

                note = "the team table is empty and the lobby does not let a node create a team";
                return null;

            case TeamWish.Random:
                var open = table.Where(t => !t.Locked && !t.PasswordProtected && (t.MaxMembers == 0 || (t.Members?.Count ?? 0) < t.MaxMembers)).ToList();
                // When the lobby lets nodes create teams (and a slot is free), "a new team" is one more choice besides the open ones.
                bool mayCreate = canCreate && (teams.Policy!.MaxTeams == 0 || table.Count < teams.Policy.MaxTeams);
                int choices = open.Count + (mayCreate ? 1 : 0);
                if (choices > 0 && (int)(rng.NextUInt64() % (ulong)choices) is var index && index < open.Count)
                {
                    var pick = open[index];
                    note = $"random open team '{pick.Name}' (team {pick.TeamId}) of {open.Count}";
                    return FakeTeamState.BuildTeamChoice(key, pick.TeamId);
                }

                if (canCreate)
                {
                    note = $"create a new team '{playerName}' ({open.Count} open)";
                    return FakeTeamState.BuildTeamCreateRequest(key, playerName);
                }

                note = "no open team and the lobby does not let a node create one";
                return null;

            default:
                note = "no team wish";
                return null;
        }
    }

    private static bool IsId(string value) => ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// Answers the lobby while the node is in <c>AwaitingTeam</c>. Returns when the server placed the node (phase past <c>AwaitingTeam</c>), after
    /// <see cref="MaxAttempts"/> refusals, or when there is nothing to ask for.
    /// </summary>
    internal static async Task<TeamStepResult> AnswerLobbyAsync(
        NodeLink link, FakeTeamState teams, TeamWish wish, string playerName, ulong seed, TimeSpan timeout, CancellationToken ct)
    {
        var rng = new DetRandom(DetHash.Hash(seed, 0x7EA17, (ulong)link.PlayerId));
        int requests = 0;
        int rejections = 0;
        string note = string.Empty;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (link.Phase != NodePhase.AwaitingTeam)
                return new TeamStepResult(true, requests, rejections, note);
            ulong key = ((ulong)link.PlayerId << 16) | (uint)(attempt + 1);
            var request = Decide(wish, teams, playerName, key, rng, out note);
            if (request is null)
            {
                // The table may not be complete yet (another node is creating the team): look again shortly.
                await Task.Delay(100, ct).ConfigureAwait(false);
                if (attempt == MaxAttempts - 1)
                    return new TeamStepResult(false, requests, rejections, note);
                continue;
            }

            await link.Client.SendPayloadAsync(request.Type, request.Payload, ct).ConfigureAwait(false);
            requests++;
            var result = await WaitResultAsync(link, teams, key, timeout, ct).ConfigureAwait(false);
            if (result is null)
                return new TeamStepResult(link.Phase != NodePhase.AwaitingTeam, requests, rejections, note + " (no answer)");
            if (result.Status == TeamRequestStatus.Ok)
            {
                await link.WaitPhaseAsync(NodePhase.SyncingSave, timeout, ct).ConfigureAwait(false);
                return new TeamStepResult(true, requests, rejections, $"{note}: placed in team {result.TeamId}");
            }

            rejections++;
            note = $"{note}: {result.Reason} {result.Detail}".TrimEnd();
            await Task.Delay(60, ct).ConfigureAwait(false);
        }

        return new TeamStepResult(link.Phase != NodePhase.AwaitingTeam, requests, rejections, note);
    }

    private static async Task<TeamRequestResultT?> WaitResultAsync(NodeLink link, FakeTeamState teams, ulong key, TimeSpan timeout, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (teams.ResultFor(key) is { } result)
                return result;
            if (link.Closed)
                return null;
            await Task.Delay(5, ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// A node that was placed by Auto (no lobby) and wants another team asks for the move once it is in game (<c>TeamChangeRequest</c>; the server
    /// answers NotPermitted unless <c>Teams.AllowSelfTeamChange</c> is on). Returns null when the node is where it wants to be or the team does not exist.
    /// </summary>
    internal static async Task<TeamStepResult?> RequestMoveAsync(NodeLink link, FakeTeamState teams, TeamWish wish, TimeSpan timeout, CancellationToken ct)
    {
        TeamInfoT? target = wish switch
        {
            TeamWish.Named named => teams.FindTeam(named.IdOrName),
            TeamWish.Slot slot => teams.FindTeam(TeamWish.SlotName(slot.Number)),
            _ => null,
        };
        if (target is null || target.TeamId == link.Team)
            return null;
        ulong key = ((ulong)link.PlayerId << 16) | 0xFFFF;
        var message = FakeTeamState.BuildTeamChangeRequest(key, target.TeamId);
        await link.Client.SendPayloadAsync(message.Type, message.Payload, ct).ConfigureAwait(false);
        var result = await WaitResultAsync(link, teams, key, timeout, ct).ConfigureAwait(false);
        if (result is null)
            return new TeamStepResult(false, 1, 0, $"move to '{target.Name}': no answer");
        return result.Status == TeamRequestStatus.Ok
            ? new TeamStepResult(true, 1, 0, $"moved to '{target.Name}' (team {target.TeamId})")
            : new TeamStepResult(false, 1, 1, $"move to '{target.Name}': {result.Reason} {result.Detail}".TrimEnd());
    }
}

/// <summary>Server settings that give a swarm layout (<c>--relations</c>, the presets of server-design 2.13), as command-line arguments for the server.</summary>
public static class TeamLayout
{
    /// <summary>
    /// The <c>--X4MP:Teams:...</c> arguments of a layout. The clients place themselves through the lobby (JoinMode=Lobby with AllowCreateInLobby); the
    /// authority is auto-assigned by Balance, which gives it the first team, "Team 1". Coop is the default Auto/SingleTeam join.
    /// </summary>
    public static IReadOnlyList<string> ServerSettings(RelationsPreset preset) => preset switch
    {
        RelationsPreset.Coop => ["--X4MP:Teams:JoinMode=Auto", "--X4MP:Teams:AutoAssign=SingleTeam"],
        RelationsPreset.Allied => Lobby("Allied"),
        RelationsPreset.Ffa or RelationsPreset.TwoTeams => Lobby("Hostile"),
        _ => [],
    };

    private static string[] Lobby(string relation) =>
    [
        "--X4MP:Teams:JoinMode=Lobby", "--X4MP:Teams:AllowCreateInLobby=true", "--X4MP:Teams:AutoAssign=Balance",
        "--X4MP:Teams:DefaultRelation=" + relation,
    ];
}
