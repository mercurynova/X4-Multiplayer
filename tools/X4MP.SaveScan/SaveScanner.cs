using System.IO.Compression;
using System.Xml;

namespace X4MP.SaveScan;

/// <summary>
/// Streams a save and reports the X4MP traces in it. The reader never builds a DOM (real saves are hundreds of MB). Assumed save shape, kept
/// deliberately loose because the fixtures are synthetic (verify against a real save at the first in-game sitting):
/// <c>&lt;component class="ship_s" macro="..." code="ABC-123" owner="x4mp_team_1" name="[MP] Alice" id="[0x1]"&gt;</c> nested under a
/// <c>&lt;component class="sector" macro="..."&gt;</c>; <c>&lt;faction id="..." active="..."/&gt;</c>; <c>&lt;cue name="x4mp..."&gt;</c> with variables
/// as descendant elements whose <c>name</c> starts with "$".
/// </summary>
public static class SaveScanner
{
    public const string MpPrefix = "[MP] ";
    private const string TeamFactionPrefix = "x4mp_team_";
    private const int MaxCueVariables = 64;

    /// <summary>Opens a file (gzip or plain XML) and scans it.</summary>
    public static ScanReport ScanFile(string path, ScanExpectations expectations)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
        return Scan(file, Path.GetFileName(path), expectations);
    }

    public static ScanReport Scan(Stream input, string label, ScanExpectations expectations)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(expectations);
        var head = new byte[2];
        int got = input.Read(head, 0, 2);
        bool gzip = got == 2 && head[0] == 0x1F && head[1] == 0x8B;
        if (!input.CanSeek)
        {
            throw new ArgumentException("the stream must be seekable", nameof(input));
        }

        input.Position = 0;
        using var gz = gzip ? new GZipStream(input, CompressionMode.Decompress, leaveOpen: true) : null;
        var raw = Read(gz ?? input);
        return Evaluate(raw, label, gzip, expectations);
    }

    private sealed record RawScan(
        int Components, List<ScanObject> Found, List<FactionTrace> Factions, List<CueTrace> Cues, GameInfo Game);

    private readonly record struct Ctx(int Depth, string Class, string Macro, string Name);

    private static RawScan Read(Stream source)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            CloseInput = false,
        };
        var found = new List<ScanObject>();
        var factions = new Dictionary<string, FactionTrace>(StringComparer.OrdinalIgnoreCase);
        var cues = new List<(string Name, List<string> Vars)>();
        var stack = new List<Ctx>();
        string version = "", build = "", saveName = "";
        int components = 0;
        int cueDepth = -1;
        List<string>? cueVars = null;
        bool sawRoot = false;

        using var reader = XmlReader.Create(source, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            int depth = reader.Depth;
            if (!sawRoot)
            {
                sawRoot = true;
                if (!string.Equals(reader.LocalName, "savegame", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("not an X4 save: the root element is <" + reader.LocalName + ">, expected <savegame>");
                }
            }

            while (stack.Count > 0 && stack[^1].Depth >= depth)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            if (cueVars is not null && depth <= cueDepth)
            {
                cueVars = null;
                cueDepth = -1;
            }

            switch (reader.LocalName)
            {
                case "game":
                    version = reader.GetAttribute("version") ?? version;
                    build = reader.GetAttribute("build") ?? build;
                    break;
                case "save":
                    saveName = reader.GetAttribute("name") ?? saveName;
                    break;
                case "faction":
                {
                    string id = reader.GetAttribute("id") ?? "";
                    if (IsOurFactionId(id) && !factions.ContainsKey(id))
                    {
                        factions[id] = new FactionTrace(id, reader.GetAttribute("active"));
                    }

                    break;
                }

                case "cue":
                {
                    string name = reader.GetAttribute("name") ?? "";
                    if (name.StartsWith("x4mp", StringComparison.OrdinalIgnoreCase) && !reader.IsEmptyElement)
                    {
                        cueVars = [];
                        cueDepth = depth;
                        cues.Add((name, cueVars));
                    }
                    else if (name.StartsWith("x4mp", StringComparison.OrdinalIgnoreCase))
                    {
                        cues.Add((name, []));
                    }

                    break;
                }

                case "component":
                    components++;
                    HandleComponent(reader, depth, stack, found);
                    break;
                default:
                    if (cueVars is not null && cueVars.Count < MaxCueVariables)
                    {
                        string? n = reader.GetAttribute("name");
                        if (n is not null && n.StartsWith('$'))
                        {
                            string? v = reader.GetAttribute("value");
                            cueVars.Add(v is null ? n : n + "=" + v);
                        }
                    }

                    break;
            }
        }

        if (!sawRoot)
        {
            throw new InvalidDataException("the save is empty");
        }

        return new RawScan(
            components,
            found,
            [.. factions.Values.OrderBy(f => f.Id, StringComparer.Ordinal)],
            [.. cues.Select(c => new CueTrace(c.Name, c.Vars))],
            new GameInfo(version, build, saveName));
    }

    private static void HandleComponent(XmlReader reader, int depth, List<Ctx> stack, List<ScanObject> found)
    {
        string cls = reader.GetAttribute("class") ?? "";
        string macro = reader.GetAttribute("macro") ?? "";
        string name = reader.GetAttribute("name") ?? "";
        string owner = reader.GetAttribute("owner") ?? "";
        string code = reader.GetAttribute("code") ?? "";
        string id = reader.GetAttribute("id") ?? "";
        string sector = "";
        for (int i = stack.Count - 1; i >= 0; i--)
        {
            if (string.Equals(stack[i].Class, "sector", StringComparison.Ordinal))
            {
                sector = stack[i].Macro.Length > 0 ? stack[i].Macro : stack[i].Name;
                break;
            }
        }

        if (!reader.IsEmptyElement)
        {
            stack.Add(new Ctx(depth, cls, macro, name));
        }

        var kinds = new List<string>(3);
        if (name.StartsWith(MpPrefix, StringComparison.Ordinal))
        {
            kinds.Add(ScanKinds.MpNamed);
        }

        if (owner.StartsWith(TeamFactionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            kinds.Add(ScanKinds.TeamOwned);
        }

        if (IsReferenceTrace(owner) || IsReferenceTrace(name))
        {
            kinds.Add(ScanKinds.Reference);
        }

        if (kinds.Count > 0)
        {
            found.Add(new ScanObject { Id = id, Class = cls, Name = name, Macro = macro, Idcode = code, Owner = owner, Sector = sector, Kinds = kinds });
        }
    }

    private static bool IsOurFactionId(string id) =>
        id.StartsWith(TeamFactionPrefix, StringComparison.OrdinalIgnoreCase) || IsReferenceTrace(id);

    /// <summary>The previous (reference) mod's traces: an x4mp_host or x4mp_client_* faction / owner / name.</summary>
    private static bool IsReferenceTrace(string text) =>
        text.Contains("x4mp_host", StringComparison.OrdinalIgnoreCase) || text.Contains("x4mp_client_", StringComparison.OrdinalIgnoreCase);

    private static ScanReport Evaluate(RawScan raw, string label, bool gzip, ScanExpectations expect)
    {
        var objects = new List<ScanObject>(raw.Found.Count);
        var seenAvatars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        int unexpected = 0, expectedCount = 0, mp = 0, team = 0, reference = 0;
        foreach (var o in raw.Found)
        {
            if (o.Kinds.Contains(ScanKinds.MpNamed))
            {
                mp++;
            }

            if (o.Kinds.Contains(ScanKinds.TeamOwned))
            {
                team++;
            }

            if (o.Kinds.Contains(ScanKinds.Reference))
            {
                reference++;
            }

            bool isExpected = o.Idcode.Length > 0 && expect.AvatarIdcodes.Contains(o.Idcode) && !o.Kinds.Contains(ScanKinds.Reference);
            bool tolerated = expect.AllowTeamOwned && o.Kinds.Count == 1 && o.Kinds[0] == ScanKinds.TeamOwned;
            if (isExpected)
            {
                expectedCount++;
                seenAvatars.Add(o.Idcode);
                if (expect.AvatarOwner == AvatarOwner.Team && !o.Owner.StartsWith(TeamFactionPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"avatar {o.Idcode} ({o.Name}) is owned by '{o.Owner}', expected a team faction ({TeamFactionPrefix}*)");
                }
                else if (expect.AvatarOwner == AvatarOwner.Player && !string.Equals(o.Owner, "player", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"avatar {o.Idcode} ({o.Name}) is owned by '{o.Owner}', expected the player");
                }
            }
            else if (!tolerated)
            {
                unexpected++;
            }

            objects.Add(o with { Expected = isExpected });
        }

        if (expect.RequireAvatars)
        {
            foreach (string code in expect.AvatarIdcodes.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!seenAvatars.Contains(code))
                {
                    problems.Add($"expected avatar {code} is not in the save");
                }
            }
        }

        foreach (var f in raw.Factions)
        {
            if (IsReferenceTrace(f.Id))
            {
                unexpected++;
                reference++;
            }
        }

        var summary = new ScanSummary
        {
            ObjectsScanned = raw.Components,
            MpNamed = mp,
            TeamOwned = team,
            ReferenceLeftovers = reference,
            ExpectedAvatars = expectedCount,
            Unexpected = unexpected,
            Problems = problems.Count,
            Ok = unexpected == 0 && problems.Count == 0,
        };
        return new ScanReport
        {
            File = label,
            Gzip = gzip,
            Game = raw.Game,
            Objects = objects,
            TeamFactions = [.. raw.Factions.Where(f => !IsReferenceTrace(f.Id))],
            ReferenceFactions = [.. raw.Factions.Where(f => IsReferenceTrace(f.Id))],
            Cues = raw.Cues,
            ExpectedAvatars = [.. expect.AvatarIdcodes.Order(StringComparer.OrdinalIgnoreCase)],
            Problems = problems,
            Summary = summary,
        };
    }
}
