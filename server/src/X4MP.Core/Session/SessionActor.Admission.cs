using X4MP.Core.Events;
using X4MP.Core.Net;
using X4MP.Proto;

namespace X4MP.Core.Session;

public sealed partial class SessionActor
{
    // ------------------------------------------------------------------ IAdmissionHandler

    /// <summary>
    /// Decides on the actor thread whether the node may join, resume or must be refused, and registers its slot.
    /// Nothing is sent here (the gateway sends <c>Welcome</c> next); announcements follow in
    /// <see cref="OnAdmittedAsync"/>.
    /// </summary>
    public async ValueTask<AdmissionVerdict> BeforeWelcomeAsync(AdmittedNode node, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(node);
        var reply = new TaskCompletionSource<AdmissionVerdict>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(new AdmitInput(node, reply)))
        {
            return new AdmissionVerdict(DisconnectCode.ServerShutdown, "server stopping");
        }

        using var registration = ct.Register(static state => ((TaskCompletionSource<AdmissionVerdict>)state!).TrySetCanceled(), reply);
        return await reply.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Attaches the node (announces the session to it) and then pumps its frames into the mailbox until the
    /// connection ends.
    /// </summary>
    public async Task OnAdmittedAsync(AdmittedNode node, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!Post(new AttachInput(node)))
        {
            node.Connection.Close(DisconnectCode.ServerShutdown, "server stopping");
            return;
        }

        var inbox = new NodeInbox(
            node.Connection.Stats,
            Math.Max(1, _netOptions.InboundQueueFramesPerNode),
            new ViolationTracker(_netOptions.InboundOverflowLimitPerMinute, _time),
            exempt: node.IsAuthority);
        try
        {
            while (await node.Reader.ReadAsync(ct).ConfigureAwait(false) is { } frame)
            {
                if (frame.Type == MsgType.PlayerState)
                {
                    // Latest wins: a flood of states costs one mailbox entry, never a backlog.
                    if (inbox.OfferLatest(frame) && !Post(new StateInput(node, inbox)))
                    {
                        break;
                    }

                    continue;
                }

                if (inbox.Full)
                {
                    if (inbox.Exempt)
                    {
                        await inbox.WaitForRoomAsync(ct).ConfigureAwait(false); // the authority is never dropped: back-pressure
                    }
                    else
                    {
                        if (inbox.RecordDrop())
                        {
                            LogInboundOverflow(node.PlayerId);
                            node.Connection.Close(DisconnectCode.RateLimited, "inbound queue overflow");
                            break;
                        }

                        continue;
                    }
                }

                inbox.Enter();
                if (!Post(new MessageInput(node, frame, inbox)))
                {
                    inbox.Leave();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            LogReadFailed(node.PlayerId, ex);
        }
        finally
        {
            Post(new EndedInput(node));
        }
    }

    // ------------------------------------------------------------------ admission on the actor

    private async ValueTask HandleAdmitAsync(AdmitInput input)
    {
        AdmissionVerdict verdict;
        try
        {
            verdict = await AdmitAsync(input.Node).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogInputFailed("Admit", ex);
            verdict = new AdmissionVerdict(DisconnectCode.InternalError, "internal error");
        }

        input.Reply.TrySetResult(verdict);
    }

    private async ValueTask<AdmissionVerdict> AdmitAsync(AdmittedNode node)
    {
        if (_phase is SessionPhase.Stopping or SessionPhase.Ended or SessionPhase.Migrating)
        {
            return new AdmissionVerdict(DisconnectCode.NotJoinable, $"the session is {_phase}");
        }

        if (node.IsAuthority && _authorityPlayerId != 0 && _authorityPlayerId != node.PlayerId)
        {
            return new AdmissionVerdict(DisconnectCode.RoleUnavailable, "the session already has an authority");
        }

        long now = Now;
        _nodes.TryGetValue(node.PlayerId, out var slot);
        var token = node.Hello.ResumeToken;
        bool hasToken = token is not null && (token.Lo | token.Hi) != 0;

        if (hasToken)
        {
            // Resume (protocol.md 4.3): token and key (the slot is found by key) must match, inside the grace.
            // A node whose old socket is not yet declared dead may take its slot over: it was superseded already.
            bool valid = slot is not null
                && slot.ResumeToken.Lo == token!.Lo && slot.ResumeToken.Hi == token.Hi
                && (slot.Attached is not null || now < slot.DetachDeadline)
                && (slot.Roles & ~Role.Admin) == (node.Roles & ~Role.Admin);
            if (!valid)
            {
                return new AdmissionVerdict(DisconnectCode.ResumeExpired, "the resume token is unknown or expired; join again");
            }

            ResumeSlot(slot!, node, now);
        }
        else
        {
            if (slot is not null)
            {
                // The same player joining fresh replaces the old slot (a reload that lost its token).
                LeaveNode(slot, "Rejoined", closeWith: null);
            }

            await EnsureSessionAsync().ConfigureAwait(false);
            JoinSlot(node, now);
        }

        node.Welcome.ServerTimeUs = (ulong)Math.Max(0, ServerUs(now));
        node.Welcome.HeartbeatIntervalMs = (ushort)Math.Clamp(Opt.PingIntervalMs, 1, ushort.MaxValue);
        node.Welcome.HeartbeatTimeoutMs = (ushort)Math.Clamp(Opt.HeartbeatTimeoutMs, 1, ushort.MaxValue);
        if (node.IsAuthority)
        {
            // The authority's resume window is the authority grace (architecture 4.2: AuthorityLost to Running on resume).
            node.Welcome.ResumeGraceS = (ushort)Math.Clamp(AuthorityResumeSeconds, 0, ushort.MaxValue);
        }

        if (_nodes.TryGetValue(node.PlayerId, out var admitted))
        {
            var refusal = AskModulesAboutAdmission(admitted, node.Welcome, hasToken);
            if (!refusal.Accepted)
            {
                LeaveNode(admitted, refusal.Message ?? refusal.Code.ToString(), closeWith: null); // the gateway sends the Disconnect
                return refusal;
            }
        }

        return AdmissionVerdict.Accept;
    }

    /// <summary>Lets the modules fill the <c>Welcome</c> (teams) or refuse the node, on the actor thread, before the Welcome is sent.</summary>
    private AdmissionVerdict AskModulesAboutAdmission(SessionNode slot, WelcomeT welcome, bool resumed)
    {
        foreach (var module in _modules)
        {
            try
            {
                var verdict = module.OnNodeAdmitting(slot, welcome, resumed);
                if (!verdict.Accepted)
                {
                    return verdict;
                }
            }
            catch (Exception ex)
            {
                LogModuleFailed(module.GetType().Name, nameof(ISessionModule.OnNodeAdmitting), ex);
            }
        }

        return AdmissionVerdict.Accept;
    }

    private void JoinSlot(AdmittedNode node, long now)
    {
        var slot = new SessionNode(node.PlayerId, node.Name, node.KeyHash, node.Roles, _time.GetUtcNow())
        {
            Attached = node,
            RemoteAddress = node.RemoteAddress,
            ResumeToken = node.Welcome.ResumeToken ?? new Id128T(),
            LastJournalSeq = node.Hello.LastJournalSeq,
            LastInboundTicks = now,
            NextPingTicks = now,
            Clock = new ClockSync(Opt.ClockSampleWindow),
        };

        // Observers need no save: they skip the load pipeline.
        if ((node.Roles & (Role.Authority | Role.Client)) == 0)
        {
            slot.Phase = NodePhase.InGame;
        }

        _nodes[slot.PlayerId] = slot;
        if (_dbSessionId > 0)
        {
            _store.PlayerJoined(_dbSessionId, slot.PlayerId, slot.Roles, slot.JoinedAt);
        }

        Publish(new PlayerJoined(_time.GetUtcNow(), SessionId, slot.PlayerId, slot.Name, slot.Roles.ToString()));
        LogJoined(slot.PlayerId, slot.Name, slot.Roles);

        if (slot.IsAuthority)
        {
            _authorityPlayerId = slot.PlayerId;
            _gateway.DesignatedAuthorityPlayerId = slot.PlayerId;
            _gateway.AuthorityLive = true;
            Publish(new AuthorityChanged(_time.GetUtcNow(), SessionId, null, slot.PlayerId, "authority admitted"));
            if (_gateway.Authority is null)
            {
                var h = node.Hello;
                _gateway.Authority = new AuthorityIdentity(h.GameBuild ?? string.Empty, h.ModVersion ?? string.Empty, h.ModBuild ?? string.Empty,
                    h.ExtensionsHash?.ToArray() ?? [], h.Extensions ?? [], h.ExtensionList);
            }

            switch (_phase)
            {
                case SessionPhase.Idle:
                    Transition(SessionTrigger.Start, "authority connected");
                    Transition(SessionTrigger.AuthorityAdmitted, "authority admitted");
                    break;
                case SessionPhase.WaitingForAuthority:
                    Transition(SessionTrigger.AuthorityAdmitted, "authority admitted");
                    break;
                case SessionPhase.AuthorityLost:
                    Transition(SessionTrigger.AuthorityReadmitted, "authority rejoined without resuming");
                    break;
            }
        }
    }

    private void ResumeSlot(SessionNode slot, AdmittedNode node, long now)
    {
        var previous = slot.Attached;
        if (slot.Phase == NodePhase.Detached)
        {
            var step = NodeTransitions.Apply(slot.Phase, NodeTrigger.Resume, slot.PhaseBeforeDetach);
            ChangeNodePhase(slot, step.Phase);
        }

        if (previous is not null)
        {
            previous.Phase = NodePhase.Detached; // superseded by this connection
        }

        slot.Attached = node;
        slot.RemoteAddress = node.RemoteAddress;
        slot.Roles = node.Roles;
        slot.Name = node.Name;
        slot.DetachDeadline = long.MaxValue;
        slot.DetachedAt = null;
        slot.DetachReason = null;
        slot.BaselineEpoch++;
        slot.LastJournalSeq = node.Hello.LastJournalSeq;
        slot.LastInboundTicks = now;
        slot.NextPingTicks = now;
        slot.Announced = false;
        slot.Clock.Reset();

        node.Welcome.Resumed = true;
        node.Welcome.ResumeToken = slot.ResumeToken;

        Publish(new PlayerResumed(_time.GetUtcNow(), SessionId, slot.PlayerId, slot.Name, slot.BaselineEpoch));
        LogResumed(slot.PlayerId, slot.Name, slot.BaselineEpoch);

        if (slot.IsAuthority && _authorityPlayerId == slot.PlayerId)
        {
            _gateway.AuthorityLive = true;
            if (_phase == SessionPhase.AuthorityLost)
            {
                Publish(new AuthorityChanged(_time.GetUtcNow(), SessionId, null, slot.PlayerId, "authority resumed"));
                Transition(_phaseBeforeLoss == SessionPhase.Paused ? SessionTrigger.AuthorityResumedPaused : SessionTrigger.AuthorityResumed, "authority resumed");
            }
        }
    }

    // ------------------------------------------------------------------ attach (after Welcome)

    private void OnAttach(AdmittedNode node)
    {
        if (!TryGetSlot(node, out var slot))
        {
            return; // superseded or gone before the attach ran
        }

        node.Phase = slot.Phase; // the gateway reset it to Admitted; restore what the machine says (resume)
        Publish(new NodeConnected(_time.GetUtcNow(), SessionId, node.Connection.Id.Value, node.RemoteAddress?.ToString()));
        slot.Announced = true;
        SendSessionState(slot);
        SendRoster(slot);
        SendSettings(slot);
        if (node.ModWarning is { } modWarning)
        {
            var notice = new ServerNoticeT
            {
                Severity = NoticeSeverity.Warning,
                Text = $"Your mods differ from this session's list ({Mods.ModPolicyEvaluator.Describe(modWarning)}). You were admitted because mod enforcement is set to Warn.",
                DisplayMs = 15000,
            };
            SendTo(slot, ControlFrames.Encode(MsgType.ServerNotice, fbb => ServerNotice.Pack(fbb, notice).Value, 256));
        }

        if (!node.Welcome.Resumed)
        {
            BroadcastRosterUpsert(slot, except: slot); // the joiner already got the full roster
        }

        SendPing(slot, Now);
        foreach (var module in _modules)
        {
            try
            {
                module.OnNodeAttached(slot, node.Welcome.Resumed);
            }
            catch (Exception ex)
            {
                LogModuleFailed(module.GetType().Name, nameof(ISessionModule.OnNodeAttached), ex);
            }
        }
    }

    private bool TryGetSlot(AdmittedNode node, out SessionNode slot) =>
        _nodes.TryGetValue(node.PlayerId, out slot!) && ReferenceEquals(slot.Attached, node);

    private void OnConnectionEnded(AdmittedNode node)
    {
        if (TryGetSlot(node, out var slot))
        {
            DetachNode(slot, DetachReason.SocketLost, closeWith: null);
        }
    }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "player {PlayerId}: sustained inbound queue overflow, closing the connection")]
    private partial void LogInboundOverflow(int playerId);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "player {PlayerId}: reading the connection failed")]
    private partial void LogReadFailed(int playerId, Exception ex);
}
