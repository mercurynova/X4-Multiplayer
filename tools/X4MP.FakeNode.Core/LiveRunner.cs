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
}

/// <summary>Counters of one connected (or failed) node; read by the reporter, written by the node's loop.</summary>
public sealed class LiveNodeStats(string name, Role role)
{
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
/// First live slice of FakeNode (M1-F1b part 2): connects <c>authority</c>, <c>client</c> and <c>swarm</c> nodes
/// to a real server with <see cref="TcpNodeClient"/>, keeps them alive with Ping/Pong, prints the Welcome summary
/// and RTT figures, and ends on cancellation or after <see cref="CliOptions.Duration"/>. Replication (M1-05..08)
/// is not part of this slice.
/// </summary>
public static class LiveRunner
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

        if (o.Verify)
        {
            await output.WriteLineAsync("fakenode --verify: replication verification needs M1-05..08; not available yet. Re-run without --verify.").ConfigureAwait(false);
            return ExitNotAvailable;
        }

        var plan = Plan(o);
        var lines = new SynchronizedWriter(output);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        if (o.Duration is { } seconds)
            cts.CancelAfter(TimeSpan.FromSeconds(seconds));

        var stats = plan.Select(p => new LiveNodeStats(p.Name, p.Role)).ToList();
        bool single = plan.Count == 1;
        await lines.WriteAsync($"fakenode {o.Command.ToString().ToLowerInvariant()}: {plan.Count} node(s) -> {o.Host}:{o.Port}" +
                               (o.Duration is { } d ? $" for {d}s" : " until Ctrl+C")).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();

        var tasks = new List<Task>();
        for (int i = 0; i < plan.Count; i++)
        {
            tasks.Add(RunNodeAsync(o, plan[i], stats[i], single, run, lines, cts.Token));
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

        int errors = stats.Sum(s => s.Errors);
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
        CliOptions o, NodePlan plan, LiveNodeStats stats, bool print, LiveRunOptions run, SynchronizedWriter lines, CancellationToken ct)
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

            var nextReport = DateTime.UtcNow + run.ReportInterval;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    pingCts.CancelAfter(run.PingTimeout);
                    var rtt = await client.PingAsync(pingCts.Token).ConfigureAwait(false);
                    stats.RecordRtt(rtt);

                    if (print && DateTime.UtcNow >= nextReport)
                    {
                        nextReport = DateTime.UtcNow + run.ReportInterval;
                        await lines.WriteAsync($"[{plan.Name}] rtt={Ms(rtt)} avg={Ms(stats.AvgRtt)} max={Ms(stats.MaxRtt)} pings={stats.Pings}").ConfigureAwait(false);
                    }

                    await Task.Delay(run.PingInterval, ct).ConfigureAwait(false);
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
