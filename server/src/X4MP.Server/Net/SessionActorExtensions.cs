using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using X4MP.Core.Events;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Core.Settings;
using X4MP.Persistence;

namespace X4MP.Server.Net;

/// <summary>Registers the <see cref="SessionActor"/> (the single hook <c>ServerHost</c> calls).</summary>
public static class SessionActorExtensions
{
    /// <summary>
    /// Adds the <see cref="SessionActor"/> as a singleton, makes it the gateway's <see cref="IAdmissionHandler"/>
    /// (replacing <see cref="DefaultAdmissionHandler"/> whichever order the two extensions run in), persists
    /// the session and player rows through <see cref="SqliteSessionStore"/>, publishes events on the
    /// <see cref="IEventPublisher"/> (the event bus, when registered), becomes the <see cref="ISessionSettingsPusher"/>
    /// and runs its loop as a hosted service. Options bind from <c>X4MP:Session</c> (Live settings follow
    /// <see cref="IOptionsMonitor{T}"/>). Any <see cref="ISessionModule"/> registered in DI is attached to the actor.
    /// </summary>
    public static IServiceCollection AddSessionActor(this IServiceCollection services, Action<SessionActorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<SessionActorOptions>().BindConfiguration(SessionActorOptions.SectionName);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => GatewayState.FromOptions(sp.GetRequiredService<NetOptions>()));
        services.TryAddSingleton<ISessionStore>(sp => new SqliteSessionStore(sp.GetRequiredService<SqliteConnectionFactory>(), sp.GetRequiredService<PersistenceWriter>()));
        services.TryAddSingleton(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<SessionActorOptions>>();
            return new SessionActor(
                () => monitor.CurrentValue,
                sp.GetRequiredService<NetOptions>(),
                sp.GetRequiredService<GatewayState>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ISessionStore>(),
                sp.GetService<IEventPublisher>(),
                sp.GetServices<ISessionModule>(),
                sp.GetService<ILogger<SessionActor>>())
            {
                ModPolicy = sp.GetService<X4MP.Core.Mods.IModPolicyProvider>(),
                ModStore = sp.GetService<X4MP.Core.Mods.IModStore>(),
            };
        });
        services.RemoveAll<IAdmissionHandler>();
        services.AddSingleton<IAdmissionHandler>(sp => sp.GetRequiredService<SessionActor>());
        services.RemoveAll<ISessionSettingsPusher>();
        services.AddSingleton<ISessionSettingsPusher>(sp => sp.GetRequiredService<SessionActor>());
        services.AddHostedService<SessionActorService>();
        return services;
    }
}

/// <summary>Runs the <see cref="SessionActor"/> mailbox loop for the lifetime of the host.</summary>
internal sealed class SessionActorService(SessionActor actor, IServiceProvider services) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Seed the node-relevant settings, so the first node to attach gets them (ServerSettingsUpdate) right after its Welcome.
        // Resolved here, not in the constructor: SettingsService itself depends on the actor (as the settings pusher).
        if (services.GetService<X4MP.Server.Settings.SettingsService>() is { } settings)
        {
            await actor.PushAsync(settings.GetSessionSettings(), stoppingToken).ConfigureAwait(false);
        }

        await actor.RunAsync(stoppingToken).ConfigureAwait(false);
    }
}
