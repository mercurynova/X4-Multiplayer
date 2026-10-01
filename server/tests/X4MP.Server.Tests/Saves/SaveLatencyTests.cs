using System.Diagnostics;
using System.Globalization;
using Google.FlatBuffers;
using X4MP.Core.Session;
using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;
using Xunit.Abstractions;

namespace X4MP.Server.Tests.Saves;

/// <summary>
/// "Other clients' realtime p99 rises by at most 10 ms while a save transfers" (M1-12 acceptance), approximated in process. A probe node
/// sits in game and pings the server every 20 ms; the probe's round trip crosses the same path a realtime frame takes (its connection's
/// writer, the session actor, the socket stack), and its own connection carries no bulk, exactly like the other players in the real
/// case. The load is three nodes downloading the current save over and over plus the authority uploading new checkpoints back to back.
/// Loopback has no bandwidth limit, so this is a harsher test of CPU and scheduler coupling than of the uplink; the per-connection lane
/// priority itself (Bulk drained only when Control and Realtime are empty) is covered by the send-queue tests.
/// </summary>
public sealed class SaveLatencyTests(ITestOutputHelper output)
{
    private const double AllowedRiseMs = 10;

    /// <summary>A node that does nothing but download the save in a loop (and ack, like a real node), without touching the disk.</summary>
    private sealed class DownloadHammer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly TcpNodeClient _client;
        private Task _loop = Task.CompletedTask;
        private long _bytes;

        private DownloadHammer(TcpNodeClient client) => _client = client;

        public long Bytes => Interlocked.Read(ref _bytes);

        public static async Task<DownloadHammer> StartAsync(SaveServer server, string name)
        {
            var hammer = new DownloadHammer(await server.ConnectAsync(name, Role.Client));
            hammer._loop = Task.Run(hammer.RunAsync);
            return hammer;
        }

        private async Task RunAsync()
        {
            var ct = _cts.Token;
            byte[] sha = [];
            uint id = 0;
            long size = 0;
            long received = 0;
            int sinceAck = 0;
            async Task RequestAsync() =>
                await _client.SendAsync(MsgType.SaveDownloadRequest, b => SaveDownloadRequest.Pack(b, new SaveDownloadRequestT { Sha256 = [.. sha], Kind = UploadKind.Save, Offset = 0 }), ct);
            try
            {
                while (!ct.IsCancellationRequested && await _client.ReceiveAsync(ct) is { } frame)
                {
                    switch (frame.Type)
                    {
                        case MsgType.SessionSaveInfo:
                            sha = [.. MessageRegistry.Default.Decode<SessionSaveInfo>(frame).UnPack().Sha256];
                            await RequestAsync();
                            break;
                        case MsgType.SaveDownloadAccept:
                            var accept = MessageRegistry.Default.Decode<SaveDownloadAccept>(frame);
                            id = accept.DownloadId;
                            size = (long)accept.Size;
                            received = 0;
                            sinceAck = 0;
                            break;
                        case MsgType.SaveChunk:
                            var chunk = SaveChunk.GetRootAsSaveChunk(new ByteBuffer(frame.Payload));
                            if (chunk.TransferId != id)
                            {
                                break;
                            }

                            received += chunk.DataLength;
                            Interlocked.Add(ref _bytes, chunk.DataLength);
                            if (++sinceAck >= 4 || received >= size)
                            {
                                sinceAck = 0;
                                long next = received;
                                uint transfer = id;
                                await _client.SendAsync(MsgType.SaveChunkAck, b => SaveChunkAck.CreateSaveChunkAck(b, transfer, (ulong)next), ct);
                            }

                            if (received >= size)
                            {
                                await RequestAsync();
                            }

                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // stopped
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _client.Abort();
            await _loop;
            _cts.Dispose();
        }
    }

    private static double Percentile(List<double> samples, double p)
    {
        var sorted = samples.Order().ToList();
        return sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];
    }

    private static async Task<List<double>> SampleAsync(TcpNodeClient probe, List<double> sink, TimeSpan duration)
    {
        sink.Clear();
        void OnPong(TimeSpan rtt)
        {
            lock (sink)
            {
                sink.Add(rtt.TotalMilliseconds);
            }
        }

        probe.PongReceived += OnPong;
        var until = Stopwatch.StartNew();
        while (until.Elapsed < duration)
        {
            await probe.SendPingAsync();
            await Task.Delay(20);
        }

        await Task.Delay(100); // the last pongs
        probe.PongReceived -= OnPong;
        lock (sink)
        {
            return [.. sink];
        }
    }

    [Fact]
    public async Task ATransferRaisesAnInGameNodesP99LatencyByNoMoreThan10Ms()
    {
        // A loaded CI machine can hit one bad scheduling second: judge the best of two attempts, and report both.
        double rise = double.MaxValue;
        for (int attempt = 1; attempt <= 2 && rise > AllowedRiseMs; attempt++)
        {
            rise = await MeasureOnceAsync(attempt);
        }

        Assert.True(rise <= AllowedRiseMs, $"p99 rose by {rise:F1} ms (allowed {AllowedRiseMs} ms)");
    }

    private async Task<double> MeasureOnceAsync(int attempt)
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        await using var authority = await AuthorityRig.StartAsync(
            server, new FakeAuthoritySaveOptions { SaveBytes = 24L * 1024 * 1024, Directory = Path.Combine(server.Dir, "fake-authority") });
        await server.WaitForAsync(s => s.Phase == SessionPhase.Running, 60_000, "first checkpoint");

        await using var probe = await ClientRig.StartAsync(server, "Probe");
        await probe.WaitReadyAsync(30_000);
        var samples = new List<double>();

        // baseline: nothing else moving
        var baseline = await SampleAsync(probe.Client, samples, TimeSpan.FromSeconds(3));

        // under load: three downloaders and an authority that keeps uploading
        var hammers = new List<DownloadHammer>();
        using var uploads = new CancellationTokenSource();
        Task uploader = Task.CompletedTask;
        try
        {
            for (int i = 0; i < 3; i++)
            {
                hammers.Add(await DownloadHammer.StartAsync(server, "Hammer" + i));
            }

            uploader = Task.Run(async () =>
            {
                while (!uploads.IsCancellationRequested)
                {
                    await server.Saves.RequestSaveAsync();
                    await Task.Delay(100);
                }
            });
            await SaveServer.WaitUntilAsync(() => hammers.All(h => h.Bytes > 0), 15_000, "hammers downloading");
            await Task.Delay(500);
            long bytesBefore = hammers.Sum(h => h.Bytes);
            var clock = Stopwatch.StartNew();
            var loaded = await SampleAsync(probe.Client, samples, TimeSpan.FromSeconds(3));
            double seconds = clock.Elapsed.TotalSeconds;
            double mbps = (hammers.Sum(h => h.Bytes) - bytesBefore) / 1048576.0 / seconds;

            double b99 = Percentile(baseline, 0.99);
            double l99 = Percentile(loaded, 0.99);
            double rise = l99 - b99;
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"attempt {attempt}: probe RTT baseline p50={Percentile(baseline, 0.5):F2} p99={b99:F2} ms (n={baseline.Count}); under load p50={Percentile(loaded, 0.5):F2} p99={l99:F2} ms " +
                $"(n={loaded.Count}); rise {rise:F2} ms while {mbps:F0} MB/s went to 3 downloaders and {authority.Saves.CheckpointsStored} checkpoints were uploaded"));
            return rise;
        }
        finally
        {
            await uploads.CancelAsync();
            await uploader;
            foreach (var hammer in hammers)
            {
                await hammer.DisposeAsync();
            }
        }
    }
}
