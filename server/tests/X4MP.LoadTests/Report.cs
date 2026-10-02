using System.Globalization;
using System.Text;

namespace X4MP.LoadTests;

/// <summary>The result of one load run (serialised as report.json).</summary>
public sealed record LoadReport(
    DateTimeOffset At,
    string Machine,
    int Clients,
    int Sectors,
    int Ships,
    int DurationSeconds,
    int WarmupSeconds,
    int MeasuredSeconds,
    Dictionary<string, double> Metrics,
    Dictionary<string, double> Budgets,
    List<string> Violations,
    string? Failure,
    bool Passed,
    List<string> SwarmSummary);

public static partial class Harness
{
    private static double Pct(double[] values, double p)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(p / 100.0 * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    private static double[] Get(Dictionary<string, double[]> s, string name) => s.TryGetValue(name, out var v) ? v : [];

    private static double[] SumOf(Dictionary<string, double[]> s, params string[] names)
    {
        var parts = names.Select(n => Get(s, n)).Where(a => a.Length > 0).ToList();
        if (parts.Count == 0)
        {
            return [];
        }

        int len = parts.Min(p => p.Length);
        return [.. Enumerable.Range(0, len).Select(i => parts.Sum(p => p[i]))];
    }

    private static double Mean(double[] v) => v.Length == 0 ? 0 : v.Average();

    private static double Max(double[] v) => v.Length == 0 ? 0 : v.Max();

    internal static LoadReport Summarise(
        Options o, Dictionary<string, double[]> s, double entities, double sectors, string swarmOutput, int swarmExit, string? failure,
        Dictionary<string, double> budgets)
    {
        string[] lanes = ["control", "realtime", "bulk"];
        var bytesIn = SumOf(s, [.. lanes.Select(l => "net.bytes_in." + l)]);
        var bytesOut = SumOf(s, [.. lanes.Select(l => "net.bytes_out." + l)]);
        var queue = SumOf(s, [.. lanes.Select(l => "net.send_queue_bytes." + l)]);
        double nodes = Math.Max(1, Mean(Get(s, "net.connections")));
        var cpu = Get(s, "process.cpu_percent");
        var verify = ParseLine(swarmOutput, "verify: clients=");
        var summary = ParseLine(swarmOutput, "summary:");

        var m = new Dictionary<string, double>
        {
            ["tickP99Ms"] = Max(Get(s, "tick.p99_ms")),
            ["tickP99MsMedian"] = Pct(Get(s, "tick.p99_ms"), 50),
            ["tickP50Ms"] = Pct(Get(s, "tick.p50_ms"), 50),
            ["ticksPerSecond"] = Mean(Get(s, "tick.count")),
            ["cpuPercentMean"] = Mean(cpu),
            ["cpuPercentP95"] = Pct(cpu, 95),
            ["cpuPercentMax"] = Max(cpu),
            ["workingSetMb"] = Max(Get(s, "process.working_set_mb")),
            ["gcHeapMbMax"] = Max(Get(s, "gc.heap_mb")),
            ["gcGen0Collections"] = Math.Round(Get(s, "gc.gen0_collections").Sum()),
            ["gcGen2Collections"] = Math.Round(Get(s, "gc.gen2_collections").Sum()),
            ["gcAllocatedMbPerSecond"] = Mean(Get(s, "gc.allocated_bytes")) / (1024.0 * 1024.0),
            ["connectionsMean"] = nodes,
            ["bytesInPerNodePerSecond"] = Mean(bytesIn) / nodes,
            ["bytesOutPerNodePerSecond"] = Mean(bytesOut) / nodes,
            ["bytesInTotalPerSecond"] = Mean(bytesIn),
            ["bytesOutTotalPerSecond"] = Mean(bytesOut),
            ["framesInPerSecond"] = Mean(Get(s, "net.frames_in")),
            ["framesOutPerSecond"] = Mean(Get(s, "net.frames_out")),
            ["sendQueueBytes"] = Max(queue),
            ["droppedFrames"] = Math.Round(Get(s, "net.dropped").Sum()),
            ["coalescedFrames"] = Math.Round(Get(s, "net.coalesced").Sum()),
            ["violations"] = Math.Round(Get(s, "net.violations").Sum()),
            ["entitiesInMirror"] = entities,
            ["sectorsCaptured"] = sectors,
            ["nodesJoined"] = summary.GetValueOrDefault("joined"),
            ["fakeNodeErrors"] = summary.GetValueOrDefault("errors"),
            ["verifyErrors"] = verify.Count == 0 ? double.NaN : verify.GetValueOrDefault("errors") + verify.GetValueOrDefault("position-errors"),
            ["verifyEntriesChecked"] = verify.GetValueOrDefault("checked"),
            ["swarmExitCode"] = swarmExit,
        };
        if (double.IsNaN(m["verifyErrors"]))
        {
            m.Remove("verifyErrors"); // no verify line: the budget then fails as "not measured"
        }

        var violations = Budgets.Evaluate(budgets, m).Select(v => v.ToString()).ToList();
        var lines = swarmOutput.Split('\n').Where(l => l.StartsWith("verify: clients=", StringComparison.Ordinal) || l.StartsWith("summary:", StringComparison.Ordinal))
            .Select(l => l.TrimEnd()).ToList();
        int measured = Get(s, "tick.p99_ms").Length;
        return new LoadReport(
            DateTimeOffset.UtcNow, $"{Environment.MachineName} ({Environment.OSVersion.Platform}, {Environment.ProcessorCount} logical cores)",
            o.Clients, o.Sectors, o.Ships, o.Duration, o.Warmup, measured, m, budgets, violations, failure, failure is null && violations.Count == 0, lines);
    }

    internal static string Markdown(LoadReport r)
    {
        var inv = CultureInfo.InvariantCulture;
        string F(string key, string fmt = "0.00") => r.Metrics.TryGetValue(key, out var v) ? v.ToString(fmt, inv) : "n/a";
        var sb = new StringBuilder();
        sb.AppendLine(inv, $"# X4MP load report: {(r.Passed ? "PASS" : "FAIL")}");
        sb.AppendLine();
        sb.AppendLine(inv, $"- when: {r.At:u}; machine: {r.Machine}");
        sb.AppendLine(inv, $"- load: 1 authority + {r.Clients} clients, galaxy {r.Sectors} sectors / {r.Ships} ships; {r.DurationSeconds} s run, {r.WarmupSeconds} s warm-up, {r.MeasuredSeconds} s measured");
        sb.AppendLine(inv, $"- world: {F("entitiesInMirror", "0")} entities in the mirror, {F("sectorsCaptured", "0")} captured sectors, {F("connectionsMean", "0.0")} connections");
        if (r.Failure is not null)
        {
            sb.AppendLine(inv, $"- **run failure:** {r.Failure}");
        }

        sb.AppendLine();
        sb.AppendLine("| metric | value | budget |");
        sb.AppendLine("|---|---:|---|");
        string Budget(string metric) =>
            r.Budgets.TryGetValue(metric + "Max", out var mx) ? $"<= {mx.ToString("0.###", inv)}" :
            r.Budgets.TryGetValue(metric + "Min", out var mn) ? $">= {mn.ToString("0.###", inv)}" : string.Empty;
        void Row(string label, string key, string fmt = "0.00") => sb.AppendLine(inv, $"| {label} | {F(key, fmt)} | {Budget(key)} |");
        Row("tick p99, worst second (ms)", "tickP99Ms");
        Row("tick p99, median (ms)", "tickP99MsMedian");
        Row("tick p50, median (ms)", "tickP50Ms");
        Row("ticks per second", "ticksPerSecond", "0.0");
        Row("server CPU mean (% of one core)", "cpuPercentMean", "0.0");
        Row("server CPU p95 (% of one core)", "cpuPercentP95", "0.0");
        Row("server CPU max (% of one core)", "cpuPercentMax", "0.0");
        Row("working set max (MB)", "workingSetMb", "0.0");
        Row("GC heap max (MB)", "gcHeapMbMax", "0.0");
        Row("GC gen0 / gen2 collections", "gcGen0Collections", "0");
        Row("  gen2 collections", "gcGen2Collections", "0");
        Row("allocation rate (MB/s)", "gcAllocatedMbPerSecond", "0.0");
        Row("bytes in per node per second", "bytesInPerNodePerSecond", "0");
        Row("bytes out per node per second", "bytesOutPerNodePerSecond", "0");
        Row("bytes in total per second", "bytesInTotalPerSecond", "0");
        Row("bytes out total per second", "bytesOutTotalPerSecond", "0");
        Row("frames in / out per second", "framesInPerSecond", "0");
        Row("  frames out per second", "framesOutPerSecond", "0");
        Row("send queue depth max (bytes)", "sendQueueBytes", "0");
        Row("frames dropped", "droppedFrames", "0");
        Row("realtime frames coalesced", "coalescedFrames", "0");
        Row("protocol violations", "violations", "0");
        Row("nodes joined", "nodesJoined", "0");
        Row("entities in mirror", "entitiesInMirror", "0");
        Row("FakeNode errors", "fakeNodeErrors", "0");
        Row("replication verify errors", "verifyErrors", "0");
        Row("replication entries checked", "verifyEntriesChecked", "0");
        Row("swarm exit code", "swarmExitCode", "0");
        sb.AppendLine();
        foreach (var l in r.SwarmSummary)
        {
            sb.AppendLine(inv, $"    {l}");
        }

        if (r.Violations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Budgets exceeded");
            foreach (var v in r.Violations)
            {
                sb.AppendLine(inv, $"- {v}");
            }
        }

        return sb.ToString();
    }
}
