using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using X4MP.Proto;
using X4MP.Protocol.Client;

namespace X4MP.FakeNode;

/// <summary>Tunables for <see cref="LiveRunner"/> (tests shorten the intervals).</summary>
public sealed record LiveRunOptions
{
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReportInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Pause between starting consecutive nodes (keeps a swarm under the server's handshake rate limit).</summary>
    public TimeSpan ConnectStagger { get; init; } = TimeSpan.FromMilliseconds(60);

    /// <summary>
    /// How long a node waits after the handshake for the session announcement (<c>SessionState</c>) that only a real session actor sends. Without
    /// it (a bare gateway) the node stays a ping-only keepalive; with it the node plays its role: the authority streams the fake world,
    /// clients fly and receive replication.
    /// </summary>
    public TimeSpan SessionDetect { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a node waits for each step of the join pipeline (the server's phase change) before it gives up.</summary>
    public TimeSpan PhaseTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Test hooks for the clients' sessions (inject a fault, shorten the clock).</summary>
    public Action<FakeClientSession>? OnClientSession { get; init; }
}

/// <summary>Counters of one connected (or failed) node; read by the reporter, written by the node's loop.</summary>
public sealed class LiveNodeStats(string name, Role role)
{
    private volatile FakeClientSession? _session;

    private long _pings;
    private long _rttTicks;
    private long _maxRttTicks;
    private long _lastRttTicks;
    private int _connected;
    private int _errors;

    public string Name { get; } = name;
    public Role Role { get; } = role;
    public bool Connected => Volatile.Read(ref _connected) != 0;
    public int Errors => Volatile.Read(ref _errors);
    public long Pings => Interlocked.Read(ref _pings);
    public TimeSpan LastRtt => TimeSpan.FromTicks(Interlocked.Read(ref _lastRttTicks));
    public TimeSpan MaxRtt => TimeSpan.FromTicks(Interlocked.Read(ref _maxRttTicks));
    public TimeSpan AvgRtt => Pings == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Interlocked.Read(ref _rttTicks) / Pings);
    public string? LastError { get; private set; }

    /// <summary>The receiving half of a client in session mode (null for the authority and for ping-only nodes).</summary>
    public FakeClientSession? Session => _session;

    /// <summary>Violations the node's verification found (0 when it does not verify).</summary>
    public long VerifyErrors => _session?.Errors ?? 0;

    internal void AttachSession(FakeClientSession session) => _session = session;

    internal void MarkConnected() => Volatile.Write(ref _connected, 1);

    internal void MarkDisconnected() => Volatile.Write(ref _connected, 0);

    internal void Fail(string error)
    {
        LastError = error;
        Interlocked.Increment(ref _errors);
        MarkDisconnected();
    }

    internal void RecordRtt(TimeSpan rtt)
    {
        long ticks = rtt.Ticks;
        Interlocked.Increment(ref _pings);
        Interlocked.Add(ref _rttTicks, ticks);
        Interlocked.Exchange(ref _lastRttTicks, ticks);
        long max;
        while (ticks > (max = Interlocked.Read(ref _maxRttTicks)) && Interlocked.CompareExchange(ref _maxRttTicks, ticks, max) != max)
        {
        }
    }
}

/// <summary>
/// The live side of FakeNode: connects <c>authority</c>, <c>client</c> and <c>swarm</c> nodes to a real server with
/// <see cref="TcpNodeClient"/>, prints the Welcome summary and RTT figures, and ends on cancellation or after
/// <see cref="CliOptions.Duration"/>. Against a server with a session actor (<c>SessionState</c> arrives after the handshake) the nodes play
/// their role (see <c>LiveRunner.Session.cs</c>): the authority runs the fake world, honours <c>CaptureSet</c> and streams
/// <c>WorldUpdate</c>; clients walk the join pipeline, fly and send <c>PlayerState</c>, and with <c>--verify</c> check every
/// <c>Replication</c> entry against ground truth. Against a bare gateway they only keep the connection alive with Ping/Pong.
/// </summary>
public static partial class LiveRunner
{
    public const int ExitOk = 0;
    public const int ExitErrors = 1;
    public const int ExitNotAvailable = 3;

    public static async Task<int> RunAsync(CliOptions o, TextWriter output, LiveRunOptions? run, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(o);
        ArgumentNullException.ThrowIfNull(output);
        run ??= new LiveRunOptions();

        if (o.Command == FakeNodeCommand.Inspect)
        {
            await output.WriteLineAsync("fakenode inspect: needs sector replication (M1-05..08); not available yet.").ConfigureAwait(false);
            return ExitNotAvailable;
        }

        var plan = Plan(o);
        var lines = new SynchronizedWriter(output);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        if (o.Duration is { } seconds)
            cts.CancelAfter(TimeSpan.FromSeconds(seconds));

        var stats = plan.Select(p => new LiveNodeStats(p.Name, p.Role)).ToList();
        var galaxy = new Lazy<FakeGalaxy>(() => FakeGalaxy.Generate(o.Seed, new GalaxyOptions { SectorCount = o.Sectors, ShipCount = o.Ships }), LazyThreadSafetyMode.ExecutionAndPublication);
        bool single = plan.Count == 1;
        await lines.WriteAsync($"fakenode {o.Command.ToString().ToLowerInvariant()}: {plan.Count} node(s) -> {o.Host}:{o.Port}" +
                               (o.Duration is { } d ? $" for {d}s" : " until Ctrl+C")).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();

        var tasks = new List<Task>();
        for (int i = 0; i < plan.Count; i++)
        {
            tasks.Add(RunNodeAsync(o, plan[i], i, stats[i], single, run, lines, galaxy, cts.Token));
            if (i + 1 < plan.Count)
            {
                try
                {
                    await Task.Delay(run.ConnectStagger, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        var reporter = single ? Task.CompletedTask : ReportLoopAsync(stats, run, lines, clock, cts.Token);
        await Task.WhenAll(tasks).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        await reporter.ConfigureAwait(false);

        foreach (var s in stats.Where(s => s.Session is not null))
            s.Session!.CheckStale();
        long verifyErrors = stats.Sum(s => s.VerifyErrors);
        if (o.Verify)
            await WriteVerifySummaryAsync(stats, lines).ConfigureAwait(false);
        long errors = stats.Sum(s => s.Errors) + verifyErrors;
        await lines.WriteAsync($"summary: nodes={stats.Count} joined={stats.Count(s => s.Pings > 0)} errors={errors} pings={stats.Sum(s => s.Pings)} " +
                               $"rtt avg={Ms(Average(stats))} max={Ms(stats.Count == 0 ? TimeSpan.Zero : stats.Max(s => s.MaxRtt))} " +
                               $"elapsed={clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s").ConfigureAwait(false);
        return errors == 0 ? ExitOk : ExitErrors;
    }

    private sealed record NodePlan(string Name, Role Role);

    private static List<NodePlan> Plan(CliOptions o)
    {
        var plan = new List<NodePlan>();
        switch (o.Command)
        {
            case FakeNodeCommand.Authority:
                plan.Add(new NodePlan(o.Name, Role.Authority));
                break;
            case FakeNodeCommand.Client:
                if (o.Clients == 1)
                    plan.Add(new NodePlan(o.Name, Role.Client));
                else
                    AddClients(plan, o);
                break;
            case FakeNodeCommand.Swarm:
                if (o.WithAuthority)
                    plan.Add(new NodePlan(o.NamePrefix + "Authority", Role.Authority));
                AddClients(plan, o);
                break;
        }

        return plan;
    }

    private static void AddClients(List<NodePlan> plan, CliOptions o)
    {
        for (int i = 1; i <= o.Clients; i++)
            plan.Add(new NodePlan($"{o.NamePrefix}{i:00}", Role.Client));
    }

    /// <summary>Stable per (seed, name) so reruns against a persistent server rejoin as the same player.</summary>
    public static byte[] DeriveKey(ulong seed, string name) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"fakenode-key:{seed.ToString(CultureInfo.InvariantCulture)}:{name}"));

    private static async Task RunNodeAsync(
        CliOptions o, NodePlan plan, int index, LiveNodeStats stats, bool print, LiveRunOptions run, SynchronizedWriter lines,
        Lazy<FakeGalaxy> galaxy, CancellationToken ct)
    {
        var options = new NodeClientOptions
        {
            PlayerName = plan.Name,
            PlayerKey = DeriveKey(o.Seed, plan.Name),
            Password = o.Password,
            RequestedRoles = plan.Role,
        };

        TcpNodeClient client;
        try
        {
            client = await TcpNodeClient.ConnectAsync(o.Host, o.Port, options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            stats.Fail(ex.Message);
            await lines.WriteAsync($"[{plan.Name}] connect failed: {ex.Message}").ConfigureAwait(false);
            return;
        }

        await using (client.ConfigureAwait(false))
        {
            stats.MarkConnected();
            var w = client.Welcome;
            await lines.WriteAsync(
                $"[{plan.Name}] welcome: server='{client.ServerHello.ServerName}' v{client.ServerHello.ServerVersion} " +
                $"proto={client.ServerHello.ProtocolMajor}.{client.ServerHello.ProtocolMinor} phase={client.ServerHello.Phase} " +
                $"player_id={w.PlayerId} roles={w.GrantedRoles} caps=0x{w.NegotiatedCaps:x} conn={w.ConnId} " +
                $"resumed={w.Resumed} resume_grace={w.ResumeGraceS}s").ConfigureAwait(false);

            client.PongReceived += stats.RecordRtt;
            var link = new NodeLink(client, w.PlayerId);
            using var readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            link.Start(readerCts.Token);
            try
            {
                // Against a session actor the node plays its role; against a bare gateway it only keeps the socket alive.
                if (await link.WaitForSessionAsync(run.SessionDetect, ct).ConfigureAwait(false))
                {
                    await RunSessionNodeAsync(o, plan, index, link, stats, run, lines, galaxy.Value, print, ct).ConfigureAwait(false);
                }
                else
                {
                    await PingLoopAsync(link, stats, run, lines, plan.Name, print, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // normal end: Ctrl+C or --duration
            }
            catch (Exception ex)
            {
                stats.Fail(ex is OperationCanceledException ? "ping timed out" : ex.Message);
                await lines.WriteAsync($"[{plan.Name}] connection lost: {stats.LastError}").ConfigureAwait(false);
            }
            finally
            {
                await readerCts.CancelAsync().ConfigureAwait(false);
                await link.StopAsync().ConfigureAwait(false);
                client.PongReceived -= stats.RecordRtt;
            }
        }

        stats.MarkDisconnected();
    }

    private static async Task ReportLoopAsync(List<LiveNodeStats> stats, LiveRunOptions run, SynchronizedWriter lines, Stopwatch clock, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(run.ReportInterval, ct).ConfigureAwait(false);
                int connected = stats.Count(s => s.Connected);
                await lines.WriteAsync(
                    $"t={clock.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s connected={connected}/{stats.Count} " +
                    $"errors={stats.Sum(s => s.Errors)} pings={stats.Sum(s => s.Pings)} rtt avg={Ms(Average(stats))} max={Ms(stats.Max(s => s.MaxRtt))}").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static TimeSpan Average(IReadOnlyCollection<LiveNodeStats> stats)
    {
        long pings = stats.Sum(s => s.Pings);
        return pings == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(stats.Sum(s => s.AvgRtt.Ticks * s.Pings) / pings);
    }

    private static string Ms(TimeSpan t) => t.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture) + "ms";

    private sealed class SynchronizedWriter(TextWriter inner)
    {
        private readonly object _gate = new();

        public Task WriteAsync(string line)
        {
            lock (_gate)
            {
                inner.WriteLine(line);
                inner.Flush();
            }

            return Task.CompletedTask;
        }
    }
}
