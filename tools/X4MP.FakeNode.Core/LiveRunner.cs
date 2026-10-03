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

    /// <summary>Called once with every node's counters when the run has ended (tests read the traders and the fake authority's ground truth).</summary>
    public Action<IReadOnlyList<LiveNodeStats>>? OnFinished { get; init; }

    /// <summary>
    /// Early stop: polled about every 25 ms against the live node counters; when it returns true the run ends as if <c>--duration</c> had elapsed
    /// (the duration stays the upper bound). Lets tests end a run as soon as its goal is observable instead of sleeping the full duration.
    /// </summary>
    public Func<IReadOnlyList<LiveNodeStats>, bool>? StopWhen { get; init; }

    /// <summary>Size of the fake save the authority uploads (null = <c>--save-mb</c>, default 4 MiB).</summary>
    public long? SaveBytes { get; init; }

    /// <summary>Where the fake saves live (null = a fresh directory under the temp path, deleted at the end of the run).</summary>
    public string? SaveDirectory { get; init; }

    /// <summary>The fake clients' simulated <c>LoadGame</c> time.</summary>
    public TimeSpan LoadDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How long a client waits for the whole save pipeline (the authority's first checkpoint, the download, the match) against a server with a save service.</summary>
    public TimeSpan SaveTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>An authority run with <c>--expect-session-save</c> fails when the server sent no <c>SessionSaveInfo</c> within this long.</summary>
    public TimeSpan SessionSaveInfoTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Test hook: called with the fake authority when it is created (M1-F3: read its owners, hostility and reassign log).</summary>
    public Action<FakeAuthority>? OnAuthority { get; init; }

    /// <summary>Test hook: called with a client's handle once it is in game (M1-F3: send a targeted order, read its team).</summary>
    public Action<FakeClientHandle>? OnClientReady { get; init; }

    /// <summary>How long a client waits for the server's answer to a lobby team request.</summary>
    public TimeSpan TeamRequestTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long a <c>--reload-every</c> client stays away (the simulated extension reload).</summary>
    public TimeSpan ReloadDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>How long a client waits for the keyframes after a resume before it reports them missing.</summary>
    public TimeSpan ResumeKeyframeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a client with the avatar flow waits for the authority's answer to its <c>PlayerShip</c> (m3-plan 4.3).</summary>
    public TimeSpan AvatarTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Test hook for <c>inspect</c>: receives the printed lines in addition to the output writer.</summary>
    public Action<string>? OnInspectLine { get; init; }
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
    private int _inGame;
    private long _saveBytes;

    /// <summary>True once a client finished the join pipeline (save downloaded, matched, <c>NodeReady</c> sent) or the authority stored its first checkpoint.</summary>
    public bool InGame => Volatile.Read(ref _inGame) != 0;

    /// <summary>Bytes of save data this node moved (downloaded by a client, uploaded by the authority).</summary>
    public long SaveBytes => Interlocked.Read(ref _saveBytes);

    internal void MarkInGame() => Volatile.Write(ref _inGame, 1);

    internal void SetSaveBytes(long bytes) => Interlocked.Exchange(ref _saveBytes, bytes);

    public string Name { get; } = name;
    public Role Role { get; } = role;
    public bool Connected => Volatile.Read(ref _connected) != 0;
    public int Errors => Volatile.Read(ref _errors);
    public long Pings => Interlocked.Read(ref _pings);
    public TimeSpan LastRtt => TimeSpan.FromTicks(Interlocked.Read(ref _lastRttTicks));
    public TimeSpan MaxRtt => TimeSpan.FromTicks(Interlocked.Read(ref _maxRttTicks));
    public TimeSpan AvgRtt => Pings == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Interlocked.Read(ref _rttTicks) / Pings);
    public string? LastError { get; private set; }

    /// <summary>"off" (UDP not asked for or not offered), "binding", "bound" or "fallback" (no ack within 3 s: Realtime on TCP).</summary>
    public string UdpState { get; internal set; } = "off";

    /// <summary>The node's UDP lane (null when it has none).</summary>
    public UdpRealtimeClient? Udp { get; internal set; }

    /// <summary>The receiving half of a client in session mode (null for the authority and for ping-only nodes).</summary>
    public FakeClientSession? Session => _session;

    /// <summary>Violations the node's verification found (0 when it does not verify).</summary>
    public long VerifyErrors => _session?.Errors ?? 0;

    internal void AttachSession(FakeClientSession session) => _session = session;

    private long _avatarSpawnsSent;
    private long _avatarChangesSent;
    private int _avatarState;

    /// <summary>authority: <c>EntitySpawn</c>s (avatars and the host ship, also re-sent ones) it sent for <c>PlayerShip</c> requests.</summary>
    public long AvatarSpawnsSent => Interlocked.Read(ref _avatarSpawnsSent);

    /// <summary>authority: <c>EntityChange</c>s (a player left: Controller 0) it sent for avatars.</summary>
    public long AvatarChangesSent => Interlocked.Read(ref _avatarChangesSent);

    internal void CountAvatarMessage(MsgType type)
    {
        if (type == MsgType.EntitySpawn)
            Interlocked.Increment(ref _avatarSpawnsSent);
        else if (type == MsgType.EntityChange)
            Interlocked.Increment(ref _avatarChangesSent);
    }

    /// <summary>client with the avatar flow: 0 = not asked, 1 = <c>PlayerShip</c> sent and waiting, 2 = the avatar spawn arrived and the bot flies, 3 = no avatar within the timeout.</summary>
    public int AvatarState => Volatile.Read(ref _avatarState);

    internal void SetAvatarState(int state) => Volatile.Write(ref _avatarState, state);

    /// <summary>How long the avatar took from the request to its spawn (client with the avatar flow).</summary>
    public TimeSpan AvatarLatency { get; internal set; }

    /// <summary>The wingman controller of this client (null otherwise).</summary>
    public FakeWingman? Wingman { get; internal set; }

    /// <summary>The trading behaviour of a client started with <c>--trade</c> (null otherwise).</summary>
    public FakeTrader? Trader { get; internal set; }

    /// <summary>The player id the server gave this node (set once it is in the session).</summary>
    public int PlayerId { get; internal set; }

    /// <summary>The wallet model of this node (M1-F4): the authority sees every wallet, a client its own and its team's.</summary>
    public EconomyReconciler? Reconciler { get; internal set; }

    /// <summary>The credit behaviour of a client started with <c>--economy</c> or <c>--income-rate</c> (null otherwise).</summary>
    public FakeEconomist? Economist { get; internal set; }

    /// <summary>The <c>CreditDelta</c> emitter (<c>--income-rate</c>): the authority's income for the players, or a client's own changes.</summary>
    public FakeIncomeSource? Income { get; internal set; }

    /// <summary>The authority's trade executor with its ground truth (authority only).</summary>
    public FakeTradeAuthority? TradeAuthority { get; internal set; }
    // ---- M1-F3: teams
    private int _teamId;
    private long _lobbyRequests;
    private long _lobbyRejections;
    private long _reassigns;
    private long _assetsMoved;

    /// <summary>The team the server last reported for this client (0 = none).</summary>
    public int TeamId
    {
        get => Volatile.Read(ref _teamId);
        internal set => Volatile.Write(ref _teamId, value);
    }

    /// <summary>The wish this client had and what the lobby answered (empty when it had no wish).</summary>
    public string TeamNote { get; internal set; } = string.Empty;

    /// <summary><c>TeamChoice</c>, <c>TeamCreateRequest</c> and <c>TeamChangeRequest</c> messages this client sent.</summary>
    public long LobbyRequests => Interlocked.Read(ref _lobbyRequests);

    public long LobbyRejections => Interlocked.Read(ref _lobbyRejections);

    /// <summary><c>ReassignPlayerAssets</c> messages this node (the authority) applied.</summary>
    public long Reassigns => Interlocked.Read(ref _reassigns);

    /// <summary>Assets the authority re-owned (one <c>EntityChange</c> each).</summary>
    public long AssetsMoved => Interlocked.Read(ref _assetsMoved);

    internal void CountTeamStep(TeamStepResult step)
    {
        Interlocked.Add(ref _lobbyRequests, step.Requests);
        Interlocked.Add(ref _lobbyRejections, step.Rejections);
    }

    internal void CountReassign(int assets)
    {
        Interlocked.Increment(ref _reassigns);
        Interlocked.Add(ref _assetsMoved, assets);
    }

    /// <summary>The fake authority behind this node (null for clients).</summary>
    public FakeAuthority? Authority { get; internal set; }

    /// <summary>The client's handle (null for the authority and for ping-only nodes).</summary>
    public FakeClientHandle? Handle { get; internal set; }

    // ---- M1-T4: commander orders (clients) and forwarded intents (authority)
    private long _ordersSent;
    private long _ordersAccepted;
    private long _ordersRejected;
    private long _intentsReceived;
    private long _noTargets;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<RejectReason, long> _rejectReasons = new();

    /// <summary><c>AssetOrder</c>s this client sent (<c>--commander</c>).</summary>
    public long OrdersSent => Interlocked.Read(ref _ordersSent);

    public long OrdersAccepted => Interlocked.Read(ref _ordersAccepted);

    public long OrdersRejected => Interlocked.Read(ref _ordersRejected);

    /// <summary>Ticks where the commander found no asset of its kind to order (the authority has no team assets yet).</summary>
    public long OrderTicksWithoutTarget => Interlocked.Read(ref _noTargets);

    /// <summary>Intents the server forwarded to this node (authority only): with <c>--commander foreign</c> this must stay 0.</summary>
    public long IntentsReceived => Interlocked.Read(ref _intentsReceived);

    public IReadOnlyDictionary<RejectReason, long> RejectReasons => _rejectReasons;

    internal void CountOrderSent() => Interlocked.Increment(ref _ordersSent);

    internal void CountNoTarget() => Interlocked.Increment(ref _noTargets);

    internal void CountIntentReceived() => Interlocked.Increment(ref _intentsReceived);

    internal void CountOrderResult(IntentResult result)
    {
        if (result.Status == IntentStatus.Accepted)
        {
            Interlocked.Increment(ref _ordersAccepted);
            return;
        }

        Interlocked.Increment(ref _ordersRejected);
        _rejectReasons.AddOrUpdate(result.Reason, 1, (_, n) => n + 1);
    }

    // ---- M1-F2: failure injection

    private int _disconnects;
    private int _reloads;
    private int _resumeFailures;
    private int _resumeNoKeyframe;
    private int _resumeKeyframes;
    private long _resumeKeyframeMs;
    private long _resumeKeyframeMaxMs;

    /// <summary>The latency / slow-reader settings of this node's TCP stream (null = none).</summary>
    public StreamImpairment? Impairment { get; internal set; }

    /// <summary>Set when the server closed a slow reader (the expected outcome): why and how long after the slow reading began. Not an error.</summary>
    public string? ExpectedClose { get; internal set; }

    /// <summary>Abrupt socket drops followed by a resume (<c>--disconnect-every</c>).</summary>
    public int Disconnects => Volatile.Read(ref _disconnects);

    /// <summary><c>ClientReload</c> departures followed by a resume (<c>--reload-every</c>).</summary>
    public int Reloads => Volatile.Read(ref _reloads);

    /// <summary>Resumes the server did not accept as a resume (a fresh slot, ResumeExpired, handshake errors that did not clear).</summary>
    public int ResumeFailures => Volatile.Read(ref _resumeFailures);

    /// <summary>Resumes after which no keyframe arrived within the timeout.</summary>
    public int ResumesWithoutKeyframe => Volatile.Read(ref _resumeNoKeyframe);

    /// <summary>Resumes after which keyframes arrived, and how long the first took.</summary>
    public int ResumesWithKeyframe => Volatile.Read(ref _resumeKeyframes);

    public TimeSpan MaxKeyframeDelay => TimeSpan.FromMilliseconds(Interlocked.Read(ref _resumeKeyframeMaxMs));

    public TimeSpan AvgKeyframeDelay => ResumesWithKeyframe == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Interlocked.Read(ref _resumeKeyframeMs) / ResumesWithKeyframe);

    internal void CountResume(bool reload)
    {
        if (reload)
            Interlocked.Increment(ref _reloads);
        else
            Interlocked.Increment(ref _disconnects);
    }

    internal void CountResumeFailure() => Interlocked.Increment(ref _resumeFailures);

    internal void CountNoKeyframe() => Interlocked.Increment(ref _resumeNoKeyframe);

    internal void CountKeyframe(TimeSpan after)
    {
        Interlocked.Increment(ref _resumeKeyframes);
        long ms = (long)after.TotalMilliseconds;
        Interlocked.Add(ref _resumeKeyframeMs, ms);
        long max;
        while (ms > (max = Interlocked.Read(ref _resumeKeyframeMaxMs)) && Interlocked.CompareExchange(ref _resumeKeyframeMaxMs, ms, max) != max)
        {
        }
    }

    // ---- M1-X5: extension presets

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _notices = new();

    /// <summary>What the server should do with this bot's extension report (null when it does not verify: no <c>--extensions-preset</c>, or the authority).</summary>
    public ModExpectation? ModExpected { get; internal set; }

    /// <summary>The preset variant this bot reports (<c>MissingRequiredMod</c>, <c>modded</c>, ...).</summary>
    public string ModLabel { get; internal set; } = string.Empty;

    /// <summary>True once the server let the bot in.</summary>
    public bool ModAdmitted { get; internal set; }

    /// <summary>True when the rejection (or admission) matched the expectation, false on a mismatch, null while undecided.</summary>
    public bool? ModMatch { get; internal set; }

    /// <summary>The actual outcome in words (printed in the summary).</summary>
    public string ModDetail { get; internal set; } = string.Empty;

    /// <summary>The <c>ServerNotice</c> texts this node received.</summary>
    public IReadOnlyCollection<string> Notices => _notices;

    internal void AddNotice(string text) => _notices.Enqueue(text);

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

        if (o.Command == FakeNodeCommand.Fuzz)
            return await RunFuzzAsync(o, output, stop).ConfigureAwait(false);

        var plan = Plan(o);
        var lines = new SynchronizedWriter(output);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        if (o.Duration is { } seconds)
            cts.CancelAfter(TimeSpan.FromSeconds(seconds));

        string saveRoot = run.SaveDirectory ?? Path.Combine(Path.GetTempPath(), "x4mp-fakenode", Guid.NewGuid().ToString("N")[..8]);
        var stats = plan.Select(p => new LiveNodeStats(p.Name, p.Role)).ToList();
        var galaxy = new Lazy<FakeGalaxy>(o.BuildGalaxy, LazyThreadSafetyMode.ExecutionAndPublication);
        bool single = plan.Count == 1;
        var inspector = o.Command == FakeNodeCommand.Inspect
            ? new FrameInspector(o, line => { lines.WriteAsync(line); run.OnInspectLine?.Invoke(line); }, () => cts.Cancel())
            : null;
        await lines.WriteAsync($"fakenode {o.Command.ToString().ToLowerInvariant()}: {plan.Count} node(s) -> {o.Host}:{o.Port}" +
                               (o.Duration is { } d ? $" for {d}s" : " until Ctrl+C")).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();

        var tasks = new List<Task>();
        for (int i = 0; i < plan.Count; i++)
        {
            if (i == 1 && plan[0].Role == Role.Authority && o.ExtensionsPreset != ExtensionPreset.None)
            {
                // Mod verification needs the server to know the authority's list first (before an authority exists AuthorityDefines admits everyone).
                var wait = Stopwatch.StartNew();
                while (!stats[0].Connected && !tasks[0].IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(10) && !cts.IsCancellationRequested)
                    await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
            }

            tasks.Add(RunNodeAsync(o, plan[i], i, stats[i], single, run, lines, galaxy, saveRoot, cts.Token, inspector));
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
        var stopWatcher = run.StopWhen is { } stopWhen ? StopWhenAsync(stats, stopWhen, cts) : Task.CompletedTask;
        await Task.WhenAll(tasks).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        await reporter.ConfigureAwait(false);
        await stopWatcher.ConfigureAwait(false);

        foreach (var s in stats.Where(s => s.Session is not null))
            s.Session!.CheckStale();
        if (o.Commander != CommanderMode.None)
            await WriteCommanderSummaryAsync(o, stats, lines).ConfigureAwait(false);
        await WriteTradeSummaryAsync(stats, lines).ConfigureAwait(false);
        await WriteAvatarSummaryAsync(o, stats, lines).ConfigureAwait(false);
        long economyErrors = await WriteEconomySummaryAsync(o, stats, lines).ConfigureAwait(false);
        long modErrors = await WriteModsSummaryAsync(stats, lines).ConfigureAwait(false);
        run.OnFinished?.Invoke(stats);
        await WriteTeamSummaryAsync(o, stats, lines).ConfigureAwait(false);
        long verifyErrors = stats.Where(s => s.Impairment is not { Slow: not null }).Sum(s => s.VerifyErrors); // a slow reader is allowed to fall behind
        if (o.Verify)
            await WriteVerifySummaryAsync(stats, lines).ConfigureAwait(false);
        if (o.Udp)
        {
            var lanes = stats.Where(s => s.Udp is not null).ToList();
            await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
                $"udp: nodes={stats.Count} bound={stats.Count(s => s.UdpState == "bound")} fallback={stats.Count(s => s.UdpState == "fallback")} " +
                $"off={stats.Count(s => s.UdpState == "off")} datagrams-rx={lanes.Sum(s => s.Udp!.DatagramsReceived)} datagrams-tx={lanes.Sum(s => s.Udp!.DatagramsSent)} " +
                $"simulated-drops={lanes.Sum(s => s.Udp!.SimulatedDrops)} rx-loss-max={(lanes.Count == 0 ? 0 : lanes.Max(s => s.Udp!.RxLossPercent)):F1}%")).ConfigureAwait(false);
        }

        await WriteInjectionSummaryAsync(o, stats, lines).ConfigureAwait(false);
        if (inspector is not null)
            await lines.WriteAsync(inspector.Summary()).ConfigureAwait(false);
        long errors = stats.Sum(s => s.Errors) + verifyErrors + economyErrors + modErrors;
        await lines.WriteAsync($"summary: nodes={stats.Count} joined={stats.Count(s => s.Pings > 0)} errors={errors} pings={stats.Sum(s => s.Pings)} " +
                               $"rtt avg={Ms(Average(stats))} max={Ms(stats.Count == 0 ? TimeSpan.Zero : stats.Max(s => s.MaxRtt))} " +
                               $"elapsed={clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s ingame={stats.Count(s => s.InGame)}").ConfigureAwait(false);
        if (run.SaveDirectory is null)
        {
            try
            {
                Directory.Delete(saveRoot, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort temp cleanup
            }
        }

        return errors == 0 ? ExitOk : ExitErrors;
    }

    private static async Task StopWhenAsync(IReadOnlyList<LiveNodeStats> stats, Func<IReadOnlyList<LiveNodeStats>, bool> stopWhen, CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(25, cts.Token).ConfigureAwait(false);
                bool done;
                try
                {
                    done = stopWhen(stats);
                }
                catch (InvalidOperationException)
                {
                    done = false; // a node mutated a collection under the predicate: ask again
                }

                if (done)
                {
                    await cts.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// The M3-05 results: what the authority provisioned (<c>avatars:</c>), per bot what it received about the player ships (<c>[sync]</c> lines, only for bots in
    /// the avatar flow) with the lowest Near rate (<c>sync:</c>), and the chat traffic (<c>chat:</c>).
    /// </summary>
    private static async Task WriteAvatarSummaryAsync(CliOptions o, List<LiveNodeStats> stats, SynchronizedWriter lines)
    {
        foreach (var authority in stats.Where(s => s.Authority is not null))
        {
            var a = authority.Authority!.Avatars;
            if (a.Provisioned == 0 && a.Reissued == 0 && a.Host is null)
                continue;
            await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
                $"avatars: provisioned={a.Provisioned} reissued={a.Reissued} parked={a.Leaves} host={(a.Host is { } h ? $"net_id={h.NetId} sector={h.Sector} name={h.Name}" : "none")} " +
                $"spawns-sent={authority.AvatarSpawnsSent} changes-sent={authority.AvatarChangesSent}")).ConfigureAwait(false);
        }

        var flyers = stats.Where(s => s.Role == Role.Client && s.AvatarState != 0).ToList();
        if (flyers.Count > 0)
        {
            double latency = flyers.Where(s => s.AvatarState == 2).Select(s => s.AvatarLatency.TotalMilliseconds).DefaultIfEmpty(0).Average();
            await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
                $"avatar-flow: bots={flyers.Count} with-avatar={flyers.Count(s => s.AvatarState == 2)} without={flyers.Count(s => s.AvatarState == 3)} avg-latency={latency:F0} ms " +
                $"wingmen={flyers.Count(s => s.Wingman is not null)} followed-samples={flyers.Sum(s => s.Wingman?.FollowedSamples ?? 0)} follow-jumps={flyers.Sum(s => s.Wingman?.SectorChanges ?? 0)}")).ConfigureAwait(false);
            double minNear = double.MaxValue;
            int ships = 0;
            foreach (var s in flyers.Where(s => s.Session is not null))
            {
                foreach (var summary in s.Session!.SyncSummaries().Where(x => x.Entries > 1))
                {
                    ships++;
                    await lines.WriteAsync(summary.ToLine(s.Name)).ConfigureAwait(false);
                    if (summary.NearEntries > 20)
                        minNear = Math.Min(minNear, summary.NearRateHz);
                }
            }

            await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
                $"sync: bots={flyers.Count} ships-tracked={ships} min-near-rate-hz={(minNear == double.MaxValue ? 0 : minNear):F1}")).ConfigureAwait(false);
        }

        var chatters = stats.Where(s => s.Session is { ChatReceived: > 0 }).ToList();
        if (chatters.Count > 0 || o.ChatEcho)
        {
            await lines.WriteAsync($"chat: bots={chatters.Count} received={chatters.Sum(s => s.Session!.ChatReceived)} echoed={chatters.Sum(s => s.Session!.ChatEchoed)}").ConfigureAwait(false);
        }
    }

    /// <summary>The M1-T4 result: what the commanders sent, what the server answered, and what reached the authority.</summary>
    private static async Task WriteCommanderSummaryAsync(CliOptions o, List<LiveNodeStats> stats, SynchronizedWriter lines)
    {
        long sent = stats.Sum(s => s.OrdersSent);
        long accepted = stats.Sum(s => s.OrdersAccepted);
        long rejected = stats.Sum(s => s.OrdersRejected);
        long forwarded = stats.Where(s => s.Role == Role.Authority).Sum(s => s.IntentsReceived);
        var reasons = string.Join(",", stats.SelectMany(s => s.RejectReasons).GroupBy(r => r.Key).Select(g => $"{g.Key}={g.Sum(r => r.Value)}"));
        await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
            $"commander({o.Commander.ToString().ToLowerInvariant()}): orders-sent={sent} accepted={accepted} rejected={rejected} forwarded-to-authority={forwarded} " +
            $"no-target-ticks={stats.Sum(s => s.OrderTicksWithoutTarget)} reasons=[{reasons}]")).ConfigureAwait(false);
    }

    /// <summary>The M1-E5 result: what the trading clients proposed and how the server settled it, and what the fake authority did with the orders.</summary>
    private static async Task WriteTradeSummaryAsync(List<LiveNodeStats> stats, SynchronizedWriter lines)
    {
        var traders = stats.Where(s => s.Trader is not null).ToList();
        if (traders.Count > 0)
        {
            var results = traders.SelectMany(s => s.Trader!.Results).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Sum(r => r.Value));
            long Count(TradeState state) => results.GetValueOrDefault(state);
            var reasons = string.Join(",", traders.SelectMany(s => s.Trader!.RejectReasons).GroupBy(r => r.Key).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Sum(r => r.Value)}"));
            await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
                $"trade: clients={traders.Count} proposals={traders.Sum(s => s.Trader!.ProposalsSent)} accepts={traders.Sum(s => s.Trader!.AcceptsSent)} " +
                $"results(one per party)=[completed={Count(TradeState.Completed)} rolled-back={Count(TradeState.RolledBack)} cancelled={Count(TradeState.Cancelled)} " +
                $"rejected={Count(TradeState.Rejected)} expired={Count(TradeState.Expired)}] open-at-end={traders.Sum(s => s.Trader!.Open)} request-rejects=[{reasons}]")).ConfigureAwait(false);
        }

        foreach (var s in stats.Where(s => s.TradeAuthority is { OrdersReceived: > 0 }))
            await lines.WriteAsync(s.TradeAuthority!.Summary()).ConfigureAwait(false);
}

    /// <summary>
    /// The M1-F3 results: where the clients ended up (<c>teams:</c>, when they had a team wish), what the fake NPC AI makes of the relation matrix
    /// (<c>npc-hostility:</c>) and what the authority did for moved players (<c>reassign:</c>).
    /// </summary>
    private static async Task WriteTeamSummaryAsync(CliOptions o, List<LiveNodeStats> stats, SynchronizedWriter lines)
    {
        var clients = stats.Where(s => s.Role == Role.Client && s.Session is not null).ToList();
        bool wished = o.Team is not null || o.TeamPick != TeamPickMode.None || o.EffectiveTeams > 0;
        if (wished && clients.Count > 0)
        {
            string relations = o.Relations != RelationsPreset.None
                ? $" relations={o.Relations.ToString().ToLowerInvariant()} server-settings=[{string.Join(' ', TeamLayout.ServerSettings(o.Relations))}]"
                : string.Empty;
            var spread = string.Join(",", clients.Where(s => s.TeamId != 0).GroupBy(s => s.TeamId).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"));
            await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
                $"teams: clients={clients.Count} placed={clients.Count(s => s.TeamId != 0)} unplaced={clients.Count(s => s.TeamId == 0)} " +
                $"teams-used={clients.Where(s => s.TeamId != 0).Select(s => s.TeamId).Distinct().Count()} spread=[{spread}] " +
                $"requests={clients.Sum(s => s.LobbyRequests)} rejected={clients.Sum(s => s.LobbyRejections)}{relations}")).ConfigureAwait(false);
            foreach (var s in clients.Where(s => s.TeamId == 0))
                await lines.WriteAsync($"[{s.Name}] no team: {s.TeamNote}").ConfigureAwait(false);
        }

        foreach (var authority in stats.Where(s => s.Authority is not null))
        {
            var a = authority.Authority!;
            if (a.RelationChangesApplied > 0 || a.OwnershipChanges.Count > 0 || wished)
            {
                var h = a.Hostility();
                await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
                    $"npc-hostility: relation-changes={a.RelationChangesApplied} hostile-team-pairs=[{string.Join(',', h.HostileTeamPairs.Select(p => $"{p.TeamA}-{p.TeamB}"))}] " +
                    $"engaged-ship-pairs={h.EngagedShipPairs} ships-at-war={h.ShipsAtWar} sectors-with-fights={h.SectorsWithFights}")).ConfigureAwait(false);
            }

            if (authority.Reassigns > 0)
            {
                await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
                    $"reassign: requests={authority.Reassigns} assets-moved={authority.AssetsMoved} owner-changes-seen-by-clients={clients.Sum(s => s.Session!.OwnerChanges)}")).ConfigureAwait(false);
            }
        }
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
            case FakeNodeCommand.Inspect:
                plan.Add(new NodePlan(o.Name, Role.Client));
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
        Lazy<FakeGalaxy> galaxy, string saveRoot, CancellationToken ct, FrameInspector? inspector = null)
    {
        // Failure injection (M1-F2): latency for every node, a slow reader for the first --slow-clients clients.
        int ordinal = o.Command == FakeNodeCommand.Swarm && o.WithAuthority ? index - 1 : index;
        var slowSpec = plan.Role == Role.Client && o.IsSlowReader(ordinal) ? o.SlowReader : null;
        var impairment = o.LatencyMs > 0 || o.JitterMs > 0 || slowSpec is not null
            ? new StreamImpairment(TimeSpan.FromMilliseconds(o.LatencyMs), TimeSpan.FromMilliseconds(o.JitterMs), slowSpec, BitConverter.ToInt32(DeriveKey(o.Seed, plan.Name), 4))
            : null;
        stats.Impairment = impairment;
        var extensions = o.ExtensionsFor(plan.Role, ordinal);
        if (plan.Role == Role.Client && o.ExtensionsPreset != ExtensionPreset.None && extensions is not null)
        {
            stats.ModLabel = ExtensionPresets.Label(o.ExtensionsPreset, ordinal);
            stats.ModExpected = ModExpectation.Compute(extensions, o.AuthorityExtensions ?? [], o.ExpectMods);
        }

        var options = new NodeClientOptions
        {
            PlayerName = plan.Name,
            PlayerKey = DeriveKey(o.Seed, plan.Name),
            Password = o.Password,
            ExtensionList = extensions,
            RequestedRoles = plan.Role,
            ClientCaps = o.Udp ? (ulong)Capability.UdpRealtime : 0,
            StreamWrapper = impairment is null ? null : stream => new ImpairedStream(stream, impairment),
            ReceiveBufferBytes = slowSpec is null ? 0 : 4096, // a small window: flow control stops the server's sender soon after the node stops reading
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
        catch (HandshakeRejectedException rejected) when (stats.ModExpected is not null)
        {
            await ReportModRejectionAsync(stats, rejected, lines, plan.Name).ConfigureAwait(false);
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
            if (stats.ModExpected is { } admittedExpectation)
            {
                stats.ModAdmitted = true;
                if (admittedExpectation.Outcome == ExpectedModOutcome.Reject)
                {
                    stats.ModMatch = false;
                    stats.ModDetail = $"admitted, expected rejection ({admittedExpectation.Describe()})";
                    await lines.WriteAsync($"[{plan.Name}] mods: MISMATCH variant={stats.ModLabel} expected={admittedExpectation.OutcomeName} {admittedExpectation.Describe()} actual=admitted").ConfigureAwait(false);
                }
            }

            var w = client.Welcome;
            await lines.WriteAsync(
                $"[{plan.Name}] welcome: server='{client.ServerHello.ServerName}' v{client.ServerHello.ServerVersion} " +
                $"proto={client.ServerHello.ProtocolMajor}.{client.ServerHello.ProtocolMinor} phase={client.ServerHello.Phase} " +
                $"player_id={w.PlayerId} roles={w.GrantedRoles} caps=0x{w.NegotiatedCaps:x} conn={w.ConnId} " +
                $"resumed={w.Resumed} resume_grace={w.ResumeGraceS}s").ConfigureAwait(false);

            client.PongReceived += stats.RecordRtt;
            var link = new NodeLink(client, w.PlayerId) { NoticeSink = stats.AddNotice };
            if (inspector is not null)
                link.Tap = inspector.Tap;
            using var readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            link.Start(readerCts.Token);
            Task? udpBind = StartUdp(o, link, stats, lines, plan.Name, ct);
            try
            {
                // Against a session actor the node plays its role; against a bare gateway it only keeps the socket alive.
                if (o.Command == FakeNodeCommand.Inspect && o.NoJoin)
                {
                    await InspectOnlyAsync(link, o, run, lines, ct).ConfigureAwait(false);
                }
                else if (await link.WaitForSessionAsync(run.SessionDetect, ct).ConfigureAwait(false))
                {
                    await RunSessionNodeAsync(o, plan, index, link, stats, run, lines, galaxy.Value, Path.Combine(saveRoot, plan.Name), print, ct).ConfigureAwait(false);
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
            catch (Exception ex) when (impairment is { SlowActive: true } && ex is IOException or System.Net.Sockets.SocketException or TimeoutException)
            {
                // A slow reader that the server closed is the outcome the injection is after (SlowConsumer, or the heartbeat for a node that never answers).
                var code = link.DisconnectedBy?.ToString() ?? "connection reset";
                stats.ExpectedClose = $"{code} after {impairment.SlowFor.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s of slow reading";
                await lines.WriteAsync($"[{plan.Name}] slow reader closed by the server: {stats.ExpectedClose}").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                stats.Fail(ex is OperationCanceledException ? "ping timed out" : DescribeFailure(ex));
                await lines.WriteAsync($"[{plan.Name}] connection lost: {stats.LastError}").ConfigureAwait(false);
            }
            finally
            {
                await readerCts.CancelAsync().ConfigureAwait(false);
                await link.StopAsync().ConfigureAwait(false);
                if (link.Udp is { } udpLane)
                    await udpLane.DisposeAsync().ConfigureAwait(false);
                if (udpBind is not null)
                    await udpBind.ConfigureAwait(false);
                client.PongReceived -= stats.RecordRtt;
            }
        }

        stats.MarkDisconnected();
    }

    /// <summary>
    /// <c>--udp</c>: binds the UDP Realtime lane in the background (UdpHello every 250 ms, 3 s at most). The node keeps working over TCP meanwhile
    /// and for good when the bind fails (server without UDP, packets that never arrive). Returns the bind task (null when UDP is not used).
    /// </summary>
    private static Task? StartUdp(CliOptions o, NodeLink link, LiveNodeStats stats, SynchronizedWriter lines, string name, CancellationToken ct)
    {
        if (!o.Udp)
            return null;
        var w = link.Client.Welcome;
        if (w.UdpPort == 0 || (w.NegotiatedCaps & (ulong)Capability.UdpRealtime) == 0)
        {
            stats.UdpState = "off";
            _ = lines.WriteAsync($"[{name}] udp: the server offers no UDP lane (udp_port={w.UdpPort}); Realtime stays on TCP");
            return null;
        }

        int seed = BitConverter.ToInt32(DeriveKey(o.Seed, name), 0);
        var udp = new UdpRealtimeClient(o.Host, w.UdpPort, w.ConnId, w.UdpToken, o.LossPercent / 100.0, seed);
        udp.Latency = TimeSpan.FromMilliseconds(o.LatencyMs);
        udp.Jitter = TimeSpan.FromMilliseconds(o.JitterMs);
        udp.FrameReceived = link.DispatchDatagram;
        link.Udp = udp;
        stats.Udp = udp;
        stats.UdpState = "binding";
        return Task.Run(async () =>
        {
            var watch = Stopwatch.StartNew();
            try
            {
                bool bound = await udp.BindAsync(UdpRealtimeClient.BindTimeout, ct).ConfigureAwait(false);
                stats.UdpState = bound ? "bound" : "fallback";
                await lines.WriteAsync(bound
                    ? $"[{name}] udp: bound to :{w.UdpPort} after {watch.ElapsedMilliseconds} ms"
                    : $"[{name}] udp: no UdpHelloAck within {UdpRealtimeClient.BindTimeout.TotalSeconds:F0} s; Realtime stays on TCP").ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // the run ended while binding
            }
        }, CancellationToken.None);
    }

    /// <summary>The message of a node failure; programming errors (a collection changed during enumeration, a null) also name the exception and its top frame.</summary>
    private static string DescribeFailure(Exception ex) =>
        ex is InvalidOperationException or NullReferenceException or ArgumentException or KeyNotFoundException
            ? $"{ex.Message} [{ex.GetType().Name} at {ex.StackTrace?.Split((char)10).FirstOrDefault()?.Trim()}]"
            : ex.Message;

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
                    $"errors={stats.Sum(s => s.Errors)} pings={stats.Sum(s => s.Pings)} rtt avg={Ms(Average(stats))} max={Ms(stats.Max(s => s.MaxRtt))} ingame={stats.Count(s => s.InGame)}").ConfigureAwait(false);
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
