using System.Net;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.FileProviders;
using Serilog;
using X4MP.Persistence;
using X4MP.Server.Api;
using X4MP.Server.Logging;
using X4MP.Server.Net;

namespace X4MP.Server.Hosting;

/// <summary>Builds the web application: data dir, logging, persistence, embedded SPA, /healthz.</summary>
public static partial class ServerHost
{
    public const int DefaultHttpPort = 47790;
    public const string DefaultServiceName = "X4MP";

    /// <summary>Paths that are never answered with the SPA's index.html.</summary>
    private static readonly string[] NonSpacePrefixes = ["/api", "/hubs", "/healthz", "/files"];

    public static WebApplication Build(string[] args, CliArguments cli, bool isService)
    {
        ArgumentNullException.ThrowIfNull(cli);
        var builder = WebApplication.CreateBuilder(args);

        // Resolution order: --data-dir, config X4MP:DataDir (used by tests and appsettings), X4MP_DATA_DIR env,
        // then the default for the run mode.
        var dataDir = DataDirResolver.Resolve(
            cli.DataDir ?? builder.Configuration["X4MP:DataDir"],
            Environment.GetEnvironmentVariable(DataDirResolver.EnvironmentVariable),
            isService,
            AppContext.BaseDirectory);

        var loggingOptions = new LoggingOptions();
        builder.Configuration.GetSection(LoggingOptions.SectionName).Bind(loggingOptions);
        loggingOptions.DataDir = dataDir;

        var persistenceOptions = new PersistenceOptions();
        builder.Configuration.GetSection("X4MP:Persistence").Bind(persistenceOptions);
        persistenceOptions.DataDir = dataDir;

        var ringBuffer = new RingBufferSink(loggingOptions.RingBufferCapacity);
        builder.Services.AddSingleton(loggingOptions);
        builder.Services.AddSingleton(ringBuffer);
        builder.Host.UseSerilog((_, _, configuration) =>
            ServerLogging.Configure(configuration, loggingOptions, ringBuffer));

        if (isService)
        {
            builder.Services.AddWindowsService(o => o.ServiceName = cli.ServiceName ?? DefaultServiceName);
        }

        builder.Services.AddSingleton(persistenceOptions);
        builder.Services.AddSingleton<SqliteConnectionFactory>();
        builder.Services.AddSingleton<MigrationRunner>(sp => new MigrationRunner(sp.GetRequiredService<SqliteConnectionFactory>()));
        builder.Services.AddSingleton<PersistenceWriter>();
        builder.Services.AddSingleton(new ServerInfo());
        builder.Services.AddSingleton(new DataDirInfo(dataDir));
        builder.Services.AddHostedService<DatabaseStartup>();
        builder.Services.AddHostedService<StartupBanner>();

        var admin = builder.Configuration.GetSection("X4MP:Admin");
        var port = cli.Port ?? admin.GetValue("Port", DefaultHttpPort);
        var allowRemote = admin.GetValue("AllowRemote", true);
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(allowRemote ? IPAddress.Any : IPAddress.Loopback, port));
        builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default));
        builder.Services.AddNodeNetworking(builder.Configuration);

        var app = builder.Build();
        MapWeb(app);
        return app;
    }

    internal const string GuiUnavailableHtml =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>X4MP</title></head><body>"
        + "<h1>GUI not available</h1><p>This x4mp-server build does not contain the web GUI. "
        + "The server itself is running; see <a href=\"/healthz\">/healthz</a>.</p></body></html>";

    /// <summary>The embedded GUI files; never throws (an unreadable or missing manifest yields an empty provider).</summary>
    internal static IFileProvider CreateGuiProvider(Microsoft.Extensions.Logging.ILogger logger)
    {
        try
        {
            return new ManifestEmbeddedFileProvider(typeof(ServerHost).Assembly, "wwwroot");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            LogGuiProviderFailed(logger, ex);
            return new NullFileProvider();
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Embedded GUI provider could not be created; the web GUI is unavailable")]
    private static partial void LogGuiProviderFailed(Microsoft.Extensions.Logging.ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Critical, Message = "FATAL: embedded GUI root 'wwwroot/index.html' is missing from the assembly; serving a built-in 'GUI not available' page. /healthz and the API still work.")]
    private static partial void LogGuiMissing(Microsoft.Extensions.Logging.ILogger logger);

    /// <summary>Embedded SPA, cache headers, /healthz and the SPA fallback.</summary>
    public static void MapWeb(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseSerilogRequestLogging();

        var embedded = CreateGuiProvider(app.Logger);
        if (!embedded.GetFileInfo("index.html").Exists)
        {
            LogGuiMissing(app.Logger);
        }

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = embedded,
            OnPrepareResponse = ctx =>
            {
                var headers = ctx.Context.Response.Headers;
                if (ctx.Context.Request.Path.StartsWithSegments("/assets", StringComparison.Ordinal))
                {
                    headers.CacheControl = "public, max-age=31536000, immutable";
                }
                else if (string.Equals(ctx.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
                {
                    headers.CacheControl = "no-cache";
                }
            },
        });

        app.MapGet("/healthz", (ServerInfo info) => Results.Json(
            new HealthzResponse(
                "ok",
                info.Version,
                new ProtocolRangeDto(ServerInfo.ProtocolMin, ServerInfo.ProtocolMax),
                (long)info.Uptime.TotalSeconds),
            ApiJsonContext.Default.HealthzResponse));

        app.MapFallback(async context =>
        {
            var path = context.Request.Path;
            var isSpaRoute = HttpMethods.IsGet(context.Request.Method)
                && !NonSpacePrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase))
                && !path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase); // a missing hashed asset is a 404, not HTML
            if (!isSpaRoute)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            var index = embedded.GetFileInfo("index.html");
            if (!index.Exists)
            {
                await context.Response.WriteAsync(GuiUnavailableHtml, context.RequestAborted);
                return;
            }

            await using var stream = index.CreateReadStream();
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
    }
}

/// <summary>The resolved data directory, available through DI.</summary>
public sealed record DataDirInfo(string Path);

/// <summary>Applies database migrations and starts the write-behind writer before the HTTP server starts.</summary>
internal sealed partial class DatabaseStartup(
    MigrationRunner migrations,
    IServiceProvider services,
    ILogger<DatabaseStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var version = migrations.Migrate();
        LogReady(version);
        _ = services.GetRequiredService<PersistenceWriter>(); // eager so shutdown flushes it
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Database ready at schema version {SchemaVersion}")]
    private partial void LogReady(int schemaVersion);
}

/// <summary>Logs where the server keeps its data, where the GUI is, and the plain-HTTP warning.</summary>
internal sealed partial class StartupBanner(
    DataDirInfo dataDir,
    ServerInfo info,
    IHostApplicationLifetime lifetime,
    IServiceProvider services,
    ILogger<StartupBanner> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(() =>
        {
            var addresses = services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                ?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses
                ?? [];
            var urls = string.Join(", ", addresses);
            LogStarted(info.Version, info.BuildHash, dataDir.Path, urls);
        });
        lifetime.ApplicationStopped.Register(() => LogStopped());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "x4mp-server {Version} ({Build}) data dir {DataDir}; GUI at {Urls}. Traffic is plain HTTP: use a trusted network or VPN.")]
    private partial void LogStarted(string version, string build, string dataDir, string urls);

    [LoggerMessage(Level = LogLevel.Information, Message = "shutdown complete")]
    private partial void LogStopped();
}
