using System.Text.Json;

namespace X4MP.FakeNode;

/// <summary>One sector of a galaxy dump: its macro, its cluster macro (may be empty) and the macros of the sectors its gates lead to.</summary>
public sealed record GalaxyDumpSector(string Macro, string Cluster, IReadOnlyList<string> Gates);

/// <summary>
/// The galaxy dump the sitting-0 spike writes (<c>galaxy_dump</c> block, <c>tools/session4/extract-galaxy-dump.ps1</c>, S13.11) and
/// <c>--galaxy-file</c> reads:
/// <code>{"format":1,"generator":"...","sector_count":N,"sectors":[{"macro":"cluster_01_sector001_macro","cluster":"cluster_01_macro","gates":["cluster_01_sector002_macro"]}]}</code>
/// The gates name the destination sector macros only (the dump has no gate positions: FakeNode derives them). Unknown properties are ignored and
/// a missing <c>cluster</c> or <c>gates</c> is empty, so a hand-made file can be as small as <c>{"sectors":[{"macro":"a"}]}</c>.
/// </summary>
public sealed record GalaxyDump(IReadOnlyList<GalaxyDumpSector> Sectors, int UnknownGateTargets)
{
    /// <summary>Reads and checks a dump file. Returns the dump, or null and an error text.</summary>
    public static (GalaxyDump? Dump, string? Error) Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"cannot read galaxy file '{path}': {ex.Message}");
        }

        return Parse(text);
    }

    /// <summary>Parses dump JSON text.</summary>
    public static (GalaxyDump? Dump, string? Error) Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            return (null, $"galaxy file is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("sectors", out var sectors) || sectors.ValueKind != JsonValueKind.Array)
                return (null, "galaxy file has no 'sectors' array");

            var list = new List<GalaxyDumpSector>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in sectors.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("macro", out var m) || m.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(m.GetString()))
                    return (null, $"galaxy file: sector #{list.Count + 1} has no 'macro' string");
                string macro = m.GetString()!;
                if (!seen.Add(macro))
                    return (null, $"galaxy file: sector macro '{macro}' appears twice");
                string cluster = s.TryGetProperty("cluster", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : string.Empty;
                var gates = new List<string>();
                if (s.TryGetProperty("gates", out var g) && g.ValueKind == JsonValueKind.Array)
                {
                    foreach (var gate in g.EnumerateArray())
                    {
                        if (gate.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(gate.GetString()))
                            gates.Add(gate.GetString()!);
                    }
                }

                list.Add(new GalaxyDumpSector(macro, cluster, gates));
            }

            if (list.Count < 2)
                return (null, "galaxy file needs at least 2 sectors");
            int unknown = list.Sum(s => s.Gates.Count(t => !seen.Contains(t)));
            return (new GalaxyDump(list, unknown), null);
        }
    }
}
