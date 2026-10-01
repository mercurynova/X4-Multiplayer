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

    private volatile int _team;

    /// <summary>The team the server last reported for this node (<c>RosterUpdate</c>); 0 = none yet.</summary>
    public int Team => _team;

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
                    {
                        _phase = p.Phase;
                        _team = p.TeamId;
                    }
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

    // ------------------------------------------------------------------ the save pipeline (M1-12)

    /// <summary>True when the server runs the save service (it says so in <c>server_caps</c> with the <c>SaveHttp</c> bit); a bare session actor does not.</summary>
    private static bool HasSaveService(TcpNodeClient client) => (client.ServerHello.ServerCaps & (ulong)Capability.SaveHttp) != 0;

    private static bool IsSaveFrame(MsgType type) => type is
        MsgType.SessionSaveInfo or MsgType.SaveDownloadAccept or MsgType.SaveChunk or MsgType.StringTableAdd or MsgType.WorldCatchUp or MsgType.RosterUpdate;

    /// <summary>Runs the frames of the save pipeline in order, one at a time (a chunk is written and acked before the next is looked at).</summary>
    private static async Task PumpSaveFramesAsync(FakeSaveClient saves, ChannelReader<Frame> frames, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in frames.ReadAllAsync(ct).ConfigureAwait(false))
                await saves.HandleAsync(frame, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or ProtocolViolation)
        {
            saves.Abort(ex.Message);
        }
    }

    /// <summary>Waits for the whole join: the checkpoint, the download, the match and the catch-up, then the server's InGame.</summary>
    private static async Task JoinWithSavesAsync(NodeLink link, FakeSaveClient saves, LiveRunOptions run, CancellationToken ct)
    {
        await link.WaitPhaseAsync(NodePhase.SyncingSave, TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        if (link.Phase == NodePhase.AwaitingTeam && !await link.WaitPhaseAsync(NodePhase.SyncingSave, run.PhaseTimeout, ct).ConfigureAwait(false))
            throw new TimeoutException("the server keeps the node in AwaitingTeam (assign it a team)");

        var watch = Stopwatch.StartNew();
        while (!saves.Ready.IsCompleted)
        {
            if (saves.Error is { } error)
                throw new InvalidOperationException("the save pipeline failed: " + error);
            if (link.Closed)
                throw new IOException(link.DisconnectedBy is { } code ? $"server closed the connection ({code})" : "connection closed");
            if (watch.Elapsed > run.SaveTimeout)
                throw new TimeoutException($"the join did not finish within {run.SaveTimeout.TotalSeconds:F0} s (stage {saves.Stage}; does the session have an authority that uploads a checkpoint?)");
            await Task.Delay(20, ct).ConfigureAwait(false);
        }

        if (!await link.WaitPhaseAsync(NodePhase.InGame, run.PhaseTimeout, ct).ConfigureAwait(false))
            throw new TimeoutException($"the server did not put the node in game (it is {link.Phase})");
    }

    private static async Task RunSessionNodeAsync(
        CliOptions o, NodePlan plan, int index, NodeLink link, LiveNodeStats stats, LiveRunOptions run, SynchronizedWriter lines,
        FakeGalaxy galaxy, string saveDir, bool print, CancellationToken ct)
    {
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pings = PingLoopAsync(link, stats, run, lines, plan.Name, print, pingCts.Token);
        try
        {
            Task role = plan.Role == Role.Authority
                ? RunAuthorityAsync(link, o, galaxy, stats, run, saveDir, plan.Name, lines, ct)
                : RunClientAsync(link, o, index, galaxy, stats, run, saveDir, plan.Name, lines, ct);
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
        NodeLink link, CliOptions o, FakeGalaxy galaxy, LiveNodeStats stats, LiveRunOptions run, string saveDir, string name, SynchronizedWriter lines, CancellationToken ct)
    {
        var world = new FakeWorld(galaxy);
        var authority = new FakeAuthority(world, new FakeAuthorityOptions { TickRateHz = o.TickRate, Fps = o.Fps, TeamAssets = o.TeamAssets || o.Commander != CommanderMode.None });
        var captures = new ConcurrentQueue<CaptureSetT>();
        var intents = new ConcurrentQueue<IntentT>();

        // Against a server with a save service the authority also answers RequestSave: it builds a fake save and manifest, uploads them
        // in-band and the session starts from that checkpoint (protocol.md 6.3). Its GalaxyMetadata then travels with the checkpoint, keyed
        // by the save's real hash, so the startup messages carry only the string table.
        FakeAuthoritySaves? saves = null;
        if (HasSaveService(link.Client))
        {
            saves = new FakeAuthoritySaves(
                link.Client, authority,
                new FakeAuthoritySaveOptions { SaveBytes = run.SaveBytes ?? o.SaveMb * 1024L * 1024, Directory = saveDir },
                text => _ = lines.WriteAsync($"[{name}] {text}"));
            saves.CheckpointStored += result =>
            {
                stats.MarkInGame();
                stats.SetSaveBytes(saves.BytesSent);
                _ = lines.WriteAsync($"[{name}] checkpoint stored: save {result.Save.ShaHex[..12]} ({result.Save.Size} bytes), the session can start");
            };
        }

        link.Handler = frame =>
        {
            if (frame.Type == MsgType.CaptureSet)
            {
                captures.Enqueue(MessageRegistry.Default.Decode<CaptureSet>(frame).UnPack());
            }
            else if (frame.Type == MsgType.Intent)
            {
                // The server forwards an intent only after its permission checks: the fake authority just counts it and accepts.
                stats.CountIntentReceived();
                intents.Enqueue(MessageRegistry.Default.Decode<Intent>(frame).UnPack());
            }
            else
                saves?.Handle(frame);
        };

        foreach (var message in saves is null ? authority.StartupMessages() : authority.StringTableMessages())
            await link.Client.SendPayloadAsync(message.Type, message.Payload, ct).ConfigureAwait(false);
        saves?.MarkStringTableSent();
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
            while (intents.TryDequeue(out var intent))
            {
                var result = new IntentResultT
                {
                    RequestKey = intent.RequestKey ?? new Id128T(),
                    RequestId = intent.RequestId,
                    PlayerId = intent.PlayerId,
                    Status = IntentStatus.Accepted,
                    Detail = string.Empty,
                };
                await link.Client.SendPayloadAsync(MsgType.IntentResult, MessageEncoder.EncodePayload(b => IntentResult.Pack(b, result), 96), ct).ConfigureAwait(false);
            }

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
                    var nodeStats = authority.BuildNodeStats(tick);
                    await link.Client.SendPayloadAsync(nodeStats.Type, nodeStats.Payload, ct).ConfigureAwait(false);
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
        NodeLink link, CliOptions o, int index, FakeGalaxy galaxy, LiveNodeStats stats, LiveRunOptions run, string saveDir, string name, SynchronizedWriter lines,
        CancellationToken ct)
    {
        var session = new FakeClientSession(new FakeWorld(galaxy), o.Verify);
        run.OnClientSession?.Invoke(session);
        stats.AttachSession(session);
        var outbox = Channel.CreateUnbounded<OutMessage>();

        // Against a server with a save service the join is the real one: wait for the checkpoint, download and verify the save and its
        // manifest, match, take the WorldCatchUp (protocol.md 6.4/6.5). The frames of that pipeline are handled in order on their own task.
        FakeSaveClient? saves = null;
        Channel<Frame>? saveFrames = null;
        Task saveWorker = Task.CompletedTask;
        if (HasSaveService(link.Client))
        {
            saves = new FakeSaveClient(
                link.Client, new FakeSaveClientOptions { Directory = saveDir, LoadDelay = run.LoadDelay },
                text => _ = lines.WriteAsync($"[{name}] {text}"));
            saveFrames = Channel.CreateUnbounded<Frame>(new UnboundedChannelOptions { SingleReader = true });
        }

        link.Handler = frame =>
        {
            if (frame.Type == MsgType.IntentResult)
                stats.CountOrderResult(MessageRegistry.Default.Decode<IntentResult>(frame));
            foreach (var reply in session.Handle(frame))
                outbox.Writer.TryWrite(reply);
            if (saveFrames is not null && IsSaveFrame(frame.Type))
                saveFrames.Writer.TryWrite(frame);
        };

        try
        {
            if (saves is null)
            {
                await AdvanceToInGameAsync(link, run, ct).ConfigureAwait(false);
            }
            else
            {
                saveWorker = Task.Run(() => PumpSaveFramesAsync(saves, saveFrames!.Reader, ct), CancellationToken.None);
                var started = Stopwatch.StartNew();
                await JoinWithSavesAsync(link, saves, run, ct).ConfigureAwait(false);
                stats.MarkInGame();
                stats.SetSaveBytes(saves.BytesReceived);
                await lines.WriteAsync(
                    $"[{name}] joined with the save after {started.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s: " +
                    $"{saves.BytesReceived} bytes downloaded and verified ({saves.VerifiedSaveSha?[..12]}), {saves.StringEntries} strings, {saves.CatchUpEntries} catch-up entries").ConfigureAwait(false);
            }
        }
        finally
        {
            saveFrames?.Writer.TryComplete();
            try
            {
                await saveWorker.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // reported through the join
            }

            saves?.Dispose();
        }

        var player = new FakePlayer(galaxy, o.Seed, index + 1, o.Behavior);
        await lines.WriteAsync($"[{name}] in game, flying {o.Behavior} from sector {player.Sector}").ConfigureAwait(false);

        var clock = Stopwatch.StartNew();
        long lastTick = -1;
        long nextStale = 0;
        long nextOrder = (long)FakePlayer.TickRateHz;
        ulong orderKey = 0;
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

            if (o.Commander != CommanderMode.None && tick >= nextOrder)
            {
                nextOrder = tick + ((long)FakePlayer.TickRateHz / 2); // 2 orders per second
                if (session.PickAsset(o.Commander, link.Team, link.PlayerId, (int)orderKey) is { } asset)
                {
                    ulong key = ++orderKey;
                    var intent = new IntentT
                    {
                        RequestKey = new Id128T { Lo = key, Hi = (ulong)link.PlayerId },
                        RequestId = (uint)key,
                        GameTime = tick / FakePlayer.TickRateHz,
                        Body = IntentBodyUnion.FromAssetOrder(new AssetOrderT { Asset = asset.NetId, Order = OrderKind.MoveTo, Sector = asset.Sector }),
                    };
                    await link.Client.SendAsync(MsgType.Intent, b => Intent.Pack(b, intent), ct).ConfigureAwait(false);
                    stats.CountOrderSent();
                }
                else
                {
                    stats.CountNoTarget();
                }
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
