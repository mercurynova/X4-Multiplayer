using X4MP.Core.Events;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;

namespace X4MP.Server.Tests.Net;

/// <summary>Collects every event the actor publishes.</summary>
public sealed class CollectingEvents : IEventPublisher
{
    private readonly object _gate = new();
    private readonly List<DomainEvent> _events = [];

    public IReadOnlyList<DomainEvent> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public IReadOnlyList<T> OfType<T>() where T : DomainEvent => [.. All.OfType<T>()];

    public void Publish(DomainEvent domainEvent)
    {
        lock (_gate)
        {
            _events.Add(domainEvent);
        }
    }
}

/// <summary>
/// A running <see cref="SessionActor"/> to hand to <see cref="NetHarness"/> as the admission handler:
/// <c>handler: fixture.Handler</c>. Real clock, in-memory stores, events collected.
/// </summary>
public sealed class ActorFixture : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;

    public ActorFixture(NetOptions? net = null, SessionActorOptions? options = null)
    {
        Net = net ?? new NetOptions();
        Options = options ?? new SessionActorOptions();
    }

    public NetOptions Net { get; }

    public SessionActorOptions Options { get; }

    public CollectingEvents Events { get; } = new();

    /// <summary>Modules the actor attaches (fill before <see cref="Handler"/> runs, that is before the harness is created).</summary>
    public List<ISessionModule> Modules { get; } = [];

    public SessionActor Actor { get; private set; } = null!;

    /// <summary>For <c>NetHarness.CreateAsync(handler: fixture.Handler)</c>.</summary>
    public IAdmissionHandler Handler(GatewayState state)
    {
        Actor = new SessionActor(Options, Net, state, TimeProvider.System, events: Events, modules: Modules);
        _loop = Actor.RunAsync(_stop.Token);
        return Actor;
    }

    public async Task<SessionSnapshot> WaitForAsync(Func<SessionSnapshot, bool> condition, int timeoutMs = 5000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var snapshot = await Actor.GetSnapshotAsync();
            if (condition(snapshot))
            {
                return snapshot;
            }

            Assert.True(Environment.TickCount64 < until, "condition not met in time");
            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }
}

public static class ClientReading
{
    /// <summary>
    /// Reads until the server's <c>Disconnect</c> arrives and returns its code, or null if the stream ended or was reset
    /// first. Answering a server Ping on a connection the server has just closed can reset it and drop the
    /// Disconnect frame, so callers must not insist on seeing it.
    /// </summary>
    public static async Task<DisconnectCode?> UntilDisconnectAsync(TcpNodeClient client, CancellationToken ct)
    {
        try
        {
            while (await client.ReceiveAsync(ct).ConfigureAwait(false) is { } frame)
            {
                if (frame.Type == MsgType.Disconnect)
                {
                    return MessageRegistry.Default.Decode<Disconnect>(frame).Code;
                }
            }
        }
        catch (IOException)
        {
            // reset by the peer
        }

        return null;
    }
}
