using System.Globalization;
using System.Text.Json;
using Google.FlatBuffers;
using X4MP.Proto;

namespace X4MP.SaveScan;

/// <summary>The command line of X4MP.SaveScan. <see cref="Run"/> never throws and never calls Environment.Exit (tests call it).</summary>
public static class SaveScanCli
{
    public const int ExitOk = 0;
    public const int ExitLeftovers = 1;
    public const int ExitError = 2;

    public const string Usage =
        """
        usage: X4MP.SaveScan <save.xml.gz | save.xml> [options]

          Lists the X4MP traces in an X4 save: "[MP] "-named objects, x4mp_team_* owned ships, the reference mod's
          x4mp_host / x4mp_client_* traces, the team factions and our MD cues. Exit code 0 = nothing unexpected,
          1 = leftovers (or an expected avatar is missing), 2 = usage / unreadable save.

          --manifest <file.x4mf>        the checkpoint manifest: its PlayerShip entries (avatars) are expected in the save
          --expect-avatar <idcode>      one more expected avatar idcode (repeatable)
          --expect-avatars-file <file>  expected avatar idcodes, one per line ('#' comments)
          --avatar-owner any|team|player  who must own an expected avatar (default any; a checkpoint: team, a client save: player)
          --allow-team-owned            team-owned ships without the "[MP] " prefix are not leftovers
          --no-require-avatars          an expected avatar missing from the save is not a problem
          --json <file | ->             write the JSON report there ('-' = stdout; the summary then goes to stderr)
          --quiet                       no human summary
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        string? save = null;
        string? json = null;
        string? manifest = null;
        string? avatarsFile = null;
        bool quiet = false;
        var expect = new ScanExpectations();
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a)
                {
                    case "-h" or "--help":
                        stdout.WriteLine(Usage);
                        return ExitOk;
                    case "--json":
                        json = Value(args, ref i, a);
                        break;
                    case "--manifest":
                        manifest = Value(args, ref i, a);
                        break;
                    case "--expect-avatar":
                        expect.AvatarIdcodes.Add(Value(args, ref i, a));
                        break;
                    case "--expect-avatars-file":
                        avatarsFile = Value(args, ref i, a);
                        break;
                    case "--avatar-owner":
                        expect.AvatarOwner = Value(args, ref i, a).ToLowerInvariant() switch
                        {
                            "any" => AvatarOwner.Any,
                            "team" => AvatarOwner.Team,
                            "player" => AvatarOwner.Player,
                            _ => throw new ArgumentException("--avatar-owner takes any, team or player"),
                        };
                        break;
                    case "--allow-team-owned":
                        expect.AllowTeamOwned = true;
                        break;
                    case "--no-require-avatars":
                        expect.RequireAvatars = false;
                        break;
                    case "--quiet":
                        quiet = true;
                        break;
                    default:
                        if (a.StartsWith("--", StringComparison.Ordinal) || save is not null)
                        {
                            throw new ArgumentException("unknown argument '" + a + "'");
                        }

                        save = a;
                        break;
                }
            }

            if (save is null)
            {
                throw new ArgumentException("no save file given");
            }

            if (manifest is not null)
            {
                foreach (string code in ManifestAvatars(manifest))
                {
                    expect.AvatarIdcodes.Add(code);
                }
            }

            if (avatarsFile is not null)
            {
                foreach (string line in File.ReadLines(avatarsFile))
                {
                    string t = line.Trim();
                    if (t.Length > 0 && t[0] != '#')
                    {
                        expect.AvatarIdcodes.Add(t);
                    }
                }
            }
        }
        catch (ArgumentException ex)
        {
            stderr.WriteLine("savescan: " + ex.Message);
            stderr.WriteLine(Usage);
            return ExitError;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            stderr.WriteLine("savescan: " + ex.Message);
            return ExitError;
        }

        ScanReport report;
        try
        {
            report = SaveScanner.ScanFile(save, expect);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
        {
            stderr.WriteLine("savescan: cannot read '" + save + "': " + ex.Message);
            return ExitError;
        }

        var summaryOut = json == "-" ? stderr : stdout;
        if (json is not null)
        {
            string text = JsonSerializer.Serialize(report, JsonOptions);
            if (json == "-")
            {
                stdout.WriteLine(text);
            }
            else
            {
                File.WriteAllText(json, text + "\n");
            }
        }

        if (!quiet)
        {
            WriteSummary(report, summaryOut);
        }

        return report.Summary.Ok ? ExitOk : ExitLeftovers;
    }

    private static string Value(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException(name + " needs a value");
        }

        return args[++i];
    }

    /// <summary>The idcodes of the manifest's avatar entries (origin PlayerShip).</summary>
    public static IReadOnlyList<string> ManifestAvatars(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 8 || bytes[4] != (byte)'X' || bytes[5] != (byte)'4' || bytes[6] != (byte)'M' || bytes[7] != (byte)'F')
        {
            throw new InvalidDataException("'" + path + "' is not an X4MF manifest");
        }

        var m = Manifest.GetRootAsManifest(new ByteBuffer(bytes));
        var codes = new List<string>();
        for (int i = 0; i < m.EntriesLength; i++)
        {
            var e = m.Entries(i);
            if (e is { } entry && entry.Origin == EntityOrigin.PlayerShip && !string.IsNullOrEmpty(entry.Idcode))
            {
                codes.Add(entry.Idcode);
            }
        }

        return codes;
    }

    public static void WriteSummary(ScanReport r, TextWriter w)
    {
        var s = r.Summary;
        w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"savescan: {r.File} ({(r.Gzip ? "gzip" : "plain xml")}), game {r.Game.Version}/{r.Game.Build}, save '{r.Game.SaveName}'"));
        w.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {s.ObjectsScanned} components scanned: {s.MpNamed} '[MP] ' named, {s.TeamOwned} team-owned, {s.ReferenceLeftovers} reference-mod traces; {s.ExpectedAvatars} expected avatar(s), {s.Unexpected} unexpected"));
        foreach (var o in r.Objects)
        {
            string tag = o.Expected ? "avatar    " : "LEFTOVER  ";
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {tag}{o.Idcode,-10} {o.Class,-8} '{o.Name}' owner={o.Owner} macro={o.Macro} sector={o.Sector} [{string.Join(",", o.Kinds)}]"));
        }

        foreach (var f in r.TeamFactions)
        {
            w.WriteLine("  faction   " + f.Id + (f.Active is null ? "" : " active=" + f.Active));
        }

        foreach (var f in r.ReferenceFactions)
        {
            w.WriteLine("  LEFTOVER  reference faction " + f.Id);
        }

        foreach (var c in r.Cues)
        {
            w.WriteLine("  cue       " + c.Name + (c.Variables.Count == 0 ? "" : " (" + string.Join(", ", c.Variables) + ")"));
        }

        foreach (string p in r.Problems)
        {
            w.WriteLine("  PROBLEM   " + p);
        }

        w.WriteLine(s.Ok ? "  result: OK" : "  result: LEFTOVERS FOUND");
    }
}
