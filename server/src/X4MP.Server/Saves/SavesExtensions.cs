using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using X4MP.Core.Economy;
using X4MP.Core.Events;
using X4MP.Core.Saves;
using X4MP.Core.Session;
using X4MP.Core.World;
using X4MP.Persistence;
using X4MP.Server.Hosting;

namespace X4MP.Server.Saves;

/// <summary>Registers the save service (M1-12), the HTTP fallback and the janitor (the single hook <c>ServerHost</c> calls).</summary>
public static class SavesExtensions
{
    /// <summary>
    /// Adds the <see cref="SaveService"/> as a singleton and as an <see cref="ISessionModule"/> (the <see cref="SessionActor"/> attaches
    /// every registered module), the content-addressed <see cref="SaveFileStore"/> under <c>&lt;data-dir&gt;/saves</c>, the SQLite catalog,
    /// the economy seeding hook and the hourly janitor. Options bind from <c>X4MP:Saves</c> (listed with the other settings sections).
    /// Register it after <c>AddWorldMirror</c> (the service reads the world mirror's journal) and <c>AddEconomy</c>.
    /// </summary>
    public static IServiceCollection AddSaves(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<SaveOptions>().BindConfiguration(SaveOptions.SectionName);
        services.TryAddSingleton(TimeProvider.System);

        // A server with a save service offers the HTTP fallback: say so in ServerHello.server_caps (the node negotiates it with its own bit).
        // Fake nodes use the bit to tell a real save service from a bare gateway.
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(X4MP.Core.Net.NetOptions)))
        {
            if (descriptor.ImplementationInstance is X4MP.Core.Net.NetOptions net)
            {
                net.ServerCaps |= (ulong)X4MP.Proto.Capability.SaveHttp;
            }
        }

        services.TryAddSingleton<ISaveCatalog>(sp => new SqliteSaveCatalog(sp.GetRequiredService<SqliteConnectionFactory>(), sp.GetRequiredService<PersistenceWriter>()));
        services.TryAddSingleton(sp => new SaveFileStore(sp.GetRequiredService<DataDirInfo>().Path));
        services.AddSingleton<ISaveSeedHook>(sp => new EconomySaveSeeder(
            () => sp.GetService<EconomyModule>()?.Service, sp.GetService<ILogger<EconomySaveSeeder>>()));
        services.AddSingleton(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<SaveOptions>>();
            return new SaveService(
                () => monitor.CurrentValue,
                sp.GetRequiredService<SaveFileStore>(),
                sp.GetRequiredService<ISaveCatalog>(),
                sp.GetRequiredService<WorldMirror>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<IEventPublisher>(),
                sp.GetServices<ISaveSeedHook>(),
                sp.GetService<ILogger<SaveService>>());
        });
        services.AddSingleton<ISessionModule>(sp => sp.GetRequiredService<SaveService>());
        services.AddSingleton(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<SaveOptions>>();
            var saves = sp.GetRequiredService<SaveService>();
            return new SaveJanitor(
                sp.GetRequiredService<SaveFileStore>(),
                sp.GetRequiredService<ISaveCatalog>(),
                () => monitor.CurrentValue,
                saves.ProtectedSha256,
                saves.IsPartInUse,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<ILogger<SaveJanitor>>());
        });
        services.AddSingleton<HttpUploads>();
        services.AddHostedService<SaveJanitorService>();
        return services;
    }
}

/// <summary>Runs the <see cref="SaveJanitor"/> a minute after start and then every <see cref="SaveOptions.JanitorIntervalMinutes"/>.</summary>
internal sealed partial class SaveJanitorService(SaveJanitor janitor, IOptionsMonitor<SaveOptions> options, ILogger<SaveJanitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    janitor.Run();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    LogFailed(ex);
                }

                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.JanitorIntervalMinutes)), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "the save janitor failed; it runs again next interval")]
    private partial void LogFailed(Exception ex);
}
