using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using X4MP.Core.Admin;
using X4MP.Core.Session;

namespace X4MP.Server.Admin;

/// <summary>Registers the in-game admin bridge (the <c>AdminCommand</c> handler: kick and ban).</summary>
public static class AdminExtensions
{
    /// <summary>
    /// Adds the <see cref="AdminModule"/> as the <see cref="ISessionModule"/> the <see cref="SessionActor"/> attaches. It needs the
    /// actor's driver (<see cref="ISessionNodeDriver"/>) and the player server's ban store, both registered by
    /// <c>AddNodeNetworking</c>; the admin API is a separate path and stays untouched. The module order is the registration order,
    /// and nothing here depends on another module, so it can be added after the relay.
    /// </summary>
    public static IServiceCollection AddAdmin(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new AdminModule(
            sp.GetRequiredService<IBanStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<AdminModule>>()));
        services.AddSingleton<ISessionModule>(sp => sp.GetRequiredService<AdminModule>());
        return services;
    }
}
