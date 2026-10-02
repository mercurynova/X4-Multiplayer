using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;

namespace X4MP.FakeNode;

/// <summary>Counters of one <c>fuzz</c> run.</summary>
internal sealed class FuzzStats
{
    public long Connections;
    public long HandshakesOk;
    public long Frames;
    public long PolicyViolationFrames;
    public long Bytes;
    public long Refused;
    public long Banned;
    public long Resets;
    public long Errors;
    public readonly ConcurrentDictionary<string, long> Scenarios = new();
    public readonly ConcurrentDictionary<string, long> Closes = new();

    /// <summary>The local address the fuzzers currently use with <c>--rotate-ip</c> (moves on to the next loopback address when the server bans it).</summary>
    public string? Ip;

    public int Rotations;

    public void Rotate(string current)
    {
        var parts = current.Split('.');
        if (parts.Length != 4 || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int last))
            return;
        string next = $"{parts[0]}.{parts[1]}.{parts[2]}.{(last >= 250 ? 2 : last + 1)}";
        if (Interlocked.CompareExchange(ref Ip, next, current) == current)
            Interlocked.Increment(ref Rotations);
    }

    public static void Count(ConcurrentDictionary<string, long> table, string key) => table.AddOrUpdate(key, 1, (_, v) => v + 1);

    public static string Format(ConcurrentDictionary<string, long> table) =>
        string.Join(",", table.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
}

/// <summary>
/// <c>fakenode fuzz</c>: a hostile peer (server-design 6.4). Seeded, so a run can be repeated: it opens connections and sends garbage before the
/// handshake, oversized and zero-length frames, bad flags and lanes, unknown types, truncated frames, half-open floods, and after a valid handshake
/// frames of the wrong role, lane or phase and payloads that are not FlatBuffers. The server must stay up, count the violations, close the
/// connection and, past 20 policy violations a minute, temp-ban the address.
/// </summary>
public static partial class LiveRunner
{
    private static readonly MsgType[] KnownTypes = [.. Enum.GetValues<MsgType>()];

    /// <summary>Types only an authority may send (a client sending them is a role violation).</summary>
    private static readonly MsgType[] AuthorityOnly =
    [
        MsgType.WorldUpdate, MsgType.EntitySpawn, MsgType.EntityDespawn, MsgType.SaveChunk, MsgType.GalaxyMetadata, MsgType.StringTableAdd,
        MsgType.SectorComplete, MsgType.EntityStatusBatch, MsgType.SaveUploadBegin,
    ];

    /// <summary>Types only the server sends (a node sending them is a violation).</summary>
    private static readonly MsgType[] ServerOnly =
    [
        MsgType.Welcome, MsgType.Replication, MsgType.CaptureSet, MsgType.InterestChecksum, MsgType.WorldCatchUp, MsgType.ServerHello, MsgType.RosterUpdate,
    ];

    /// <summary>Types a client may send but only in some phases or only with a valid payload.</summary>
    private static readonly MsgType[] ClientTypes =
    [
        MsgType.Ping, MsgType.Pong, MsgType.LoadStatus, MsgType.NodeReady, MsgType.NodeStats, MsgType.PlayerState, MsgType.ResyncRequest, MsgType.Intent,
        MsgType.SaveReady, MsgType.Disconnect,
    ];

    private static async Task<int> RunFuzzAsync(CliOptions o, TextWriter output, CancellationToken stop)
    {
        var lines = new SynchronizedWriter(output);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        if (o.Duration is { } seconds)
            cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        var stats = new FuzzStats();
        var clock = Stopwatch.StartNew();
        await lines.WriteAsync($"fakenode fuzz: {o.Clients} fuzzer(s) -> {o.Host}:{o.Port} seed={o.Seed} mode={o.FuzzMode.ToString().ToLowerInvariant()}" +
                               (o.LocalIp is { } ip ? $" from {ip}" : string.Empty) + (o.Duration is { } d ? $" for {d}s" : " until Ctrl+C")).ConfigureAwait(false);

        var workers = Enumerable.Range(0, o.Clients).Select(i => Task.Run(() => FuzzWorkerAsync(o, i, stats, cts.Token), CancellationToken.None)).ToList();
        var reporter = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), cts.Token).ConfigureAwait(false);
                    await lines.WriteAsync(FuzzLine($"t={clock.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s", stats)).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
        await Task.WhenAll(workers).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        await reporter.ConfigureAwait(false);

        await lines.WriteAsync(FuzzLine("fuzz:", stats) + $" scenarios=[{FuzzStats.Format(stats.Scenarios)}]").ConfigureAwait(false);

        // Is the server still there and answering? A plain handshake (a refusal such as Banned also proves it answers).
        string alive;
        try
        {
            await using var probe = await TcpNodeClient.ConnectAsync(o.Host, o.Port,
                new NodeClientOptions { PlayerName = "FuzzProbe", PlayerKey = DeriveKey(o.Seed, "fuzz-probe"), Password = o.Password, RequestedRoles = Role.Observer, HandshakeTimeout = TimeSpan.FromSeconds(3) }, CancellationToken.None)
                .ConfigureAwait(false);
            alive = "yes (handshake ok)";
        }
        catch (HandshakeRejectedException ex) when (ex.Code is not (DisconnectCode.None or DisconnectCode.HandshakeTimeout))
        {
            alive = $"yes (the server answered: {ex.Code})";
        }
        catch (HandshakeRejectedException ex)
        {
            alive = $"NO (no answer: {ex.Message})";
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or OperationCanceledException)
        {
            alive = $"NO ({ex.GetType().Name}: {ex.Message})";
        }

        await lines.WriteAsync($"fuzz: server-alive={alive} elapsed={clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s").ConfigureAwait(false);
        return alive.StartsWith("yes", StringComparison.Ordinal) ? ExitOk : ExitErrors;
    }

    private static string FuzzLine(string prefix, FuzzStats s) => string.Create(CultureInfo.InvariantCulture,
        $"{prefix} connections={s.Connections} handshakes-ok={s.HandshakesOk} frames-sent={s.Frames} (policy-violating={s.PolicyViolationFrames}) bytes={s.Bytes} " +
        $"refused={s.Refused} banned-answers={s.Banned} ip-rotations={s.Rotations} resets={s.Resets} closed-by-server=[{FuzzStats.Format(s.Closes)}]");

    private static async Task FuzzWorkerAsync(CliOptions o, int worker, FuzzStats st, CancellationToken ct)
    {
        var rng = new Random(unchecked((int)(o.Seed * 2654435761UL) ^ (worker * 7919)));
        var key = DeriveKey(o.Seed, $"fuzz{worker}");
        while (!ct.IsCancellationRequested)
        {
            string scenario = PickScenario(o.FuzzMode, rng);
            FuzzStats.Count(st.Scenarios, scenario);
            string? ip = o.RotateIp ? Volatile.Read(ref st.Ip) ?? (st.Ip = o.LocalIp ?? "127.0.0.2") : o.LocalIp;
            long bansBefore = Interlocked.Read(ref st.Banned);
            try
            {
                await RunScenarioAsync(o with { LocalIp = ip }, scenario, rng, key, worker, st, ct).ConfigureAwait(false);
                if (o.RotateIp && ip is not null && Interlocked.Read(ref st.Banned) > bansBefore)
                    st.Rotate(ip);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (HandshakeRejectedException ex)
            {
                FuzzStats.Count(st.Closes, "handshake:" + ex.Code);
                if (ex.Code == DisconnectCode.Banned)
                {
                    Interlocked.Increment(ref st.Banned);
                    if (o.RotateIp && ip is not null)
                        st.Rotate(ip);
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.TimedOut)
            {
                Interlocked.Increment(ref st.Refused);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or ProtocolViolation or OperationCanceledException or TimeoutException)
            {
                Interlocked.Increment(ref st.Resets);
            }
            catch (Exception ex)
            {
                // a bug in the fuzzer itself: keep going, but let the summary show it
                Interlocked.Increment(ref st.Errors);
                FuzzStats.Count(st.Closes, "fuzzer-bug:" + ex.GetType().Name);
            }

            try
            {
                await Task.Delay(rng.Next(15, 90), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static readonly string[] PreHandshake =
    [
        "garbage", "oversized-length", "zero-length", "bad-flags", "bad-lane", "compressed-flag", "truncated-hold", "slow-header", "hello-garbage",
        "unknown-type", "wrong-first-message", "immediate-close",
    ];

    private static readonly string[] InSession =
    [
        "wrong-role", "server-only", "wrong-lane", "unknown-type-in-session", "wrong-phase", "invalid-flatbuffers", "violation-burst", "oversized-in-session",
        "zero-length-in-session", "bad-flags-in-session", "truncated-in-session", "junk-after-valid", "random-frames",
    ];

    private static string PickScenario(FuzzMode mode, Random rng) => mode switch
    {
        FuzzMode.Handshake => "pre:" + PreHandshake[rng.Next(PreHandshake.Length)],
        FuzzMode.Flood => "flood",
        FuzzMode.Session => "session:" + InSession[rng.Next(InSession.Length)],
        _ => rng.Next(100) switch
        {
            < 35 => "pre:" + PreHandshake[rng.Next(PreHandshake.Length)],
            < 40 => "flood",
            _ => "session:" + InSession[rng.Next(InSession.Length)],
        },
    };

    private static byte[] RawFrame(uint length, ushort type, byte flags, byte lane, byte[] payload)
    {
        var bytes = new byte[FrameCodec.HeaderSize + payload.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), type);
        bytes[6] = flags;
        bytes[7] = lane;
        payload.CopyTo(bytes.AsSpan(FrameCodec.HeaderSize));
        return bytes;
    }

    private static byte[] RandomBytes(Random rng, int length)
    {
        var bytes = new byte[length];
        rng.NextBytes(bytes);
        return bytes;
    }

    private static Lane CatalogLane(MsgType type) =>
        MessageRegistry.Default.TryGetDescriptor(type, out var d) ? d.Lane : Lane.Control;

    private static ushort UnknownType(Random rng)
    {
        while (true)
        {
            ushort candidate = (ushort)rng.Next(1, 0xFFFF);
            if (!Enum.IsDefined((MsgType)candidate))
                return candidate;
        }
    }

    private static async Task<TcpClient> OpenRawAsync(CliOptions o, FuzzStats st, CancellationToken ct)
    {
        var tcp = o.LocalIp is { } ip ? new TcpClient(new IPEndPoint(IPAddress.Parse(ip), 0)) : new TcpClient();
        tcp.NoDelay = true;
        try
        {
            await tcp.ConnectAsync(o.Host, o.Port, ct).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        Interlocked.Increment(ref st.Connections);
        return tcp;
    }

    private static async Task SendRawAsync(Stream stream, byte[] bytes, FuzzStats st, bool policyViolation, CancellationToken ct)
    {
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref st.Frames);
        Interlocked.Add(ref st.Bytes, bytes.Length);
        if (policyViolation)
            Interlocked.Increment(ref st.PolicyViolationFrames);
    }

    /// <summary>Reads what the server does with the connection for up to <paramref name="wait"/>: a Disconnect code, a clean close, a reset, or nothing.</summary>
    private static async Task ObserveAsync(Stream stream, TimeSpan wait, FuzzStats st, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        string outcome = "still-open";
        try
        {
            while (true)
            {
                var frame = await FrameCodec.ReadFrameAsync(stream, 8 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    outcome = outcome == "still-open" ? "eof" : outcome;
                    break;
                }

                if (frame.Value.Type == MsgType.Disconnect)
                {
                    outcome = MessageRegistry.Default.Decode<Disconnect>(frame.Value).Code.ToString();
                    if (outcome == nameof(DisconnectCode.Banned))
                        Interlocked.Increment(ref st.Banned);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // waited long enough
        }
        catch (Exception ex) when (ex is IOException or SocketException or ProtocolViolation or ObjectDisposedException)
        {
            outcome = outcome == "still-open" ? "reset" : outcome;
        }

        FuzzStats.Count(st.Closes, outcome);
    }

    private static async Task RunScenarioAsync(CliOptions o, string scenario, Random rng, byte[] key, int worker, FuzzStats st, CancellationToken ct)
    {
        if (scenario == "flood")
        {
            await FloodAsync(o, rng, st, ct).ConfigureAwait(false);
            return;
        }

        if (scenario.StartsWith("session:", StringComparison.Ordinal))
        {
            await SessionScenarioAsync(o, scenario[8..], rng, key, worker, st, ct).ConfigureAwait(false);
            return;
        }

        using var tcp = await OpenRawAsync(o, st, ct).ConfigureAwait(false);
        var stream = tcp.GetStream();
        var hello = (ushort)MsgType.ClientHello;
        switch (scenario[4..])
        {
            case "garbage":
                await SendRawAsync(stream, RandomBytes(rng, rng.Next(1, 4096)), st, false, ct).ConfigureAwait(false);
                break;
            case "oversized-length":
                uint[] sizes = [(uint)FrameCodec.DefaultMaxFrameBytes + 1, 64 * 1024 + 1, 0x7FFFFFFF, 0xFFFFFFFF, 0x10000000];
                await SendRawAsync(stream, RawFrame(sizes[rng.Next(sizes.Length)], hello, 0, 0, RandomBytes(rng, rng.Next(0, 32))), st, false, ct).ConfigureAwait(false);
                break;
            case "zero-length":
                await SendRawAsync(stream, RawFrame(0, hello, 0, 0, []), st, false, ct).ConfigureAwait(false);
                break;
            case "bad-flags":
                await SendRawAsync(stream, RawFrame(24, hello, (byte)(rng.Next(2, 256) | 0x80), 0, RandomBytes(rng, 24)), st, false, ct).ConfigureAwait(false);
                break;
            case "bad-lane":
                await SendRawAsync(stream, RawFrame(24, hello, 0, (byte)rng.Next(3, 256), RandomBytes(rng, 24)), st, false, ct).ConfigureAwait(false);
                break;
            case "compressed-flag":
                await SendRawAsync(stream, RawFrame(24, hello, 1, 0, RandomBytes(rng, 24)), st, false, ct).ConfigureAwait(false);
                break;
            case "truncated-hold":
                await SendRawAsync(stream, RawFrame(200, hello, 0, 0, RandomBytes(rng, 20))[..28], st, false, ct).ConfigureAwait(false);
                break;
            case "slow-header":
                await SendRawAsync(stream, RawFrame(64, hello, 0, 0, [])[..rng.Next(1, 8)], st, false, ct).ConfigureAwait(false);
                break;
            case "hello-garbage":
                int n = rng.Next(1, 300);
                await SendRawAsync(stream, RawFrame((uint)n, hello, 0, 0, RandomBytes(rng, n)), st, false, ct).ConfigureAwait(false);
                break;
            case "unknown-type":
                int u = rng.Next(1, 200);
                await SendRawAsync(stream, RawFrame((uint)u, UnknownType(rng), 0, (byte)rng.Next(0, 3), RandomBytes(rng, u)), st, true, ct).ConfigureAwait(false);
                break;
            case "wrong-first-message":
                var first = ClientTypes[rng.Next(ClientTypes.Length)];
                int w = rng.Next(1, 100);
                await SendRawAsync(stream, RawFrame((uint)w, (ushort)first, 0, (byte)CatalogLane(first), RandomBytes(rng, w)), st, true, ct).ConfigureAwait(false);
                break;
            case "immediate-close":
                Interlocked.Increment(ref st.Frames);
                break;
        }

        await ObserveAsync(stream, TimeSpan.FromMilliseconds(rng.Next(300, 1500)), st, ct).ConfigureAwait(false);
    }

    private static async Task FloodAsync(CliOptions o, Random rng, FuzzStats st, CancellationToken ct)
    {
        var open = new List<TcpClient>();
        try
        {
            for (int i = 0; i < 24 && !ct.IsCancellationRequested; i++)
            {
                try
                {
                    open.Add(await OpenRawAsync(o, st, ct).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is SocketException or IOException)
                {
                    Interlocked.Increment(ref st.Refused);
                    break;
                }
            }

            await Task.Delay(rng.Next(200, 1000), ct).ConfigureAwait(false);
            foreach (var tcp in open.Take(4))
                await ObserveAsync(tcp.GetStream(), TimeSpan.FromMilliseconds(50), st, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var tcp in open)
                tcp.Dispose();
        }
    }

    private static async Task SessionScenarioAsync(CliOptions o, string scenario, Random rng, byte[] key, int worker, FuzzStats st, CancellationToken ct)
    {
        var options = new NodeClientOptions
        {
            PlayerName = $"Fz{o.Seed}-{worker:00}",
            PlayerKey = key,
            Password = o.Password,
            RequestedRoles = rng.Next(4) == 0 ? Role.Observer : Role.Client,
            LocalAddress = o.LocalIp,
        };
        await using var client = await TcpNodeClient.ConnectAsync(o.Host, o.Port, options, ct).ConfigureAwait(false);
        Interlocked.Increment(ref st.Connections);
        Interlocked.Increment(ref st.HandshakesOk);

        // Reads whatever the server sends (answering its pings, as a node should) and notes how it ends the connection.
        string outcome = "still-open";
        var reader = Task.Run(async () =>
        {
            try
            {
                while (await client.ReceiveAsync(ct).ConfigureAwait(false) is { } frame)
                {
                    if (frame.Type == MsgType.Disconnect)
                        outcome = MessageRegistry.Default.Decode<Disconnect>(frame).Code.ToString();
                }

                if (outcome == "still-open")
                    outcome = "eof";
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or ProtocolViolation or OperationCanceledException)
            {
                if (outcome == "still-open")
                    outcome = "reset";
            }
        }, CancellationToken.None);

        await Task.Delay(rng.Next(50, 400), ct).ConfigureAwait(false); // let the server place the node before it misbehaves
        async Task Send(byte[] bytes, bool violation)
        {
            await client.SendRawFrameAsync(bytes, ct).ConfigureAwait(false);
            Interlocked.Increment(ref st.Frames);
            Interlocked.Add(ref st.Bytes, bytes.Length);
            if (violation)
                Interlocked.Increment(ref st.PolicyViolationFrames);
        }

        byte[] RoleViolation() { var t = AuthorityOnly[rng.Next(AuthorityOnly.Length)]; return FrameCodec.Encode(t, CatalogLane(t), RandomBytes(rng, rng.Next(8, 96))); }
        byte[] ServerOnlyFrame() { var t = ServerOnly[rng.Next(ServerOnly.Length)]; return FrameCodec.Encode(t, CatalogLane(t), RandomBytes(rng, rng.Next(8, 96))); }
        byte[] WrongLane() { var t = ClientTypes[rng.Next(ClientTypes.Length)]; var lane = (Lane)(((int)CatalogLane(t) + rng.Next(1, 3)) % 3); return FrameCodec.Encode(t, lane, RandomBytes(rng, rng.Next(8, 64))); }
        byte[] Unknown() { int n = rng.Next(1, 64); return RawFrame((uint)n, UnknownType(rng), 0, (byte)rng.Next(0, 3), RandomBytes(rng, n)); }
        byte[] WrongPhase()
        {
            // frames that are fine in game but not in the phase this node is in (or while it has no team): garbage payloads, the policy refuses first
            MsgType[] types = [MsgType.PlayerState, MsgType.ResyncRequest, MsgType.InterestHint, MsgType.SaveReady, MsgType.ManifestReport];
            var t = types[rng.Next(types.Length)];
            return FrameCodec.Encode(t, CatalogLane(t), RandomBytes(rng, rng.Next(8, 96)));
        }

        switch (scenario)
        {
            case "wrong-role":
                for (int i = 0, n = rng.Next(1, 6); i < n; i++)
                    await Send(RoleViolation(), true).ConfigureAwait(false);
                break;
            case "server-only":
                for (int i = 0, n = rng.Next(1, 6); i < n; i++)
                    await Send(ServerOnlyFrame(), true).ConfigureAwait(false);
                break;
            case "wrong-lane":
                for (int i = 0, n = rng.Next(1, 6); i < n; i++)
                    await Send(WrongLane(), true).ConfigureAwait(false);
                break;
            case "unknown-type-in-session":
                for (int i = 0, n = rng.Next(1, 6); i < n; i++)
                    await Send(Unknown(), true).ConfigureAwait(false);
                break;
            case "wrong-phase":
                for (int i = 0, n = rng.Next(1, 6); i < n; i++)
                    await Send(WrongPhase(), true).ConfigureAwait(false);
                break;
            case "invalid-flatbuffers":
                // allowed types whose payload is not a FlatBuffers table: the decoder must refuse, never crash
                MsgType[] decodable = [MsgType.Ping, MsgType.Pong, MsgType.LoadStatus, MsgType.NodeStats, MsgType.Disconnect, MsgType.Intent];
                var type = decodable[rng.Next(decodable.Length)];
                await Send(FrameCodec.Encode(type, CatalogLane(type), RandomBytes(rng, rng.Next(1, 200))), false).ConfigureAwait(false);
                break;
            case "violation-burst":
                // more than 20 policy violations inside a minute: the server closes the connection and temp-bans the address
                for (int i = 0, n = rng.Next(24, 40); i < n; i++)
                {
                    await Send(rng.Next(3) switch { 0 => RoleViolation(), 1 => ServerOnlyFrame(), _ => WrongLane() }, true).ConfigureAwait(false);
                    if (outcome != "still-open")
                        break;
                }

                break;
            case "oversized-in-session":
                uint[] sizes = [(uint)FrameCodec.DefaultMaxFrameBytes + 1, 0x7FFFFFFF, 0xFFFFFFFF];
                await Send(RawFrame(sizes[rng.Next(sizes.Length)], (ushort)MsgType.Ping, 0, 0, RandomBytes(rng, 16)), false).ConfigureAwait(false);
                break;
            case "zero-length-in-session":
                await Send(RawFrame(0, (ushort)MsgType.Ping, 0, 0, []), false).ConfigureAwait(false);
                break;
            case "bad-flags-in-session":
                await Send(RawFrame(16, (ushort)MsgType.Ping, (byte)rng.Next(2, 256), 0, RandomBytes(rng, 16)), false).ConfigureAwait(false);
                break;
            case "truncated-in-session":
                await Send(RawFrame(400, (ushort)MsgType.NodeStats, 0, 0, RandomBytes(rng, 30))[..38], false).ConfigureAwait(false);
                break;
            case "junk-after-valid":
                await client.SendPingAsync(ct).ConfigureAwait(false);
                await Send(RandomBytes(rng, rng.Next(1, 2000)), false).ConfigureAwait(false);
                break;
            default:
                // a stream of random frames of every kind
                for (int i = 0, n = rng.Next(5, 40); i < n && outcome == "still-open"; i++)
                {
                    var t = KnownTypes[rng.Next(KnownTypes.Length)];
                    var lane = rng.Next(5) == 0 ? (Lane)rng.Next(0, 3) : CatalogLane(t);
                    await Send(FrameCodec.Encode(t, lane, RandomBytes(rng, rng.Next(1, 200))), true).ConfigureAwait(false);
                    if (rng.Next(4) == 0)
                        await Task.Delay(rng.Next(1, 20), ct).ConfigureAwait(false);
                }

                break;
        }

        // Give the server a moment to react, then leave politely if it let us stay.
        for (int i = 0; i < 20 && outcome == "still-open"; i++)
            await Task.Delay(50, ct).ConfigureAwait(false);
        string seen = outcome;
        FuzzStats.Count(st.Closes, seen);
        if (seen == nameof(DisconnectCode.Banned))
            Interlocked.Increment(ref st.Banned);
        await client.DisposeAsync().ConfigureAwait(false); // a polite Disconnect(ClientQuit) when the server let us stay
        await reader.ConfigureAwait(false);
    }
}
