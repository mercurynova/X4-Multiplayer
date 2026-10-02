using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace X4MP.LoadTests;

/// <summary>A running child process whose output goes to a log file; the whole tree is killed on dispose.</summary>
internal sealed class ChildProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _log;
    private readonly Task _pump;
    private readonly StringBuilder _tail = new();

    public ChildProcess(string exe, IEnumerable<string> args, string logPath, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        if (env is not null)
        {
            foreach (var (k, v) in env)
            {
                psi.Environment[k] = v;
            }
        }

        _log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        _process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        _process.OutputDataReceived += (_, e) => Append(e.Data);
        _process.ErrorDataReceived += (_, e) => Append(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _pump = Task.CompletedTask;
    }

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    public string Output
    {
        get
        {
            lock (_tail)
            {
                return _tail.ToString();
            }
        }
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_tail)
        {
            _log.WriteLine(line);
            _tail.AppendLine(line);
        }
    }

    public async Task WaitForExitAsync(CancellationToken ct) => await _process.WaitForExitAsync(ct).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }

        await _pump.ConfigureAwait(false);
        lock (_tail)
        {
            _log.Dispose();
        }

        _process.Dispose();
    }
}

/// <summary>Starts the server and the swarm, samples, summarises, writes the report and checks the budgets.</summary>
public static partial class Harness
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> RunAsync(Options o)
    {
        Directory.CreateDirectory(o.OutDir);
        string dataDir = Path.Combine(Path.GetTempPath(), "x4mp-load-" + Environment.ProcessId);
        if (Directory.Exists(dataDir))
        {
            Directory.Delete(dataDir, recursive: true);
        }

        Directory.CreateDirectory(dataDir);
        var budgets = Budgets.Load(o.BudgetsPath);
        Budgets.ApplyOverrides(budgets, o.BudgetOverrides);

        var serverEnv = new Dictionary<string, string>
        {
            // A swarm from one machine needs more than the default 4 connections per IP and 8 players.
            ["X4MP__Net__MaxConnectionsPerIp"] = "64",
            ["X4MP__Net__MaxPlayers"] = Math.Max(16, o.Clients + 4).ToString(CultureInfo.InvariantCulture),
            ["X4MP__Net__NodeTcpEndpoint"] = $"0.0.0.0:{o.TcpPort}",
            ["X4MP__Net__UdpPort"] = o.UdpPort.ToString(CultureInfo.InvariantCulture),
        };

        string baseUrl = $"http://127.0.0.1:{o.HttpPort}";
        var swarmStopwatch = new Stopwatch();
        string? failure = null;
        Dictionary<string, double[]> series = [];
        double entities = 0;
        double sectorsCaptured = 0;
        string swarmOutput = string.Empty;
        int swarmExit = -1;
        int samplesAvailable = 0;

        await using (var server = new ChildProcess(
            o.ServerExe!, ["--data-dir", dataDir, "--port", o.HttpPort.ToString(CultureInfo.InvariantCulture)],
            Path.Combine(o.OutDir, "server.log"), serverEnv))
        {
            try
            {
                using var http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(baseUrl) };
                http.DefaultRequestHeaders.Add("X-X4MP", "1");
                await LoginAsync(http, server, dataDir).ConfigureAwait(false);
                Console.WriteLine($"server up on {baseUrl}; starting the swarm: 1 authority + {o.Clients} clients, {o.Duration}s");

                var swarmArgs = new List<string>
                {
                    "swarm", "--clients", o.Clients.ToString(CultureInfo.InvariantCulture), "--with-authority", "--verify",
                    "--server", $"127.0.0.1:{o.TcpPort}",
                    "--sectors", o.Sectors.ToString(CultureInfo.InvariantCulture), "--ships", o.Ships.ToString(CultureInfo.InvariantCulture),
                    "--max-ships-per-sector", o.MaxShipsPerSector.ToString(CultureInfo.InvariantCulture),
                    "--duration", o.Duration.ToString(CultureInfo.InvariantCulture),
                };
                swarmArgs.AddRange(o.FakeNodeArgs);

                await using var swarm = new ChildProcess(o.FakeNodeExe!, swarmArgs, Path.Combine(o.OutDir, "fakenode.log"));
                swarmStopwatch.Start();

                // Read the metrics just before the swarm's clients leave, so the ring holds the whole run.
                var readAt = TimeSpan.FromSeconds(o.Duration - 3);
                using var cts = new CancellationTokenSource();
                var exited = swarm.WaitForExitAsync(cts.Token);
                var delay = Task.Delay(readAt, cts.Token);
                if (await Task.WhenAny(exited, delay).ConfigureAwait(false) == exited)
                {
                    failure = $"the swarm exited early after {swarmStopwatch.Elapsed.TotalSeconds:0}s with code {swarm.ExitCode}";
                }

                double elapsed = swarmStopwatch.Elapsed.TotalSeconds;
                if (failure is null || !server.HasExited)
                {
                    (series, entities, sectorsCaptured) = await FetchAsync(http).ConfigureAwait(false);
                }

                samplesAvailable = (int)Math.Min(elapsed, 600);
                int window = samplesAvailable - o.Warmup;
                if (window < 5)
                {
                    failure ??= "not enough measured seconds";
                }
                else
                {
                    series = series.ToDictionary(kv => kv.Key, kv => kv.Value.TakeLast(window).ToArray());
                }

                if (failure is null)
                {
                    await exited.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                }

                swarmOutput = swarm.Output;
                swarmExit = swarm.HasExited ? swarm.ExitCode : -1;
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidOperationException or IOException)
            {
                failure = ex.Message;
            }
        }

        try
        {
            Directory.Delete(dataDir, recursive: true);
        }
        catch (IOException)
        {
            // a sqlite file may still be closing; the temp dir is disposable
        }

        var report = Summarise(o, series, entities, sectorsCaptured, swarmOutput, swarmExit, failure, budgets);
        await File.WriteAllTextAsync(Path.Combine(o.OutDir, "report.json"), JsonSerializer.Serialize(report, Json)).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(o.OutDir, "report.md"), Markdown(report)).ConfigureAwait(false);
        Console.WriteLine(Markdown(report));
        Console.WriteLine($"report: {Path.Combine(o.OutDir, "report.md")}");
        return report.Passed ? 0 : 1;
    }

    public static int Evaluate(Options o)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(o.ReportPath!));
        var metrics = doc.RootElement.GetProperty("metrics").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());
        var budgets = Budgets.Load(o.BudgetsPath);
        Budgets.ApplyOverrides(budgets, o.BudgetOverrides);
        var violations = Budgets.Evaluate(budgets, metrics);
        foreach (var v in violations)
        {
            Console.WriteLine("BUDGET EXCEEDED: " + v);
        }

        Console.WriteLine(violations.Count == 0 ? "all budgets met" : $"{violations.Count} budget(s) exceeded");
        return violations.Count == 0 ? 0 : 1;
    }

    private static async Task LoginAsync(HttpClient http, ChildProcess server, string dataDir)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            if (server.HasExited)
            {
                throw new InvalidOperationException($"the server exited with {server.ExitCode} during start-up; see server.log");
            }

            try
            {
                if ((await http.GetAsync(new Uri("/healthz", UriKind.Relative)).ConfigureAwait(false)).IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch (HttpRequestException)
            {
                // not listening yet
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the server did not answer /healthz within 60 s");
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        string passwordFile = Path.Combine(dataDir, "initial-admin-password.txt");
        for (int i = 0; i < 40 && !File.Exists(passwordFile); i++)
        {
            await Task.Delay(250).ConfigureAwait(false);
        }

        string initial = (await File.ReadAllTextAsync(passwordFile).ConfigureAwait(false)).Trim();
        const string adminPassword = "Load-harness-only-password-12345";
        var login = await http.PostAsJsonAsync(new Uri("/api/v1/auth/login", UriKind.Relative), new { username = "admin", password = initial }).ConfigureAwait(false);
        login.EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync(new Uri("/api/v1/auth/change-password", UriKind.Relative), new { current = initial, @new = adminPassword }).ConfigureAwait(false))
            .EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync(new Uri("/api/v1/auth/login", UriKind.Relative), new { username = "admin", password = adminPassword }).ConfigureAwait(false))
            .EnsureSuccessStatusCode();
    }

    private static async Task<(Dictionary<string, double[]> Series, double Entities, double Sectors)> FetchAsync(HttpClient http)
    {
        using var metrics = JsonDocument.Parse(await http.GetStringAsync(new Uri("/api/v1/diagnostics/metrics?window=600", UriKind.Relative)).ConfigureAwait(false));
        var series = new Dictionary<string, double[]>();
        foreach (var s in metrics.RootElement.EnumerateArray())
        {
            series[s.GetProperty("name").GetString()!] = [.. s.GetProperty("samples").EnumerateArray().Select(x => x.GetDouble())];
        }

        using var dash = JsonDocument.Parse(await http.GetStringAsync(new Uri("/api/v1/dashboard", UriKind.Relative)).ConfigureAwait(false));
        return (series, dash.RootElement.GetProperty("entitiesInMirror").GetDouble(), dash.RootElement.GetProperty("sectorsCaptured").GetDouble());
    }

    [GeneratedRegex(@"(\w[\w-]*)=(\S+)")]
    private static partial Regex KeyValue();

    /// <summary>Parses the swarm's last line that starts with <paramref name="prefix"/> into its key=value pairs (values with a trailing unit keep their number).</summary>
    internal static Dictionary<string, double> ParseLine(string output, string prefix)
    {
        var result = new Dictionary<string, double>();
        var line = output.Split('\n').LastOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        if (line is null)
        {
            return result;
        }

        foreach (Match m in KeyValue().Matches(line))
        {
            var text = m.Groups[2].Value;
            int slash = text.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0)
            {
                text = text[..slash]; // "checksums-ok=44/44"
            }

            var digits = new string([.. text.TakeWhile(c => char.IsDigit(c) || c is '.' or '-')]);
            if (double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                result[m.Groups[1].Value] = v;
            }
        }

        return result;
    }
}
