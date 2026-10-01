using Serilog;
using Serilog.Events;

namespace X4MP.Server.Logging;

/// <summary>Builds the Serilog pipeline: console, rolling file under the data dir, ring buffer, redaction.</summary>
public static class ServerLogging
{
    /// <summary>Console-only pipeline for the bootstrap logger (does not hold the log file open).</summary>
    public static LoggerConfiguration ConfigureBootstrap(LoggerConfiguration configuration, LoggingOptions options) =>
        configuration
            .MinimumLevel.Information()
            .Enrich.With<RedactionEnricher>()
            .WriteTo.Console(outputTemplate: options.ConsoleOutputTemplate);

    public static LoggerConfiguration Configure(
        LoggerConfiguration configuration,
        LoggingOptions options,
        RingBufferSink ringBuffer)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ringBuffer);

        Directory.CreateDirectory(options.LogsDir);

        return configuration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.With<RedactionEnricher>()
            .WriteTo.Console(outputTemplate: options.ConsoleOutputTemplate)
            .WriteTo.File(
                Path.Combine(options.LogsDir, options.FileName),
                outputTemplate: options.FileOutputTemplate,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: options.RetainedFileCount,
                fileSizeLimitBytes: options.FileSizeLimitBytes,
                rollOnFileSizeLimit: true)
            .WriteTo.Sink(ringBuffer);
    }
}
