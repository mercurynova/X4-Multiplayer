using Microsoft.Extensions.DependencyInjection.Extensions;

namespace X4MP.Server.Join;

/// <summary>Wires the join endpoints and their state (mod-management 4.1).</summary>
public static class JoinExtensions
{
    public static IServiceCollection AddJoinApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<JoinNonceStore>();
        services.TryAddSingleton<JoinModManifestBuilder>();
        return services;
    }

    public static IEndpointRouteBuilder MapJoinApi(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        JoinEndpoints.Map(routes);
        return routes;
    }
}
