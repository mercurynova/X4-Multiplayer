using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;
using static X4MP.Core.Tests.World.WorldKit;

namespace X4MP.Core.Tests.World;

/// <summary>F9b: a player's samples are stamped with their own time mapped to game time, not with the latest WorldUpdate's game time.</summary>
public sealed class PlayerSampleClockTests
{
    private const double GameBase = 100.0;
    private const double ClockBase = 200.0; // server clock seconds at game time GameBase

    private static void WorldUpdateAt(WorldMirror mirror, uint tick, double secondsIn) =>
        mirror.IngestWorldUpdate(MessageEncoder.EncodePayload(
            b => WorldUpdate.Pack(b, new WorldUpdateT
            {
                AuthorityTick = tick,
                CaptureTimeUs = (ulong)((ClockBase + secondsIn) * 1e6),
                GameTime = GameBase + secondsIn,
                States = [],
            }),
            256));

    private static void Sample(WorldMirror mirror, uint seq, ulong sampleTimeUs) =>
        mirror.ApplyPlayerState(4, Decode<PlayerState>(MsgType.PlayerState, MessageEncoder.EncodePayload(
            b => PlayerState.Pack(b, new PlayerStateT { Seq = seq, SampleTimeUs = sampleTimeUs, NetId = 50, Sector = 2, Px = (int)seq, Hull = 200, Shield = 100 }),
            128)));

    private static WorldMirror NewMirror()
    {
        var mirror = new WorldMirror();
        mirror.Spawn(Rec(50, EntityKind.ShipM, 2, 10, controller: 4, origin: EntityOrigin.PlayerShip));
        return mirror;
    }

    [Fact]
    public void SamplesArrivingInBurstsGetEvenStrictlyIncreasingStamps()
    {
        var mirror = NewMirror();
        Assert.True(mirror.TryGet(50, out var ship));
        var stamps = new List<double>();
        for (uint n = 0; n < 200; n++)
        {
            // The WorldUpdate (20 Hz too) only reaches the server every 4th sample: the other three arrive against a stale game time.
            if (n % 4 == 0)
            {
                WorldUpdateAt(mirror, n + 1, n * 0.05);
            }

            Sample(mirror, n + 1, (ulong)((ClockBase + (n * 0.05) + 0.01) * 1e6));
            stamps.Add(ship.SampleGameTime);
        }

        for (int i = 1; i < stamps.Count; i++)
        {
            double step = stamps[i] - stamps[i - 1];
            Assert.True(step > 0, $"stamp {i} does not increase ({step})");
            Assert.InRange(step, 0.045, 0.055);
        }
    }

    [Fact]
    public void ClockEstimateJumpAndDuplicateSampleTimeStayMonotonic()
    {
        var mirror = NewMirror();
        Assert.True(mirror.TryGet(50, out var ship));
        WorldUpdateAt(mirror, 1, 0);
        double last = double.NegativeInfinity;
        for (uint n = 0; n < 60; n++)
        {
            double t = n * 0.05;
            double sampleClock = ClockBase + t + (n >= 30 ? 5 : 0); // the sender's server-clock estimate jumps 5 s ahead
            ulong us = n == 40 ? (ulong)((ClockBase + (39 * 0.05) + 5) * 1e6) : (ulong)(sampleClock * 1e6); // sample 40 repeats sample 39's time
            WorldUpdateAt(mirror, n + 2, t);
            Sample(mirror, n + 1, us);
            Assert.True(ship.SampleGameTime > last, $"sample {n} does not increase");
            Assert.InRange(ship.SampleGameTime, GameBase + t - 1.0, GameBase + t + 0.5);
            last = ship.SampleGameTime;
        }
    }

    [Fact]
    public void MissingSampleTimeKeepsTheLatestWorldUpdateGameTime()
    {
        var mirror = NewMirror();
        Assert.True(mirror.TryGet(50, out var ship));
        WorldUpdateAt(mirror, 1, 0);
        Sample(mirror, 1, 0);
        Assert.Equal(GameBase, ship.SampleGameTime);
        Sample(mirror, 2, 0);
        Sample(mirror, 3, 0);
        Assert.Equal(GameBase, ship.SampleGameTime);
        WorldUpdateAt(mirror, 2, 0.05);
        Sample(mirror, 4, 0);
        Assert.Equal(GameBase + 0.05, ship.SampleGameTime, 9);
    }

    private static void WorldUpdateExplicit(WorldMirror mirror, uint tick, double captureSeconds, double gameTime) =>
        mirror.IngestWorldUpdate(MessageEncoder.EncodePayload(
            b => WorldUpdate.Pack(b, new WorldUpdateT { AuthorityTick = tick, CaptureTimeUs = (ulong)(captureSeconds * 1e6), GameTime = gameTime, States = [] }),
            256));

    [Fact]
    public void StampsStayStrictlyIncreasingWhileGameTimeStalls()
    {
        // Review 2026-10-28 (2): a paused authority keeps the game time still while the players' samples (20 Hz) go on. Once the stamp reached the
        // ceiling the nudge was clamped to it too, so every later sample got the same stamp (zero dt for viewers).
        var mirror = NewMirror();
        Assert.True(mirror.TryGet(50, out var ship));
        WorldUpdateAt(mirror, 1, 0);
        double last = double.NegativeInfinity;
        for (uint n = 0; n < 80; n++)
        {
            Sample(mirror, n + 1, (ulong)((ClockBase + (n * 0.05) + 0.01) * 1e6));
            Assert.True(ship.SampleGameTime > last, $"sample {n} does not increase ({ship.SampleGameTime} after {last})");
            last = ship.SampleGameTime;
        }
    }

    [Fact]
    public void StampsFollowTheAuthoritysGameTimeBackAfterARollback()
    {
        // Review 2026-10-28 (1, 6): the authority reloads an older checkpoint (game time 160 -> 80) while the player stays connected: the stamps must
        // follow the new axis at once, not stay pinned at the old maximum until game time catches up.
        var mirror = NewMirror();
        Assert.True(mirror.TryGet(50, out var ship));
        uint tick = 1;
        for (uint n = 0; n < 40; n++)
        {
            double t = 60.0 + (n * 0.05);
            WorldUpdateExplicit(mirror, tick++, ClockBase + t, GameBase + t);
            Sample(mirror, n + 1, (ulong)((ClockBase + t + 0.01) * 1e6));
        }

        Assert.True(ship.SampleGameTime > GameBase + 60);
        double last = double.NegativeInfinity;
        for (uint n = 0; n < 40; n++)
        {
            double t = 62.0 + (n * 0.05); // the server clock goes on; the game time restarts at 80
            double game = 80.0 + (n * 0.05);
            WorldUpdateExplicit(mirror, tick++, ClockBase + t, game);
            Sample(mirror, 100 + n, (ulong)((ClockBase + t + 0.01) * 1e6));
            Assert.InRange(ship.SampleGameTime, game - 1.0, game + 0.5);
            if (n > 0)
            {
                Assert.True(ship.SampleGameTime > last, $"sample {n} does not increase");
            }

            last = ship.SampleGameTime;
        }
    }
}
