using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using X4MP.Core.Metrics;

namespace X4MP.Core.Events;

/// <summary>
/// In-process <see cref="IEventBus"/>: a copy-on-write subscriber array read lock-free by <see cref="Publish"/>,
/// and a bounded <see cref="Channel{T}"/> plus consumer task per subscriber. Publishing is a
/// <c>TryWrite</c> per subscriber, so a stalled handler can never delay a publisher.
/// </summary>
public sealed partial class EventBus : IEventBus, IAsyncDisposable
{
    private static readonly TimeSpan DropAlertInterval = TimeSpan.FromSeconds(30);

    private sealed class Subscriber : IEventSubscription
    {
        private readonly EventBus _bus;
        private readonly Channel<DomainEvent> _channel;
        private readonly Func<DomainEvent, CancellationToken, ValueTask> _handler;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private long _dropped;
        private long _delivered;
        private long _lastDropAlert = long.MinValue;
        private int _disposed;

        public Subscriber(EventBus bus, string name, Func<DomainEvent, CancellationToken, ValueTask> handler, SubscriberOptions options)
        {
            _bus = bus;
            _handler = handler;
            Name = name;
            Options = options;
            _channel = Channel.CreateBounded<DomainEvent>(
                new BoundedChannelOptions(options.Capacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = options.DropPolicy == EventDropPolicy.DropOldest ? BoundedChannelFullMode.DropOldest : BoundedChannelFullMode.DropWrite,
                    AllowSynchronousContinuations = false,
                },
                _ =>
                {
                    Interlocked.Increment(ref _dropped);
                    Interlocked.Increment(ref bus._totalDropped);
                    ServerMetrics.RecordEventDropped(name);
                });
            _loop = Task.Run(RunAsync);
        }

        public string Name { get; }

        public SubscriberOptions Options { get; }

        public long Dropped => Interlocked.Read(ref _dropped);

        public long Delivered => Interlocked.Read(ref _delivered);

        public int Pending => _channel.Reader.Count;

        /// <summary>Never blocks. Returns true if the event was refused or evicted something.</summary>
        public bool Offer(DomainEvent domainEvent)
        {
            var before = Interlocked.Read(ref _dropped);
            _channel.Writer.TryWrite(domainEvent);
            return Interlocked.Read(ref _dropped) != before;
        }

        /// <summary>True at most once per <see cref="DropAlertInterval"/>.</summary>
        public bool ClaimDropAlert(long now, TimeProvider time)
        {
            var last = Interlocked.Read(ref _lastDropAlert);
            return (last == long.MinValue || time.GetElapsedTime(last, now) >= DropAlertInterval)
                && Interlocked.CompareExchange(ref _lastDropAlert, now, last) == last;
        }

        private async Task RunAsync()
        {
            try
            {
                await foreach (var e in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await _handler(e, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _bus.LogHandlerFailed(Name, e.GetType().Name, ex);
                    }
                    finally
                    {
                        Interlocked.Increment(ref _delivered);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // cancelled while idle or after the drain timeout
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _bus.Remove(this);
            _channel.Writer.TryComplete();
            try
            {
                // Drain what is queued; a stalled handler is cancelled after the timeout.
                await _loop.WaitAsync(_bus._drainTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await _cts.CancelAsync().ConfigureAwait(false);
                await _loop.ConfigureAwait(false);
            }

            _cts.Dispose();
        }
    }

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _drainTimeout;
    private readonly ILogger _logger;
    private Subscriber[] _subscribers = [];
    private long _totalDropped;
    private int _disposed;

    /// <param name="time">Clock for the throttle of drop alerts and for <see cref="Now"/>.</param>
    /// <param name="drainTimeout">How long disposal waits for a subscriber to finish its queue before cancelling it.</param>
    public EventBus(TimeProvider? time = null, TimeSpan? drainTimeout = null, ILogger<EventBus>? logger = null)
    {
        _time = time ?? TimeProvider.System;
        _drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(5);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EventBus>.Instance;
    }

    public long TotalDropped => Interlocked.Read(ref _totalDropped);

    public IReadOnlyList<IEventSubscription> Subscriptions => Volatile.Read(ref _subscribers);

    /// <summary>The bus clock, for publishers that stamp <see cref="DomainEvent.At"/>.</summary>
    public DateTimeOffset Now => _time.GetUtcNow();

    public IEventSubscription Subscribe(string name, Func<DomainEvent, CancellationToken, ValueTask> handler, SubscriberOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new SubscriberOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Capacity, 1);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var subscriber = new Subscriber(this, name, handler, options);
        lock (_gate)
        {
            _subscribers = [.. _subscribers, subscriber];
        }

        return subscriber;
    }

    public void Publish(DomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ServerMetrics.RecordEventPublished();
        foreach (var subscriber in Volatile.Read(ref _subscribers))
        {
            if (subscriber.Offer(domainEvent) && subscriber.Options.AlertOnDrop && domainEvent is not AlertRaised)
            {
                var now = _time.GetTimestamp();
                if (subscriber.ClaimDropAlert(now, _time))
                {
                    LogDropped(subscriber.Name, subscriber.Dropped);
                    Publish(new AlertRaised(
                        _time.GetUtcNow(), null, AlertSeverity.Warning, "event_bus_drop",
                        $"Event subscriber '{subscriber.Name}' is not keeping up and has dropped {subscriber.Dropped} events."));
                }
            }
        }
    }

    private void Remove(Subscriber subscriber)
    {
        lock (_gate)
        {
            _subscribers = [.. _subscribers.Where(s => !ReferenceEquals(s, subscriber))];
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Subscriber[] all;
        lock (_gate)
        {
            all = _subscribers;
        }

        await Task.WhenAll(all.Select(s => s.DisposeAsync().AsTask())).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "event subscriber {Subscriber} failed on {EventType}")]
    private partial void LogHandlerFailed(string subscriber, string eventType, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "event subscriber {Subscriber} is full and dropped events ({Dropped} so far)")]
    private partial void LogDropped(string subscriber, long dropped);
}
