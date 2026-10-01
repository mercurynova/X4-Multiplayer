using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using X4MP.Core.Events;
using X4MP.Core.Net;
using X4MP.Core.Settings;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Session;

/// <summary>
/// The single-threaded owner of one session (architecture 7, server-design 2.1): the <see cref="SessionPhase"/>
/// machine, every node's <see cref="NodePhase"/>, ping/RTT/clock sync, <c>NodeStats</c>, resume and the authority
/// grace. All state lives behind one unbounded <see cref="Channel{T}"/> mailbox and is touched only by the loop
/// in <see cref="RunAsync"/>, so there are no locks and producers (connection readers, timers, the admin side)
/// never wait: posting is a non-blocking <c>TryWrite</c>. The mailbox is unbounded on purpose; what feeds it is bounded
/// instead: a node's reader goes through a <see cref="NodeInbox"/> (<c>PlayerState</c> latest-wins, a per-node cap on everything
/// else) and timers queue at most one tick, so the backlog is at most nodes x <c>InboundQueueFramesPerNode</c> plus admin work.
/// <para>
/// It is the <see cref="IAdmissionHandler"/> of the <see cref="NodeGateway"/>. Time comes from the injected
/// <see cref="TimeProvider"/> only (timestamps and a timer), so tests drive it on a fake clock.
/// </para>
/// <para>
/// Other server components plug in as <see cref="ISessionModule"/>s and observe through
/// <see cref="ISessionEventPublisher"/>; the admin side reads <see cref="Snapshot"/> or posts commands.
/// </para>
/// </summary>
public sealed partial class SessionActor : IAdmissionHandler, ISessionSettingsPusher, ISessionNodeDriver
{
    private abstract class Input
    {
        /// <summary>The actor stopped before this input ran: release anyone waiting on it.</summary>
        public virtual void Abandon()
        {
        }
    }

    private sealed class TickInput : Input;

    private sealed class AdmitInput(AdmittedNode node, TaskCompletionSource<AdmissionVerdict> reply) : Input
    {
        public AdmittedNode Node { get; } = node;

        public TaskCompletionSource<AdmissionVerdict> Reply { get; } = reply;

        public override void Abandon() =>
            Reply.TrySetResult(new AdmissionVerdict(DisconnectCode.ServerShutdown, "server stopping"));
    }

    private sealed class AttachInput(AdmittedNode node) : Input
    {
        public AdmittedNode Node { get; } = node;
    }

    private sealed class MessageInput(AdmittedNode node, InboundFrame frame, NodeInbox inbox) : Input
    {
        public AdmittedNode Node { get; } = node;

        public InboundFrame Frame { get; } = frame;

        public NodeInbox Inbox { get; } = inbox;
    }

    /// <summary>Marker for the node's parked <c>PlayerState</c> (latest wins, see <see cref="NodeInbox"/>): at most one is queued per node.</summary>
    private sealed class StateInput(AdmittedNode node, NodeInbox inbox) : Input
    {
        public AdmittedNode Node { get; } = node;

        public NodeInbox Inbox { get; } = inbox;
    }

    private sealed class EndedInput(AdmittedNode node) : Input
    {
        public AdmittedNode Node { get; } = node;
    }

    private sealed class WorkInput(Func<ValueTask> work, Action abandon) : Input
    {
        public Func<ValueTask> Work { get; } = work;

        public override void Abandon() => abandon();
    }

    private readonly Func<SessionActorOptions> _options;
    private readonly NetOptions _netOptions;
    private readonly GatewayState _gateway;
    private readonly TimeProvider _time;
    private readonly ISessionStore _store;
    private readonly IEventPublisher _events;
    private readonly ISessionModule[] _modules;
    private readonly ILogger _logger;
    private readonly long _startTimestamp;
    private readonly Channel<Input> _mailbox = Channel.CreateUnbounded<Input>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });

    private int _tickQueued;
    private long _framesReceived;
    private long _pending;
    private int _started;
    private volatile SessionSnapshot _snapshot = SessionSnapshot.Empty;

    /// <param name="options">Fixed options (tests, simple hosts).</param>
    public SessionActor(
        SessionActorOptions? options,
        NetOptions netOptions,
        GatewayState gateway,
        TimeProvider? time = null,
        ISessionStore? store = null,
        IEventPublisher? events = null,
        IEnumerable<ISessionModule>? modules = null,
        ILogger<SessionActor>? logger = null)
        : this(Constant(options ?? new SessionActorOptions()), netOptions, gateway, time, store, events, modules, logger)
    {
    }

    /// <param name="options">
    /// Supplies the options on every use, so Live settings (<c>IOptionsMonitor.CurrentValue</c>) take effect without a
    /// restart. Boot settings (tick interval, clock window) are read when they are needed once.
    /// </param>
    public SessionActor(
        Func<SessionActorOptions> options,
        NetOptions netOptions,
        GatewayState gateway,
        TimeProvider? time = null,
        ISessionStore? store = null,
        IEventPublisher? events = null,
        IEnumerable<ISessionModule>? modules = null,
        ILogger<SessionActor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(netOptions);
        ArgumentNullException.ThrowIfNull(gateway);
        _options = options;
        _netOptions = netOptions;
        _gateway = gateway;
        _time = time ?? TimeProvider.System;
        _store = store ?? new NullSessionStore();
        _events = events ?? NullEventPublisher.Instance;
        _modules = modules?.ToArray() ?? [];
        _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _startTimestamp = _time.GetTimestamp();
        _sessionGuid = gateway.SessionId;
        _phaseSince = _time.GetUtcNow();
        _gateway.Phase = SessionPhase.Idle;
        foreach (var module in _modules)
        {
            (module as ISessionActorBound)?.Bind(this);
        }
    }

    private static Func<SessionActorOptions> Constant(SessionActorOptions options) => () => options;

    private SessionActorOptions Opt => _options();

    private sealed class NullEventPublisher : IEventPublisher
    {
        public static NullEventPublisher Instance { get; } = new();

        public void Publish(DomainEvent domainEvent)
        {
        }
    }

    /// <summary>
    /// The last published read model (refreshed about every <see cref="SessionActorOptions.TickIntervalMs"/>;
    /// use <see cref="GetSnapshotAsync"/> for a fresh one). Safe to read from any thread.
    /// </summary>
    public SessionSnapshot Snapshot => _snapshot;

    /// <summary><see cref="ISessionNodeDriver.StoreSessionId"/>: the <c>sessions</c> row id; read it on the actor thread.</summary>
    public long? StoreSessionId => SessionId;

    /// <summary><see cref="ISessionNodeDriver.ServerTimeUs"/>: the monotonic server clock (since the actor was created) in microseconds.</summary>
    public long ServerTimeUs => ServerUs(Now);

    /// <summary>Frames from nodes the actor has processed so far (diagnostics, test synchronisation).</summary>
    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    /// <summary>Inputs waiting for the actor (diagnostics).</summary>
    public int PendingInputs => (int)Math.Min(int.MaxValue, Interlocked.Read(ref _pending));

    // ------------------------------------------------------------------ loop

    /// <summary>Runs the mailbox loop until <paramref name="ct"/> is cancelled. Call once.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The SessionActor loop is already running.");
        }

        var period = TimeSpan.FromMilliseconds(Math.Max(1, Opt.TickIntervalMs));
        using var timer = _time.CreateTimer(static state => ((SessionActor)state!).QueueTick(), this, period, period);
        PublishSnapshot();
        try
        {
            await foreach (var input in _mailbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _pending);
                try
                {
                    await HandleAsync(input).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogInputFailed(input.GetType().Name, ex);
                    input.Abandon();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            _mailbox.Writer.TryComplete();
            while (_mailbox.Reader.TryRead(out var pending))
            {
                Interlocked.Decrement(ref _pending);
                pending.Abandon();
            }
        }
    }

    private void QueueTick()
    {
        // At most one tick waits in the mailbox, however many timer callbacks fire (a fake clock can fire hundreds in one Advance).
        if (Interlocked.Exchange(ref _tickQueued, 1) == 0 && !Post(new TickInput()))
        {
            Volatile.Write(ref _tickQueued, 0);
        }
    }

    private bool Post(Input input)
    {
        Interlocked.Increment(ref _pending);
        if (_mailbox.Writer.TryWrite(input))
        {
            return true;
        }

        Interlocked.Decrement(ref _pending);
        return false;
    }

    private async ValueTask HandleAsync(Input input)
    {
        switch (input)
        {
            case TickInput:
                Volatile.Write(ref _tickQueued, 0);
                OnTick();
                break;
            case AdmitInput admit:
                await HandleAdmitAsync(admit).ConfigureAwait(false);
                break;
            case AttachInput attach:
                OnAttach(attach.Node);
                break;
            case MessageInput message:
                message.Inbox.Leave();
                OnMessage(message.Node, message.Frame);
                break;
            case StateInput state:
                if (state.Inbox.TakeLatest() is { } latest)
                {
                    OnMessage(state.Node, latest);
                }

                break;
            case EndedInput ended:
                OnConnectionEnded(ended.Node);
                break;
            case WorkInput work:
                await work.Work().ConfigureAwait(false);
                break;
        }
    }

    // ------------------------------------------------------------------ admin side and module commands

    /// <summary>Runs <paramref name="work"/> on the actor thread and returns its result (the way to read or change actor state safely).</summary>
    public Task<T> CallAsync<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return CallAsync<T>(() => Task.FromResult(work()));
    }

    /// <summary>Async variant of <see cref="CallAsync{T}(Func{T})"/>; the actor waits for <paramref name="work"/>, so keep it short.</summary>
    public Task<T> CallAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new WorkInput(
            async () =>
            {
                try
                {
                    tcs.TrySetResult(await work().ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            },
            () => tcs.TrySetCanceled());
        if (!Post(input))
        {
            tcs.TrySetCanceled();
        }

        return tcs.Task;
    }

    /// <summary>Fire-and-forget: queues <paramref name="work"/> for the actor thread. Returns false after shutdown.</summary>
    public bool Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Post(new WorkInput(() =>
        {
            work();
            return ValueTask.CompletedTask;
        }, static () => { }));
    }

    /// <summary>A freshly built snapshot (taken on the actor thread).</summary>
    public Task<SessionSnapshot> GetSnapshotAsync() => CallAsync(() => PublishSnapshot());

    /// <summary>Completes once every input queued before this call has been processed (tests, graceful stops).</summary>
    public Task FlushAsync() => CallAsync(() => true);

    /// <summary>Admin start (Idle to WaitingForAuthority); <paramref name="sessionName"/> overrides the configured name.</summary>
    public Task<TransitionResult> StartAsync(string? sessionName = null) => CallAsync(async () =>
    {
        if (_phase != SessionPhase.Idle)
        {
            return TransitionResult.Failed(_phase.ToString(), $"cannot start from {_phase}");
        }

        if (!string.IsNullOrWhiteSpace(sessionName))
        {
            _sessionName = sessionName.Trim();
        }

        await EnsureSessionAsync().ConfigureAwait(false);
        return Transition(SessionTrigger.Start, "admin start");
    });

    /// <summary>
    /// Raises an admin-level trigger (pause, resume, stop, checkpoint stored, ...). Triggers the actor raises
    /// itself (authority lost, grace expired, ...) are refused here.
    /// </summary>
    public Task<TransitionResult> ApplyAsync(SessionTrigger trigger, string? reason = null) => CallAsync(async () =>
    {
        if (!SessionTransitions.IsExternal(trigger))
        {
            return TransitionResult.Failed(_phase.ToString(), $"{trigger} is raised by the session itself");
        }

        if (trigger == SessionTrigger.Start)
        {
            await EnsureSessionAsync().ConfigureAwait(false);
        }

        return Transition(trigger, reason ?? $"admin {trigger}");
    });

    /// <summary>
    /// Raises a server-driven node trigger (team required/assigned, ready, fail, ...). The socket-level triggers
    /// (<see cref="NodeTrigger.SocketLost"/>, resume, grace, quit) are refused: use <see cref="RemoveNodeAsync"/>.
    /// </summary>
    public Task<TransitionResult> ApplyNodeTriggerAsync(int playerId, NodeTrigger trigger) => CallAsync(() =>
    {
        if (!_nodes.TryGetValue(playerId, out var slot))
        {
            return TransitionResult.Failed(string.Empty, $"player {playerId} is not in the session");
        }

        if (trigger is NodeTrigger.SocketLost or NodeTrigger.ClientReload or NodeTrigger.Resume or NodeTrigger.GraceExpired or NodeTrigger.Quit)
        {
            return TransitionResult.Failed(slot.Phase.ToString(), $"{trigger} is raised by the session itself");
        }

        var from = slot.Phase;
        var applied = ApplyNodeStep(slot, trigger);
        return new TransitionResult(applied, from.ToString(), slot.Phase.ToString(), applied ? null : $"{trigger} is not legal in {from}");
    });

    /// <summary>Kicks or removes a player now: sends <c>Disconnect{code}</c>, frees the slot (no resume).</summary>
    public Task<bool> RemoveNodeAsync(int playerId, DisconnectCode code, string reason) => CallAsync(() =>
    {
        if (!_nodes.TryGetValue(playerId, out var slot))
        {
            return false;
        }

        LeaveNode(slot, reason, closeWith: code);
        return true;
    });

    // ------------------------------------------------------------------ settings

    /// <summary>The latest session settings (flagged <c>PushToNodes</c>) the actor was told about; null before the first push.</summary>
    public SessionSettingsSnapshot? Settings => Volatile.Read(ref _settings);

    private SessionSettingsSnapshot? _settings;

    /// <summary>
    /// <see cref="ISessionSettingsPusher"/>: the settings service calls this after a change. The actor keeps the snapshot
    /// and tells its modules, then sends the full node-relevant set to every announced node as <c>ServerSettingsUpdate</c>
    /// (a node that attaches later gets it from <see cref="OnAttach"/>). The protocol's <c>SessionSettings</c> table stays the
    /// team and economy policy owned by M1-T1/E1.
    /// </summary>
    public ValueTask PushAsync(SessionSettingsSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Post(() =>
        {
            Volatile.Write(ref _settings, snapshot);
            BroadcastSettings(snapshot);
            foreach (var module in _modules)
            {
                try
                {
                    module.OnSettingsChanged(snapshot);
                }
                catch (Exception ex)
                {
                    LogModuleFailed(module.GetType().Name, nameof(ISessionModule.OnSettingsChanged), ex);
                }
            }
        });
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------ logging

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "session actor: input {Input} failed")]
    private partial void LogInputFailed(string input, Exception ex);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "session module {Module} threw in {Callback}")]
    private partial void LogModuleFailed(string module, string callback, Exception ex);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "session {Session}: {From} -> {To} ({Reason})")]
    private partial void LogPhase(Guid session, SessionPhase from, SessionPhase to, string? reason);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "player {PlayerId} ({Name}) joined as {Roles}")]
    private partial void LogJoined(int playerId, string name, Role roles);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "player {PlayerId} ({Name}) resumed (baseline epoch {Epoch})")]
    private partial void LogResumed(int playerId, string name, int epoch);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "player {PlayerId} ({Name}) detached: {Reason}")]
    private partial void LogDetached(int playerId, string name, DetachReason reason);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "player {PlayerId} ({Name}) left: {Reason}")]
    private partial void LogLeft(int playerId, string name, string reason);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "player {PlayerId}: ignored {Type} in {Phase}: {Why}")]
    private partial void LogIgnored(int playerId, MsgType type, NodePhase phase, string why);
}
