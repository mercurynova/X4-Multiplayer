using System.Globalization;
using System.Threading.Channels;
using X4MP.Core.Teams;
using X4MP.Server.Api;

namespace X4MP.Server.Hubs;

/// <summary>
/// The teams topic of the admin hub (M1-T5). The team module raises <c>Changed</c> on the actor thread after every team, membership or relation
/// change; the broadcaster only marks the topic dirty there. A short while later (so a burst, such as a preset, becomes one picture) it builds the
/// whole state on the actor, compares it with the last one it sent and pushes only what differs: <c>TeamUpserted</c>, <c>TeamDeleted</c>,
/// <c>TeamMemberChanged</c>, <c>PlayerAwaitingTeam</c>, <c>TeamRelationsChanged</c> and <c>TeamPolicyChanged</c>, or one <c>TeamsReset</c> when
/// a player vanished from both lists. Nothing is built while the topic has no subscribers (<see cref="PayloadsBuilt"/> is the spy).
/// </summary>
public sealed partial class AdminBroadcaster
{
    private const int TeamsCoalesceMs = 40;

    private readonly Channel<bool> _teamsSignal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly TeamsPushState _teamsState;

    private void AttachTeams() => _teams.Changed += OnTeamsChanged;

    private void DetachTeams() => _teams.Changed -= OnTeamsChanged;

    private void OnTeamsChanged(TeamDirectoryChanged change)
    {
        _ = change;
        MarkTeamsDirty();
    }

    /// <summary>Asks for a diff and push of the teams state (no-op, and the baseline is dropped, while nobody subscribed).</summary>
    private void MarkTeamsDirty()
    {
        if (_subs.Count(HubTopic.Teams) == 0)
        {
            _teamsState.Clear();
            return;
        }

        _teamsSignal.Writer.TryWrite(true);
    }

    private async Task TeamsLoop(CancellationToken ct)
    {
        try
        {
            while (await _teamsSignal.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                _teamsSignal.Reader.TryRead(out _);
                await Task.Delay(TimeSpan.FromMilliseconds(TeamsCoalesceMs), _time, ct).ConfigureAwait(false);
                try
                {
                    await FlushTeamsAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogTickFailed(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task FlushTeamsAsync()
    {
        if (_subs.Count(HubTopic.Teams) == 0)
        {
            _teamsState.Clear();
            return;
        }

        var next = await _actor.CallAsync(_teamViews.State).ConfigureAwait(false);
        Built("teams");
        var previous = _teamsState.Exchange(next);
        var clients = _subs.In(HubTopic.Teams).ToList();
        if (clients.Count == 0)
        {
            return;
        }

        foreach (var push in TeamsDiff.Compute(previous, next))
        {
            PostTo(clients, push.Send, push.Key);
        }
    }
}

/// <summary>The last teams state sent to the topic (the baseline of the diff), shared by the hub (which seeds it on subscribe) and the broadcaster.</summary>
internal sealed class TeamsPushState
{
    private readonly Lock _gate = new();
    private TeamsStateDto? _last;

    public void Clear()
    {
        lock (_gate)
        {
            _last = null;
        }
    }

    /// <summary>Remembers what a new subscriber was given, unless a newer picture is already the baseline.</summary>
    public void SeedIfEmpty(TeamsStateDto state)
    {
        lock (_gate)
        {
            _last ??= state;
        }
    }

    public TeamsStateDto? Exchange(TeamsStateDto next)
    {
        lock (_gate)
        {
            var previous = _last;
            _last = next;
            return previous;
        }
    }
}

/// <summary>One hub push of the teams topic: the call and its coalescing key.</summary>
public sealed record TeamsPush(Func<IAdminClient, Task> Send, string? Key);

/// <summary>Compares two teams states and lists the pushes that bring a client from the first to the second.</summary>
public static class TeamsDiff
{
    public static List<TeamsPush> Compute(TeamsStateDto? previous, TeamsStateDto next)
    {
        if (previous is null)
        {
            return [new(c => c.TeamsReset(next), "teams-reset")];
        }

        var pushes = new List<TeamsPush>();
        var oldPlayers = Players(previous);
        var newPlayers = Players(next);
        if (oldPlayers.Keys.Any(id => !newPlayers.ContainsKey(id)))
        {
            return [new(c => c.TeamsReset(next), "teams-reset")];
        }

        var oldTeams = previous.Teams.ToDictionary(t => t.Id);
        foreach (var gone in oldTeams.Keys.Where(id => next.Teams.All(t => t.Id != id)))
        {
            long id = gone;
            pushes.Add(new(c => c.TeamDeleted(id), "team-deleted:" + id.ToString(CultureInfo.InvariantCulture)));
        }

        foreach (var team in next.Teams)
        {
            if (!oldTeams.TryGetValue(team.Id, out var before) || before != team)
            {
                var dto = team;
                pushes.Add(new(c => c.TeamUpserted(dto), "team:" + team.Id.ToString(CultureInfo.InvariantCulture)));
            }
        }

        var waiting = previous.Unassigned.Select(m => m.PlayerId).ToHashSet();
        foreach (var (id, member) in newPlayers)
        {
            if (oldPlayers.TryGetValue(id, out var before) && before == member)
            {
                continue;
            }

            var dto = member;
            string suffix = id.ToString(CultureInfo.InvariantCulture);
            pushes.Add(new(c => c.TeamMemberChanged(dto), "member:" + suffix));
            if (dto.TeamId is null && !waiting.Contains(id))
            {
                pushes.Add(new(c => c.PlayerAwaitingTeam(dto), "awaiting:" + suffix));
            }
        }

        if (!RelationsEqual(previous.Relations, next.Relations))
        {
            pushes.Add(new(c => c.TeamRelationsChanged(next.Relations), "teams-relations"));
        }

        if (previous.Policy != next.Policy)
        {
            pushes.Add(new(c => c.TeamPolicyChanged(next.Policy), "teams-policy"));
        }

        return pushes;
    }

    private static Dictionary<long, TeamMemberDto> Players(TeamsStateDto state)
    {
        var all = new Dictionary<long, TeamMemberDto>();
        foreach (var m in state.Unassigned)
        {
            all[m.PlayerId] = m;
        }

        foreach (var m in state.Members)
        {
            all[m.PlayerId] = m;
        }

        return all;
    }

    private static bool RelationsEqual(TeamRelationsDto a, TeamRelationsDto b) =>
        a.Version == b.Version && a.DefaultRelation == b.DefaultRelation && a.Entries.SequenceEqual(b.Entries);
}
