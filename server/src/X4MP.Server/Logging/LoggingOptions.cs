namespace X4MP.Server.Logging;

/// <summary>
/// Boot-time logging settings (server-design 2.10). M0-11 will replace the data-dir default with
/// full data-dir resolution; only <see cref="DataDir"/> needs to change when it does.
/// </summary>
public sealed class LoggingOptions
{
    public const string SectionName = "X4MP:Logging";

    /// <summary>Server data directory. Placeholder until M0-11 resolves it properly.</summary>
    public string DataDir { get; set; } = "./data";

    /// <summary>Directory for rolling log files: <c>&lt;data-dir&gt;/logs</c>.</summary>
    public string LogsDir => Path.Combine(DataDir, "logs");

    /// <summary>Rolling file name pattern (Serilog inserts the date before the extension).</summary>
    public string FileName { get; set; } = "server-.log";

    public int RetainedFileCount { get; set; } = 14;

    public long FileSizeLimitBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>Capacity of the in-memory ring buffer feeding the GUI Logs page.</summary>
    public int RingBufferCapacity { get; set; } = 20_000;

    public string ConsoleOutputTemplate { get; set; } =
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}";

    public string FileOutputTemplate { get; set; } =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}";
}
