using X4MP.Core.Relay;
using X4MP.Core.Session;
using X4MP.Core.World;
using X4MP.Persistence;
using X4MP.Proto;
using X4MP.Server.Api;

namespace X4MP.Server.Admin;

/// <summary>What the admin side knows about the live session at one moment.</summary>
internal sealed record LiveSession(SessionSnapshot Snapshot, long DbId, SessionRecord? Row, IReadOnlySet<int> Muted, IReadOnlyDictionary<int, MuteEntry> Mutes)
{
    /// <summary>True when there is a session to show: its row exists, or it left Idle (a node joined or it started).</summary>
    public bool Exists => DbId > 0 || Snapshot.Phase != SessionPhase.Idle;

    public string Phase => Snapshot.Phase.ToString();

    public bool IsConnected(long playerId) => Snapshot.Nodes.Any(n => n.PlayerId == playerId && n.Connected);

    public bool IsInSession(long playerId) => Snapshot.Nodes.Any(n => n.PlayerId == playerId);
}

/// <summary>
/// Reads the live session for the admin endpoints (the actor mailbox for fresh state, the database for history) and keeps the save an
/// admin selected when creating a session. The save is also written to <c>sessions.save_id</c>; the in-memory copy covers the write-behind
/// lag, so deleting that save is refused immediately.
/// </summary>
internal sealed class AdminSessions(SessionActor actor, SqliteAdminQueries queries, IChatControl chat, WorldMirror mirror, TimeProvider time)
{
    private volatile string? _selectedSha;

    public SessionActor Actor => actor;

    public TimeProvider Time => time;

    /// <summary>Remembers the save the admin chose for the current session (null = none).</summary>
    public void SelectSave(string? sha256) => _selectedSha = sha256;

    /// <summary>True while a session that has not ended holds this save as its selection.</summary>
    public bool IsSelected(string sha256) =>
        string.Equals(_selectedSha, sha256, StringComparison.Ordinal) && actor.Snapshot.Phase != SessionPhase.Ended;

    public async Task<LiveSession> GetLiveAsync()
    {
        var snapshot = await actor.GetSnapshotAsync().ConfigureAwait(false);
        long dbId = await actor.GetStoreSessionIdAsync().ConfigureAwait(false);
        var mutes = (await chat.MutedAsync().ConfigureAwait(false)).ToDictionary(m => m.PlayerId);
        var row = dbId > 0 ? queries.FindSession(dbId) : null;
        return new LiveSession(snapshot, dbId, row, mutes.Keys.ToHashSet(), mutes);
    }

    public Task<int> EntityCountAsync() => actor.CallAsync(() => mirror.Count);

    private long Uptime(DateTimeOffset? started, DateTimeOffset? ended)
    {
        if (started is null)
        {
            return 0;
        }

        return (long)Math.Max(0, ((ended ?? time.GetUtcNow()) - started.Value).TotalSeconds);
    }

    /// <summary>The summary of the live session (its phase and name come from the actor, the rest from its row).</summary>
    public SessionSummaryDto Summary(LiveSession live)
    {
        var row = live.Row;
        return new SessionSummaryDto(
            Math.Max(live.DbId, 0), live.Snapshot.SessionName, live.Phase, row?.SaveName, row?.SaveSha256, row?.StartedAt,
            Uptime(row?.StartedAt, null), live.Snapshot.Nodes.Count);
    }

    public SessionSummaryDto Summary(SessionRecord row) => new(
        row.Id, row.Name, row.State, row.SaveName, row.SaveSha256, row.StartedAt, Uptime(row.StartedAt, row.EndedAt), row.Players);

    public SessionDetailDto Detail(LiveSession live)
    {
        var now = time.GetUtcNow();
        var row = live.Row;
        return new SessionDetailDto(
            Math.Max(live.DbId, 0), live.Snapshot.SessionName, live.Phase, row?.SaveName, row?.SaveSha256, row?.CreatedAt ?? live.Snapshot.PhaseSince,
            row?.StartedAt, row?.EndedAt, row?.EndReason, Uptime(row?.StartedAt, null), live.Snapshot.Nodes.Count, true, live.Snapshot.PhaseSince,
            AdminMapping.ToDto(live.Snapshot.Authority), [.. live.Snapshot.Nodes.Select(n => AdminMapping.ToLive(n, now, live.Muted))]);
    }

    /// <summary>The detail of a past session (no live nodes).</summary>
    public SessionDetailDto Detail(SessionRecord row) => new(
        row.Id, row.Name, row.State, row.SaveName, row.SaveSha256, row.CreatedAt, row.StartedAt, row.EndedAt, row.EndReason,
        Uptime(row.StartedAt, row.EndedAt), row.Players, false, null, null, []);
}
