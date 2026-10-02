using System.Globalization;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

/// <summary>
/// Which assets a fake client orders around (M1-T4, server-design 6.3): <c>Own</c> = team-common assets of its team, <c>Shared</c> = a
/// teammate's assets (allowed under SharedCommand, rejected under OwnerOnly), <c>Foreign</c> = another team's assets (always rejected).
/// </summary>
public enum CommanderMode
{
    None,
    Shared,
    Own,
    Foreign,
}

/// <summary>How a client answers the <c>AwaitingTeam</c> lobby when it has no explicit <c>--team</c> (M1-F3).</summary>
public enum TeamPickMode
{
    None,

    /// <summary>A random team that is open (not locked, not full, no password); a new team when there is none and the lobby allows it.</summary>
    LobbyRandom,
}

/// <summary>Swarm team layouts (<c>--relations</c>, server-design 2.13 presets): the number of teams and how they relate.</summary>
public enum RelationsPreset
{
    None,

    /// <summary>One team, everyone co-operates (Teams.AutoAssign=SingleTeam).</summary>
    Coop,

    /// <summary>One team per player, all Allied (Teams.DefaultRelation=Allied, AutoAssign=NewTeamPerPlayer).</summary>
    Allied,

    /// <summary>One team per player, all Hostile (Teams.DefaultRelation=Hostile, AutoAssign=NewTeamPerPlayer).</summary>
    Ffa,

    /// <summary>Two teams that are Hostile to each other (Teams.DefaultRelation=Hostile, AutoAssign=Balance).</summary>
    TwoTeams,
}

/// <summary>How much a fake client does with credits (<c>--economy</c>, M1-F4): nothing but track its wallet, or a realistic mix of requests.</summary>
public enum EconomyMode
{
    Off,

    /// <summary>Joins the economy but sends no requests; it still reconciles its wallet against the server's updates.</summary>
    Idle,

    /// <summary>About one action every 8 s (transfers, donations, pool, loans) and a trade proposal every 15 s.</summary>
    Casual,

    /// <summary>About one action every 3 s, an attack every 7 s and a trade proposal every 8 s: close to the 5 requests per 10 s the server allows.</summary>
    Heavy,
}

public enum FakeNodeCommand
{
    Authority,
    Client,
    Swarm,
    Inspect,

    /// <summary>Hostile peer: random, malformed and out-of-policy frames against a real server (M1-F2).</summary>
    Fuzz,

    /// <summary>Offline: generate the galaxy and print stats.</summary>
    Galaxy,
    Help,
}

/// <summary>Parsed command line (server-design 6.1; part 1 covers the options that exist so far).</summary>
public sealed record CliOptions
{
    public FakeNodeCommand Command { get; init; } = FakeNodeCommand.Help;
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = ProtocolConstants.DefaultTcpPort;
    public ulong Seed { get; init; } = 42;
    public int Clients { get; init; } = 1;
    public string Name { get; init; } = "FakeNode";
    public string NamePrefix { get; init; } = "Bot";
    public ClientBehavior Behavior { get; init; } = ClientBehavior.Wander;
    public bool Verify { get; init; }
    public bool Udp { get; init; }

    /// <summary>Failure injection: percent (0..100) of UDP datagrams dropped in each direction (only with --udp).</summary>
    public double LossPercent { get; init; }
    public int Sectors { get; init; } = 152;
    public int Ships { get; init; } = 10225;

    /// <summary>Per-sector ship cap of the fake galaxy (default 800); raise it with <c>--ships</c> for load tests that need a denser world.</summary>
    public int MaxShipsPerSector { get; init; } = 800;
    public int TickRate { get; init; } = 20;
    public int Fps { get; init; } = 60;
    public ushort? Sector { get; init; }
    public int Seconds { get; init; } = 60;

    /// <summary>Live commands: stop after this many seconds; null = run until Ctrl+C.</summary>
    public int? Duration { get; init; }

    /// <summary>swarm: also start one authority node.</summary>
    public bool WithAuthority { get; init; }
    public string? Password { get; init; }

    /// <summary>client/swarm: the extension report every node sends in <c>ClientHello.extension_list</c> (<c>--extensions file.json</c>, see <see cref="ExtensionListFile"/>); null = none.</summary>
    public IReadOnlyList<ExtensionInfoT>? Extensions { get; init; }

    /// <summary>client/swarm: send <c>AssetOrder</c>s for assets of this kind (default: none).</summary>
    public CommanderMode Commander { get; init; } = CommanderMode.None;

    /// <summary>authority: tag its ships with team owners (implied by <see cref="Commander"/>), so clients have team assets to command.</summary>
    public bool TeamAssets { get; init; }

    /// <summary>client/swarm: the team to join, a team id or a team name (lobby: <c>TeamChoice</c>, creates it when the lobby allows; later: <c>TeamChangeRequest</c>).</summary>
    public string? Team { get; init; }

    /// <summary>client/swarm: how to answer the lobby when there is no <see cref="Team"/>.</summary>
    public TeamPickMode TeamPick { get; init; }

    /// <summary>swarm: spread the clients over this many teams (client i joins team <c>i mod N + 1</c>, named "Team k"); 0 = not set. The authority tags its ships for teams 1..N.</summary>
    public int Teams { get; init; }

    /// <summary>swarm: a team layout; the swarm joins the clients accordingly and prints the server settings the layout needs.</summary>
    public RelationsPreset Relations { get; init; }

    /// <summary>The number of teams the swarm uses: <c>--teams</c>, else what <c>--relations</c> implies (coop 1, twoteams 2, allied/ffa one per client), else 0 (the server decides).</summary>
    public int EffectiveTeams => Teams > 0 ? Teams : Relations switch
    {
        RelationsPreset.Coop => 1,
        RelationsPreset.TwoTeams => 2,
        RelationsPreset.Allied or RelationsPreset.Ffa => Math.Max(1, Clients),
        _ => 0,
    };

    /// <summary>authority: size of the fake save it uploads on <c>RequestSave</c> (megabytes).</summary>
    public int SaveMb { get; init; } = 4;

    /// <summary>client/swarm: clients propose ship-for-credits trades to each other and accept the ones they receive (M1-E5); implies team assets.</summary>
    public bool Trade { get; init; }

    /// <summary>Trading clients stop proposing this many seconds before a timed run ends (so the last trades can finish).</summary>
    public int TradeQuietSeconds { get; init; } = 10;

    /// <summary>authority: percent (0..100) of the <c>AssetTransferOrder</c>s that fail (<c>ok=false</c>, compensated).</summary>
    public double TradeFailPercent { get; init; }

    /// <summary>authority: percent (0..100) of the orders whose confirm is withheld (answered only by a <c>TradeQuery</c>, or never).</summary>
    public double TradeTimeoutPercent { get; init; }

    // ---- M1-F4: economy behaviours

    /// <summary>client/swarm: the credit behaviour of the clients (transfers, donations, pool, loans; casual and heavy also trade).</summary>
    public EconomyMode Economy { get; init; }

    /// <summary>client/swarm: replay request keys, reuse them with other payloads and race identical requests; all of it must be rejected or idempotent. Implies at least <c>--economy casual</c>.</summary>
    public bool DupeAttack { get; init; }

    /// <summary>client/swarm: borrowers never repay and loans fall due after a few seconds, so they go overdue. Implies at least <c>--economy casual</c>.</summary>
    public bool LoanDefault { get; init; }

    /// <summary>authority/client/swarm: <c>CreditDelta{seq}</c> income and spend per second and player from the authority (clients book a quarter of it as their own local changes); 0 = none.</summary>
    public double IncomeRate { get; init; }

    /// <summary>End-of-run audit over the admin API (for example <c>http://127.0.0.1:47790</c>): invariants, ledger duplicate check, loans, wallet drift.</summary>
    public string? AdminUrl { get; init; }

    public string AdminUser { get; init; } = "admin";

    public string? AdminPassword { get; init; }

    /// <summary>The economy mode the clients really run: <c>--dupe-attack</c> and <c>--loan-default</c> raise Off to Casual.</summary>
    public EconomyMode EffectiveEconomy => Economy == EconomyMode.Off && (DupeAttack || LoanDefault) ? EconomyMode.Casual : Economy;

    /// <summary>The clients take part in the economy (even when idle: they reconcile).</summary>
    public bool EconomyActive => EffectiveEconomy != EconomyMode.Off || IncomeRate > 0;

    /// <summary>Clients propose and accept trades: <c>--trade</c>, or an economy mode that sends requests (casual, heavy).</summary>
    public bool TradesEnabled => Trade || EffectiveEconomy is EconomyMode.Casual or EconomyMode.Heavy;

    // ---- M1-F2: failure injection

    /// <summary>client/swarm: the first <see cref="SlowClients"/> clients read their socket slowly once in game (a rate, or a read/pause pattern); null = off.</summary>
    public SlowReaderSpec? SlowReader { get; init; }

    /// <summary>How many clients (counted from the first) are slow readers; the others must not notice.</summary>
    public int SlowClients { get; init; } = 1;

    /// <summary>Delay added to every TCP chunk and UDP datagram in each direction, milliseconds (0 = none).</summary>
    public double LatencyMs { get; init; }

    /// <summary>Latency varies by up to this much either way, milliseconds.</summary>
    public double JitterMs { get; init; }

    /// <summary>client/swarm: every N seconds drop the socket without a goodbye and come back with the resume token (0 = never).</summary>
    public double DisconnectEverySeconds { get; init; }

    /// <summary>client/swarm: every N seconds send <c>Disconnect(ClientReload)</c>, "reload", come back with the resume token and send <c>NodeReady</c> again (0 = never).</summary>
    public double ReloadEverySeconds { get; init; }

    /// <summary>fuzz: what to throw at the server.</summary>
    public FuzzMode FuzzMode { get; init; } = FuzzMode.All;

    /// <summary>fuzz: bind the fuzzer's sockets to this local address (for example <c>127.0.0.2</c>), so the temporary ban it earns hits only the fuzzer.</summary>
    public string? LocalIp { get; init; }

    /// <summary>fuzz: when the server bans the fuzzer's address, continue from the next loopback address (127.0.0.x), so the fuzzing goes on after the ban.</summary>
    public bool RotateIp { get; init; }

    /// <summary>inspect: print only these message types (names, case-insensitive; empty = all).</summary>
    public IReadOnlyList<string> Filter { get; init; } = [];

    /// <summary>inspect: stop after this many printed frames (0 = no limit).</summary>
    public int MaxFrames { get; init; }

    /// <summary>inspect: only connect and listen; do not walk the join path.</summary>
    public bool NoJoin { get; init; }

    /// <summary>The injection that changes how the TCP stream behaves (latency, slow reader) is on.</summary>
    public bool InjectsStreamFaults => LatencyMs > 0 || JitterMs > 0 || SlowReader is not null;

    /// <summary>True when client number <paramref name="ordinal"/> (0-based among the clients) is a slow reader.</summary>
    public bool IsSlowReader(int ordinal) => SlowReader is not null && ordinal < SlowClients;
}

/// <summary>What the <c>fuzz</c> command sends.</summary>
public enum FuzzMode
{
    /// <summary>A mix of everything below.</summary>
    All,

    /// <summary>Garbage and malformed frames before the handshake completes.</summary>
    Handshake,

    /// <summary>A valid handshake, then random, wrong-lane, wrong-role, wrong-phase and invalid-FlatBuffers frames.</summary>
    Session,

    /// <summary>Handshake floods: many connections that never say hello.</summary>
    Flood,
}

public sealed record CliParseResult(CliOptions? Options, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// <c>fakenode authority|client|swarm|inspect|galaxy [options]</c>. Options accept <c>--name value</c> and
/// <c>--name=value</c>; <c>--verify</c> and <c>--udp</c> are flags.
/// </summary>
public static class CliParser
{
    public const string Usage = """
        usage: fakenode <command> [options]

        commands:
          authority   connect as the X4 authority node: runs the fake world, honours CaptureSet, streams WorldUpdate
          client      connect as a fake player node: joins, flies, sends PlayerState, receives Replication (--verify checks it)
          swarm       --clients K client nodes in one process (+ an authority with --with-authority)
          inspect     connect as a client, walk the join path and print every decoded frame (--filter, --max-frames, --no-join)
          fuzz        hostile peer: random/malformed frames, wrong lanes/phases, oversized lengths, invalid FlatBuffers (--seed, --duration)
          galaxy      offline: generate the galaxy and print its stats

        options:
          --server host:port   server address (default 127.0.0.1:47780)
          --seed N             universe seed (default 42)
          --clients K          swarm: number of fake clients (also --count)
          --name NAME          node name; --name-prefix PREFIX for several
          --behavior wander|patrol|explore
          --verify             check every Replication entry against the fake world's ground truth; exit code 1 on any error
          --udp                use the UDP realtime lane (binds with UdpHello; falls back to TCP after 3 s)
          --loss PCT           with --udp: drop PCT percent of the UDP datagrams in each direction (failure injection)
          --sectors N --ships N --tick HZ --fps N   universe / authority shape
          --max-ships-per-sector N   per-sector ship cap of the fake galaxy (default 800)
          --sector ID          (unused; kept for old scripts)
          --duration N         live commands: exit after N seconds (default: run until Ctrl+C; alias --seconds)
          --with-authority     swarm: also connect one authority node
          --password PW        session password
          --extensions FILE    client/swarm: report the extensions listed in this JSON file in ClientHello (array of {id,name,version,source,enabled,workshopId,classHint,...})
          --commander shared|own|foreign   clients send AssetOrders for teammates' / own team-common / another team's ships (0.5 s apart)
          --team <id|name>     client/swarm: join this team (a lobby gets a TeamChoice, or a TeamCreateRequest when the team does not exist and the lobby allows creating; a node that already has another team asks for a move)
          --team-pick lobby-random   client/swarm: answer the lobby with a random open team
          --teams N            swarm: spread the clients over N teams ("Team 1".."Team N"; the authority tags its ships for them); needs JoinMode=Lobby (+ AllowCreateInLobby) to place clients
          --relations coop|allied|ffa|twoteams   swarm: team layout; implies --teams (coop 1, twoteams 2, allied/ffa one per client) and prints the server settings it needs
          --team-assets        authority: give its ships team owners (implied by --commander; start the authority with it when clients run elsewhere)
          --save-mb N          authority: size of the fake save it uploads (default 4)
          --trade              clients propose ship-for-credits trades to each other and accept incoming ones (implies --team-assets)
          --trade-fail PCT     authority: fail PCT percent of the AssetTransferOrders (the server must refund and unlock)
          --trade-timeout PCT  authority: withhold the confirm of PCT percent of the orders; a third of those never answer a TradeQuery either (InDoubt)
          --economy idle|casual|heavy   clients: a realistic mix of transfers, donations, pool deposits/withdrawals, loan offers/accepts/repays and trades
                               (casual ~1 action per 8 s, heavy ~1 per 3 s; the server allows 5 requests per 10 s); idle only reconciles. casual and heavy imply --trade
          --dupe-attack        clients replay request keys, reuse keys with other payloads and race identical requests (all must be rejected or idempotent); implies --economy casual
          --loan-default       borrowers never repay and loans fall due after ~5 s, so they go overdue; implies --economy casual
          --income-rate R      authority: R CreditDelta{seq} per second and player (income, some spend); clients book R/4 of their own; every node reconciles its wallet against WalletUpdate/acked_delta_seq
          --admin-url URL      end of run: audit over the admin API (auditor, ledger duplicate check, loans, wallet drift); --admin-user (default admin), --admin-password
          --slow-reader R      failure injection: the first --slow-clients clients (default 1) read their socket slowly once in game; R is bytes per
                               second, or pause=<read s>/<pause s> (read normally, then stop reading). The server must cope; only they suffer
          --slow-clients N     how many clients are slow readers (default 1)
          --latency MS         failure injection: add MS milliseconds to every TCP chunk and UDP datagram in each direction (round trip +2*MS)
          --jitter MS          latency varies by up to +-MS (TCP keeps its order; UDP datagrams may reorder)
          --disconnect-every S clients drop the socket abruptly every S seconds and resume with the resume token (ghost state restarts, keyframes follow)
          --reload-every S     clients send Disconnect(ClientReload) every S seconds, pause like a reload, resume and send NodeReady again
          --fuzz-mode all|handshake|session|flood   fuzz: what to send (default all); --clients N runs N fuzzers in parallel
          --local-ip ADDR      fuzz: bind to this local address (127.0.0.2 keeps the temp ban away from a swarm on 127.0.0.1)
          --rotate-ip          fuzz: after a ban continue from the next loopback address (127.0.0.2, .3, ...)
          --filter T1,T2       inspect: print only these message types
          --max-frames N       inspect: stop after N printed frames
          --no-join            inspect: connect and listen without joining
        """;

    private static readonly Dictionary<string, FakeNodeCommand> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["authority"] = FakeNodeCommand.Authority,
        ["client"] = FakeNodeCommand.Client,
        ["swarm"] = FakeNodeCommand.Swarm,
        ["inspect"] = FakeNodeCommand.Inspect,
        ["fuzz"] = FakeNodeCommand.Fuzz,
        ["galaxy"] = FakeNodeCommand.Galaxy,
        ["help"] = FakeNodeCommand.Help,
    };

    public static CliParseResult Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is "-h" or "--help" or "help")
            return new CliParseResult(new CliOptions { Command = FakeNodeCommand.Help }, null);
        if (!Commands.TryGetValue(args[0], out var command))
            return Fail($"unknown command '{args[0]}'");

        var o = new CliOptions { Command = command };
        for (int i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                return Fail($"unexpected argument '{arg}'");
            string key = arg[2..];
            string? value = null;
            int eq = key.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                value = key[(eq + 1)..];
                key = key[..eq];
            }

            bool isFlag = key is "verify" or "udp" or "with-authority" or "team-assets" or "trade" or "no-join" or "rotate-ip" or "dupe-attack" or "loan-default";
            if (isFlag)
            {
                bool on = value is null || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (value is not null && !on && !value.Equals("false", StringComparison.OrdinalIgnoreCase))
                    return Fail($"--{key} takes no value (got '{value}')");
                o = key switch
                {
                    "verify" => o with { Verify = on },
                    "with-authority" => o with { WithAuthority = on },
                    "team-assets" => o with { TeamAssets = on },
                    "trade" => o with { Trade = on },
                    "no-join" => o with { NoJoin = on },
                    "rotate-ip" => o with { RotateIp = on },
                    "dupe-attack" => o with { DupeAttack = on },
                    "loan-default" => o with { LoanDefault = on },
                    _ => o with { Udp = on },
                };
                continue;
            }

            if (value is null)
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    return Fail($"--{key} needs a value");
                value = args[++i];
            }

            var (next, error) = Apply(o, key, value);
            if (error is not null)
                return Fail(error);
            o = next;
        }

        if (command == FakeNodeCommand.Inspect && o.Clients != 1)
            return Fail("inspect watches one connection (no --clients)");
        if (o.SlowReader is not null && command is not (FakeNodeCommand.Client or FakeNodeCommand.Swarm))
            return Fail("--slow-reader is a client/swarm option");
        if ((o.DisconnectEverySeconds > 0 || o.ReloadEverySeconds > 0) && command is not (FakeNodeCommand.Client or FakeNodeCommand.Swarm))
            return Fail("--disconnect-every and --reload-every are client/swarm options");
        bool economyCommand = command is FakeNodeCommand.Client or FakeNodeCommand.Swarm;
        if ((o.Economy != EconomyMode.Off || o.DupeAttack || o.LoanDefault) && !economyCommand)
            return Fail("--economy, --dupe-attack and --loan-default are client/swarm options");
        if (o.IncomeRate > 0 && command is not (FakeNodeCommand.Authority or FakeNodeCommand.Client or FakeNodeCommand.Swarm))
            return Fail("--income-rate is an authority/client/swarm option");
        if (o.AdminUrl is not null && !economyCommand)
            return Fail("--admin-url is a client/swarm option");
        if (command == FakeNodeCommand.Swarm && o.Clients < 1)
            return Fail("swarm needs --clients >= 1");
        if (o.Team is not null && o.TeamPick != TeamPickMode.None)
            return Fail("--team and --team-pick are mutually exclusive");
        if (o.Teams > 0 && o.Team is not null)
            return Fail("--teams and --team are mutually exclusive");
        if (o.Relations != RelationsPreset.None && command != FakeNodeCommand.Swarm)
            return Fail("--relations is a swarm option");
        return new CliParseResult(o, null);
    }

    private static CliParseResult Fail(string error) => new(null, error);

    private static (CliOptions, string?) Apply(CliOptions o, string key, string value)
    {
        switch (key)
        {
            case "server":
                int colon = value.LastIndexOf(':');
                if (colon <= 0 || colon == value.Length - 1)
                    return (o, $"--server must be host:port (got '{value}')");
                if (!int.TryParse(value[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
                    return (o, $"invalid port in --server '{value}'");
                return (o with { Host = value[..colon], Port = port }, null);
            case "seed":
                return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong seed)
                    ? (o with { Seed = seed }, null) : (o, $"--seed must be a non-negative integer (got '{value}')");
            case "clients" or "count":
                return PositiveInt(o, key, value, v => o with { Clients = v });
            case "sectors":
                return PositiveInt(o, key, value, v => v < 2 ? null : o with { Sectors = v });
            case "ships":
                return PositiveInt(o, key, value, v => o with { Ships = v });
            case "max-ships-per-sector":
                return PositiveInt(o, key, value, v => o with { MaxShipsPerSector = v });
            case "tick":
                return PositiveInt(o, key, value, v => o with { TickRate = v });
            case "fps":
                return PositiveInt(o, key, value, v => o with { Fps = v });
            case "seconds" or "duration":
                return PositiveInt(o, key, value, v => o with { Seconds = v, Duration = v });
            case "sector":
                return PositiveInt(o, key, value, v => v > ushort.MaxValue ? null : o with { Sector = (ushort)v });
            case "save-mb":
                return PositiveInt(o, key, value, v => v > 4096 ? null : o with { SaveMb = v });
            case "loss":
                string number = value.TrimEnd('%');
                return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double loss) && loss is >= 0 and <= 100
                    ? (o with { LossPercent = loss }, null) : (o, $"--loss must be a percentage between 0 and 100 (got '{value}')");
            case "slow-reader":
                return SlowReaderSpec.TryParse(value, out var slow, out string? slowError)
                    ? (o with { SlowReader = slow }, null) : (o, slowError);
            case "slow-clients":
                return PositiveInt(o, key, value, v => o with { SlowClients = v });
            case "latency":
                return Milliseconds(o, key, value, v => o with { LatencyMs = v });
            case "jitter":
                return Milliseconds(o, key, value, v => o with { JitterMs = v });
            case "disconnect-every":
                return Seconds(o, key, value, v => o with { DisconnectEverySeconds = v });
            case "reload-every":
                return Seconds(o, key, value, v => o with { ReloadEverySeconds = v });
            case "fuzz-mode":
                return Enum.TryParse<FuzzMode>(value, ignoreCase: true, out var fm) && Enum.IsDefined(fm)
                    ? (o with { FuzzMode = fm }, null) : (o, $"--fuzz-mode must be all|handshake|session|flood (got '{value}')");
            case "local-ip":
                return System.Net.IPAddress.TryParse(value, out _) ? (o with { LocalIp = value }, null) : (o, $"--local-ip must be an IP address (got '{value}')");
            case "filter":
                return (o with { Filter = [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] }, null);
            case "max-frames":
                return PositiveInt(o, key, value, v => o with { MaxFrames = v });
            case "trade-fail":
                return Percent(o, key, value, v => o with { TradeFailPercent = v });
            case "trade-timeout":
                return Percent(o, key, value, v => o with { TradeTimeoutPercent = v });
            case "name":
                return (o with { Name = value }, null);
            case "name-prefix":
                return (o with { NamePrefix = value }, null);
            case "password":
                return (o with { Password = value }, null);
            case "extensions":
                var (list, error) = ExtensionListFile.Load(value);
                return list is null ? (o, error) : (o with { Extensions = list }, null);
            case "commander":
                return Enum.TryParse<CommanderMode>(value, ignoreCase: true, out var c) && Enum.IsDefined(c) && c != CommanderMode.None
                    ? (o with { Commander = c }, null) : (o, $"--commander must be shared|own|foreign (got '{value}')");
            case "team":
                return value.Length is >= 1 and <= 24 ? (o with { Team = value }, null) : (o, $"--team must be a team id or a name of 1-24 characters (got '{value}')");
            case "team-pick":
                return value.Equals("lobby-random", StringComparison.OrdinalIgnoreCase)
                    ? (o with { TeamPick = TeamPickMode.LobbyRandom }, null) : (o, $"--team-pick must be lobby-random (got '{value}')");
            case "teams":
                return PositiveInt(o, key, value, v => v > 8 ? null : o with { Teams = v });
            case "relations":
                return Enum.TryParse<RelationsPreset>(value, ignoreCase: true, out var r) && Enum.IsDefined(r) && r != RelationsPreset.None
                    ? (o with { Relations = r }, null) : (o, $"--relations must be coop|allied|ffa|twoteams (got '{value}')");
            case "economy":
                return Enum.TryParse<EconomyMode>(value, ignoreCase: true, out var em) && Enum.IsDefined(em) && em != EconomyMode.Off
                    ? (o with { Economy = em }, null) : (o, $"--economy must be idle|casual|heavy (got '{value}')");
            case "income-rate":
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate) && rate is >= 0 and <= 50
                    ? (o with { IncomeRate = rate }, null) : (o, $"--income-rate must be between 0 and 50 deltas per second and player (got '{value}')");
            case "admin-url":
                return Uri.TryCreate(value, UriKind.Absolute, out var adminUri) && adminUri.Scheme is "http" or "https"
                    ? (o with { AdminUrl = value.TrimEnd('/') }, null) : (o, $"--admin-url must be an http(s) URL (got '{value}')");
            case "admin-user":
                return value.Length > 0 ? (o with { AdminUser = value }, null) : (o, "--admin-user must not be empty");
            case "admin-password":
                return (o with { AdminPassword = value }, null);
            case "behavior":
                return Enum.TryParse<ClientBehavior>(value, ignoreCase: true, out var b) && Enum.IsDefined(b)
                    ? (o with { Behavior = b }, null) : (o, $"--behavior must be wander|patrol|explore (got '{value}')");
            default:
                return (o, $"unknown option --{key}");
        }
    }

    private static (CliOptions, string?) Milliseconds(CliOptions o, string key, string value, Func<double, CliOptions> apply) =>
        double.TryParse(value.Replace("ms", string.Empty, StringComparison.OrdinalIgnoreCase), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v is >= 0 and <= 60000
            ? (apply(v), null) : (o, $"--{key} must be a number of milliseconds between 0 and 60000 (got '{value}')");

    private static (CliOptions, string?) Seconds(CliOptions o, string key, string value, Func<double, CliOptions> apply) =>
        double.TryParse(value.TrimEnd('s'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v is >= 1 and <= 86400
            ? (apply(v), null) : (o, $"--{key} must be a number of seconds between 1 and 86400 (got '{value}')");

    private static (CliOptions, string?) Percent(CliOptions o, string key, string value, Func<double, CliOptions> apply) =>
        double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v is >= 0 and <= 100
            ? (apply(v), null) : (o, $"--{key} must be a percentage between 0 and 100 (got '{value}')");

    private static (CliOptions, string?) PositiveInt(CliOptions o, string key, string value, Func<int, CliOptions?> apply)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int v) || v < 1)
            return (o, $"--{key} must be a positive integer (got '{value}')");
        var next = apply(v);
        return next is null ? (o, $"--{key} value {v} is out of range") : (next, null);
    }
}

/// <summary>Offline galaxy statistics (the <c>galaxy</c> command and tests).</summary>
public sealed record GalaxyStats(int Sectors, int Clusters, int Links, int Highways, int Stations, int Ships, int MinShipsInSector, int MaxShipsInSector, int MaxDegree)
{
    public static GalaxyStats Of(FakeGalaxy g)
    {
        var perSector = g.Entities.Where(e => !e.IsStation).GroupBy(e => e.HomeSector).Select(x => x.Count()).ToList();
        return new GalaxyStats(
            g.Sectors.Count,
            g.Sectors.Select(s => s.ClusterNumber).Distinct().Count(),
            g.Links.Count,
            g.Links.Count(l => l.Kind == X4MP.Proto.LinkKind.Highway),
            g.StationCount,
            g.ShipCount,
            perSector.Min(),
            perSector.Max(),
            g.Sectors.Max(s => g.Neighbors(s.Index).Count));
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"sectors={Sectors} clusters={Clusters} links={Links} (highways {Highways}) stations={Stations} ships={Ships} ships/sector={MinShipsInSector}..{MaxShipsInSector} max-degree={MaxDegree}");
}
