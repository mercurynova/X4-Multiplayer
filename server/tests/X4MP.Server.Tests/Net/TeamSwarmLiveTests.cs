using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using X4MP.Core.Relay;
using X4MP.Core.Teams;
using X4MP.Core.World;
using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Server.Hosting;
using Xunit.Abstractions;
using TeamRelation = X4MP.Core.Teams.TeamRelation;

namespace X4MP.Server.Tests.Net;

/// <summary>
/// M1-F3 over real sockets: FakeNode swarms with teams against the real server host. Lobby placement (<c>--team-pick</c>, <c>--teams</c>),
/// foreign orders across several teams, an admin move that makes the fake authority re-own the player's assets (the server mirror follows) and the
/// fake NPC hostility that follows a relation change.
/// </summary>
[Collection("net")]
public sealed partial class TeamSwarmLiveTests(ITestOutputHelper output)
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static readonly string[] LobbyWithCreate =
    [
        "--X4MP:Teams:JoinMode=Lobby", "--X4MP:Teams:AllowCreateInLobby=true", "--X4MP:Teams:AutoAssign=Balance",
    ];

    private sealed class Host : IAsyncDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-teamswarm-" + Guid.NewGuid().ToString("N"));
        private readonly WebApplication _app;

        public Host(params string[] settings)
        {
            TcpPort = FreePort();
            string[] args =
            [
                "--data-dir", _dir, "--port", FreePort().ToString(CultureInfo.InvariantCulture),
                $"--X4MP:Net:NodeTcpEndpoint=127.0.0.1:{TcpPort}", "--X4MP:Net:MaxConnectionsPerIp=64", "--X4MP:Net:MaxPlayers=16",
                .. settings,
            ];
            var cli = CliArguments.Parse(args);
            _app = ServerHost.Build(cli.Remaining, cli, isService: false);
        }

        public int TcpPort { get; }

        public RelayModule Relay => (RelayModule)_app.Services.GetService(typeof(RelayModule))!;

        public TeamModule Teams => (TeamModule)_app.Services.GetService(typeof(TeamModule))!;

        public WorldMirror Mirror => (WorldMirror)_app.Services.GetService(typeof(WorldMirror))!;

        public Task StartAsync() => _app.StartAsync();

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

    private static readonly LiveRunOptions Fast = new() { ReportInterval = TimeSpan.FromSeconds(5), ConnectStagger = TimeSpan.FromMilliseconds(30) };

    private static LiveRunOptions Until(Func<IReadOnlyList<LiveNodeStats>, bool> stopWhen) => Fast with { StopWhen = stopWhen };

    private static bool AllPlaced(IReadOnlyList<LiveNodeStats> s, int clients) =>
        s.Count(n => n.Role == Role.Client && n.InGame && n.TeamId != 0) >= clients && s.Any(n => n.Role == Role.Authority && n.InGame);

    private static CliOptions Swarm(Host host, params string[] args) =>
        CliParser.Parse(["swarm", .. args]).Options! with { Port = host.TcpPort };

    private async Task<(int Exit, string Text)> RunAsync(CliOptions options, LiveRunOptions? run = null, CancellationToken stop = default)
    {
        var text = new StringWriter();
        int exit = await LiveRunner.RunAsync(options, text, run ?? Fast, stop);
        output.WriteLine(text.ToString());
        return (exit, text.ToString());
    }

    private static long Num(Match m, string group) => long.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^commander\(\w+\): orders-sent=(?<sent>\d+) accepted=(?<accepted>\d+) rejected=(?<rejected>\d+) forwarded-to-authority=(?<forwarded>\d+)", RegexOptions.Multiline)]
    private static partial Regex CommanderLine();

    [GeneratedRegex(@"^teams: clients=(?<clients>\d+) placed=(?<placed>\d+) unplaced=(?<unplaced>\d+) teams-used=(?<used>\d+) spread=\[(?<spread>[^\]]*)\]", RegexOptions.Multiline)]
    private static partial Regex TeamsLine();

    [GeneratedRegex(@"^verify: clients=.* position-errors=(?<pos>\d+) errors=(?<errors>\d+)", RegexOptions.Multiline)]
    private static partial Regex VerifyLine();

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout, string what) where T : class
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (probe() is { } value)
                return value;
            await Task.Delay(25);
        }

        throw new TimeoutException("timed out waiting for " + what);
    }

    // ------------------------------------------------------------------ 1. foreign orders across teams

    [Fact]
    public async Task ForeignOrdersAcrossSeveralTeamsAreAllRejectedAndNoneIsForwarded()
    {
        await using var host = new Host(LobbyWithCreate);
        await host.StartAsync();

        var (exit, text) = await RunAsync(Swarm(host, "--clients", "4", "--with-authority", "--teams", "2", "--commander", "foreign", "--duration", "14"),
            Until(s => AllPlaced(s, 4) && LiveStop.OrdersSent(s) >= 12 && LiveStop.OrdersAnswered(s) == LiveStop.OrdersSent(s)));

        Assert.Equal(0, exit);
        var teams = TeamsLine().Match(text);
        Assert.True(teams.Success, "no teams line");
        Assert.Equal(4, Num(teams, "placed"));
        Assert.Equal(2, Num(teams, "used")); // the clients sit in two different teams
        var c = CommanderLine().Match(text);
        Assert.True(c.Success, "no commander line");
        Assert.True(Num(c, "sent") >= 10, $"only {Num(c, "sent")} orders were sent");
        Assert.Equal(0, Num(c, "accepted"));
        Assert.True(Num(c, "rejected") >= Num(c, "sent") - 4, text);
        Assert.Equal(0, Num(c, "forwarded"));
        Assert.Equal(0, host.Relay.Stats.IntentsForwarded);
        Assert.Equal(Num(c, "sent"), host.Relay.Stats.IntentsPermissionDenied);
    }

    // ------------------------------------------------------------------ 2. lobby placement

    [Fact]
    public async Task LobbyRandomPlacesEveryClientOnAnExistingTeam()
    {
        await using var host = new Host("--X4MP:Teams:JoinMode=Lobby", "--X4MP:Teams:AutoAssign=Balance");
        await host.StartAsync();
        foreach (string name in new[] { "Alpha", "Beta", "Gamma" })
            Assert.True((await host.Teams.CreateTeamAsync(name)).Ok);

        var (exit, text) = await RunAsync(Swarm(host, "--clients", "6", "--with-authority", "--team-pick", "lobby-random", "--duration", "10"), Until(s => AllPlaced(s, 6)));

        Assert.Equal(0, exit);
        var teams = TeamsLine().Match(text);
        Assert.True(teams.Success, "no teams line");
        Assert.Equal(6, Num(teams, "placed"));
        Assert.Equal(0, Num(teams, "unplaced"));
        Assert.True(Num(teams, "used") >= 2, "a random pick that never varies is not random: " + teams.Value);
        Assert.Equal(3, host.Teams.Teams.Count); // nobody created a team
        Assert.Equal(7, host.Teams.Teams.Sum(t => host.Teams.MembersOf(t.TeamId).Count)); // the six clients and the authority
    }

    [Fact]
    public async Task TeamsAreCreatedByTheClientsWhenTheLobbyAllowsIt()
    {
        await using var host = new Host(LobbyWithCreate);
        await host.StartAsync();

        var (exit, text) = await RunAsync(Swarm(host, "--clients", "6", "--with-authority", "--teams", "3", "--duration", "10"), Until(s => AllPlaced(s, 6)));

        Assert.Equal(0, exit);
        var teams = TeamsLine().Match(text);
        Assert.Equal(6, Num(teams, "placed"));
        Assert.Equal(3, Num(teams, "used"));
        Assert.Equal(["Team 1", "Team 2", "Team 3"], host.Teams.Teams.Select(t => t.Name).Order());
    }

    // ------------------------------------------------------------------ 5. verify in a team swarm

    [Fact]
    public async Task VerifyStaysCleanInATeamSwarm()
    {
        await using var host = new Host(LobbyWithCreate.Append("--X4MP:Teams:DefaultRelation=Hostile").ToArray());
        await host.StartAsync();

        var (exit, text) = await RunAsync(Swarm(host, "--clients", "4", "--with-authority", "--relations", "twoteams", "--verify", "--commander", "own", "--duration", "14"),
            Until(s => AllPlaced(s, 4) && LiveStop.Verified(s, 4, 1500) && s.Sum(n => n.Session?.ChecksumsOk ?? 0) >= 4));

        Assert.Equal(0, exit);
        var v = VerifyLine().Match(text);
        Assert.True(v.Success, "no verify line");
        Assert.Equal(0, Num(v, "errors"));
        Assert.Equal(0, Num(v, "pos"));
        Assert.Equal(4, Num(TeamsLine().Match(text), "placed"));
    }

    // ------------------------------------------------------------------ 3. moving a player

    [Fact]
    public async Task MovingAPlayerMakesTheFakeAuthorityReassignItsAssetsAndOrdersFollowTheNewTeam()
    {
        await using var host = new Host();
        await host.StartAsync();
        Assert.True((await host.Teams.CreateTeamAsync("One")).Ok); // Auto/SingleTeam puts everybody in the first team ...
        Assert.True((await host.Teams.CreateTeamAsync("Two")).Ok); // ... and team 2 is where the mover goes

        var handles = new ConcurrentDictionary<string, FakeClientHandle>();
        FakeAuthority? authority = null;
        var run = new LiveRunOptions
        {
            ReportInterval = TimeSpan.FromSeconds(5),
            ConnectStagger = TimeSpan.FromMilliseconds(30),
            OnAuthority = a => authority = a,
            OnClientReady = h => handles[h.Name] = h,
        };
        var options = Swarm(host, "--clients", "3", "--with-authority", "--commander", "own", "--verify", "--sectors", "12", "--ships", "4000", "--duration", "24");
        using var stopSwarm = new CancellationTokenSource(); // the test body is the whole scenario: the swarm ends when it does
        var swarm = RunAsync(options, run, stopSwarm.Token);

        var report = new List<string>();
        bool failed = false;
        try
        {
            await WaitForAsync(() => handles.Count == 3 ? handles : null, TimeSpan.FromSeconds(15), "three clients in game");
            var mover = handles["Bot01"];
            var teammate = handles["Bot02"];
            Assert.Equal(1, mover.TeamId);

            // assets of the mover that the mover's view and the server's mirror both know
            var mine = await WaitForAsync(
                () => mover.Session.GhostsOwnedBy(1, mover.PlayerId).Where(g => host.Mirror.TryGet(g.NetId, out var e) && e.OwnerPlayer == mover.PlayerId).ToList() is { Count: > 0 } l ? l : null,
                TimeSpan.FromSeconds(15), "assets owned by the mover");
            uint asset = mine[0].NetId;
            ushort sector = mine[0].Sector;
            report.Add($"mover player {mover.PlayerId} owns {mine.Count} visible asset(s), probing {asset}");

            // before the move both players of team 1 may command it
            Assert.Equal(IntentStatus.Accepted, (await mover.SendOrderAsync(asset, sector, TimeSpan.FromSeconds(3)))?.Status);
            Assert.Equal(IntentStatus.Accepted, (await teammate.SendOrderAsync(asset, sector, TimeSpan.FromSeconds(3)))?.Status);

            var moved = await host.Teams.AssignPlayerAsync(mover.PlayerId, 2);
            Assert.True(moved.Ok, moved.Detail);

            // the authority re-owns the assets (EntityChange), and the server mirror follows
            await WaitForAsync(() => host.Mirror.TryGet(asset, out var e) && e.OwnerTeam == 2 ? e : null, TimeSpan.FromSeconds(5), "the mirror to show the new owner team");
            host.Mirror.TryGet(asset, out var entity);
            Assert.Equal(2, entity.OwnerTeam);
            Assert.Equal(mover.PlayerId, entity.OwnerPlayer);
            Assert.NotNull(authority);
            Assert.Contains(authority!.OwnershipChanges, c => c.NetId == asset && c.FromTeam == 1 && c.ToTeam == 2);
            Assert.All(mine, g => Assert.True(host.Mirror.TryGet(g.NetId, out var e2) && e2.OwnerTeam == 2, "every visible asset moved with its player"));
            await WaitForAsync(() => mover.Session.OwnerOf(asset) is { Team: 2 } ? mover.Session : null, TimeSpan.FromSeconds(5), "the mover's client to learn the new owner");

            // after the move: the new team passes, the old team fails
            Assert.Equal(2, mover.TeamId);
            var newTeam = await mover.SendOrderAsync(asset, sector, TimeSpan.FromSeconds(3));
            var oldTeam = await teammate.SendOrderAsync(asset, sector, TimeSpan.FromSeconds(3));
            Assert.Equal(IntentStatus.Accepted, newTeam?.Status);
            Assert.Equal(IntentStatus.Rejected, oldTeam?.Status);
            Assert.Equal(RejectReason.NotYourAsset, oldTeam?.Reason);
            report.Add("after the move: new team accepted, old team NotYourAsset");
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            await stopSwarm.CancelAsync();
            var (exit, text) = await swarm;
            foreach (string line in report)
                output.WriteLine(line);
            if (!failed)
            {
                Assert.Equal(0, exit); // --verify found nothing wrong, with ownership changing mid-run
                Assert.Contains("reassign: requests=1 assets-moved=", text);
            }
        }
    }

    // ------------------------------------------------------------------ 4. NPC hostility follows the relations

    [Fact]
    public async Task FakeNpcHostilityFollowsARelationChangeWithinOneSecond()
    {
        await using var host = new Host();
        await host.StartAsync();
        Assert.True((await host.Teams.CreateTeamAsync("One")).Ok);
        Assert.True((await host.Teams.CreateTeamAsync("Two")).Ok);

        FakeAuthority? authority = null;
        var run = new LiveRunOptions { ReportInterval = TimeSpan.FromSeconds(5), ConnectStagger = TimeSpan.FromMilliseconds(30), OnAuthority = a => authority = a };
        using var stopSwarm = new CancellationTokenSource();
        var swarm = RunAsync(Swarm(host, "--clients", "2", "--with-authority", "--team-assets", "--duration", "14"), run, stopSwarm.Token);
        var lags = new List<double>();
        bool failed = false;
        try
        {
            await WaitForAsync(() => authority is { Teams.TableVersion: > 0 } a && a.World is not null && host.Teams.Teams.Count == 2 ? a : null, TimeSpan.FromSeconds(10), "the authority");
            await WaitForAsync(() => authority!.Teams.Team(2) is not null ? authority : null, TimeSpan.FromSeconds(10), "the authority to hold both teams");
            await Task.Delay(1500); // let the session settle
            Assert.Equal(0, authority!.Hostility().EngagedShipPairs);

            foreach (var relation in new[] { TeamRelation.Hostile, TeamRelation.Allied, TeamRelation.Hostile, TeamRelation.Neutral })
            {
                bool war = relation == TeamRelation.Hostile;
                var clock = Stopwatch.StartNew();
                Assert.True((await host.Teams.SetRelationAsync(1, 2, relation)).Ok);
                while ((authority.Hostility().EngagedShipPairs > 0) != war)
                {
                    Assert.True(clock.ElapsedMilliseconds < 1000, $"the fake NPC hostility did not follow {relation} within 1 s");
                    await Task.Delay(5);
                }

                lags.Add(clock.Elapsed.TotalMilliseconds);
                var h = authority.Hostility();
                Assert.Equal(war ? [(1, 2)] : [], h.HostileTeamPairs);
                if (war)
                    Assert.True(h.ShipsAtWar > 0 && h.SectorsWithFights > 0);
            }
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            await stopSwarm.CancelAsync();
            var (exit, text) = await swarm;
            output.WriteLine("relation change -> hostility lag (ms): " + string.Join(", ", lags.Select(l => l.ToString("F0", CultureInfo.InvariantCulture))));
            if (!failed)
            {
                Assert.Equal(0, exit);
                Assert.Contains("npc-hostility: relation-changes=4", text);
            }
        }
    }
}
