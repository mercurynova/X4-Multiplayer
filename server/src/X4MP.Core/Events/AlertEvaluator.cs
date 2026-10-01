using X4MP.Core.Settings;

namespace X4MP.Core.Events;

/// <summary>Where a rule reports. <see cref="Raise"/> and <see cref="Clear"/> are edge-triggered by the rule itself.</summary>
public interface IAlertSink
{
    void Raise(long? session, AlertSeverity severity, string code, string text);

    void Clear(long? session, string code);
}

/// <summary>
/// One alert rule. <see cref="Evaluate"/> is called for every event (<paramref name="domainEvent"/> set,
/// <paramref name="now"/> = the event's time) and once a second (<paramref name="domainEvent"/> null, <paramref name="now"/>
/// from the evaluator's <see cref="TimeProvider"/>). Calls are serialized.
/// </summary>
public interface IAlertRule
{
    void Evaluate(DomainEvent? domainEvent, DateTimeOffset now, IAlertSink sink);
}

/// <summary>"The authority's FPS stayed below the threshold for the configured time" (default 15 FPS for 30 s).</summary>
public sealed class AuthorityLowFpsRule(Func<AlertOptions> options) : IAlertRule
{
    public const string AlertCode = "authority_fps_low";

    private DateTimeOffset? _lowSince;
    private bool _active;
    private long? _session;
    private string _player = "";
    private double _fps;

    public void Evaluate(DomainEvent? domainEvent, DateTimeOffset now, IAlertSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var current = options();
        if (domainEvent is NodeStatsReported { IsAuthority: true } stats)
        {
            _session = stats.Session;
            _player = stats.Player;
            _fps = stats.Fps;
            if (stats.Fps < current.AuthorityFpsThreshold)
            {
                _lowSince ??= stats.At;
            }
            else
            {
                _lowSince = null;
                if (_active)
                {
                    _active = false;
                    sink.Clear(_session, AlertCode);
                }
            }
        }

        if (_lowSince is { } since && !_active && now - since >= TimeSpan.FromSeconds(current.AuthorityFpsSeconds))
        {
            _active = true;
            sink.Raise(
                _session,
                AlertSeverity.Warning,
                AlertCode,
                $"Authority {_player} has been below {current.AuthorityFpsThreshold} FPS for {current.AuthorityFpsSeconds} s (now {_fps:0.#}).");
        }
    }
}

/// <summary>
/// Subscribes to the bus, runs the <see cref="IAlertRule"/>s and publishes <see cref="AlertRaised"/> /
/// <see cref="AlertCleared"/> events (server-design 2.7). A one-second timer from the <see cref="TimeProvider"/>
/// lets time-based rules fire even when no events arrive.
/// </summary>
public sealed class AlertEvaluator : IAsyncDisposable, IAlertSink
{
    private readonly IEventBus _bus;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<IAlertRule> _rules;
    private readonly Lock _gate = new();
    private IEventSubscription? _subscription;
    private ITimer? _timer;

    public AlertEvaluator(IEventBus bus, TimeProvider time, IEnumerable<IAlertRule> rules)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(rules);
        _bus = bus;
        _time = time;
        _rules = [.. rules];
    }

    /// <summary>The standard evaluator: the authority low-FPS rule driven by <paramref name="options"/> (read on every evaluation, so live settings apply).</summary>
    public static AlertEvaluator CreateDefault(IEventBus bus, TimeProvider time, Func<AlertOptions> options) =>
        new(bus, time, [new AuthorityLowFpsRule(options)]);

    public void Start()
    {
        _subscription = _bus.Subscribe("alerts", (e, _) =>
        {
            if (e is not (AlertRaised or AlertCleared))
            {
                Evaluate(e, e.At);
            }

            return ValueTask.CompletedTask;
        });
        _timer = _time.CreateTimer(_ => Evaluate(null, _time.GetUtcNow()), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void Evaluate(DomainEvent? domainEvent, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var rule in _rules)
            {
                rule.Evaluate(domainEvent, now, this);
            }
        }
    }

    void IAlertSink.Raise(long? session, AlertSeverity severity, string code, string text) =>
        _bus.Publish(new AlertRaised(_time.GetUtcNow(), session, severity, code, text));

    void IAlertSink.Clear(long? session, string code) =>
        _bus.Publish(new AlertCleared(_time.GetUtcNow(), session, code));

    public async ValueTask DisposeAsync()
    {
        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
        }

        if (_subscription is not null)
        {
            await _subscription.DisposeAsync().ConfigureAwait(false);
        }
    }
}
