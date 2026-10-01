using X4MP.Core.Metrics;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using static X4MP.Core.Tests.Relay.RelayFrames;

namespace X4MP.Core.Tests.Session;

/// <summary>
/// Inbound flood protection with the actor held still (so nothing drains): the mailbox stays bounded, PlayerState is latest-wins,
/// other frames are capped per node, sustained overflow closes the node, and the authority is never dropped.
/// </summary>
public class InboundFloodTests
{
    private static NetOptions Small(int queue = 64, int overflowPerMinute = 100) =>
        new() { InboundQueueFramesPerNode = queue, InboundOverflowLimitPerMinute = overflowPerMinute, ViolationLimitPerMinute = 100000 };

    /// <summary>Blocks the actor thread until disposed.</summary>
    private sealed class Stall : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Task _task;

        public Stall(SessionActor actor)
        {
            _task = actor.CallAsync(() =>
            {
                _entered.Set();
                _release.Wait(TimeSpan.FromSeconds(30));
                return true;
            });
            Assert.True(_entered.Wait(TimeSpan.FromSeconds(5)));
        }

        public void Dispose()
        {
            _release.Set();
            _task.GetAwaiter().GetResult();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        long until = Environment.TickCount64 + 10_000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < until, what);
            await Task.Delay(2);
        }
    }

    [Fact]
    public async Task AFloodOfPlayerStatesCostsOneMailboxEntryAndTheLatestWins()
    {
        await using var rig = new ActorRig(null, null, Small(), []);
        await rig.JoinAuthorityAsync();
        var flooder = await rig.JoinAsync("Flooder");
        await rig.BringInGameAsync(flooder);
        long coalescedBefore = ServerMetrics.InboundCoalesced;
        long handledBefore = rig.Actor.FramesReceived;

        using (new Stall(rig.Actor))
        {
            for (uint i = 1; i <= 50_000; i++)
            {
                flooder.Connection.Push(MsgType.PlayerState, State(i, i * 1000UL, px: (int)i));
            }

            await WaitUntilAsync(() => flooder.Connection.PendingInbound == 0, "the reader never drained the connection");
            await Task.Delay(50);

            Assert.True(rig.Actor.PendingInputs <= 3, $"pending inputs: {rig.Actor.PendingInputs}"); // not 50,000
        }

        await rig.Actor.FlushAsync();
        await rig.Actor.FlushAsync();

        long handled = rig.Actor.FramesReceived - handledBefore;
        Assert.InRange(handled, 1, 5000); // far fewer than were sent
        Assert.True(flooder.Connection.Stats.InboundCoalesced >= 50_000 - handled - 2, $"coalesced {flooder.Connection.Stats.InboundCoalesced}, handled {handled}");
        Assert.True(ServerMetrics.InboundCoalesced - coalescedBefore >= 40_000);
        Assert.False(flooder.Connection.IsClosed); // coalescing is not a punishment
    }

    [Fact]
    public async Task OtherFramesAreCappedPerNodeAndSustainedOverflowClosesTheNode()
    {
        await using var rig = new ActorRig(null, null, Small(queue: 64, overflowPerMinute: 200), []);
        await rig.JoinAuthorityAsync();
        var flooder = await rig.JoinAsync("Flooder");
        var bystander = await rig.JoinAsync("Bystander");
        await rig.BringInGameAsync(flooder);
        long droppedBefore = ServerMetrics.InboundDropped;

        using (new Stall(rig.Actor))
        {
            for (int i = 0; i < 5000; i++)
            {
                flooder.Connection.Push(MsgType.ChatSend, Chat(ChatChannel.All, "flood " + i));
            }

            await WaitUntilAsync(() => flooder.Connection.IsClosed, "the flooder was never closed");
            await Task.Delay(50);

            Assert.Equal(DisconnectCode.RateLimited, flooder.Connection.CloseCode);
            Assert.True(rig.Actor.PendingInputs <= 64 + 8, $"pending inputs: {rig.Actor.PendingInputs}"); // the per-node cap, not 5000
            Assert.InRange(flooder.Connection.Stats.InboundDropped, 201, 5000);
            Assert.True(ServerMetrics.InboundDropped - droppedBefore >= 201);
            Assert.False(bystander.Connection.IsClosed);
        }

        await rig.Actor.FlushAsync();
        Assert.False(bystander.Connection.IsClosed);
    }

    [Fact]
    public async Task AFewFramesOverTheCapAreDroppedButTheNodeStaysConnected()
    {
        await using var rig = new ActorRig(null, null, Small(queue: 16, overflowPerMinute: 1000), []);
        await rig.JoinAuthorityAsync();
        var node = await rig.JoinAsync("Busy");
        await rig.BringInGameAsync(node);

        using (new Stall(rig.Actor))
        {
            for (int i = 0; i < 40; i++)
            {
                node.Connection.Push(MsgType.ChatSend, Chat(ChatChannel.All, "x" + i));
            }

            await WaitUntilAsync(() => node.Connection.PendingInbound == 0, "never read");
            await Task.Delay(50);
        }

        await rig.Actor.FlushAsync();
        Assert.False(node.Connection.IsClosed);
        Assert.InRange(node.Connection.Stats.InboundDropped, 20, 30); // 40 sent, about 16 kept
    }

    [Fact]
    public async Task TheAuthorityIsNeverDroppedItsReaderWaitsForRoomInstead()
    {
        await using var rig = new ActorRig(null, null, Small(queue: 16, overflowPerMinute: 5), []);
        var authority = await rig.JoinAuthorityAsync();
        await rig.BringInGameAsync(authority);
        long handledBefore = rig.Actor.FramesReceived;

        using (new Stall(rig.Actor))
        {
            for (int i = 0; i < 200; i++)
            {
                authority.Connection.Push(MsgType.NodeStats, NodeStatsFrame());
            }

            await Task.Delay(200);
            Assert.True(rig.Actor.PendingInputs <= 16 + 4, $"pending inputs: {rig.Actor.PendingInputs}"); // held at the cap by waiting
        }

        await WaitUntilAsync(() => rig.Actor.FramesReceived - handledBefore >= 200, "the authority's frames were lost");
        Assert.Equal(0, authority.Connection.Stats.InboundDropped);
        Assert.False(authority.Connection.IsClosed);
    }

    private static Google.FlatBuffers.FlatBufferBuilder NodeStatsFrame() => Frames.NodeStats(60, 5, 0, 0);
}
