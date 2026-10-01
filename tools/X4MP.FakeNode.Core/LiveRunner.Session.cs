using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;

namespace X4MP.FakeNode;

/// <summary>The frames of one node connection: one persistent reader (a frame read is never cancelled half way) that tracks the node's own phase and hands the rest to a handler.</summary>
internal sealed class NodeLink(TcpNodeClient client, int playerId)
{
    private readonly TaskCompletionSource<bool> _session = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _reader = Task.CompletedTask;
    private volatile NodePhase _phase = NodePhase.Admitted;
    private volatile Action<Frame>? _handler;

    public TcpNodeClient Client { get; } = client;

    public int PlayerId { get; } = playerId;

    /// <summary>The phase the server last reported for this node (<c>RosterUpdate</c>).</summary>
    public NodePhase Phase => _phase;

    /// <summary>Receives every frame the link does not handle itself (set before sending anything that triggers answers).</summary>
    public Action<Frame>? Handler
    {
        get => _handler;
        set => _handler = value;
    }

    public bool Closed { get; private set; }

    public DisconnectCode? DisconnectedBy { get; private set; }

    public Exception? Failure { get; private set; }

    public void Start(CancellationToken ct) => _reader = Task.Run(() => ReadAsync(ct), CancellationToken.None);

    private async Task ReadAsync(CancellationToken ct)
    {
        try
        {
            while (await Client.ReceiveAsync(ct).ConfigureAwait(false) is { } frame)
            {
                Dispatch(frame);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            Failure = ex;
        }
        finally
        {
            Closed = true;
            _session.TrySetResult(false);
        }
    }

    private void Dispatch(Frame frame)
    {
        switch (frame.Type)
        {
            case MsgType.SessionState:
                _session.TrySetResult(true);
                break;
            case MsgType.RosterUpdate:
                foreach (var p in MessageRegistry.Default.Decode<RosterUpdate>(frame).UnPack().Players ?? [])
                {
                    if (p.PlayerId == PlayerId)
                        _phase = p.Phase;
                }
                break;
            case MsgType.Disconnect:
                DisconnectedBy = MessageRegistry.Default.Decode<Disconnect>(frame).Code;
                break;
        }

        _handler?.Invoke(frame);
    }

    /// <summary>True when the server announced a session (a real session actor); false after <paramref name="timeout"/> or when the connection ended first.</summary>
    public async Task<bool> WaitForSessionAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await _session.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public async Task StopAsync()
    {
        try
        {
            await _reader.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // the reader reports through Failure
        }
    }

    /// <summary>Waits until the server reports a phase at or past <paramref name="phase"/>; false on timeout.</summary>
    public async Task<bool> WaitPhaseAsync(NodePhase phase, TimeSpan timeout, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (Rank(Phase) < Rank(phase))
        {
            if (Closed)
                return false;
            if (watch.Elapsed > timeout)
                return false;
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
        return true;
    }

    private static int Rank(NodePhase phase) => phase <= NodePhase.InGame ? (int)phase : -1;
}

public static partial class LiveRunner
{
    private static readonly NodePhase[] JoinSteps =
        [NodePhase.SyncingSave, NodePhase.Verifying, NodePhase.Loading, NodePhase.Matching, NodePhase.CatchingUp];

    /// <summary>Pings every <c>PingInterval</c>; the Pongs reach the stats through <see cref="TcpNodeClient.PongReceived"/>. Ends with an error when the link dies or a Pong stays away.</summary>
    private static async Task PingLoopAsync(
        NodeLink link, LiveNodeStats stats, LiveRunOptions run, SynchronizedWriter lines, string name, bool print, CancellationToken ct)
    {
        var nextReport = DateTime.UtcNow + run.ReportInterval;
        var lastPongAt = Stopwatch.StartNew();
        long pongs = stats.Pings;
        while (!ct.IsCancellationRequested)
        {
            if (link.Closed)
                throw new IOException(link.DisconnectedBy is { } code ? $"server closed the connection ({code})" : "connection closed");
            await link.Client.SendPingAsync(ct).ConfigureAwait(false);
            await Task.Delay(run.PingInterval, ct).ConfigureAwait(false);

            if (stats.Pings > pongs)
            {
                pongs = stats.Pings;
                lastPongAt.Restart();
            }
            else if (lastPongAt.Elapsed > run.PingTimeout)
            {
                throw new TimeoutException("ping timed out");
            }

            if (print && DateTime.UtcNow >= nextReport)
            {
                nextReport = DateTime.UtcNow + run.ReportInterval;
                await lines.WriteAsync($"[{name}] rtt={Ms(stats.LastRtt)} avg={Ms(stats.AvgRtt)} max={Ms(stats.MaxRtt)} pings={stats.Pings}").ConfigureAwait(false);
            }
        }
    }

    private static async Task SendLoadStatusAsync(TcpNodeClient client, NodePhase phase, CancellationToken ct) =>
        await client.SendAsync(MsgType.LoadStatus, b => LoadStatus.CreateLoadStatus(b, phase, 0f, 0, default, DisconnectCode.None), ct).ConfigureAwait(false);

    /// <summary>
    /// The mod's join pipeline in miniature: waits for the team step, reports every load phase, waits for the server to move the node
    /// along (a report before the server's reader saw the previous phase would be a policy violation), sends <c>NodeReady</c> and waits for InGame.
    /// </summary>
    private static async Task AdvanceToInGameAsync(NodeLink link, LiveRunOptions run, CancellationToken ct)
    {
        // The team module (if any) moves a node out of Admitted at once (Auto) or parks it in AwaitingTeam (Lobby, AdminAssign).
        await link.WaitPhaseAsync(NodePhase.SyncingSave, TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        if (link.Phase == NodePhase.AwaitingTeam)
        {
            if (!await link.WaitPhaseAsync(NodePhase.SyncingSave, run.PhaseTimeout, ct).ConfigureAwait(false))
                throw new TimeoutException("the server keeps the node in AwaitingTeam (assign it a team)");
        }

        foreach (var step in JoinSteps)
        {
            await SendLoadStatusAsync(link.Client, step, ct).ConfigureAwait(false);
            if (!await link.WaitPhaseAsync(step, run.PhaseTimeout, ct).ConfigureAwait(false))
                throw new TimeoutException($"the server did not move the node to {step} (it is {link.Phase})");
        }

        await link.Client.SendAsync(MsgType.NodeReady, b => NodeReady.CreateNodeReady(b, 42), ct).ConfigureAwait(false);
        if (!await link.WaitPhaseAsync(NodePhase.InGame, run.PhaseTimeout, ct).ConfigureAwait(false))
            throw new TimeoutException($"the server did not put the node in game (it is {link.Phase})");
    }

    private static async Task RunSessionNodeAsync(
        CliOptions o, NodePlan plan, int index, NodeLink link, LiveNodeStats stats, LiveRunOptions run, SynchronizedWriter lines,
        FakeGalaxy galaxy, bool print, CancellationToken ct)
    {
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pings = PingLoopAsync(link, stats, run, lines, plan.Name, print, pingCts.Token);
        try
        {
            Task role = plan.Role == Role.Authority
                ? RunAuthorityAsync(link, o, galaxy, run, plan.Name, lines, ct)
                : RunClientAsync(link, o, index, galaxy, stats, run, plan.Name, lines, ct);
            var finished = await Task.WhenAny(role, pings).ConfigureAwait(false);
            await finished.ConfigureAwait(false); // rethrows a failure of either side
            await role.ConfigureAwait(false);
        }
        finally
        {
            await pingCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await pings.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // reported through the other task
            }
        }
    }

    // ------------------------------------------------------------------ the fake authority

    private static async Task RunAuthorityAsync(
        NodeLink link, CliOptions o, FakeGalaxy galaxy, LiveRunOptions run, string name, SynchronizedWriter lines, CancellationToken ct)
    {
        var world = new FakeWorld(galaxy);
        var authority = new FakeAuthority(world, new FakeAuthorityOptions { TickRateHz = o.TickRate, Fps = o.Fps });
        var captures = new ConcurrentQueue<CaptureSetT>();
        link.Handler = frame =>
        {
            if (frame.Type == MsgType.CaptureSet)
                captures.Enqueue(MessageRegistry.Default.Decode<CaptureSet>(frame).UnPack());
        };

        foreach (var message in authority.StartupMessages())
            await link.Client.SendPayloadAsync(message.Type, message.Payload, ct).ConfigureAwait(false);
        await AdvanceToInGameAsync(link, run, ct).ConfigureAwait(false);
        await lines.WriteAsync($"[{name}] authority in game: {galaxy.Sectors.Count} sectors, {galaxy.Entities.Count} entities, streaming at {o.TickRate} Hz").ConfigureAwait(false);

        var clock = Stopwatch.StartNew();
        long lastTick = -1;
        long nextStats = 0;
        long nextSummary = 0;
        long sentMessages = 0;
        while (!ct.IsCancellationRequested)
        {
            if (link.Closed)
                throw new IOException(link.DisconnectedBy is { } code ? $"server closed the connection ({code})" : "connection closed");
            long target = (long)(clock.Elapsed.TotalSeconds * o.TickRate);
            if (target - lastTick > 40)
                lastTick = target - 40; // far behind: skip rather than burst
            while (lastTick < target)
            {
                long tick = ++lastTick;
                while (captures.TryDequeue(out var set))
                    authority.OnCaptureSet(set, tick);
                foreach (var message in authority.Tick(tick))
                {
                    await link.Client.SendPayloadAsync(message.Type, message.Payload, ct).ConfigureAwait(false);
                    sentMessages++;
                }

                if (tick >= nextStats)
                {
                    nextStats = tick + (2L * o.TickRate);
                    var stats = authority.BuildNodeStats(tick);
                    await link.Client.SendPayloadAsync(stats.Type, stats.Payload, ct).ConfigureAwait(false);
                }

                if (tick >= nextSummary)
                {
                    nextSummary = tick + (5L * o.TickRate);
                    var summary = authority.BuildGalaxySummary(tick);
                    await link.Client.SendPayloadAsync(summary.Type, summary.Payload, ct).ConfigureAwait(false);
                }
            }

            await Task.Delay(8, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ the fake client

    private static async Task RunClientAsync(
        NodeLink link, CliOptions o, int index, FakeGalaxy galaxy, LiveNodeStats stats, LiveRunOptions run, string name, SynchronizedWriter lines,
        CancellationToken ct)
    {
        var session = new FakeClientSession(new FakeWorld(galaxy), o.Verify);
        run.OnClientSession?.Invoke(session);
        stats.AttachSession(session);
        var outbox = Channel.CreateUnbounded<OutMessage>();
        link.Handler = frame =>
        {
            foreach (var reply in session.Handle(frame))
                outbox.Writer.TryWrite(reply);
        };

        await AdvanceToInGameAsync(link, run, ct).ConfigureAwait(false);
        var player = new FakePlayer(galaxy, o.Seed, index + 1, o.Behavior);
        await lines.WriteAsync($"[{name}] in game, flying {o.Behavior} from sector {player.Sector}").ConfigureAwait(false);

        var clock = Stopwatch.StartNew();
        long lastTick = -1;
        long nextStale = 0;
        while (!ct.IsCancellationRequested)
        {
            if (link.Closed)
                throw new IOException(link.DisconnectedBy is { } code ? $"server closed the connection ({code})" : "connection closed");
            while (outbox.Reader.TryRead(out var reply))
                await link.Client.SendPayloadAsync(reply.Type, reply.Payload, ct).ConfigureAwait(false);

            long tick = (long)(clock.Elapsed.TotalSeconds * FakePlayer.TickRateHz);
            if (tick > lastTick)
            {
                lastTick = tick;
                var state = player.Step(tick);
                await link.Client.SendAsync(MsgType.PlayerState, b => PlayerState.Pack(b, state), ct).ConfigureAwait(false);
            }

            if (tick >= nextStale)
            {
                nextStale = tick + (10L * (long)FakePlayer.TickRateHz);
                session.CheckStale();
            }

            await Task.Delay(8, ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ the summary

    private static async Task WriteVerifySummaryAsync(List<LiveNodeStats> stats, SynchronizedWriter lines)
    {
        var sessions = stats.Where(s => s.Session is not null).Select(s => (s.Name, Session: s.Session!)).ToList();
        if (sessions.Count == 0)
        {
            await lines.WriteAsync("verify: no client received a session (is the server running with a session actor, and an authority connected?)").ConfigureAwait(false);
            return;
        }

        foreach (var (name, session) in sessions)
        {
            await lines.WriteAsync($"[{name}] verify: {session.Summary()} checked={session.Verifier.EntriesChecked} stale={session.StaleEntries} tombstoned={session.TombstonedEntries}").ConfigureAwait(false);
            foreach (var v in session.Violations.Take(5))
                await lines.WriteAsync($"[{name}]   violation {v.Kind} net_id={v.NetId} t={v.GameTime.ToString("F2", CultureInfo.InvariantCulture)}: {v.Detail}").ConfigureAwait(false);
        }

        long errors = sessions.Sum(s => s.Session.Errors);
        await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
            $"verify: clients={sessions.Count} frames={sessions.Sum(s => s.Session.ReplicationFrames)} entries={sessions.Sum(s => s.Session.ReplicationEntries)} " +
            $"checked={sessions.Sum(s => s.Session.Verifier.EntriesChecked)} ghosts={sessions.Sum(s => s.Session.Ghosts)} spawns={sessions.Sum(s => s.Session.SpawnsApplied)} " +
            $"despawns={sessions.Sum(s => s.Session.DespawnsApplied)} checksums-ok={sessions.Sum(s => s.Session.ChecksumsOk)}/{sessions.Sum(s => s.Session.ChecksumsOk + s.Session.ChecksumMismatches)} " +
            $"resyncs={sessions.Sum(s => s.Session.ResyncsRequested)} position-errors={sessions.Sum(s => s.Session.Verifier.Errors)} errors={errors}")).ConfigureAwait(false);
    }
}
