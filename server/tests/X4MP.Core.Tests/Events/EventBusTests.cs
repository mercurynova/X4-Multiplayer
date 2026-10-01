using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Events;
using X4MP.Core.Settings;

namespace X4MP.Core.Tests.Events;

public class EventBusTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static ChatPosted Chat(int n) => new(T0, 1, null, "admin", "all", n.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("condition not reached");
            }

            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task StalledSubscriberNeverBlocksPublishAndOthersReceiveEverything()
    {
        await using var bus = new EventBus(drainTimeout: TimeSpan.FromMilliseconds(100));
        var never = new TaskCompletionSource();
        var stalledEntered = new TaskCompletionSource();
        var stalled = bus.Subscribe("stalled", async (_, ct) =>
        {
            stalledEntered.TrySetResult();
            await never.Task.WaitAsync(ct);
        }, new SubscriberOptions { Capacity = 8 });
        var received = new ConcurrentQueue<int>();
        const int count = 50_000;
        var fast = bus.Subscribe("fast", (e, _) =>
        {
            received.Enqueue(int.Parse(((ChatPosted)e).Text, System.Globalization.CultureInfo.InvariantCulture));
            return ValueTask.CompletedTask;
        }, new SubscriberOptions { Capacity = count + 1 });

        var publisher = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
            {
                bus.Publish(Chat(i));
            }

            return sw.Elapsed;
        });
        var finished = await Task.WhenAny(publisher, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(publisher, finished); // publish loop completed although "stalled" never returns
        await stalledEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitUntil(() => received.Count == count);
        Assert.Equal(Enumerable.Range(0, count), received); // in order, none lost
        Assert.True(stalled.Dropped >= count - 9, $"dropped {stalled.Dropped}");
        Assert.Equal(0, fast.Dropped);
        Assert.Equal(stalled.Dropped, bus.TotalDropped);
    }

    [Fact]
    public async Task DisposingTheBusCancelsAStalledHandlerAfterTheDrainTimeout()
    {
        var bus = new EventBus(drainTimeout: TimeSpan.FromMilliseconds(100));
        var entered = new TaskCompletionSource();
        bus.Subscribe("stalled", async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        bus.Publish(Chat(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var dispose = bus.DisposeAsync().AsTask();
        Assert.Same(dispose, await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task DropOldestKeepsTheNewestEvents()
    {
        await using var bus = new EventBus();
        var gate = new TaskCompletionSource();
        var seen = new List<string>();
        var sub = bus.Subscribe("s", async (e, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            lock (seen)
            {
                seen.Add(((ChatPosted)e).Text);
            }
        }, new SubscriberOptions { Capacity = 3, DropPolicy = EventDropPolicy.DropOldest });

        bus.Publish(Chat(0));
        await WaitUntil(() => sub.Pending == 0); // event 0 is now inside the handler
        for (int i = 1; i <= 10; i++)
        {
            bus.Publish(Chat(i));
        }

        gate.SetResult();
        await WaitUntil(() => sub.Delivered == 4);
        Assert.Equal(["0", "8", "9", "10"], seen);
        Assert.Equal(7, sub.Dropped);
    }

    [Fact]
    public async Task DropIncomingKeepsTheOldestEvents()
    {
        await using var bus = new EventBus();
        var gate = new TaskCompletionSource();
        var seen = new List<string>();
        var sub = bus.Subscribe("s", async (e, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            lock (seen)
            {
                seen.Add(((ChatPosted)e).Text);
            }
        }, new SubscriberOptions { Capacity = 3, DropPolicy = EventDropPolicy.DropIncoming });

        bus.Publish(Chat(0));
        await WaitUntil(() => sub.Pending == 0);
        for (int i = 1; i <= 10; i++)
        {
            bus.Publish(Chat(i));
        }

        gate.SetResult();
        await WaitUntil(() => sub.Delivered == 4);
        Assert.Equal(["0", "1", "2", "3"], seen);
        Assert.Equal(7, sub.Dropped);
    }

    [Fact]
    public async Task AHandlerExceptionDoesNotStopTheSubscriber()
    {
        await using var bus = new EventBus();
        var ok = new ConcurrentQueue<string>();
        var sub = bus.Subscribe("flaky", (e, _) =>
        {
            var text = ((ChatPosted)e).Text;
            if (text == "1")
            {
                throw new InvalidOperationException("boom");
            }

            ok.Enqueue(text);
            return ValueTask.CompletedTask;
        });
        for (int i = 0; i < 3; i++)
        {
            bus.Publish(Chat(i));
        }

        await WaitUntil(() => sub.Delivered == 3);
        Assert.Equal(["0", "2"], ok);
    }

    [Fact]
    public async Task TypedSubscribeOnlySeesItsType()
    {
        await using var bus = new EventBus();
        var chats = new ConcurrentQueue<ChatPosted>();
        var sub = bus.Subscribe<ChatPosted>("chat", (c, _) =>
        {
            chats.Enqueue(c);
            return ValueTask.CompletedTask;
        });
        bus.Publish(new SaveStored(T0, null, "abc", 1, "authority"));
        bus.Publish(Chat(1));
        await WaitUntil(() => sub.Delivered == 2);
        Assert.Single(chats);
    }

    [Fact]
    public async Task DisposeDrainsQueuedEventsAndUnsubscribeStopsDelivery()
    {
        await using var bus = new EventBus();
        var seen = new ConcurrentQueue<int>();
        var sub = bus.Subscribe("s", async (e, ct) =>
        {
            await Task.Delay(1, ct);
            seen.Enqueue(int.Parse(((ChatPosted)e).Text, System.Globalization.CultureInfo.InvariantCulture));
        });
        for (int i = 0; i < 20; i++)
        {
            bus.Publish(Chat(i));
        }

        await sub.DisposeAsync();
        Assert.Equal(20, seen.Count);
        Assert.Empty(bus.Subscriptions);
        bus.Publish(Chat(99));
        await Task.Delay(20);
        Assert.Equal(20, seen.Count);
    }

    [Fact]
    public async Task DropAlertIsPublishedOncePerThirtySeconds()
    {
        var clock = new FakeTimeProvider(T0);
        await using var bus = new EventBus(clock, TimeSpan.FromMilliseconds(100));
        var alerts = new ConcurrentQueue<AlertRaised>();
        var alertSub = bus.Subscribe<AlertRaised>("alerts", (a, _) =>
        {
            alerts.Enqueue(a);
            return ValueTask.CompletedTask;
        });
        bus.Subscribe("stalled", (_, ct) => new ValueTask(Task.Delay(Timeout.Infinite, ct)), new SubscriberOptions { Capacity = 2, AlertOnDrop = true });

        for (int i = 0; i < 100; i++)
        {
            bus.Publish(Chat(i));
        }

        await WaitUntil(() => alerts.Count == 1);
        Assert.Equal("event_bus_drop", alerts.Single().Code);

        clock.Advance(TimeSpan.FromSeconds(31));
        for (int i = 0; i < 100; i++)
        {
            bus.Publish(Chat(i));
        }

        await WaitUntil(() => alerts.Count == 2);
        await Task.Delay(30);
        Assert.Equal(2, alerts.Count);
        Assert.True(alertSub.Dropped == 0);
    }
}

public class AlertEvaluatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig : IAsyncDisposable
    {
        public FakeTimeProvider Clock { get; } = new(T0);
        public EventBus Bus { get; }
        public AlertEvaluator Evaluator { get; }
        public ConcurrentQueue<DomainEvent> Alerts { get; } = new();
        private readonly IEventSubscription _watch;
        private long _published;

        public Rig(AlertOptions? options = null)
        {
            options ??= new AlertOptions();
            Bus = new EventBus(Clock, TimeSpan.FromMilliseconds(100));
            Evaluator = AlertEvaluator.CreateDefault(Bus, Clock, () => options);
            Evaluator.Start();
            _watch = Bus.Subscribe("watch", (e, _) =>
            {
                if (e is AlertRaised or AlertCleared)
                {
                    Alerts.Enqueue(e);
                }

                return ValueTask.CompletedTask;
            });
        }

        public void Stats(double fps, bool authority = true, string player = "host")
        {
            Bus.Publish(new NodeStatsReported(Clock.GetUtcNow(), 7, 1, player, authority, fps));
            _published++;
        }

        /// <summary>Waits until the evaluator has handled everything published through <see cref="Stats"/>.</summary>
        public Task SettleAsync() => EventBusTests.WaitUntil(() =>
        {
            var sub = Bus.Subscriptions.First(s => s.Name == "alerts");
            return sub.Pending == 0 && sub.Delivered >= _published;
        });

        public async ValueTask DisposeAsync()
        {
            await Evaluator.DisposeAsync();
            await _watch.DisposeAsync();
            await Bus.DisposeAsync();
        }
    }

    [Fact]
    public async Task AuthorityFpsBelow15For30SecondsRaisesOneAlertOnTheFakeClock()
    {
        await using var rig = new Rig();
        for (int t = 0; t < 28; t += 2)
        {
            rig.Stats(12);
            await rig.SettleAsync();
            rig.Clock.Advance(TimeSpan.FromSeconds(2));
        }

        rig.Clock.Advance(TimeSpan.FromSeconds(1)); // 29 s: low since 0, one second short
        await Task.Delay(30);
        Assert.Empty(rig.Alerts);

        rig.Clock.Advance(TimeSpan.FromSeconds(1)); // 30 s: the timer tick fires the alert
        await EventBusTests.WaitUntil(() => rig.Alerts.Count == 1);
        var alert = Assert.IsType<AlertRaised>(rig.Alerts.Single());
        Assert.Equal("authority_fps_low", alert.Code);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Equal(7, alert.Session);
        Assert.Contains("host", alert.Text, StringComparison.Ordinal);

        // still low: no second alert
        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        rig.Stats(11);
        await rig.SettleAsync();
        await Task.Delay(30);
        Assert.Single(rig.Alerts);

        // recovery clears it, and a new dip starts a new episode
        rig.Stats(60);
        await EventBusTests.WaitUntil(() => rig.Alerts.Count == 2);
        Assert.IsType<AlertCleared>(rig.Alerts.Last());
    }

    [Fact]
    public async Task TheTimerRaisesTheAlertEvenWhenNoFurtherStatsArrive()
    {
        await using var rig = new Rig();
        rig.Stats(5);
        await rig.SettleAsync();

        rig.Clock.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(30);
        Assert.Empty(rig.Alerts);

        rig.Clock.Advance(TimeSpan.FromSeconds(2)); // the one-second timer fires on the fake clock
        await EventBusTests.WaitUntil(() => rig.Alerts.Count == 1);
        Assert.IsType<AlertRaised>(rig.Alerts.Single());
    }

    [Fact]
    public async Task ARecoveryBeforeThirtySecondsOrANonAuthorityNeverAlerts()
    {
        await using var rig = new Rig();
        rig.Stats(10);
        await rig.SettleAsync();
        rig.Clock.Advance(TimeSpan.FromSeconds(20));
        rig.Stats(40); // recovered
        await rig.SettleAsync();
        rig.Clock.Advance(TimeSpan.FromSeconds(20));
        rig.Stats(10); // new dip starts now
        await rig.SettleAsync();
        rig.Clock.Advance(TimeSpan.FromSeconds(20));
        rig.Stats(3, authority: false, player: "client"); // clients do not count
        await rig.SettleAsync();
        rig.Clock.Advance(TimeSpan.FromSeconds(60));
        await rig.SettleAsync();
        await Task.Delay(30);
        Assert.Single(rig.Alerts); // only the second dip, which lasted 20 + 60 s without recovery
        Assert.IsType<AlertRaised>(rig.Alerts.Single());
    }

    [Fact]
    public async Task ThresholdAndDurationFollowTheLiveOptions()
    {
        var options = new AlertOptions { AuthorityFpsThreshold = 30, AuthorityFpsSeconds = 5 };
        await using var rig = new Rig(options);
        rig.Stats(25); // below 30
        await rig.SettleAsync();
        rig.Clock.Advance(TimeSpan.FromSeconds(6));
        await EventBusTests.WaitUntil(() => rig.Alerts.Count == 1);
    }
}
