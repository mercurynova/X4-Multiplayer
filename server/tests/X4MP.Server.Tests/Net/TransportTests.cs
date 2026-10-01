using System.Buffers.Binary;
using System.Diagnostics;
using X4MP.Core.Net;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Transport;

namespace X4MP.Server.Tests.Net;

[Collection("net")]
public class TransportTests(Xunit.Abstractions.ITestOutputHelper output)
{
    public static TheoryData<string> Kinds => ["inproc", "tcp"];

    /// <summary>Echo server: every frame read is sent straight back on its own lane.</summary>
    private static async Task EchoAsync(INodeConnection connection)
    {
        while (await connection.ReadAsync(CancellationToken.None) is { } inbound)
        {
            var frame = OutboundFrame.Create(inbound.Frame.Type, inbound.Frame.Lane, inbound.Frame.Payload);
            connection.TrySend(frame);
            frame.Release();
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task EchoRoundTripsFramesOverTransport(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind);
        using var stop = new CancellationTokenSource();
        var serve = net.Serve(EchoAsync, stop.Token);

        await using var client = await net.ConnectAsync();
        for (uint i = 1; i <= 50; i++)
        {
            await client.SendAsync(MsgType.Ping, TestFrames.Ping(i));
            var ping = await client.ReadAsync<Ping>(MsgType.Ping);
            Assert.Equal(i, ping.Seq);
        }

        stop.Cancel();
        await serve;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task FrameSplitIntoSingleBytesIsReassembled(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind);
        using var stop = new CancellationTokenSource();
        var serve = net.Serve(EchoAsync, stop.Token);
        await using var client = await net.ConnectAsync();

        var bytes = FrameCodec.Encode(MsgType.Ping, TestFrames.Ping(7));
        foreach (var b in bytes)
        {
            await client.Stream.WriteAsync(new[] { b });
            await client.Stream.FlushAsync();
        }

        var ping = await client.ReadAsync<Ping>(MsgType.Ping);
        Assert.Equal(7u, ping.Seq);

        // Two frames in one write.
        await client.SendRawAsync([.. bytes, .. FrameCodec.Encode(MsgType.Ping, TestFrames.Ping(8))]);
        Assert.Equal(7u, (await client.ReadAsync<Ping>(MsgType.Ping)).Seq);
        Assert.Equal(8u, (await client.ReadAsync<Ping>(MsgType.Ping)).Seq);
        stop.Cancel();
        await serve;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task OversizedLengthIsRejectedBeforeAllocation(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind);
        ProtocolViolation? violation = null;
        var seen = new TaskCompletionSource();
        using var stop = new CancellationTokenSource();
        var serve = net.Serve(async c =>
        {
            try
            {
                await c.ReadAsync(CancellationToken.None);
            }
            catch (ProtocolViolation v)
            {
                violation = v;
            }

            seen.SetResult();
        }, stop.Token);

        await using var client = await net.ConnectAsync();
        var header = new byte[8];
        FrameCodec.WriteHeader(header, 0x7FFF_FFFF, MsgType.Ping, FrameOptions.None, Lane.Control);
        long before = GC.GetTotalAllocatedBytes(precise: false);
        await client.SendRawAsync(header);
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        long grew = GC.GetTotalAllocatedBytes(precise: false) - before;

        Assert.NotNull(violation);
        Assert.Equal(ViolationCode.FrameTooLarge, violation.Code);
        Assert.True(grew < 50_000_000, $"allocated {grew} bytes while rejecting a 2 GiB length");
        stop.Cancel();
        await serve;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task TruncatedFrameAtEndOfStreamIsAViolation(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind);
        var result = new TaskCompletionSource<ViolationCode?>();
        using var stop = new CancellationTokenSource();
        var serve = net.Serve(async c =>
        {
            try
            {
                await c.ReadAsync(CancellationToken.None);
                result.SetResult(null);
            }
            catch (ProtocolViolation v)
            {
                result.SetResult(v.Code);
            }
        }, stop.Token);

        var client = await net.ConnectAsync();
        var frame = FrameCodec.Encode(MsgType.Ping, TestFrames.Ping());
        await client.SendRawAsync(frame[..(frame.Length - 3)]);
        await client.DisposeAsync();

        var outcome = await result.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (kind == "inproc")
        {
            Assert.Equal(ViolationCode.TruncatedFrame, outcome);
        }
        else
        {
            // Kestrel may report the closed socket (ConnectionClosed) before the reader sees the partial
            // frame; either way the connection is over and the partial frame is never delivered.
            Assert.True(outcome is null or ViolationCode.TruncatedFrame);
        }

        stop.Cancel();
        await serve;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task CloseSendsDisconnectThenEndsTheStream(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind);
        var accepted = new TaskCompletionSource<INodeConnection>();
        using var stop = new CancellationTokenSource();
        var serve = net.Serve(c =>
        {
            accepted.SetResult(c);
            return Task.CompletedTask;
        }, stop.Token);

        await using var client = await net.ConnectAsync();
        var server = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var ping = OutboundFrame.Create(MsgType.Ping, TestFrames.Ping(3).DataBuffer.ToSizedArray());
        server.TrySend(ping);
        ping.Release();
        server.Close(DisconnectCode.Kicked, "bye");

        Assert.Equal(3u, (await client.ReadAsync<Ping>(MsgType.Ping)).Seq); // pending Control frames still go out
        var disconnect = await client.ReadAsync<Disconnect>(MsgType.Disconnect);
        Assert.Equal(DisconnectCode.Kicked, disconnect.Code);
        Assert.Equal("bye", disconnect.Message);
        Assert.Null(await client.ReadAsync()); // then EOF
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(server.Closed.IsCancellationRequested);
        Assert.Equal(SendResult.Closed, server.TrySend(OutboundFrame.Create(MsgType.Ping, TestFrames.Ping().DataBuffer.ToSizedArray())));
        stop.Cancel();
        await serve;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task PeerDisconnectEndsReadAndReleasesTheConnection(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind);
        var accepted = new TaskCompletionSource<INodeConnection>();
        using var stop = new CancellationTokenSource();
        var serve = net.Serve(c =>
        {
            accepted.SetResult(c);
            return Task.CompletedTask;
        }, stop.Token);

        var client = await net.ConnectAsync();
        var server = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync();

        Assert.Null(await server.ReadAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => net.Listener.ActiveConnections == 0);
        stop.Cancel();
        await serve;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task RealtimeFramesCoalesceWhileThePeerIsNotReading(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind);
        var accepted = new TaskCompletionSource<INodeConnection>();
        using var stop = new CancellationTokenSource();
        var serve = net.Serve(c =>
        {
            accepted.SetResult(c);
            return Task.CompletedTask;
        }, stop.Token);

        await using var client = await net.ConnectAsync();
        var server = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A peer that never reads: producers still return immediately and the lane caps hold.
        var payload = new byte[2000];
        var sw = Stopwatch.StartNew();
        int dropped = 0;
        for (int i = 0; i < 20_000; i++)
        {
            var f = OutboundFrame.Create(MsgType.Replication, payload, coalesceKey: (ulong)(i % 5000) + 1);
            if (server.TrySend(f) == SendResult.DroppedLane)
            {
                dropped++;
            }

            f.Release();
        }

        sw.Stop();
        output.WriteLine($"{kind}: 20k Realtime sends against a non-reading peer took {sw.ElapsedMilliseconds} ms, {dropped} dropped");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(server.QueuedBytes(Lane.Realtime) <= 256 * 1024);
        Assert.False(server.Closed.IsCancellationRequested); // dropping Realtime never closes the connection
        stop.Cancel();
        await serve;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ThousandConnectDisconnectCyclesLeakNothing(string kind)
    {
        await using var net = await NetHarness.CreateAsync(kind);
        using var stop = new CancellationTokenSource();
        var serve = net.Serve(EchoAsync, stop.Token);

        // warm up (thread pool, JIT, socket infrastructure) before taking the baseline
        for (int i = 0; i < 20; i++)
        {
            await using var warm = await net.ConnectAsync();
            await warm.SendAsync(MsgType.Ping, TestFrames.Ping());
            await warm.ReadAsync();
        }

        await WaitForAsync(() => net.Listener.ActiveConnections == 0);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        int handlesBefore = process.HandleCount;

        for (int i = 0; i < 1000; i++)
        {
            await using var client = await net.ConnectAsync();
            await client.SendAsync(MsgType.Ping, TestFrames.Ping((uint)i));
            var echoed = await client.ReadAsync<Ping>(MsgType.Ping);
            Assert.Equal((uint)i, echoed.Seq);
        }

        await WaitForAsync(() => net.Listener.ActiveConnections == 0, 15_000);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        process.Refresh();
        int handlesAfter = process.HandleCount;
        output.WriteLine($"{kind}: connections created={net.Listener.TotalConnections} active={net.Listener.ActiveConnections} handles {handlesBefore} -> {handlesAfter}");

        Assert.Equal(0, net.Listener.ActiveConnections);
        Assert.True(net.Listener.TotalConnections >= 1020);
        Assert.True(handlesAfter - handlesBefore < 100, $"handle count grew by {handlesAfter - handlesBefore} over 1000 cycles");
        stop.Cancel();
        await serve;
    }

    [Fact]
    public async Task DatagramPathTakesRealtimeFramesWhenAttached()
    {
        await using var net = await NetHarness.CreateAsync("inproc");
        var accepted = new TaskCompletionSource<INodeConnection>();
        using var stop = new CancellationTokenSource();
        var serve = net.Serve(c =>
        {
            accepted.SetResult(c);
            return Task.CompletedTask;
        }, stop.Token);
        await using var client = await net.ConnectAsync();
        var server = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var path = new CapturePath();
        server.AttachDatagramPath(path);
        var rt = OutboundFrame.Create(MsgType.PlayerState, new byte[16]);
        var ctl = OutboundFrame.Create(MsgType.Ping, TestFrames.Ping().DataBuffer.ToSizedArray());
        Assert.Equal(SendResult.Queued, server.TrySend(rt));
        Assert.Equal(SendResult.Queued, server.TrySend(ctl));
        Assert.Equal([MsgType.PlayerState], path.Types);
        Assert.Equal(0, server.QueuedBytes(Lane.Realtime));
        Assert.Equal(MsgType.Ping, (await client.ReadAsync())!.Value.Type); // Control still uses the stream
        stop.Cancel();
        await serve;
    }

    private sealed class CapturePath : IDatagramPath
    {
        public List<MsgType> Types { get; } = [];

        public bool TrySend(OutboundFrame frame)
        {
            Types.Add(frame.MessageType);
            return true;
        }
    }

    [Fact]
    public async Task TcpListenerAcceptsNodeConnectionsThroughKestrelConnectionHandler()
    {
        await using var net = await NetHarness.CreateAsync("tcp");
        Assert.Equal("tcp", net.Listener.Name);
        Assert.IsType<TcpNodeListener>(net.Listener);
        using var stop = new CancellationTokenSource();
        var accepted = new TaskCompletionSource<INodeConnection>();
        var serve = net.Serve(c =>
        {
            accepted.SetResult(c);
            return Task.CompletedTask;
        }, stop.Token);
        await using var client = await net.ConnectAsync();
        var server = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<System.Net.IPEndPoint>(server.RemoteEndPoint);
        Assert.True(server.Id.Value > 0);
        stop.Cancel();
        await serve;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public void HeaderHelperAgreesWithWireLayout()
    {
        var bytes = FrameCodec.Encode(MsgType.Ping, TestFrames.Ping());
        Assert.Equal((ushort)MsgType.Ping, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
    }
}
