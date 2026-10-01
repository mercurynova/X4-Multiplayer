using X4MP.Core.Events;
using X4MP.Core.Net;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Session;

public sealed partial class SessionActor
{
    // ------------------------------------------------------------------ inbound frames

    private void OnMessage(AdmittedNode node, InboundFrame frame)
    {
        Interlocked.Increment(ref _framesReceived);
        if (!TryGetSlot(node, out var slot))
        {
            return; // a frame from a superseded or detached connection
        }

        slot.LastInboundTicks = frame.ReceivedTicks; // any inbound frame is proof of life (protocol.md 7)
        try
        {
            switch (frame.Type)
            {
                case MsgType.Ping:
                    OnPing(slot, frame);
                    break;
                case MsgType.Pong:
                    OnPong(slot, frame);
                    break;
                case MsgType.NodeStats:
                    OnNodeStats(slot, frame);
                    break;
                case MsgType.LoadStatus:
                    OnLoadStatus(slot, frame);
                    break;
                case MsgType.NodeReady:
                    OnNodeReady(slot, frame);
                    break;
                case MsgType.Disconnect:
                    OnDisconnect(slot, frame);
                    break;
                default:
                    DispatchToModules(slot, frame);
                    break;
            }
        }
        catch (ProtocolViolation violation)
        {
            node.Connection.Stats.AddViolation();
            node.Connection.Close(DisconnectCode.MalformedMessage, violation.Code.ToString());
        }
    }

    private void OnPing(SessionNode slot, InboundFrame frame)
    {
        var ping = MessageRegistry.Default.Decode<Ping>(frame.Frame);
        var pong = ControlFrames.Pong(ping.Seq, ping.SendTimeUs, (ulong)Math.Max(0, ServerUs(frame.ReceivedTicks)), (ulong)Math.Max(0, ServerUs(Now)));
        SendTo(slot, pong);
    }

    private void OnPong(SessionNode slot, InboundFrame frame)
    {
        var pong = MessageRegistry.Default.Decode<Pong>(frame.Frame);
        slot.PongsReceived++;
        if (slot.Clock.AddSample(
            unchecked((long)pong.EchoSendTimeUs),
            unchecked((long)pong.RecvTimeUs),
            unchecked((long)pong.ReplyTimeUs),
            ServerUs(frame.ReceivedTicks)))
        {
            X4MP.Core.Metrics.ServerMetrics.RecordRtt(slot.Clock.LatestRttUs / 1000.0);
        }
    }

    private void OnNodeStats(SessionNode slot, InboundFrame frame)
    {
        var s = MessageRegistry.Default.Decode<NodeStats>(frame.Frame).UnPack();
        var stats = new NodeStatsSnapshot(
            s.Fps, s.FrameMsP95, s.GameTime, s.Ghosts, s.SuppressedLocal, s.PendingMainThreadJobs, s.TcpSendQueueBytes,
            s.UdpRxLossPct, s.UdpActive, s.RxBytesPerS, s.TxBytesPerS, s.InterpDelayMs, s.ClockOffsetUs, s.RttMs, s.MemoryMb,
            s.MdHookState, s.TeamSetupState, _time.GetUtcNow());
        slot.Stats = stats;
        Publish(new NodeStatsReported(_time.GetUtcNow(), SessionId, slot.PlayerId, slot.Name, slot.IsAuthority, s.Fps));
        foreach (var module in _modules)
        {
            try
            {
                module.OnNodeStats(slot, stats);
            }
            catch (Exception ex)
            {
                LogModuleFailed(module.GetType().Name, nameof(ISessionModule.OnNodeStats), ex);
            }
        }
    }

    private void OnLoadStatus(SessionNode slot, InboundFrame frame)
    {
        var status = MessageRegistry.Default.Decode<LoadStatus>(frame.Frame);
        var trigger = status.Phase switch
        {
            NodePhase.SyncingSave => NodeTrigger.ReportSyncing,
            NodePhase.Verifying => NodeTrigger.ReportVerifying,
            NodePhase.Loading => NodeTrigger.ReportLoading,
            NodePhase.Matching => NodeTrigger.ReportMatching,
            NodePhase.CatchingUp => NodeTrigger.ReportCatchingUp,
            NodePhase.Failed => NodeTrigger.Fail,
            _ => (NodeTrigger?)null,
        };

        if (trigger is null || !ApplyNodeStep(slot, trigger.Value))
        {
            LogIgnored(slot.PlayerId, frame.Type, slot.Phase, "not a legal LoadStatus report in this phase");
            slot.Connection?.Stats.AddViolation();
        }
    }

    private void OnNodeReady(SessionNode slot, InboundFrame frame)
    {
        var ready = MessageRegistry.Default.Decode<NodeReady>(frame.Frame);
        if (!ApplyNodeStep(slot, NodeTrigger.Ready))
        {
            LogIgnored(slot.PlayerId, frame.Type, slot.Phase, "NodeReady before the universe was matched");
            slot.Connection?.Stats.AddViolation();
            return;
        }

        slot.UniverseEpoch = ready.UniverseEpoch;
    }

    private void OnDisconnect(SessionNode slot, InboundFrame frame)
    {
        var code = MessageRegistry.Default.Decode<Disconnect>(frame.Frame).Code;
        if (code == DisconnectCode.ClientReload)
        {
            // Extension reload: keep the slot for the resume grace and emit no leave (ADR-025).
            DetachNode(slot, DetachReason.ClientReload, closeWith: DisconnectCode.ClientReload);
        }
        else
        {
            LeaveNode(slot, code.ToString(), closeWith: DisconnectCode.ClientQuit);
        }
    }

    private void DispatchToModules(SessionNode slot, InboundFrame frame)
    {
        foreach (var module in _modules)
        {
            try
            {
                if (module.OnMessage(slot, frame))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                LogModuleFailed(module.GetType().Name, nameof(ISessionModule.OnMessage), ex);
            }
        }

        LogIgnored(slot.PlayerId, frame.Type, slot.Phase, "no module handles it");
    }

    // ------------------------------------------------------------------ node phase machine

    /// <summary>Applies a trigger to a node's phase machine. Returns false when the trigger is illegal in the current phase.</summary>
    private bool ApplyNodeStep(SessionNode slot, NodeTrigger trigger)
    {
        var step = NodeTransitions.Apply(slot.Phase, trigger, slot.PhaseBeforeDetach);
        switch (step.Kind)
        {
            case NodeStepKind.Move:
                ChangeNodePhase(slot, step.Phase);
                return true;
            case NodeStepKind.Stay:
                return true;
            case NodeStepKind.Leave:
                LeaveNode(slot, trigger.ToString(), closeWith: DisconnectCode.ClientQuit);
                return true;
            default:
                return false;
        }
    }

    private void ChangeNodePhase(SessionNode slot, NodePhase to)
    {
        var from = slot.Phase;
        if (from == to)
        {
            return;
        }

        slot.Phase = to;
        if (slot.Attached is { } attached)
        {
            attached.Phase = to; // also what the frame reader lets through
        }

        Publish(new NodePhaseChanged(_time.GetUtcNow(), SessionId, slot.PlayerId, slot.Name, from.ToString(), to.ToString()));
        foreach (var module in _modules)
        {
            try
            {
                module.OnNodePhaseChanged(slot, from, to);
            }
            catch (Exception ex)
            {
                LogModuleFailed(module.GetType().Name, nameof(ISessionModule.OnNodePhaseChanged), ex);
            }
        }

        // A reload or a socket drop is invisible to the others: no roster change for Detached or the resume back.
        if (slot.Announced && from != NodePhase.Detached && to != NodePhase.Detached)
        {
            BroadcastRosterUpsert(slot);
        }
    }

    /// <summary>The socket is gone (or reloading) but the slot stays for <c>ResumeGraceSeconds</c>. Emits no leave.</summary>
    private void DetachNode(SessionNode slot, DetachReason reason, DisconnectCode? closeWith)
    {
        var attached = slot.Attached;
        var trigger = reason == DetachReason.ClientReload ? NodeTrigger.ClientReload : NodeTrigger.SocketLost;
        var step = NodeTransitions.Apply(slot.Phase, trigger);
        if (step.Kind == NodeStepKind.Leave)
        {
            LeaveNode(slot, reason.ToString(), closeWith);
            return;
        }

        if (step.Kind != NodeStepKind.Move || attached is null)
        {
            return;
        }

        int graceSeconds = slot.IsAuthority ? AuthorityResumeSeconds : _netOptions.ResumeGraceSeconds;
        if (graceSeconds <= 0)
        {
            LeaveNode(slot, "ResumeGraceZero", closeWith);
            return;
        }

        long now = Now;
        slot.PhaseBeforeDetach = slot.Phase;
        ChangeNodePhase(slot, NodePhase.Detached); // also tells the old reader it is detached
        slot.Attached = null;
        slot.DetachedAt = _time.GetUtcNow();
        slot.DetachReason = reason;
        slot.DetachDeadline = After(now, graceSeconds);
        if (closeWith is { } code)
        {
            attached.Connection.Close(code, reason.ToString());
        }

        Publish(new PlayerDetached(_time.GetUtcNow(), SessionId, slot.PlayerId, slot.Name, reason.ToString()));
        Publish(new NodeDisconnected(_time.GetUtcNow(), SessionId, attached.Connection.Id.Value, slot.Name, reason.ToString()));
        LogDetached(slot.PlayerId, slot.Name, reason);
        foreach (var module in _modules)
        {
            try
            {
                module.OnNodeDetached(slot, reason);
            }
            catch (Exception ex)
            {
                LogModuleFailed(module.GetType().Name, nameof(ISessionModule.OnNodeDetached), ex);
            }
        }

        if (slot.IsAuthority)
        {
            OnAuthorityGone(slot, permanent: false);
        }
    }

    /// <summary>The slot is released for good: leave event, roster removal, optional <c>Disconnect</c> to the live socket.</summary>
    private void LeaveNode(SessionNode slot, string reason, DisconnectCode? closeWith)
    {
        if (!_nodes.TryGetValue(slot.PlayerId, out var current) || !ReferenceEquals(current, slot))
        {
            return;
        }

        _nodes.Remove(slot.PlayerId);
        var attached = slot.Attached;
        slot.Attached = null;
        slot.Phase = NodePhase.Detached;
        if (attached is not null)
        {
            attached.Phase = NodePhase.Detached;
            if (closeWith is { } code)
            {
                attached.Connection.Close(code, reason);
            }
        }

        if (_dbSessionId > 0)
        {
            _store.PlayerLeft(_dbSessionId, slot.PlayerId, _time.GetUtcNow(), reason);
        }

        Publish(new PlayerLeft(_time.GetUtcNow(), SessionId, slot.PlayerId, slot.Name, reason));
        if (attached is not null)
        {
            Publish(new NodeDisconnected(_time.GetUtcNow(), SessionId, attached.Connection.Id.Value, slot.Name, reason));
        }

        LogLeft(slot.PlayerId, slot.Name, reason);
        foreach (var module in _modules)
        {
            try
            {
                module.OnNodeLeft(slot, reason);
            }
            catch (Exception ex)
            {
                LogModuleFailed(module.GetType().Name, nameof(ISessionModule.OnNodeLeft), ex);
            }
        }

        BroadcastRosterRemoval(slot.PlayerId);
        if (slot.IsAuthority)
        {
            OnAuthorityGone(slot, permanent: true);
        }
    }

    // ------------------------------------------------------------------ sending

    private static void SendTo(SessionNode slot, OutboundFrame frame)
    {
        slot.Connection?.TrySend(frame);
        frame.Release();
    }

    private void Broadcast(OutboundFrame frame, SessionNode? except = null)
    {
        foreach (var slot in _nodes.Values)
        {
            if (slot.Announced && slot.Connection is { } connection && !ReferenceEquals(slot, except))
            {
                connection.TrySend(frame);
            }
        }

        frame.Release();
    }

    private void SendPing(SessionNode slot, long now)
    {
        if (slot.Connection is null)
        {
            return;
        }

        uint seq = ++slot.PingSeq;
        slot.PingsSent++;
        slot.NextPingTicks = After(now, Opt.PingIntervalMs / 1000.0);
        ulong sendUs = (ulong)Math.Max(0, ServerUs(now));
        SendTo(slot, ControlFrames.Encode(MsgType.Ping, fbb => X4MP.Proto.Ping.CreatePing(fbb, seq, sendUs).Value, 32));
    }

    private SessionStateT BuildSessionState() => new()
    {
        Phase = _phase,
        SessionName = SessionDisplayName,
        AuthorityPlayer = (ushort)Math.Clamp(_authorityPlayerId, 0, ushort.MaxValue),
        Paused = _phase == SessionPhase.Paused,
        PauseReason = string.Empty,
        GameTime = 0,
        TimeScale = 1,
        CurrentSaveSha256 = [],
        MaxPlayers = (ushort)Math.Clamp(_gateway.MaxPlayers, 0, ushort.MaxValue),
    };

    private static OutboundFrame Encode(SessionStateT state) =>
        ControlFrames.Encode(MsgType.SessionState, fbb => X4MP.Proto.SessionState.Pack(fbb, state).Value, 256);

    private static OutboundFrame Encode(RosterUpdateT roster) =>
        ControlFrames.Encode(MsgType.RosterUpdate, fbb => X4MP.Proto.RosterUpdate.Pack(fbb, roster).Value, 512);

    private void SendSessionState(SessionNode slot) => SendTo(slot, Encode(BuildSessionState()));

    private void BroadcastSessionState() => Broadcast(Encode(BuildSessionState()));

    private static PlayerInfoT PlayerInfo(SessionNode slot) => new()
    {
        PlayerId = (ushort)slot.PlayerId,
        Name = slot.Name,
        Roles = slot.Roles,
        Phase = slot.Phase == NodePhase.Detached ? slot.PhaseBeforeDetach : slot.Phase,
        PingMs = (ushort)Math.Clamp(Math.Round(slot.Clock.SmoothedRttMs), 0, ushort.MaxValue),
    };

    private void SendRoster(SessionNode to)
    {
        var roster = new RosterUpdateT { Full = true, Players = [], Removed = [] };
        foreach (var slot in _nodes.Values.OrderBy(n => n.PlayerId))
        {
            roster.Players.Add(PlayerInfo(slot));
        }

        SendTo(to, Encode(roster));
    }

    private void BroadcastRosterUpsert(SessionNode slot, SessionNode? except = null) =>
        Broadcast(Encode(new RosterUpdateT { Full = false, Players = [PlayerInfo(slot)], Removed = [] }), except);

    private void BroadcastRosterRemoval(int playerId) =>
        Broadcast(Encode(new RosterUpdateT { Full = false, Players = [], Removed = [(ushort)playerId] }));

    // ------------------------------------------------------------------ small helpers

    private long? SessionId => _dbSessionId > 0 ? _dbSessionId : null;

    private void Publish(DomainEvent domainEvent)
    {
        try
        {
            _events.Publish(domainEvent);
        }
        catch (Exception ex)
        {
            LogModuleFailed(_events.GetType().Name, nameof(IEventPublisher.Publish), ex);
        }
    }

    /// <summary>How long a lost authority may resume: the longer of the resume grace and the authority grace.</summary>
    private int AuthorityResumeSeconds => Math.Max(_netOptions.ResumeGraceSeconds, Opt.AuthorityGraceSeconds);

    private long Now => _time.GetTimestamp();

    private long After(long from, double seconds)
    {
        double ticks = seconds * _time.TimestampFrequency;
        return ticks >= long.MaxValue - from ? long.MaxValue : from + (long)ticks;
    }

    /// <summary>Microseconds on the server clock (monotonic, since the actor was created).</summary>
    private long ServerUs(long timestamp) => (long)_time.GetElapsedTime(_startTimestamp, timestamp).TotalMicroseconds;

}
