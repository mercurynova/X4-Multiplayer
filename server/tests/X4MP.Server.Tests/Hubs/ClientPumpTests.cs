using System.Diagnostics;
using X4MP.Server.Hubs;

namespace X4MP.Server.Tests.Hubs;

/// <summary>The per-connection outbound queue: bounded, latest-wins for keyed sends, and a stalled send stops the pump (acceptance 5).</summary>
public sealed class ClientPumpTests
{
    private sealed class Client
    {
        public List<string> Got { get; } = [];
    }

    [Fact]
    public async Task SendsRunInOrderAndAKeyedSendReplacesThePendingOneWithTheSameKey()
    {
        var client = new Client();
        var gate = new TaskCompletionSource();
        await using var pump = new ClientPump<Client>(client);

        pump.Post(async c => { await gate.Task; c.Got.Add("first"); });
        await Task.Delay(50); // "first" is now running and blocked
        pump.Post(c => { c.Got.Add("frame-1"); return Task.CompletedTask; }, "frame");
        pump.Post(c => { c.Got.Add("event"); return Task.CompletedTask; });
        pump.Post(c => { c.Got.Add("frame-2"); return Task.CompletedTask; }, "frame");
        gate.SetResult();

        await Saves.SaveServer.WaitUntilAsync(() => pump.Sent == 3, 5000, "sends");
        Assert.Equal(["first", "frame-2", "event"], client.Got); // frame-2 replaced frame-1 and kept frame-1's place
        Assert.Equal(1, pump.Coalesced);
    }

    [Fact]
    public async Task AStalledClientNeverBlocksPostAndTheQueueStaysBounded()
    {
        var client = new Client();
        var neverEnds = new TaskCompletionSource();
        await using var pump = new ClientPump<Client>(client, capacity: 64, stallTimeout: TimeSpan.FromMinutes(5));
        pump.Post(_ => neverEnds.Task); // the client stops reading
        await Task.Delay(50);

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 200_000; i++)
        {
            Assert.True(pump.Post(_ => Task.CompletedTask));
            pump.Post(_ => Task.CompletedTask, "frame");
        }

        clock.Stop();
        Assert.True(clock.ElapsedMilliseconds < 15_000, $"posting took {clock.ElapsedMilliseconds} ms");
        Assert.True(pump.Pending <= 64, $"pending {pump.Pending}");
        Assert.True(pump.Dropped > 199_000);
        Assert.False(pump.Stalled);
    }

    [Fact]
    public async Task ASendThatExceedsTheStallTimeoutStopsThePumpAndRaisesOnStalled()
    {
        var client = new Client();
        int stalled = 0;
        await using var pump = new ClientPump<Client>(client, capacity: 8, stallTimeout: TimeSpan.FromMilliseconds(200), onStalled: () => Interlocked.Increment(ref stalled));
        pump.Post(_ => new TaskCompletionSource().Task);
        pump.Post(c => { c.Got.Add("queued"); return Task.CompletedTask; });

        await Saves.SaveServer.WaitUntilAsync(() => pump.Stalled, 5000, "stall detected");
        Assert.Equal(1, stalled);
        Assert.Equal(0, pump.Pending);
        Assert.False(pump.Post(_ => Task.CompletedTask)); // a stopped pump refuses new work
        Assert.Empty(client.Got);
    }

    [Fact]
    public async Task AFailingSendDoesNotStopThePump()
    {
        var client = new Client();
        await using var pump = new ClientPump<Client>(client);
        pump.Post(_ => throw new InvalidOperationException("connection closed"));
        pump.Post(c => { c.Got.Add("after"); return Task.CompletedTask; });
        await Saves.SaveServer.WaitUntilAsync(() => client.Got.Count == 1, 5000, "send after failure");
    }
}
