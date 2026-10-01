using X4MP.Core.Net;
using X4MP.Core.Settings;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Session;

/// <summary>
/// Timers and limits of the <see cref="SessionActor"/>. Defaults follow ADR-026. The resume window
/// (<c>ResumeGraceSeconds</c>, 60 s) is a <see cref="X4MP.Core.Net.NetOptions"/> setting because the gateway
/// also announces it in <c>Welcome</c>. The actor reads the Live settings through a provider on every use.
/// </summary>
[SettingsSection(SectionName, "Session")]
public sealed record SessionActorOptions
{
    public const string SectionName = "X4MP:Session";

    /// <summary>Name stored in <c>sessions.name</c> and sent in <c>SessionState</c>. Empty = the server name.</summary>
    [Setting("Session name (empty = the server name)", MaxLength = 64)]
    public string SessionName { get; set; } = string.Empty;

    /// <summary>How often the actor wakes to check deadlines and publish the snapshot (ms).</summary>
    [Setting("Session actor tick interval (ms)", Min = 20, Max = 5000)]
    public int TickIntervalMs { get; set; } = 250;

    /// <summary>Ping every attached node this often (protocol.md 7: 1000 ms).</summary>
    [Setting("Ping interval per node (ms)", Scope = SettingScope.Live, Min = 100, Max = 30000)]
    public int PingIntervalMs { get; set; } = 1000;

    /// <summary>No frame from a node for this long closes it with <c>HeartbeatTimeout</c> (protocol.md 7: 10000 ms).</summary>
    [Setting("Heartbeat timeout: close a silent node after (ms)", Scope = SettingScope.Live, Min = 1000, Max = 600000)]
    public int HeartbeatTimeoutMs { get; set; } = 10_000;

    /// <summary><c>AuthorityLost</c> to <c>Stopping</c> (ADR-026: 120 s). Also the authority's resume window.</summary>
    [Setting("Authority grace: time to get the authority back before the session stops (s)", Scope = SettingScope.Live, Min = 5, Max = 3600)]
    public int AuthorityGraceSeconds { get; set; } = 120;

    /// <summary>
    /// <c>Stopping</c> to <c>Ended</c> when nobody completes the stop (the save service does that in M1-12). A stop
    /// with a dead authority has nothing to wait for.
    /// </summary>
    [Setting("Stop timeout: end a stopping session after (s)", Scope = SettingScope.Live, Min = 1, Max = 3600)]
    public int StoppingTimeoutSeconds { get; set; } = 30;

    /// <summary>Samples the min-RTT clock filter looks at (protocol.md 7: 16).</summary>
    [Setting("Clock-sync samples kept per node", Min = 1, Max = 256)]
    public int ClockSampleWindow { get; set; } = ClockSync.DefaultWindow;
}

/// <summary>
/// Persistence seam of the actor (the <c>sessions</c> and <c>session_players</c> tables; <c>session_events</c> is written by the
/// event-bus subscriber from the domain events the actor publishes). Everything except
/// <see cref="BeginSessionAsync"/> must return without waiting for the database (write-behind).
/// </summary>
public interface ISessionStore
{
    /// <summary>Creates the <c>sessions</c> row and returns its id. Awaited once per session.</summary>
    ValueTask<long> BeginSessionAsync(string name, Guid sessionGuid, DateTimeOffset at, CancellationToken ct);

    /// <summary>The session phase changed. The store derives started_at (first WaitingForAuthority) and ended_at (Ended).</summary>
    void RecordPhase(long sessionId, SessionPhase phase, int authorityPlayerId, DateTimeOffset at, string? reason);

    /// <summary>A player joined (a new <c>session_players</c> row). Resumes do not call this.</summary>
    void PlayerJoined(long sessionId, int playerId, Role roles, DateTimeOffset at);

    /// <summary>A player left for good (closes the open <c>session_players</c> row).</summary>
    void PlayerLeft(long sessionId, int playerId, DateTimeOffset at, string reason);

}

/// <summary>Discards everything (running the server without persistence).</summary>
public sealed class NullSessionStore : ISessionStore
{
    private long _next;

    public ValueTask<long> BeginSessionAsync(string name, Guid sessionGuid, DateTimeOffset at, CancellationToken ct) =>
        ValueTask.FromResult(Interlocked.Increment(ref _next));

    public void RecordPhase(long sessionId, SessionPhase phase, int authorityPlayerId, DateTimeOffset at, string? reason)
    {
    }

    public void PlayerJoined(long sessionId, int playerId, Role roles, DateTimeOffset at)
    {
    }

    public void PlayerLeft(long sessionId, int playerId, DateTimeOffset at, string reason)
    {
    }

}

/// <summary>Why a node lost its socket without leaving.</summary>
public enum DetachReason
{
    SocketLost,
    ClientReload,
    HeartbeatTimeout,
}

/// <summary>
/// A component that runs on the actor's thread: the world mirror (M1-06), interest (M1-07), replication
/// (M1-08), relay and chat (M1-10), the save service (M1-12). Every callback runs inside the actor loop, so
/// implementations need no locks, must not block and must not throw (exceptions are logged and swallowed).
/// All methods are optional.
/// </summary>
public interface ISessionModule
{
    /// <summary>
    /// A node is attached and its <c>Welcome</c> is queued (so anything sent here follows the Welcome).
    /// <paramref name="resumed"/>: the node came back inside the grace; <see cref="SessionNode.BaselineEpoch"/> was
    /// incremented, so reset this node's replication baselines and send a keyframe (protocol.md 6.6).
    /// </summary>
    void OnNodeAttached(SessionNode node, bool resumed)
    {
    }

    /// <summary>
    /// A node passed the handshake and its slot was created (<paramref name="resumed"/> false) or taken back (true); the
    /// <c>Welcome</c> is not sent yet, so this is where a module fills the fields it owns (the team fields) and decides
    /// about the node. Return anything but <see cref="AdmissionVerdict.Accept"/> to refuse it (for example
    /// <c>NoFactionSlot</c>); the slot is released again. Do not send frames here, and keep node-phase changes to
    /// <see cref="ISessionNodeDriver"/>, which queues them behind the admission.
    /// </summary>
    AdmissionVerdict OnNodeAdmitting(SessionNode node, WelcomeT welcome, bool resumed) => AdmissionVerdict.Accept;

    /// <summary>The server-side phase of a node changed (including Detached).</summary>
    void OnNodePhaseChanged(SessionNode node, NodePhase previous, NodePhase current)
    {
    }

    /// <summary>The socket is gone but the slot is kept for the resume grace.</summary>
    void OnNodeDetached(SessionNode node, DetachReason reason)
    {
    }

    /// <summary>The node left for good (grace expired, quit, kicked, session ended). It is no longer in the roster.</summary>
    void OnNodeLeft(SessionNode node, string reason)
    {
    }

    void OnSessionPhaseChanged(SessionPhase previous, SessionPhase current)
    {
    }

    /// <summary>
    /// A frame the actor does not handle itself (everything except Ping, Pong, NodeStats, LoadStatus, NodeReady,
    /// Disconnect). Return true when consumed; the first module that does so ends the dispatch.
    /// </summary>
    bool OnMessage(SessionNode node, InboundFrame frame) => false;

    void OnNodeStats(SessionNode node, NodeStatsSnapshot stats)
    {
    }

    /// <summary>Every tick (<see cref="SessionActorOptions.TickIntervalMs"/>), with the current <see cref="TimeProvider"/> timestamp.</summary>
    void OnTick(long timestamp)
    {
    }

    /// <summary>
    /// The settings flagged <c>PushToNodes</c> changed (the actor is the <see cref="X4MP.Core.Settings.ISessionSettingsPusher"/>).
    /// The wire message for it belongs to the team and economy tasks; until then modules may use the snapshot.
    /// </summary>
    void OnSettingsChanged(X4MP.Core.Settings.SessionSettingsSnapshot settings)
    {
    }
}

/// <summary>
/// What a module may ask of the <see cref="SessionActor"/> beyond its callbacks: queue work onto the actor thread,
/// drive node phases and remove players. The actor implements it and binds every <see cref="ISessionActorBound"/> module.
/// </summary>
public interface ISessionNodeDriver
{
    /// <summary>The <c>sessions</c> row id (null before the first player or the admin started the session). Read it on the actor thread.</summary>
    long? StoreSessionId { get; }

    /// <summary>Runs <paramref name="work"/> on the actor thread and returns its result.</summary>
    Task<T> CallAsync<T>(Func<T> work);

    /// <summary>Raises a server-driven node trigger (<see cref="NodeTrigger.RequireTeam"/>, <see cref="NodeTrigger.TeamAssigned"/>, ...). Queued.</summary>
    Task<TransitionResult> ApplyNodeTriggerAsync(int playerId, NodeTrigger trigger);

    /// <summary>Kicks a player: sends <c>Disconnect{code}</c> and frees the slot.</summary>
    Task<bool> RemoveNodeAsync(int playerId, DisconnectCode code, string reason);
}

/// <summary>A module that wants the <see cref="ISessionNodeDriver"/> (called once, when the actor is constructed).</summary>
public interface ISessionActorBound
{
    void Bind(ISessionNodeDriver driver);
}
