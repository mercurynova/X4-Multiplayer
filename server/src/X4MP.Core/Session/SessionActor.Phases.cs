using X4MP.Core.Events;
using X4MP.Core.Net;
using X4MP.Proto;

namespace X4MP.Core.Session;

public sealed partial class SessionActor
{
    // State owned by the actor loop. Nothing below is touched from another thread.
    private readonly Dictionary<int, SessionNode> _nodes = [];
    private readonly List<SessionNode> _scratch = [];
    private SessionPhase _phase = SessionPhase.Idle;
    private SessionPhase _phaseBeforeLoss = SessionPhase.Running;
    private DateTimeOffset _phaseSince;
    private long _authorityDeadline = long.MaxValue;
    private long _stoppingDeadline = long.MaxValue;
    private int _authorityPlayerId;
    private Guid _sessionGuid;
    private long _dbSessionId;
    private string _sessionName = string.Empty;
    private long _snapshotVersion;

    private string SessionDisplayName =>
        !string.IsNullOrEmpty(_sessionName) ? _sessionName
        : !string.IsNullOrEmpty(Opt.SessionName) ? Opt.SessionName
        : _gateway.ServerName;

    // ------------------------------------------------------------------ session phase machine

    private async ValueTask EnsureSessionAsync()
    {
        if (_dbSessionId != 0)
        {
            return;
        }

        try
        {
            _dbSessionId = await _store.BeginSessionAsync(SessionDisplayName, _sessionGuid, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogModuleFailed(_store.GetType().Name, nameof(ISessionStore.BeginSessionAsync), ex);
            _dbSessionId = -1; // run without persistence rather than refusing players
        }
    }

    private TransitionResult Transition(SessionTrigger trigger, string? reason)
    {
        var from = _phase;
        if (!SessionTransitions.TryNext(from, trigger, out var to))
        {
            return TransitionResult.Failed(from.ToString(), $"{trigger} is not legal in {from}");
        }

        long now = Now;
        _phase = to;
        _phaseSince = _time.GetUtcNow();
        _gateway.Phase = to;
        if (to == SessionPhase.AuthorityLost)
        {
            _phaseBeforeLoss = from;
            _authorityDeadline = After(now, Opt.AuthorityGraceSeconds);
        }
        else if (from == SessionPhase.AuthorityLost)
        {
            _authorityDeadline = long.MaxValue;
        }

        if (to == SessionPhase.Stopping)
        {
            _stoppingDeadline = After(now, Opt.StoppingTimeoutSeconds);
        }

        LogPhase(_sessionGuid, from, to, reason);
        Publish(new SessionStateChanged(_time.GetUtcNow(), SessionId, from.ToString(), to.ToString(), reason ?? trigger.ToString()));
        if (_dbSessionId > 0)
        {
            _store.RecordPhase(_dbSessionId, to, _authorityPlayerId, _phaseSince, reason);
        }

        BroadcastSessionState();
        foreach (var module in _modules)
        {
            try
            {
                module.OnSessionPhaseChanged(from, to);
            }
            catch (Exception ex)
            {
                LogModuleFailed(module.GetType().Name, nameof(ISessionModule.OnSessionPhaseChanged), ex);
            }
        }

        if (to == SessionPhase.Ended)
        {
            _scratch.Clear();
            _scratch.AddRange(_nodes.Values);
            foreach (var slot in _scratch)
            {
                LeaveNode(slot, "SessionEnded", closeWith: DisconnectCode.ServerShutdown);
            }

            _scratch.Clear();
        }
        else if (to == SessionPhase.WaitingForAuthority && from == SessionPhase.AuthorityLoading)
        {
            ResetAuthority(); // the authority never got the universe up; the next one may differ
        }
        else if (to == SessionPhase.Idle)
        {
            ResetAuthority();
            _sessionGuid = Guid.NewGuid();
            _gateway.SessionId = _sessionGuid;
            _dbSessionId = 0;
            _sessionName = string.Empty;
        }

        return new TransitionResult(true, from.ToString(), to.ToString(), null);
    }

    private void ResetAuthority()
    {
        _authorityPlayerId = 0;
        _authorityDeadline = long.MaxValue;
        _gateway.Authority = null;
        _gateway.DesignatedAuthorityPlayerId = 0;
        _gateway.AuthorityLive = false;
    }

    /// <summary>The authority's slot detached (<paramref name="permanent"/> false) or left (true).</summary>
    private void OnAuthorityGone(SessionNode slot, bool permanent)
    {
        if (_authorityPlayerId != slot.PlayerId)
        {
            return;
        }

        _gateway.AuthorityLive = false;
        switch (_phase)
        {
            case SessionPhase.Running or SessionPhase.Paused:
                Publish(new AuthorityChanged(_time.GetUtcNow(), SessionId, slot.PlayerId, null, permanent ? "authority left" : "authority socket lost"));
                Transition(SessionTrigger.AuthorityLost, permanent ? "authority left" : "authority socket lost");
                break;
            case SessionPhase.AuthorityLoading when permanent:
                Transition(SessionTrigger.AuthorityLoadFailed, "authority left while loading");
                break;
        }
    }

    // ------------------------------------------------------------------ tick

    private void OnTick()
    {
        long now = Now;
        _scratch.Clear();
        _scratch.AddRange(_nodes.Values);
        foreach (var slot in _scratch)
        {
            if (!_nodes.ContainsKey(slot.PlayerId))
            {
                continue;
            }

            if (slot.Attached is { } attached)
            {
                if (_time.GetElapsedTime(slot.LastInboundTicks, now) > TimeSpan.FromMilliseconds(Opt.HeartbeatTimeoutMs))
                {
                    attached.Connection.Close(DisconnectCode.HeartbeatTimeout, "no frames within the heartbeat timeout");
                    DetachNode(slot, DetachReason.HeartbeatTimeout, closeWith: null);
                    continue;
                }

                if (slot.Announced && now >= slot.NextPingTicks)
                {
                    SendPing(slot, now);
                }
            }
            else if (now >= slot.DetachDeadline)
            {
                LeaveNode(slot, "ResumeGraceExpired", closeWith: null);
            }
        }

        _scratch.Clear();

        if (_phase == SessionPhase.AuthorityLost && now >= _authorityDeadline)
        {
            Transition(SessionTrigger.GraceExpired, "authority did not return within AuthorityGraceSeconds");
        }

        if (_phase == SessionPhase.Stopping && now >= _stoppingDeadline)
        {
            Transition(SessionTrigger.StopCompleted, "stop timeout");
        }

        foreach (var module in _modules)
        {
            try
            {
                module.OnTick(now);
            }
            catch (Exception ex)
            {
                LogModuleFailed(module.GetType().Name, nameof(ISessionModule.OnTick), ex);
            }
        }

        PublishSnapshot();
    }

    // ------------------------------------------------------------------ read model

    private SessionSnapshot PublishSnapshot()
    {
        long now = Now;
        var nodes = new List<NodeSnapshot>(_nodes.Count);
        foreach (var slot in _nodes.Values.OrderBy(n => n.PlayerId))
        {
            nodes.Add(new NodeSnapshot(
                slot.PlayerId,
                slot.Name,
                slot.Roles,
                slot.Phase,
                slot.Attached is not null,
                slot.Connection?.Id.Value,
                slot.RemoteAddress?.ToString(),
                slot.Clock.SmoothedRttMs,
                slot.Clock.MinRttUs / 1000.0,
                slot.Clock.OffsetUs,
                (int)Math.Min(slot.Clock.TotalSamples, int.MaxValue),
                slot.Stats,
                slot.JoinedAt,
                slot.DetachedAt,
                slot.DetachReason,
                slot.Attached is null && slot.DetachDeadline != long.MaxValue ? Math.Max(0, _time.GetElapsedTime(now, slot.DetachDeadline).TotalSeconds) : null,
                slot.BaselineEpoch));
        }

        AuthoritySnapshot authority;
        if (_authorityPlayerId == 0)
        {
            authority = new AuthoritySnapshot(AuthorityStatus.None, 0, null, null, _gateway.Authority?.GameBuild, _gateway.Authority?.ModVersion);
        }
        else
        {
            _nodes.TryGetValue(_authorityPlayerId, out var slot);
            bool live = slot?.Attached is not null;
            double? grace = _phase == SessionPhase.AuthorityLost && _authorityDeadline != long.MaxValue
                ? Math.Max(0, _time.GetElapsedTime(now, _authorityDeadline).TotalSeconds)
                : null;
            authority = new AuthoritySnapshot(
                live ? AuthorityStatus.Live : AuthorityStatus.Lost, _authorityPlayerId, slot?.Name, live ? null : grace,
                _gateway.Authority?.GameBuild, _gateway.Authority?.ModVersion);
        }

        var snapshot = new SessionSnapshot(
            ++_snapshotVersion, _time.GetUtcNow(), _sessionGuid, SessionDisplayName, _phase, _phaseSince, authority, nodes);
        _snapshot = snapshot;
        return snapshot;
    }
}
