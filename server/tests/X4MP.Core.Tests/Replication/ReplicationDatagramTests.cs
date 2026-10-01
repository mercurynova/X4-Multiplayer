using X4MP.Core.Replication;
using X4MP.Core.Settings;
using X4MP.Proto;
using X4MP.Protocol;
using Xunit.Abstractions;
using static X4MP.Core.Tests.World.WorldKit;
using ReplicationMsg = X4MP.Proto.Replication;

namespace X4MP.Core.Tests.Replication;

/// <summary>
/// Replication over the UDP Realtime lane (protocol.md 10.3, M1-09): acks arrive one datagram at a time and in any order, several frames are in
/// flight, a field is left out only when the baseline and every unacknowledged datagram agree with the current value.
/// </summary>
public sealed class ReplicationDatagramTests(ITestOutputHelper output)
{
    private const int Alice = 2;

    private static async Task<(ReplicationRig Rig, RigClient Alice)> SetupAsync(bool datagram = true, Action<ReplicationOptions>? replication = null)
    {
        var rig = await ReplicationRig.CreateAsync(replication, o => o.MaxGhosts = 10_000, fakeAuthority: false);
        rig.Net.Datagram = datagram;
        var alice = await rig.AddClientAsync("Alice", verify: false);
        await rig.OnActorAsync(() =>
        {
            rig.Mirror.Spawn([.. Enumerable.Range(0, 5).Select(i => Rec((uint)(21 + i), EntityKind.ShipS, 2, px: 640 * (i + 1), py: 64 * i, pz: -640))]);
        });
        await rig.PlaceAsync(alice, 2);
        await rig.RunAsync(12);
        await rig.CompleteCapturedAsync();
        await rig.PumpAsync();
        return (rig, alice);
    }

    private static List<ReplicationEntry> Entries(ReplicationRig rig, int fromFrame = 0) =>
        [.. rig.Net.RealtimeLog.Skip(fromFrame).Where(f => f.Player == Alice).SelectMany(f =>
        {
            var m = ReplicationMsg.GetRootAsReplication(new Google.FlatBuffers.ByteBuffer(f.Payload));
            return ReplicationCodec.Decode(m.GetEntriesArray(), m.EntryCount);
        })];

    private static async Task MoveAsync(ReplicationRig rig, uint id, int px) =>
        await rig.OnActorAsync(() => rig.Mirror.IngestWorldUpdate(UpdatePayload((uint)rig.Tick, rig.GameTime, [State(id, 2, px, 0, 0, 0, 0)])));

    [Fact]
    public async Task AnAckedFrameFoldsItsFieldsIntoTheBaselineAndNothingBeforeThat()
    {
        var (rig, _) = await SetupAsync();
        await using var _ = rig;
        await rig.RunAsync(3, confirm: false);
        Assert.Null(await rig.OnActorAsync(() => rig.Replication.BaselineOf(Alice, 21)));
        Assert.Equal(0, rig.Replication.Stats.InFlightSkips); // not capped to one frame in flight

        rig.Net.ConfirmAll();
        await rig.StepAsync(confirm: false);
        Assert.NotNull(await rig.OnActorAsync(() => rig.Replication.BaselineOf(Alice, 21)));
        Assert.True(rig.Replication.Stats.FramesAcked >= 1);
    }

    [Fact]
    public async Task SeveralFramesStayInFlightAndEveryTickStillSends()
    {
        var (rig, alice) = await SetupAsync();
        await using var _ = rig;
        await rig.RunAsync(4);
        int mark = rig.Net.RealtimeLog.Count;
        for (int i = 1; i <= 6; i++)
        {
            await MoveAsync(rig, 21, 640 + (i * 100));
            await rig.StepAsync(confirm: false); // nothing is acknowledged
        }

        Assert.True(rig.Net.RealtimeLog.Count - mark >= 6, "a frame per tick even though none was acked");
        Assert.Equal(0, rig.Replication.Stats.InFlightSkips);
        rig.Net.ConfirmAll();
        await rig.RunAsync(3);
        Assert.Empty(await rig.DivergenceAsync(alice));
    }

    [Fact]
    public async Task ALostDatagramsFieldsAreSentAgainAndTheClientConverges()
    {
        var (rig, alice) = await SetupAsync();
        await using var _ = rig;
        await rig.RunAsync(4);

        long lostTokenFrom = rig.Replication.Stats.FramesSent + 1;
        rig.Net.Lose = token => token >= lostTokenFrom && token < lostTokenFrom + 2; // the next two frames vanish
        await MoveAsync(rig, 22, 7777);
        await rig.RunAsync(3);
        rig.Net.Lose = null;
        Assert.NotEqual(7777, alice.States[22].Px); // really lost

        await rig.RunAsync(8); // not acked: the position still differs from the baseline, so it goes out again
        Assert.Equal(7777, alice.States[22].Px);
        Assert.Empty(await rig.DivergenceAsync(alice));
    }

    [Fact]
    public async Task AFieldThatChangedAndRevertedWhileTheFirstValueIsInFlightIsStillSent()
    {
        // protocol.md 10.3: baseline A, datagram carrying B in flight, the value goes back to A. The baseline equals the current value, but B may
        // still arrive, so the field must be sent again or the client would keep B.
        var (rig, alice) = await SetupAsync();
        await using var _ = rig;
        await rig.RunAsync(4);
        int mark = rig.Net.RealtimeLog.Count;

        await MoveAsync(rig, 21, 9999);
        await rig.StepAsync(confirm: false);
        await MoveAsync(rig, 21, 640); // back to the baseline value
        await rig.StepAsync(confirm: false);
        var sent = Entries(rig, mark).Where(e => e.NetId == 21).ToList();
        Assert.Contains(sent, e => e.PosX == 9999);
        Assert.Contains(sent.Skip(1), e => (e.Mask & ReplicationMask.Pos) != 0 && e.PosX == 640);

        rig.Net.ConfirmAll();
        await rig.RunAsync(3);
        Assert.Equal(640, alice.States[21].Px);
        Assert.Empty(await rig.DivergenceAsync(alice));
    }

    [Fact]
    public async Task AnAckThatArrivesAfterANewerOnesDoesNotOverwriteTheBaseline()
    {
        var (rig, _) = await SetupAsync();
        await using var _ = rig;
        await rig.RunAsync(4);

        await MoveAsync(rig, 23, 3000);
        await rig.StepAsync(confirm: false);
        await MoveAsync(rig, 23, 3500);
        await rig.StepAsync(confirm: false);
        var tokens = rig.Net.UnconfirmedTokens.ToList();
        long newest = tokens.Max();
        rig.Net.ConfirmWhere(t => t == newest);
        await rig.StepAsync(confirm: false);
        var afterNewest = await rig.OnActorAsync(() => rig.Replication.BaselineOf(Alice, 23));
        Assert.Equal(3500, afterNewest!.Value.Px);

        rig.Net.ConfirmWhere(t => t < newest); // the older ones are acknowledged late
        await rig.StepAsync(confirm: false);
        var afterOlder = await rig.OnActorAsync(() => rig.Replication.BaselineOf(Alice, 23));
        Assert.Equal(3500, afterOlder!.Value.Px);
    }

    [Fact]
    public async Task ADuplicateAckChangesNothing()
    {
        var (rig, alice) = await SetupAsync();
        await using var _ = rig;
        await rig.RunAsync(4);
        long acked = rig.Replication.Stats.FramesAcked;
        var tokens = rig.Net.UnconfirmedTokens;
        Assert.Empty(tokens); // RunAsync confirmed everything
        await MoveAsync(rig, 24, 4242);
        await rig.StepAsync(confirm: false);
        var pending = rig.Net.UnconfirmedTokens.ToList();
        rig.Net.ConfirmWhere(t => pending.Contains(t));
        rig.Net.ConfirmAll();
        await rig.RunAsync(2);
        Assert.Empty(await rig.DivergenceAsync(alice));
        Assert.True(rig.Replication.Stats.FramesAcked > acked);
    }

    [Fact]
    public async Task AFrameNeverAckedIsRetiredAfterTheTimeoutWithoutForcingAFullResend()
    {
        var (rig, alice) = await SetupAsync(replication: r => r.InFlightTimeoutMs = 500);
        await using var _ = rig;
        await rig.RunAsync(4);
        await MoveAsync(rig, 24, 5555);
        await rig.RunAsync(14, confirm: false); // 700 ms without a single ack
        Assert.True(rig.Replication.Stats.FramesLost >= 1);
        rig.Net.ConfirmAll();
        await rig.RunAsync(4);
        Assert.Empty(await rig.DivergenceAsync(alice));
    }

    // ------------------------------------------------------------------ the rate at 100 ms RTT

    /// <summary>Entries for the ship per second over five seconds when every ack comes <paramref name="ackSteps"/> x 50 ms after the frame.</summary>
    private async Task<double> NearRateAsync(bool datagram, int ackSteps)
    {
        var (rig, _) = await SetupAsync(datagram);
        await using var _ = rig;
        await rig.RunAsync(6);
        var born = new Dictionary<long, long>();
        int mark = rig.Net.RealtimeLog.Count;
        const int steps = 100; // 5 s
        for (int i = 1; i <= steps; i++)
        {
            await MoveAsync(rig, 21, 640 + (i * 40)); // keeps moving: due at the Near rate every tick
            await rig.StepAsync(confirm: false);
            foreach (long token in rig.Net.UnconfirmedTokens)
            {
                born.TryAdd(token, rig.Tick);
            }

            rig.Net.ConfirmWhere(t => rig.Tick - born[t] >= ackSteps);
        }

        double rate = Entries(rig, mark).Count(e => e.NetId == 21) / (steps * ReplicationRig.StepSeconds);
        output.WriteLine($"datagram={datagram} ack after {ackSteps * 50} ms: {rate:F1} entries/s for the Near ship");
        return rate;
    }

    [Fact]
    public async Task AtOneHundredMillisecondsRttAUdpClientStillGetsTheNearRate()
    {
        double udp = await NearRateAsync(datagram: true, ackSteps: 2); // 100 ms from frame to ack
        Assert.True(udp >= 18, $"{udp:F1} Hz");
    }

    [Fact]
    public async Task TheSameLinkOverTcpWithOneFrameInFlightIsCappedNearRttInverse()
    {
        double tcp = await NearRateAsync(datagram: false, ackSteps: 2); // documents the limit UDP removes (roadmap follow-up of M1-08)
        Assert.True(tcp < 12, $"{tcp:F1} Hz");
    }
}
