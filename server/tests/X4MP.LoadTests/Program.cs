using System.Globalization;
using System.Text.Json;
using X4MP.LoadTests;

// X4MP load harness (M1-C2, server-design 2.12). See Usage below.
const string Usage = """
    x4mp load harness: starts the real server exe and a FakeNode swarm (1 authority + N clients), samples the server's
    metrics for the run, writes report.json + report.md and exits 1 when a budget is exceeded.

    run [options]
      --server-exe <path>       the x4mp-server executable (published or built)           (required)
      --fakenode-exe <path>     the FakeNode executable                                   (required)
      --clients <n>             client nodes besides the authority                        (default 16)
      --duration <s>            swarm run time, at most 600 (the server keeps 600 samples) (default 180)
      --warmup <s>              seconds ignored at the start of the run (joins, ramp-up) (default 40)
      --sectors <n> --ships <n> --max-ships-per-sector <n>
                                fake galaxy density; ~20k entities end up in the mirror    (default 14 / 100000 / 2600)
      --budgets <file>          budgets (default: budgets.json next to the harness)
      --budget key=value        override one budget (repeatable), e.g. --budget tickP99MsMax=0.001
      --out <dir>               report directory (default ./load-report)
      --http-port/--tcp-port/--udp-port <n>   server ports (default 47990 / 47980 / 47981, not the product defaults)
      --fakenode-arg <arg>      extra argument for the swarm (repeatable), e.g. --fakenode-arg --slow-reader
                                (a value goes as --fakenode-arg=--slow-reader=1000 or as separate repeats)
    evaluate --report <report.json> [--budgets <file>] [--budget key=value]...
      re-checks a finished run's metrics against budgets (no server needed).

    Exit codes: 0 ok, 1 budget exceeded or the run failed, 2 bad usage.
    """;

try
{
    var (verb, options) = Cli.Parse(args);
    switch (verb)
    {
        case "run":
            return await Harness.RunAsync(options);
        case "evaluate":
            return Harness.Evaluate(options);
        default:
            Console.WriteLine(Usage);
            return verb == "help" ? 0 : 2;
    }
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Usage);
    return 2;
}

namespace X4MP.LoadTests
{
    /// <summary>Parsed command line of the harness.</summary>
    public sealed class Options
    {
        public string? ServerExe { get; set; }
        public string? FakeNodeExe { get; set; }
        public int Clients { get; set; } = 16;
        public int Duration { get; set; } = 180;
        public int Warmup { get; set; } = 40;
        public int Sectors { get; set; } = 14;
        public int Ships { get; set; } = 100000;
        public int MaxShipsPerSector { get; set; } = 2600;
        public string BudgetsPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "budgets.json");
        public List<string> BudgetOverrides { get; } = [];
        public string OutDir { get; set; } = Path.GetFullPath("load-report");
        public int HttpPort { get; set; } = 47990;
        public int TcpPort { get; set; } = 47980;
        public int UdpPort { get; set; } = 47981;
        public List<string> FakeNodeArgs { get; } = [];
        public string? ReportPath { get; set; }
    }

    public static class Cli
    {
        public static (string Verb, Options Options) Parse(string[] args)
        {
            var o = new Options();
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                return ("help", o);
            }

            string verb = args[0];
            if (verb is not ("run" or "evaluate"))
            {
                throw new ArgumentException($"unknown command '{verb}'");
            }

            for (int i = 1; i < args.Length; i++)
            {
                string key = args[i];
                if (!key.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"unexpected argument '{key}'");
                }

                string? value = null;
                int eq = key.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    value = key[(eq + 1)..];
                    key = key[..eq];
                }

                if (value is null)
                {
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException($"{key} needs a value");
                    }

                    value = args[++i];
                }

                switch (key)
                {
                    case "--server-exe": o.ServerExe = Path.GetFullPath(value); break;
                    case "--fakenode-exe": o.FakeNodeExe = Path.GetFullPath(value); break;
                    case "--clients": o.Clients = Int(key, value); break;
                    case "--duration": o.Duration = Int(key, value); break;
                    case "--warmup": o.Warmup = Int(key, value); break;
                    case "--sectors": o.Sectors = Int(key, value); break;
                    case "--ships": o.Ships = Int(key, value); break;
                    case "--max-ships-per-sector": o.MaxShipsPerSector = Int(key, value); break;
                    case "--budgets": o.BudgetsPath = Path.GetFullPath(value); break;
                    case "--budget": o.BudgetOverrides.Add(value); break;
                    case "--out": o.OutDir = Path.GetFullPath(value); break;
                    case "--http-port": o.HttpPort = Int(key, value); break;
                    case "--tcp-port": o.TcpPort = Int(key, value); break;
                    case "--udp-port": o.UdpPort = Int(key, value); break;
                    case "--fakenode-arg": o.FakeNodeArgs.Add(value); break;
                    case "--report": o.ReportPath = value; break;
                    default: throw new ArgumentException($"unknown option '{key}'");
                }
            }

            if (verb == "run")
            {
                if (o.ServerExe is null || o.FakeNodeExe is null)
                {
                    throw new ArgumentException("run needs --server-exe and --fakenode-exe");
                }

                if (o.Duration is < 20 or > 600)
                {
                    throw new ArgumentException("--duration must be 20..600 seconds");
                }

                if (o.Warmup < 0 || o.Warmup >= o.Duration - 10)
                {
                    throw new ArgumentException("--warmup must leave at least 10 measured seconds");
                }

                if (o.Clients < 1)
                {
                    throw new ArgumentException("--clients must be >= 1");
                }
            }
            else if (o.ReportPath is null)
            {
                throw new ArgumentException("evaluate needs --report");
            }

            return (verb, o);
        }

        private static int Int(string key, string value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : throw new ArgumentException($"{key} expects an integer, got '{value}'");
    }
}
