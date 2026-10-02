using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using X4MP.Core.Economy;
using X4MP.Core.Events;
using X4MP.Core.Interest;
using X4MP.Core.Metrics;
using X4MP.Core.Net;
using X4MP.Core.Relay;
using X4MP.Core.Saves;
using X4MP.Core.Session;
using X4MP.Core.World;
using X4MP.Persistence;
using X4MP.Server.Admin;
using X4MP.Server.Api;
using X4MP.Server.Auth;
using X4MP.Server.Economy;
using X4MP.Server.Logging;
using X4MP.Server.Settings;
using X4MP.Server.Teams;

namespace X4MP.Server.Hubs;

/// <summary>Wires the admin SignalR hub (the hooks <c>ServerHost</c> calls).</summary>
public static class AdminHubExtensions
{
    /// <summary>
    /// Registers SignalR (System.Text.Json with the API's source-generated contexts, so the hub sends exactly what REST sends), the
    /// subscription registry, <see cref="AdminHubCore"/> and the <see cref="AdminBroadcaster"/>. Call after the session actor, the world
    /// mirror, the interest manager, the saves, the relay, the settings and the admin API are registered.
    /// </summary>
    public static IServiceCollection AddAdminHub(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<AdminHubOptions>().BindConfiguration(AdminHubOptions.SectionName);
        services.AddSignalR(o =>
            {
                o.MaximumReceiveMessageSize = 64 * 1024; // requests are small; a large frame from a browser is abuse
                o.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
                o.KeepAliveInterval = TimeSpan.FromSeconds(10);
            })
            .AddJsonProtocol(o =>
            {
                // The API's source-generated metadata first; the reflection resolver covers primitives (the sector id) and anything unregistered.
                o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
                o.PayloadSerializerOptions.TypeInfoResolverChain.Add(new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver());
            });

        services.TryAddSingleton<ActiveAlerts>();
        services.TryAddSingleton<TeamsPushState>();
        services.TryAddSingleton<AdminSubscriptions>();
        services.TryAddSingleton(sp => new DashboardBuilder(
            sp.GetRequiredService<AdminSessions>(),
            sp.GetRequiredService<NetOptions>(),
            sp.GetRequiredService<WorldMirror>(),
            sp.GetRequiredService<InterestManager>(),
            sp.GetRequiredService<MetricsSampler>(),
            sp.GetRequiredService<ActiveAlerts>()));
        services.TryAddSingleton(sp => new AdminHubCore(
            sp.GetRequiredService<AdminSubscriptions>(),
            sp.GetRequiredService<IHubContext<AdminHub, IAdminClient>>(),
            sp.GetRequiredService<SessionActor>(),
            sp.GetRequiredService<InterestManager>(),
            sp.GetRequiredService<WorldMirror>(),
            sp.GetRequiredService<AdminSessions>(),
            sp.GetRequiredService<DashboardBuilder>(),
            sp.GetRequiredService<RingBufferSink>(),
            sp.GetRequiredService<IChatControl>(),
            sp.GetRequiredService<AdminStore>(),
            sp.GetRequiredService<EconomyModule>(),
            sp.GetRequiredService<EconomyViews>(),
            sp.GetRequiredService<TeamViews>(),
            sp.GetRequiredService<TeamsPushState>(),
            sp.GetRequiredService<IOptionsMonitor<AdminHubOptions>>(),
            sp.GetRequiredService<ILogger<AdminHubCore>>()));
        services.TryAddSingleton(sp => new AdminBroadcaster(
            sp.GetRequiredService<IEventBus>(),
            sp.GetRequiredService<AdminSubscriptions>(),
            sp.GetRequiredService<SessionActor>(),
            sp.GetRequiredService<AdminSessions>(),
            sp.GetRequiredService<DashboardBuilder>(),
            sp.GetRequiredService<WorldMirror>(),
            sp.GetRequiredService<InterestManager>(),
            sp.GetRequiredService<SaveService>(),
            sp.GetRequiredService<RingBufferSink>(),
            sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<ActiveAlerts>(),
            sp.GetRequiredService<EconomyModule>(),
            sp.GetRequiredService<EconomyViews>(),
            sp.GetRequiredService<TeamViews>(),
            sp.GetRequiredService<X4MP.Core.Teams.TeamModule>(),
            sp.GetRequiredService<TeamsPushState>(),
            sp,
            sp.GetRequiredService<IOptionsMonitor<AdminHubOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<AdminBroadcaster>>()));
        services.AddHostedService(sp => sp.GetRequiredService<AdminBroadcaster>());
        return services;
    }

    /// <summary>Maps <c>/hubs/admin</c>. Authorization is declared on the hub (Viewer; <c>SendChat</c> needs Admin).</summary>
    public static IEndpointRouteBuilder MapAdminHub(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapHub<AdminHub>(AdminHubMethods.Route);
        return routes;
    }
}
