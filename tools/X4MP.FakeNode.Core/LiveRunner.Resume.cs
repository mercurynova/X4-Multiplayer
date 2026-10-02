using System.Diagnostics;
using System.Globalization;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;

namespace X4MP.FakeNode;

/// <summary>
/// <c>--disconnect-every</c> / <c>--reload-every</c> (M1-F2): a client that loses its socket (or "reloads") and comes back with the resume token.
/// The server keeps the slot, resets the node's baselines (new epoch) and re-sends spawns and keyframes for the whole interest set
/// (protocol.md 6.6), so the client restarts its ghost table and expects keyframes again; <c>--verify</c> keeps checking every entry.
/// </summary>
public static partial class LiveRunner
{
    /// <summary>Waiting for the keyframes of one resume.</summary>
    private sealed class PendingResume
    {
        public Stopwatch Since { get; } = Stopwatch.StartNew();
    }

    /// <summary>
    /// Swaps the node's connection. <paramref name="reload"/>: sends <c>Disconnect(ClientReload)</c> first, stays away for the reload delay, and
    /// sends <c>NodeReady</c> after the resume (the server keeps the node in game; the join path itself cannot restart from InGame).
    /// Otherwise the socket is dropped without a goodbye (a crash or a pulled cable).
    /// </summary>
    private static async Task ResumeAsync(
        NodeLink link, CliOptions o, LiveNodeStats stats, FakeClientSession session, LiveRunOptions run, SynchronizedWriter lines, string name,
        bool reload, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var old = link.Client;
        var resumeOptions = old.ForResume();
        link.Resuming = true;
        try
        {
            if (reload)
            {
                try
                {
                    using var goodbye = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    goodbye.CancelAfter(TimeSpan.FromSeconds(2));
                    var message = new DisconnectT { Code = DisconnectCode.ClientReload, Message = "fakenode reload", Expected = "" };
                    await old.SendAsync(MsgType.Disconnect, b => Disconnect.Pack(b, message), goodbye.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    // the socket was already gone: the reload is a drop then
                }
            }

            old.PongReceived -= stats.RecordRtt;
            old.Abort();
            if (link.Udp is { } oldUdp)
            {
                link.Udp = null;
                stats.Udp = null;
                await oldUdp.DisposeAsync().ConfigureAwait(false);
            }

            // The reload takes time; a plain drop comes back at once (the server may not have noticed yet: the token then takes the slot over).
            await Task.Delay(reload ? run.ReloadDelay : TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);

            TcpNodeClient? replacement = null;
            Exception? last = null;
            for (int attempt = 0; attempt < 4 && replacement is null; attempt++)
            {
                try
                {
                    var candidate = await TcpNodeClient.ConnectAsync(o.Host, o.Port, resumeOptions, ct).ConfigureAwait(false);
                    if (!candidate.Welcome.Resumed)
                    {
                        // The slot is gone (grace over, or the server restarted): this is a fresh join, which this client cannot continue from.
                        await candidate.DisposeAsync().ConfigureAwait(false);
                        throw new InvalidOperationException("the server did not resume the node (it answered with a fresh join)");
                    }

                    replacement = candidate;
                }
                catch (HandshakeRejectedException ex) when (ex.Code is DisconnectCode.RateLimited or DisconnectCode.HandshakeTimeout or DisconnectCode.InternalError)
                {
                    last = ex;
                    await Task.Delay(TimeSpan.FromMilliseconds(250 << attempt), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
                {
                    last = ex;
                    await Task.Delay(TimeSpan.FromMilliseconds(250 << attempt), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    stats.CountResumeFailure();
                    session.Report("resume-refused", ex.Message);
                    throw new IOException($"resume failed: {ex.Message}", ex);
                }
            }

            if (replacement is null)
            {
                stats.CountResumeFailure();
                session.Report("resume-failed", last?.Message ?? "unknown");
                throw new IOException($"resume failed after 4 attempts: {last?.Message}", last);
            }

            replacement.PongReceived += stats.RecordRtt;
            session.ResetForResume();
            session.Teams.ApplyWelcome(replacement.Welcome);
            await link.RebindAsync(replacement).ConfigureAwait(false);
            _ = StartUdp(o, link, stats, lines, name, ct);
            if (reload)
                await replacement.SendAsync(MsgType.NodeReady, b => NodeReady.CreateNodeReady(b, 42), ct).ConfigureAwait(false);
            stats.CountResume(reload);
            await lines.WriteAsync(
                $"[{name}] {(reload ? "reloaded" : "reconnected")} in {watch.ElapsedMilliseconds} ms: resumed={replacement.Welcome.Resumed} player_id={replacement.Welcome.PlayerId} " +
                $"phase={link.Phase} ghosts-before={session.GhostsBeforeResume}").ConfigureAwait(false);
        }
        finally
        {
            link.Resuming = false;
        }
    }

    /// <summary>A resume whose keyframes never came is a failure, unless the client held no ghosts before (then there was nothing to re-send).</summary>
    private static void NoteMissingKeyframes(LiveNodeStats stats, FakeClientSession session, PendingResume pending)
    {
        if (session.KeyframesSinceResume > 0)
        {
            stats.CountKeyframe(pending.Since.Elapsed);
            return;
        }

        if (session.GhostsBeforeResume == 0 || pending.Since.Elapsed < TimeSpan.FromSeconds(5))
            return;
        stats.CountNoKeyframe();
        session.Report("no-keyframe-after-resume", string.Create(CultureInfo.InvariantCulture,
            $"no full entry {pending.Since.Elapsed.TotalSeconds:F0} s after a resume (held {session.GhostsBeforeResume} ghosts before, {session.Ghosts} now)"));
    }

    /// <summary>Resume schedule: the first one comes after one period plus a per-client offset (a swarm must not reconnect all at once), then every period.</summary>
    private static double FirstResumeAt(double every, int ordinal, int clients) =>
        every + (clients <= 1 ? 0 : every * 0.5 * (ordinal % clients) / clients);

    private static async Task WriteInjectionSummaryAsync(CliOptions o, List<LiveNodeStats> stats, SynchronizedWriter lines)
    {
        if (o.LatencyMs > 0 || o.JitterMs > 0)
        {
            await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
                $"latency: +{o.LatencyMs:0.#} ms (jitter +-{o.JitterMs:0.#} ms) in each direction on TCP{(o.Udp ? " and UDP" : string.Empty)}, round trip +{2 * o.LatencyMs:0.#} ms; " +
                $"nodes={stats.Count} rtt avg={Ms(Average(stats))} max={Ms(stats.Count == 0 ? TimeSpan.Zero : stats.Max(s => s.MaxRtt))}")).ConfigureAwait(false);
        }

        if (o.SlowReader is not null)
        {
            var slow = stats.Where(s => s.Impairment is { Slow: not null }).ToList();
            foreach (var s in slow)
            {
                var imp = s.Impairment!;
                string outcome = s.ExpectedClose is { } closed ? $"closed by the server: {closed}" : s.Connected ? "still connected" : "disconnected";
                await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
                    $"[{s.Name}] slow-reader({imp.Slow}): slow-for={imp.SlowFor.TotalSeconds:F1}s read={imp.BytesReadSlowly} B paused={imp.PausedMilliseconds / 1000.0:F1}s -> {outcome}")).ConfigureAwait(false);
            }

            var others = stats.Where(s => s.Impairment is not { Slow: not null } && s.Role == Role.Client).ToList();
            await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
                $"slow-reader: slow-clients={slow.Count} closed-by-server={slow.Count(s => s.ExpectedClose is not null)} still-connected={slow.Count(s => s.Connected)}; " +
                $"other-clients={others.Count} with-errors={others.Count(s => s.Errors > 0 || s.VerifyErrors > 0)} disconnected={others.Count(s => !s.Connected)}")).ConfigureAwait(false);
        }

        if (o.DisconnectEverySeconds > 0 || o.ReloadEverySeconds > 0)
        {
            var clients = stats.Where(s => s.Session is not null).ToList();
            await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
                $"resume: clients={clients.Count} disconnects={clients.Sum(s => s.Disconnects)} reloads={clients.Sum(s => s.Reloads)} refused={clients.Sum(s => s.ResumeFailures)} " +
                $"keyframes-after-resume={clients.Sum(s => s.ResumesWithKeyframe)} missing={clients.Sum(s => s.ResumesWithoutKeyframe)} " +
                $"first-keyframe avg={(clients.Sum(s => s.ResumesWithKeyframe) == 0 ? 0 : clients.Sum(s => s.AvgKeyframeDelay.TotalMilliseconds * s.ResumesWithKeyframe) / clients.Sum(s => s.ResumesWithKeyframe)):F0} ms " +
                $"max={(clients.Count == 0 ? 0 : clients.Max(s => s.MaxKeyframeDelay.TotalMilliseconds)):F0} ms")).ConfigureAwait(false);
        }
    }
}
