using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using X4MP.Persistence;

namespace X4MP.Server.Admin;

/// <summary>
/// Wires the admin REST API of server-design 4.4 (the hook <c>ServerHost</c> calls): server info, dashboard, sessions, players, bans,
/// chat, logs, diagnostics, galaxy, tokens and audit. The auth, settings, metrics and save endpoints have their own <c>Map*</c> hooks. Every
/// endpoint declares its role (<c>Viewer</c> reads, <c>Admin</c> mutations), answers errors as RFC 7807 problems (<see cref="Api.Problems"/>)
/// and audits every mutation (actor, action, target, reason; never a secret).
/// </summary>
public static class AdminApiExtensions
{
    public static IServiceCollection AddAdminApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new SqliteAdminQueries(sp.GetRequiredService<SqliteConnectionFactory>()));
        services.TryAddSingleton<AdminSessions>();
        services.TryAddSingleton(sp => new X4MP.Server.Economy.EconomyViews(
            sp.GetRequiredService<SqliteAdminQueries>(),
            sp.GetService<X4MP.Core.Teams.ITeamDirectory>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<X4MP.Core.Economy.EconomyOptions>>(),
            sp.GetRequiredService<TimeProvider>()));

        // A malformed body answers 400 (turned into a problem by the status code pages) instead of throwing.
        services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = false);
        return services;
    }

    public static IEndpointRouteBuilder MapAdminApi(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        SessionEndpoints.Map(routes);
        PlayerEndpoints.Map(routes);
        ChatLogEndpoints.Map(routes);
        DiagnosticsEndpoints.Map(routes);
        TokenEndpoints.Map(routes);
        EconomyEndpoints.Map(routes);
        return routes;
    }
}
