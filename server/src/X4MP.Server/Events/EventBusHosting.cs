using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using X4MP.Core.Events;
using X4MP.Core.Settings;
using X4MP.Persistence;
using X4MP.Server.Auth;

namespace X4MP.Server.Events;

/// <summary>Wires the event bus and its subscribers (the hook <c>ServerHost</c> calls).</summary>
public static class EventBusServiceExtensions
{
    /// <summary>
    /// Registers <see cref="EventBus"/> as <see cref="IEventBus"/> and <see cref="IEventPublisher"/>, and a hosted
    /// service that attaches the standard subscribers: <c>session_events</c> persistence, the audit forwarder and
    /// the alert evaluator. Register after <c>AddAdminAuth</c> and the settings (it needs
    /// <see cref="AdminStore"/>, <see cref="PersistenceWriter"/> and <see cref="AlertOptions"/>).
    /// </summary>
    public static IServiceCollection AddEventBus(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new EventBus(sp.GetRequiredService<TimeProvider>(), logger: sp.GetService<ILogger<EventBus>>()));
        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<EventBus>());
        services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<EventBus>());
        services.AddSingleton<SessionEventSink>();
        services.AddSingleton<AuditEventForwarder>();
        services.AddHostedService<EventSubscribersService>();
        return services;
    }
}

/// <summary>Turns <see cref="AdminActionTaken"/> and <see cref="AlertRaised"/> events into <c>audit_log</c> rows.</summary>
public sealed class AuditEventForwarder(AdminStore audit)
{
    public ValueTask HandleAsync(DomainEvent domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        switch (domainEvent)
        {
            case AdminActionTaken action:
                audit.Audit(action.Actor, action.Action, action.Target, action.RemoteIp, action.Data);
                break;
            case AlertRaised alert:
                audit.Audit("system", "alert.raised", alert.Code, null, new Dictionary<string, string?>
                {
                    ["severity"] = alert.Severity.ToString(),
                    ["text"] = alert.Text,
                });
                break;
            default:
                break;
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Attaches the standard subscribers at startup and drains them at shutdown (before the persistence writer is disposed).</summary>
internal sealed class EventSubscribersService(
    EventBus bus,
    SessionEventSink persistence,
    AuditEventForwarder audit,
    IOptionsMonitor<AlertOptions> alertOptions,
    TimeProvider time) : IHostedService
{
    // Persistence and audit must not lose order and should rarely drop: big queues, newest event refused when full.
    private static readonly SubscriberOptions Durable = new() { Capacity = 10_000, DropPolicy = EventDropPolicy.DropIncoming, AlertOnDrop = true };

    private readonly List<IEventSubscription> _subscriptions = [];
    private AlertEvaluator? _alerts;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscriptions.Add(bus.Subscribe("persistence", persistence.HandleAsync, Durable));
        _subscriptions.Add(bus.Subscribe("audit", audit.HandleAsync, Durable));
        _alerts = AlertEvaluator.CreateDefault(bus, time, () => alertOptions.CurrentValue);
        _alerts.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_alerts is not null)
        {
            await _alerts.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var subscription in _subscriptions)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }
}
