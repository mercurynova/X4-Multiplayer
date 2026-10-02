using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode.Tests;

/// <summary>M1-F2: the failure-injection options (parsing, the impaired stream, the resume bookkeeping of a client, the frame inspector, the fuzzer's own behaviour).</summary>
public sealed class FailureInjectionTests
{
    private static CliOptions Parse(params string[] args)
    {
        var r = CliParser.Parse(args);
        Assert.True(r.Ok, r.Error);
        return r.Options!;
    }

    // ---- command line

    [Fact]
    public void ParsesTheFailureInjectionOptions()
    {
        var o = Parse("swarm", "--clients", "6", "--slow-reader", "4096", "--slow-clients", "2", "--latency", "100", "--jitter", "15ms", "--disconnect-every", "10", "--reload-every", "15s");
        Assert.Equal(4096, o.SlowReader!.BytesPerSecond);
        Assert.Equal(2, o.SlowClients);
        Assert.Equal(100, o.LatencyMs);
        Assert.Equal(15, o.JitterMs);
        Assert.Equal(10, o.DisconnectEverySeconds);
        Assert.Equal(15, o.ReloadEverySeconds);
        Assert.True(o.IsSlowReader(0) && o.IsSlowReader(1) && !o.IsSlowReader(2));
        Assert.True(o.InjectsStreamFaults);
    }

    [Fact]
    public void TheSlowReaderTakesARateOrAPausePattern()
    {
        var pattern = Parse("client", "--slow-reader=pause=3/30").SlowReader!;
        Assert.Equal(0, pattern.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(3), pattern.Run);
        Assert.Equal(TimeSpan.FromSeconds(30), pattern.Pause);
        Assert.Equal("pause=3/30", pattern.ToString());
        Assert.Equal("512 B/s", Parse("client", "--slow-reader", "512").SlowReader!.ToString());
    }

    [Theory]
    [InlineData("client", "--slow-reader", "0")]
    [InlineData("client", "--slow-reader", "fast")]
    [InlineData("client", "--slow-reader", "pause=3")]
    [InlineData("client", "--slow-reader", "pause=3/0")]
    [InlineData("authority", "--slow-reader", "100")]
    [InlineData("client", "--latency", "-5")]
    [InlineData("client", "--latency", "70000")]
    [InlineData("client", "--disconnect-every", "0")]
    [InlineData("authority", "--disconnect-every", "10")]
    [InlineData("authority", "--reload-every", "10")]
    [InlineData("fuzz", "--fuzz-mode", "loud")]
    [InlineData("fuzz", "--local-ip", "not-an-ip")]
    [InlineData("inspect", "--clients", "2")]
    public void RejectsBadFailureInjectionArguments(params string[] args)
    {
        var r = CliParser.Parse(args);
        Assert.False(r.Ok);
        Assert.False(string.IsNullOrEmpty(r.Error));
    }

    [Fact]
    public void FuzzAndInspectParse()
    {
        var fuzz = Parse("fuzz", "--duration", "60", "--seed", "9", "--clients", "3", "--fuzz-mode", "session", "--local-ip", "127.0.0.2", "--rotate-ip");
        Assert.Equal(FakeNodeCommand.Fuzz, fuzz.Command);
        Assert.Equal(60, fuzz.Duration);
        Assert.Equal(9UL, fuzz.Seed);
        Assert.Equal(3, fuzz.Clients);
        Assert.Equal(FuzzMode.Session, fuzz.FuzzMode);
        Assert.Equal("127.0.0.2", fuzz.LocalIp);
        Assert.True(fuzz.RotateIp);

        var inspect = Parse("inspect", "--filter", "SessionState, RosterUpdate", "--max-frames", "5", "--no-join");
        Assert.Equal(FakeNodeCommand.Inspect, inspect.Command);
        Assert.Equal(["SessionState", "RosterUpdate"], inspect.Filter);
        Assert.Equal(5, inspect.MaxFrames);
        Assert.True(inspect.NoJoin);
        Assert.Equal(FakeNodeCommand.Inspect, Parse("inspect").Command); // no --sector needed any more
    }

    // ---- the impaired stream

    private static async Task<(Stream Wrapped, Stream Peer, IDisposable Cleanup)> PairAsync(StreamImpairment impairment)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var server = await accept;
        listener.Stop();
        client.NoDelay = server.NoDelay = true;
        return (new ImpairedStream(client.GetStream(), impairment), server.GetStream(), new Cleanup(client, server));
    }

    private sealed class Cleanup(TcpClient a, TcpClient b) : IDisposable
    {
        public void Dispose()
        {
            a.Dispose();
            b.Dispose();
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream s, int count)
    {
        var buffer = new byte[count];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await s.ReadExactlyAsync(buffer, cts.Token);
        return buffer;
    }

    [Fact]
    public async Task LatencyDelaysBothDirectionsAndKeepsTheOrder()
    {
        var imp = new StreamImpairment(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(60), null, 1);
        var (wrapped, peer, cleanup) = await PairAsync(imp);
        using var _ = cleanup;
        await using var __ = wrapped;

        // write path: the peer sees the bytes late, in order
        var clock = Stopwatch.StartNew();
        for (byte i = 0; i < 20; i++)
            await wrapped.WriteAsync(new[] { i });
        Assert.True(clock.ElapsedMilliseconds < 1000, "a delayed write must not block the writer");
        var received = await ReadExactlyAsync(peer, 20);
        Assert.True(clock.ElapsedMilliseconds >= 85, $"arrived after {clock.ElapsedMilliseconds} ms");
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (byte)i), received);

        // read path
        clock.Restart();
        for (byte i = 0; i < 20; i++)
            await peer.WriteAsync(new[] { i });
        var back = await ReadExactlyAsync(wrapped, 20);
        Assert.True(clock.ElapsedMilliseconds >= 85, $"arrived after {clock.ElapsedMilliseconds} ms");
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (byte)i), back);
    }

    [Fact]
    public async Task ASlowReaderReadsAtItsRateOnlyOnceActivated()
    {
        var imp = new StreamImpairment(TimeSpan.Zero, TimeSpan.Zero, new SlowReaderSpec(2000, TimeSpan.Zero, TimeSpan.Zero), 1);
        var (wrapped, peer, cleanup) = await PairAsync(imp);
        using var _ = cleanup;
        await using var __ = wrapped;

        // not active yet: full speed. Unthrottled 50 000 B is near-instant; at 2000 B/s it would take 25 s, so 10 s tells them apart on any runner.
        await peer.WriteAsync(new byte[50_000]);
        var clock = Stopwatch.StartNew();
        await ReadExactlyAsync(wrapped, 50_000);
        Assert.True(clock.ElapsedMilliseconds < 10_000, $"inactive slow reader took {clock.ElapsedMilliseconds} ms");

        imp.ActivateSlowReader();
        Assert.True(imp.SlowActive);
        // The pump may be blocked in a full-size read that started before the switch and would swallow whatever arrives next in one go.
        // Feed it one sacrificial byte first; once that is read the pump's next reads are throttled to 100 B each.
        await peer.WriteAsync(new byte[1]);
        await ReadExactlyAsync(wrapped, 1);
        await peer.WriteAsync(new byte[3000]);
        clock.Restart();
        await ReadExactlyAsync(wrapped, 3000);
        // 3000 B in reads of at most 100 B, each followed by a pause of n/2000 s: at least 29 pauses of 50 ms (about 1.45 s) can never be shortened by a
        // busy machine, only stretched, so there is a hard lower bound and the upper one is only a hang guard.
        Assert.InRange(clock.ElapsedMilliseconds, 1200, 60_000);
        Assert.True(imp.BytesReadSlowly > 0);
    }

    [Fact]
    public async Task APausePatternStopsReadingForItsPause()
    {
        var imp = new StreamImpairment(TimeSpan.Zero, TimeSpan.Zero, new SlowReaderSpec(0, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(1500)), 1);
        var (wrapped, peer, cleanup) = await PairAsync(imp);
        using var _ = cleanup;
        await using var __ = wrapped;

        imp.ActivateSlowReader();
        var clock = Stopwatch.StartNew();
        await Task.Delay(500); // now inside the pause
        Assert.True(imp.PauseRemaining() > TimeSpan.FromMilliseconds(600));
        await peer.WriteAsync(new byte[10]);
        await ReadExactlyAsync(wrapped, 10);
        Assert.True(clock.ElapsedMilliseconds >= 1500, $"read during the pause after {clock.ElapsedMilliseconds} ms");
        Assert.True(imp.PausedMilliseconds >= 500);
    }

    [Fact]
    public async Task ClosingThePeerEndsTheImpairedRead()
    {
        var imp = new StreamImpairment(TimeSpan.FromMilliseconds(20), TimeSpan.Zero, null, 1);
        var (wrapped, peer, cleanup) = await PairAsync(imp);
        using var _ = cleanup;
        await using var __ = wrapped;
        await peer.WriteAsync(new byte[] { 1, 2, 3 });
        peer.Close();
        Assert.Equal([1, 2, 3], await ReadExactlyAsync(wrapped, 3));
        Assert.Equal(0, await wrapped.ReadAsync(new byte[1]));
    }

    // ---- the client's resume bookkeeping and the inspector

    [Fact]
    public void AResumeStartsTheGhostTableOverAndKeyframesCountAgain()
    {
        var galaxy = FakeGalaxy.Generate(42);
        var world = new FakeWorld(galaxy);
        var session = new FakeClientSession(new FakeWorld(galaxy), verify: true, () => 0);
        int ship = galaxy.Entities.First(e => !e.IsStation).EntityId;
        uint netId = FakeNetIds.ToNetId(ship);

        Frame Spawn() => new(MsgType.EntitySpawn, FrameOptions.None, Lane.Control, MessageEncoder.EncodePayload(
            b => EntitySpawn.Pack(b, new EntitySpawnT { Entities = [new EntityRecordT { NetId = netId, Kind = galaxy.Entities[ship - 1].Kind, State = world.GetState(ship, 0) }] }), 256));

        Frame Full()
        {
            var s = world.GetState(ship, 2.0);
            var entry = new ReplicationEntry
            {
                NetId = netId, Mask = ReplicationMask.Sector | ReplicationMask.Pos | ReplicationMask.Rot | ReplicationMask.Vel | ReplicationMask.Flags | ReplicationMask.Status | ReplicationMask.Time,
                Sector = s.Sector, PosX = s.Px, PosY = s.Py, PosZ = s.Pz, Yaw = s.Yaw, Pitch = s.Pitch, Roll = s.Roll, VelX = s.Vx, VelY = s.Vy, VelZ = s.Vz, StateFlags = s.Flags, Hull = 255, Shield = 255,
            };
            return new Frame(MsgType.Replication, FrameOptions.None, Lane.Realtime, MessageEncoder.EncodePayload(
                b => Replication.Pack(b, new ReplicationT { ServerTick = 1, AuthorityGameTime = 2.0, EntryCount = 1, Entries = [.. ReplicationCodec.Encode([entry])] }), 128));
        }

        session.Handle(Spawn());
        session.Handle(Full());
        Assert.Equal(1, session.Ghosts);
        Assert.Equal(1, session.KeyframesSinceResume);

        session.ResetForResume();
        Assert.Equal(0, session.Ghosts);
        Assert.Equal(1, session.GhostsBeforeResume);
        Assert.Equal(0, session.KeyframesSinceResume);
        Assert.Equal(1, session.Resumes);

        // the server re-sends the spawn and a keyframe; both are accepted without an error
        session.Handle(Spawn());
        session.Handle(Full());
        Assert.Equal(1, session.Ghosts);
        Assert.Equal(1, session.KeyframesSinceResume);
        Assert.Equal(0, session.Errors);

        session.Report("no-keyframe-after-resume", "test");
        Assert.Equal(1, session.Errors);
    }

    [Fact]
    public void TheInspectorDescribesADecodedMessage()
    {
        var ping = new PingT { Seq = 7, SendTimeUs = 1234 };
        var frame = new Frame(MsgType.Ping, FrameOptions.None, Lane.Control, MessageEncoder.EncodePayload(b => Ping.Pack(b, ping)));
        string text = FrameInspector.Describe(frame);
        Assert.Contains("Ping{", text, StringComparison.Ordinal);
        Assert.Contains("Seq=7", text, StringComparison.Ordinal);
        Assert.Contains("SendTimeUs=1234", text, StringComparison.Ordinal);

        var garbage = new Frame(MsgType.Ping, FrameOptions.None, Lane.Control, [1, 2, 3, 4]);
        Assert.Contains("undecodable", FrameInspector.Describe(garbage), StringComparison.Ordinal);
    }

    [Fact]
    public void TheInspectorFiltersAndStopsAtTheLimit()
    {
        var lines = new List<string>();
        int stops = 0;
        var o = Parse("inspect", "--filter", "Ping", "--max-frames", "2");
        var inspector = new FrameInspector(o, lines.Add, () => stops++);
        var ping = new Frame(MsgType.Ping, FrameOptions.None, Lane.Control, MessageEncoder.EncodePayload(b => Ping.Pack(b, new PingT { Seq = 1 })));
        var pong = new Frame(MsgType.Pong, FrameOptions.None, Lane.Control, MessageEncoder.EncodePayload(b => Pong.Pack(b, new PongT { Seq = 1 })));
        inspector.Tap(pong);
        inspector.Tap(ping);
        inspector.Tap(ping);
        inspector.Tap(ping);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Contains("Ping", l, StringComparison.Ordinal));
        Assert.Equal(1, stops);
        Assert.Equal(4, inspector.Total);
        Assert.Contains("Ping=3", inspector.Summary(), StringComparison.Ordinal);
    }

    // ---- the fuzzer's own behaviour

    [Fact]
    public async Task FuzzAgainstNothingReportsTheServerGone()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0))
        {
            l.Start();
            port = ((IPEndPoint)l.LocalEndpoint).Port;
        }

        var o = Parse("fuzz", "--server", $"127.0.0.1:{port}", "--duration", "1", "--seed", "3");
        var text = new StringWriter();
        int exit = await LiveRunner.RunAsync(o, text, null, CancellationToken.None);
        Assert.Equal(LiveRunner.ExitErrors, exit);
        Assert.Contains("server-alive=NO", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("refused=", text.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FuzzAgainstAServerThatNeverAnswersSummarisesAndReportsItDead()
    {
        // a server that accepts and swallows everything: only the client side is looked at
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var acceptor = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var c = await listener.AcceptTcpClientAsync(stop.Token);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await c.GetStream().CopyToAsync(Stream.Null, stop.Token);
                        }
                        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                        {
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        async Task<string> RunAsync(ulong seed)
        {
            var o = Parse("fuzz", "--server", $"127.0.0.1:{port}", "--duration", "2", "--seed", seed.ToString(System.Globalization.CultureInfo.InvariantCulture), "--fuzz-mode", "handshake");
            var text = new StringWriter();
            await LiveRunner.RunAsync(o, text, null, CancellationToken.None);
            return text.ToString();
        }

        string first = await RunAsync(5);
        Assert.Contains("server-alive=NO", first, StringComparison.Ordinal);
        Assert.Contains("fuzz: connections=", first, StringComparison.Ordinal);
        Assert.Contains("scenarios=[pre:", first, StringComparison.Ordinal);
        Assert.Contains("closed-by-server=[", first, StringComparison.Ordinal);

        await stop.CancelAsync();
        listener.Stop();
        await acceptor;
    }
}
