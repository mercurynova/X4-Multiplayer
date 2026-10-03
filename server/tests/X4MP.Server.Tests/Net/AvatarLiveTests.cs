using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Server.Hosting;
using Xunit.Abstractions;

namespace X4MP.Server.Tests.Net;

/// <summary>
/// M3-05 on real sockets: the real server host with FakeNode's authority provisioning avatars, wingman bots flying around a "real" player, the chat
/// echo and the galaxy file. Each test ends as soon as its goal is observable (<see cref="LiveRunOptions.StopWhen"/>), never by sleeping.
/// </summary>
[Collection("net")]
public sealed class AvatarLiveTests(ITestOutputHelper output)
{
    private sealed class Host : IAsyncDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-avatar-" + Guid.NewGuid().ToString("N"));
        private readonly string[] _settings;
        private WebApplication _app;

        public Host(params string[] settings)
        {
            _settings = settings;
            _app = Build();
        }

        public int TcpPort { get; private set; }

        private WebApplication Build()
        {
            TcpPort = TestPorts.FreeTcp();
            string[] args =
            [
                "--data-dir", _dir, "--port", TestPorts.FreeTcp().ToString(CultureInfo.InvariantCulture),
                $"--X4MP:Net:NodeTcpEndpoint=127.0.0.1:{TcpPort}", "--X4MP:Net:MaxConnectionsPerIp=64", "--X4MP:Net:MaxPlayers=16",
                .. _settings,
            ];
            var cli = CliArguments.Parse(args);
            return ServerHost.Build(cli.Remaining, cli, isService: false);
        }

        public Task StartAsync() => TestPorts.StartWithRetryAsync(() => _app.StartAsync(), async () =>
        {
            await _app.DisposeAsync();
            _app = Build();
        });

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            X4MP.Persistence.SqliteConnectionFactory.ClearPool(_dir);
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static readonly LiveRunOptions Quick = new()
    {
        ReportInterval = TimeSpan.FromSeconds(5),
        ConnectStagger = TimeSpan.FromMilliseconds(30),
    };

    private async Task<(int Exit, string Text)> RunAsync(Host host, string[] args, LiveRunOptions? run = null, CancellationToken ct = default)
    {
        var options = CliParser.Parse(args).Options! with { Port = host.TcpPort };
        var text = new StringWriter();
        int exit = await LiveRunner.RunAsync(options, text, run ?? Quick, ct);
        output.WriteLine(text.ToString());
        return (exit, text.ToString());
    }

    private static IEnumerable<LiveNodeStats> Clients(IReadOnlyList<LiveNodeStats> s) => s.Where(x => x.Role == Role.Client);

    private static bool Flowing(IReadOnlyList<LiveNodeStats> s, string target, IEnumerable<string> bots, long nearEntries) =>
        bots.All(b => s.FirstOrDefault(x => x.Name == b)?.Session is { } session
                      && session.SyncSummaries().Any(m => m.Name.Contains(target, StringComparison.Ordinal) && m.NearEntries >= nearEntries));

    [Fact]
    public async Task WingmenFlyAroundARealPlayerAndSeeItAtTheNearRate()
    {
        await using var host = new Host();
        await host.StartAsync();
        string[] wingmen = ["Bot02", "Bot03", "Bot04"];
        IReadOnlyList<LiveNodeStats>? finished = null;

        var (exit, text) = await RunAsync(
            host,
            ["swarm", "--clients", "4", "--with-authority", "--wingman", "Bot01", "--behavior", "wander", "--duration", "30"],
            Quick with { OnFinished = s => finished = s, StopWhen = s => Flowing(s, "Bot01", wingmen, nearEntries: 200) });

        Assert.Equal(0, exit);
        Assert.Contains("avatar-flow: bots=4 with-avatar=4 without=0", text);
        Assert.Contains("wingmen=3", text);
        Assert.Contains("avatars: provisioned=4", text);
        Assert.Contains("host=net_id=", text);
        Assert.Contains("name=[MP] Host", text);

        // Near rate of the leader's stream at every wingman, and of every wingman's stream at the others: the server replicates player ships in Near at the
        // 20 Hz tick. The median must reach 18 Hz; a machine hiccup (this runs next to builds and other tests) may slow single streams, so each needs 15.
        var rates = new List<double>();
        foreach (string bot in wingmen)
        {
            var session = finished!.First(x => x.Name == bot).Session!;
            foreach (var m in session.SyncSummaries().Where(m => m.Name.StartsWith("[MP] Bot", StringComparison.Ordinal)))
                rates.Add(m.NearRateHz);
            var sum = session.SyncSummaries().Single(m => m.Name == "[MP] Bot01");
            output.WriteLine(sum.ToLine(bot));
            Assert.True(sum.NearRateHz >= 15, $"{bot} sees Bot01 at {sum.NearRateHz:F1} Hz in Near");
            Assert.True(sum.NearEntries >= 200);
            Assert.True(sum.GapP95Ms < 150, $"{bot}: 95th percentile gap {sum.GapP95Ms:F0} ms");
            Assert.True(session.FindPlayerPose("Host") is not null, $"{bot} sees the self-spawned host ship");
            Assert.Equal(0, session.Errors);
        }

        rates.Sort();
        double median = rates[rates.Count / 2];
        output.WriteLine($"near rate of {rates.Count} streams: median {median:F1} Hz, min {rates[0]:F1} Hz");
        Assert.True(median >= 18, $"median Near rate {median:F1} Hz (need >= 18)");

        // every wingman really followed: its own ship was near the leader's
        var leader = finished!.First(x => x.Name == "Bot01").Session!;
        Assert.True(finished!.Where(x => wingmen.Contains(x.Name)).All(x => x.Wingman is { FollowedSamples: > 100 }));
        Assert.NotNull(leader.FindPlayerPose("Bot02")); // and the leader sees its wingmen
    }

    [Fact]
    public async Task AvatarsAreSpawnedNextToTheHostShipAndEveryoneSeesEveryone()
    {
        await using var host = new Host();
        await host.StartAsync();
        FakeAuthority? authority = null;
        IReadOnlyList<LiveNodeStats>? finished = null;

        var (exit, text) = await RunAsync(
            host,
            ["swarm", "--clients", "3", "--with-authority", "--avatars", "--verify", "--duration", "20"],
            Quick with
            {
                OnAuthority = a => authority = a,
                OnFinished = s => finished = s,
                StopWhen = s => Clients(s).Count(c => c.AvatarState == 2) == 3 && Clients(s).All(c => c.Session is { PlayerShipIds.Count: >= 4 } && c.Session.ReplicationFrames > 60),
            });

        Assert.Equal(0, exit);
        Assert.Contains("avatar-flow: bots=3 with-avatar=3 without=0", text);
        Assert.Contains("verify: clients=3", text);
        Assert.Contains("position-errors=0 errors=0", text);

        var avatars = authority!.Avatars.Snapshot();
        Assert.Equal(3, avatars.Count);
        Assert.All(avatars, a => Assert.True(a.Online));
        Assert.All(avatars, a => Assert.Equal("ship_arg_s_fighter_01_a_macro", a.Macro));
        var hostShip = authority.Avatars.Host!;
        Assert.Equal("[MP] Host", hostShip.Name);
        Assert.Equal("ship_arg_s_fighter_01_a_macro", hostShip.Macro);
        Assert.Equal(1, hostShip.Team);
        // the host ship is where the clients said they stand: the host stand of the fake galaxy
        var (standSector, standPos) = FakeAvatarFlow.HostStand(authority.World.Galaxy);
        Assert.Equal(standSector, hostShip.Sector);
        Assert.Equal(standPos.X, hostShip.Position.X, 1);

        foreach (var bot in Clients(finished!))
        {
            var session = bot.Session!;
            Assert.NotNull(session.OwnAvatar);
            // each bot knows the host ship and the other two avatars as ghosts with their [MP] names (its own avatar answer counts as one more until M3-01)
            Assert.NotNull(session.FindPlayerPose("Host"));
            foreach (var other in Clients(finished!).Where(o => o != bot))
                Assert.NotNull(session.FindPlayerPose(other.Name));
            Assert.Equal(1, authority.Avatars.AvatarOf(bot.PlayerId)!.Team);
            Assert.InRange(bot.AvatarLatency.TotalSeconds, 0, 5);
        }

        // each avatar spawned 300..600 m from the host ship, in its sector (the bots have flown on since: only the spawn answer the bot got is fixed)
        foreach (var bot in Clients(finished!))
        {
            var spawn = bot.Session!.OwnAvatar!.Value;
            Assert.Equal(hostShip.Sector, spawn.Sector);
            Assert.InRange((spawn.Position - standPos).Length, 295, 605);
        }

        Assert.Equal(3, authority.Avatars.Provisioned);
        Assert.Equal(0, authority.Avatars.Reissued);
    }

    [Fact]
    public async Task AnAvatarSurvivesAClientResumeWithoutADuplicate()
    {
        await using var host = new Host();
        await host.StartAsync();
        FakeAuthority? authority = null;

        var (exit, text) = await RunAsync(
            host,
            ["swarm", "--clients", "2", "--with-authority", "--avatars", "--disconnect-every", "3", "--duration", "14"],
            Quick with
            {
                OnAuthority = a => authority = a,
                StopWhen = s => Clients(s).All(c => c.Session is { Resumes: >= 1, ReplicationFrames: > 100 }) && Clients(s).All(c => c.Session!.KeyframesSinceResume > 0),
            });

        Assert.Equal(0, exit);
        Assert.Contains("resume: clients=2", text);
        Assert.Equal(2, authority!.Avatars.Provisioned);
        Assert.Equal(3, authority.Avatars.Snapshot().Count + (authority.Avatars.Host is null ? 0 : 1));
    }

    [Fact]
    public async Task APlayerWhoLeavesLeavesItsAvatarParkedAndStillReplicated()
    {
        await using var host = new Host("--X4MP:Net:ResumeGraceSeconds=2");
        await host.StartAsync();
        FakeAuthority? authority = null;
        int ready = 0;
        var twoReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<LiveNodeStats>? watchers = null;

        var watching = RunAsync(
            host,
            ["swarm", "--clients", "2", "--with-authority", "--avatars", "--duration", "40"],
            Quick with
            {
                OnAuthority = a => authority = a,
                OnClientReady = _ =>
                {
                    if (Interlocked.Increment(ref ready) == 2)
                        twoReady.TrySetResult();
                },
                OnFinished = s => watchers = s,
                StopWhen = s => Clients(s).All(c => c.Session is { ControllerChanges: >= 1 }),
            });

        await twoReady.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // the leaver joins, gets its avatar, and its run ends (the socket closes without a goodbye; the server lets it go after the 2 s grace)
        var (leaverExit, leaverText) = await RunAsync(
            host,
            ["client", "--name", "Leaver", "--avatars", "--duration", "30", "--seed", "77"],
            Quick with { StopWhen = s => s.Any(x => x.AvatarState == 2) });
        Assert.Equal(0, leaverExit);
        Assert.Contains("avatar net_id=", leaverText);

        var (exit, text) = await watching;
        Assert.Equal(0, exit);
        Assert.Equal(1, authority!.Avatars.Leaves);
        var leaver = authority.Avatars.Snapshot().Single(a => a.Name == "[MP] Leaver");
        Assert.False(leaver.Online);
        foreach (var bot in Clients(watchers!))
        {
            var session = bot.Session!;
            Assert.Equal(0, session.ControllerOf(leaver.NetId));
            Assert.NotNull(session.FindPlayerPose("Leaver (offline)"));
            Assert.True(session.IsGhost(leaver.NetId), "a parked avatar is still replicated");
        }
    }

    [Fact]
    public async Task ChatEchoBotsAnswerOnTheSameChannelAndWhispersStayPrivate()
    {
        await using var host = new Host();
        await host.StartAsync();
        var handles = new ConcurrentDictionary<string, FakeClientHandle>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyList<LiveNodeStats>? finished = null;
        int sent = 0;

        var running = RunAsync(
            host,
            ["swarm", "--clients", "3", "--with-authority", "--chat-echo", "--duration", "30"],
            Quick with
            {
                OnClientReady = h =>
                {
                    handles[h.Name] = h;
                    if (handles.Count == 3)
                        ready.TrySetResult();
                },
                OnFinished = s => finished = s,
                StopWhen = s => Volatile.Read(ref sent) == 1
                                && s.First(x => x.Name == "Bot01").Session!.ChatLog.Count(l => l.Text.StartsWith("echo: ", StringComparison.Ordinal)) >= 3,
            });

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var bot1 = handles["Bot01"];
        var bot2 = handles["Bot02"];
        await bot1.SendChatAsync(ChatChannel.All, "hello all");
        await bot1.SendChatAsync(ChatChannel.Whisper, "psst", (ushort)bot2.PlayerId);
        Volatile.Write(ref sent, 1);

        var (exit, text) = await running;
        Assert.Equal(0, exit);
        var log = finished!.First(x => x.Name == "Bot01").Session!.ChatLog;
        Assert.Contains(log, l => l.Text == "echo: hello all" && l.FromName == "Bot02" && l.Channel == ChatChannel.All);
        Assert.Contains(log, l => l.Text == "echo: hello all" && l.FromName == "Bot03");
        Assert.Contains(log, l => l.Text == "echo: psst" && l.FromName == "Bot02" && l.Channel == ChatChannel.Whisper);
        Assert.DoesNotContain(log, l => l.Text == "echo: psst" && l.FromName == "Bot03");
        // Bot03 never saw the whisper, and an echo is not echoed again
        Assert.DoesNotContain(finished!.First(x => x.Name == "Bot03").Session!.ChatLog, l => l.Text.Contains("psst", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.Text.StartsWith("echo: echo:", StringComparison.Ordinal));
        Assert.Contains("chat: bots=", text);
    }

    [Fact]
    public async Task AGalaxyFileDrivesTheWholeSwarmAndVerifies()
    {
        string path = Path.Combine(Path.GetTempPath(), "x4mp-galaxy-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, """
            {"format":1,"sectors":[
              {"macro":"cluster_01_sector001_macro","cluster":"cluster_01_macro","gates":["cluster_01_sector002_macro","cluster_02_sector001_macro"]},
              {"macro":"cluster_01_sector002_macro","cluster":"cluster_01_macro","gates":["cluster_01_sector001_macro"]},
              {"macro":"cluster_02_sector001_macro","cluster":"cluster_02_macro","gates":["cluster_01_sector001_macro","cluster_03_sector001_macro"]},
              {"macro":"cluster_03_sector001_macro","cluster":"cluster_03_macro","gates":["cluster_02_sector001_macro"]}]}
            """);
        try
        {
            await using var host = new Host();
            await host.StartAsync();
            FakeAuthority? authority = null;
            var (exit, text) = await RunAsync(
                host,
                ["swarm", "--clients", "2", "--with-authority", "--verify", "--behavior", "explore", "--galaxy-file", path, "--ships", "300", "--duration", "15"],
                Quick with { OnAuthority = a => authority = a, StopWhen = s => LiveStop.Verified(s, 2, 800) });

            Assert.Equal(0, exit);
            Assert.Equal(4, authority!.World.Galaxy.Sectors.Count);
            Assert.Equal("cluster_01_sector001_macro", authority.World.Galaxy.Sectors[0].Macro);
            Assert.Contains("position-errors=0 errors=0", text);
            Assert.Contains("summary: nodes=3 joined=3 errors=0", text);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
