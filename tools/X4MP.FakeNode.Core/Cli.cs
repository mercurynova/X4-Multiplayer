using System.Globalization;
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

public enum FakeNodeCommand
{
    Authority,
    Client,
    Swarm,
    Inspect,
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
    public int Sectors { get; init; } = 152;
    public int Ships { get; init; } = 10225;
    public int TickRate { get; init; } = 20;
    public int Fps { get; init; } = 60;
    public ushort? Sector { get; init; }
    public int Seconds { get; init; } = 60;

    /// <summary>Live commands: stop after this many seconds; null = run until Ctrl+C.</summary>
    public int? Duration { get; init; }

    /// <summary>swarm: also start one authority node.</summary>
    public bool WithAuthority { get; init; }
    public string? Password { get; init; }

    /// <summary>client/swarm: send <c>AssetOrder</c>s for assets of this kind (default: none).</summary>
    public CommanderMode Commander { get; init; } = CommanderMode.None;

    /// <summary>authority: tag its ships with team owners (implied by <see cref="Commander"/>), so clients have team assets to command.</summary>
    public bool TeamAssets { get; init; }

    /// <summary>authority: size of the fake save it uploads on <c>RequestSave</c> (megabytes).</summary>
    public int SaveMb { get; init; } = 4;
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
          inspect     observer printing a sector           (not available yet)
          galaxy      offline: generate the galaxy and print its stats

        options:
          --server host:port   server address (default 127.0.0.1:47780)
          --seed N             universe seed (default 42)
          --clients K          swarm: number of fake clients (also --count)
          --name NAME          node name; --name-prefix PREFIX for several
          --behavior wander|patrol|explore
          --verify             check every Replication entry against the fake world's ground truth; exit code 1 on any error
          --udp                use the UDP realtime lane
          --sectors N --ships N --tick HZ --fps N   universe / authority shape
          --sector ID          inspect: sector index to observe
          --duration N         live commands: exit after N seconds (default: run until Ctrl+C; alias --seconds)
          --with-authority     swarm: also connect one authority node
          --password PW        session password
          --commander shared|own|foreign   clients send AssetOrders for teammates' / own team-common / another team's ships (0.5 s apart)
          --team-assets        authority: give its ships team owners (implied by --commander; start the authority with it when clients run elsewhere)
          --save-mb N          authority: size of the fake save it uploads (default 4)
        """;

    private static readonly Dictionary<string, FakeNodeCommand> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["authority"] = FakeNodeCommand.Authority,
        ["client"] = FakeNodeCommand.Client,
        ["swarm"] = FakeNodeCommand.Swarm,
        ["inspect"] = FakeNodeCommand.Inspect,
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

            bool isFlag = key is "verify" or "udp" or "with-authority" or "team-assets";
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

        if (command == FakeNodeCommand.Inspect && o.Sector is null)
            return Fail("inspect needs --sector <index>");
        if (command == FakeNodeCommand.Swarm && o.Clients < 1)
            return Fail("swarm needs --clients >= 1");
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
            case "name":
                return (o with { Name = value }, null);
            case "name-prefix":
                return (o with { NamePrefix = value }, null);
            case "password":
                return (o with { Password = value }, null);
            case "commander":
                return Enum.TryParse<CommanderMode>(value, ignoreCase: true, out var c) && Enum.IsDefined(c) && c != CommanderMode.None
                    ? (o with { Commander = c }, null) : (o, $"--commander must be shared|own|foreign (got '{value}')");
            case "behavior":
                return Enum.TryParse<ClientBehavior>(value, ignoreCase: true, out var b) && Enum.IsDefined(b)
                    ? (o with { Behavior = b }, null) : (o, $"--behavior must be wander|patrol|explore (got '{value}')");
            default:
                return (o, $"unknown option --{key}");
        }
    }

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
