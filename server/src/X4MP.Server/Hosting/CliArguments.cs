namespace X4MP.Server.Hosting;

public enum CliVerb
{
    Run,
    Version,
    ServiceInstall,
    ServiceUninstall,
    Help,
}

/// <summary>
/// Plain-args parser (no System.CommandLine, server-design 1.5). Anything it does not consume
/// (for example <c>--urls</c>) is left in <see cref="Remaining"/> for the ASP.NET Core host.
/// </summary>
public sealed record CliArguments(
    CliVerb Verb,
    string? DataDir,
    int? Port,
    bool ServiceFlag,
    string? ServiceName,
    string[] Remaining,
    string? Error)
{
    public static string Usage =>
        """
        Usage: x4mp-server [command] [options]

        Commands:
          run                      Run the server in the console (default).
          version                  Print server version, protocol range and build hash.
          service install          Install the Windows service (elevated).
          service uninstall        Stop and remove the Windows service (elevated).

        Options:
          --data-dir <dir>         Data directory (else X4MP_DATA_DIR, else ./data next to the exe,
                                   or %ProgramData%\X4MP as a service).
          --port <n>               HTTP port for the admin GUI (default 47790).
          --service                Run under the Windows service host (set by 'service install').
          --name <name>            Service name for service install/uninstall (default X4MP).
        """;

    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var verb = CliVerb.Run;
        string? dataDir = null;
        int? port = null;
        string? serviceName = null;
        var service = false;
        string? error = null;
        var remaining = new List<string>();

        var index = 0;
        if (args.Count > 0 && !args[0].StartsWith('-'))
        {
            switch (args[0].ToLowerInvariant())
            {
                case "run":
                    index = 1;
                    break;
                case "version":
                    verb = CliVerb.Version;
                    index = 1;
                    break;
                case "help":
                    verb = CliVerb.Help;
                    index = 1;
                    break;
                case "service":
                    index = 2;
                    var sub = args.Count > 1 ? args[1].ToLowerInvariant() : string.Empty;
                    if (sub == "install")
                    {
                        verb = CliVerb.ServiceInstall;
                    }
                    else if (sub == "uninstall")
                    {
                        verb = CliVerb.ServiceUninstall;
                    }
                    else
                    {
                        verb = CliVerb.Help;
                        error = "Expected 'service install' or 'service uninstall'.";
                    }
                    break;
                default:
                    verb = CliVerb.Help;
                    error = $"Unknown command '{args[0]}'.";
                    index = args.Count;
                    break;
            }
        }

        for (; index < args.Count; index++)
        {
            var arg = args[index];
            var (name, inline) = SplitInline(arg);
            switch (name)
            {
                case "--data-dir" or "--data":
                    dataDir = Value(args, ref index, inline, name, ref error);
                    break;
                case "--name":
                    serviceName = Value(args, ref index, inline, name, ref error);
                    break;
                case "--port":
                    var text = Value(args, ref index, inline, name, ref error);
                    if (text is not null)
                    {
                        if (int.TryParse(text, out var parsed) && parsed is > 0 and <= 65535)
                        {
                            port = parsed;
                        }
                        else
                        {
                            error ??= $"Invalid port '{text}'.";
                        }
                    }
                    break;
                case "--service":
                    service = true;
                    break;
                case "--help" or "-h" or "-?":
                    verb = CliVerb.Help;
                    break;
                default:
                    remaining.Add(arg);
                    break;
            }
        }

        return new CliArguments(verb, dataDir, port, service, serviceName, [.. remaining], error);
    }

    private static (string Name, string? Inline) SplitInline(string arg)
    {
        if (arg.StartsWith("--", StringComparison.Ordinal))
        {
            var eq = arg.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                return (arg[..eq], arg[(eq + 1)..]);
            }
        }
        return (arg, null);
    }

    private static string? Value(IReadOnlyList<string> args, ref int index, string? inline, string name, ref string? error)
    {
        if (inline is not null)
        {
            return inline;
        }
        if (index + 1 < args.Count)
        {
            index++;
            return args[index];
        }
        error ??= $"Option {name} needs a value.";
        return null;
    }
}
