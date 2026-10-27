using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Transport.Recording;

namespace X4MP.Server.Hosting;

/// <summary>
/// <c>record</c> command: the tooling half of M4-26..M4-28. It never starts a host, so it can run against a
/// recording while a server is live.
/// </summary>
/// <remarks>
/// <code>
/// record info    &lt;dir|file&gt;              header, part count, message counts
/// record verify  &lt;dir&gt; [--real-time] [--speed x]   replay the stream and re-encode every frame
/// record compare &lt;authority&gt; &lt;mirror&gt; [--streams]  divergence report (M4-28)
/// </code>
/// </remarks>
public static class RecordCommands
{
    /// <summary>Runs one <c>record</c> subcommand.</summary>
    /// <param name="args">Arguments after the <c>record</c> verb.</param>
    /// <param name="output">Standard output.</param>
    /// <param name="error">Standard error.</param>
    /// <returns>0 on success, 1 on a failed check, 2 on a usage error.</returns>
    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Count == 0)
        {
            error.WriteLine("record: expected 'info', 'verify' or 'compare'.");
            return 2;
        }

        string sub = args[0].ToLowerInvariant();
        List<string> positional = [];
        bool realTime = false;
        bool compareStreams = false;
        double speed = 1.0;
        for (int i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--real-time":
                    realTime = true;
                    break;
                case "--streams":
                    compareStreams = true;
                    break;
                case "--speed":
                    if (i + 1 < args.Count && double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed) && parsed > 0)
                    {
                        speed = parsed;
                    }
                    else
                    {
                        error.WriteLine("record: --speed needs a positive number.");
                        return 2;
                    }
                    break;
                default:
                    positional.Add(arg);
                    break;
            }
        }

        try
        {
            switch (sub)
            {
                case "info":
                    return positional.Count == 1 ? Info(positional[0], output, error) : Usage(error);
                case "verify":
                    return positional.Count == 1 ? Verify(positional[0], realTime, speed, output, error) : Usage(error);
                case "compare":
                    return positional.Count == 2 ? Compare(positional[0], positional[1], compareStreams, output, error) : Usage(error);
                default:
                    error.WriteLine($"record: unknown subcommand '{args[0]}'.");
                    return 2;
            }
        }
        catch (IOException ex)
        {
            error.WriteLine($"record: {ex.Message}");
            return 1;
        }
        catch (InvalidDataException ex)
        {
            error.WriteLine($"record: not a readable X4MR file: {ex.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            error.WriteLine($"record: {ex.Message}");
            return 1;
        }
    }

    private static int Usage(TextWriter error)
    {
        error.WriteLine("usage: record info <dir|file> | record verify <dir> [--real-time] [--speed x] | record compare <authority> <mirror> [--streams]");
        return 2;
    }

    private static int Info(string path, TextWriter output, TextWriter error)
    {
        var parts = RecordingReader.EnumerateSeries(path);
        if (parts.Count == 0)
        {
            error.WriteLine($"record: no {RecordingFormat.Extension} parts in '{path}'.");
            return 1;
        }

        long bytes = 0;
        foreach (string part in parts)
        {
            bytes += new FileInfo(part).Length;
        }

        var records = RecordingReader.ReadSeries(path);
        var stats = ReplayStats.Of(records);
        using var first = RecordingReader.Open(parts[0]);

        output.WriteLine($"parts={parts.Count} bytes={bytes} protocol=0x{first.Header.ProtocolStamp:X4} session=0x{first.Header.SessionSeed:X16}");
        output.WriteLine($"created={DateTimeOffset.FromUnixTimeMilliseconds((long)first.Header.CreatedUnixMs).UtcDateTime:yyyy-MM-dd HH:mm:ss}Z {stats.Describe()}");
        return 0;
    }

    private static int Verify(string path, bool realTime, double speed, TextWriter output, TextWriter error)
    {
        var parts = RecordingReader.EnumerateSeries(path);
        if (parts.Count == 0)
        {
            error.WriteLine($"record: no {RecordingFormat.Extension} parts in '{path}'.");
            return 1;
        }

        // ReadSeries already walks every header, payload and padding byte: a truncated or foreign file throws here.
        var records = RecordingReader.ReadSeries(path);

        // Re-encoding is the check the AC names: every payload must turn back into the frame it came from.
        int reencoded = 0;
        foreach (var record in records)
        {
            var frame = record.EncodeFrame();
            if (!FrameCodec.TryDecode(frame, out Frame decoded, out int consumed) || consumed != frame.Length || decoded.Type != record.Type || decoded.Lane != record.Lane)
            {
                error.WriteLine($"record: message {reencoded} ({record.Type}, t={record.TimeUs}us) does not re-encode");
                return 1;
            }

            reencoded++;
        }

        var replayer = new RecordingReplayer(records)
        {
            Mode = realTime ? ReplayMode.RealTime : ReplayMode.AsFastAsPossible,
            Speed = speed,
        };

        long started = Environment.TickCount64;
        replayer.RunAsync(static _ => { }, CancellationToken.None).GetAwaiter().GetResult();
        long elapsedMs = Environment.TickCount64 - started;

        output.WriteLine($"ok {replayer.Stats.Describe()} reencoded={reencoded} mode={(realTime ? "real-time" : "as-fast-as-possible")} speed={speed} elapsed_ms={elapsedMs}");
        return 0;
    }

    private static int Compare(string authorityPath, string mirrorPath, bool compareStreams, TextWriter output, TextWriter error)
    {
        var authority = RecordingReader.ReadSeries(authorityPath);
        var mirror = RecordingReader.ReadSeries(mirrorPath);
        if (authority.Count == 0 || mirror.Count == 0)
        {
            error.WriteLine("record: both recordings must contain at least one message.");
            return 1;
        }

        var report = RecordingDivergence.Compare(authority, mirror, compareStreams);
        output.WriteLine(report.Describe());
        if (!report.Identical)
        {
            error.WriteLine($"record: divergence {report.Divergence} (missing={report.Missing} extra={report.Extra} fields={report.FieldMismatches} payload={report.PayloadMismatches} first={report.FirstDivergenceIndex})");
            return 1;
        }

        return 0;
    }
}
