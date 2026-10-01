namespace X4MP.Core.Events;

/// <summary>
/// The write side of the event bus (server-design 2.7). <see cref="Publish"/> is synchronous and never blocks:
/// a subscriber that cannot keep up loses events (and the bus counts them); the publisher is never delayed.
/// Components that only emit events (SessionActor, the gateway, the save service) depend on this interface.
/// </summary>
public interface IEventPublisher
{
    void Publish(DomainEvent domainEvent);
}

/// <summary>What a bounded subscriber queue does when it is full.</summary>
public enum EventDropPolicy
{
    /// <summary>Evict the oldest queued event (UI-facing subscribers want the latest).</summary>
    DropOldest,

    /// <summary>Refuse the incoming event, keeping the queue in order (persistence and audit).</summary>
    DropIncoming,
}

public sealed record SubscriberOptions
{
    /// <summary>Queue size per subscriber.</summary>
    public int Capacity { get; init; } = 1024;

    public EventDropPolicy DropPolicy { get; init; } = EventDropPolicy.DropOldest;

    /// <summary>Publish a (throttled) <see cref="AlertRaised"/> when this subscriber drops events.</summary>
    public bool AlertOnDrop { get; init; }
}

/// <summary>A live subscription. Disposing it unsubscribes and lets the handler drain what is queued.</summary>
public interface IEventSubscription : IAsyncDisposable
{
    string Name { get; }

    /// <summary>Events lost because the queue was full.</summary>
    long Dropped { get; }

    /// <summary>Events the handler has finished (successfully or not).</summary>
    long Delivered { get; }

    /// <summary>Events waiting in the queue.</summary>
    int Pending { get; }
}

/// <summary>Publish and subscribe (server-design 2.7). One bounded <c>Channel</c> and one consumer task per subscriber.</summary>
public interface IEventBus : IEventPublisher
{
    /// <summary>
    /// Adds a subscriber. <paramref name="handler"/> runs on the subscriber's own task, one event at a time, in
    /// publish order; an exception it throws is logged and the next event is delivered.
    /// </summary>
    IEventSubscription Subscribe(string name, Func<DomainEvent, CancellationToken, ValueTask> handler, SubscriberOptions? options = null);

    /// <summary>Events dropped across all subscribers since start.</summary>
    long TotalDropped { get; }

    IReadOnlyList<IEventSubscription> Subscriptions { get; }
}

public static class EventBusExtensions
{
    /// <summary>Subscribes a handler that only sees events of type <typeparamref name="T"/>.</summary>
    public static IEventSubscription Subscribe<T>(
        this IEventBus bus, string name, Func<T, CancellationToken, ValueTask> handler, SubscriberOptions? options = null)
        where T : DomainEvent
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(handler);
        return bus.Subscribe(name, (e, ct) => e is T typed ? handler(typed, ct) : ValueTask.CompletedTask, options);
    }
}
