using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using X4MP.Core.Interest;
using X4MP.Core.Replication;
using X4MP.Core.Session;
using X4MP.Core.Settings;
using X4MP.Core.World;

namespace X4MP.Server.Replication;

/// <summary>Registers replication (M1-08).</summary>
public static class ReplicationExtensions
{
    /// <summary>
    /// Adds the <see cref="ReplicationModule"/> as a singleton and as an <see cref="ISessionModule"/>. It listens to the
    /// <see cref="InterestManager"/> and reads the <see cref="WorldMirror"/>, so call it after <c>AddWorldMirror</c> and
    /// <c>AddInterestManager</c> (the module order is the registration order). Its options (<see cref="ReplicationOptions"/>,
    /// <c>X4MP:Replication</c>) are a settings section registered with the others; Live settings follow <see cref="IOptionsMonitor{T}"/>,
    /// so an edit applies on the next tick. It also sets <c>Welcome.max_ghosts</c> from <see cref="InterestOptions.MaxGhosts"/>.
    /// </summary>
    public static IServiceCollection AddReplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp =>
        {
            var replication = sp.GetRequiredService<IOptionsMonitor<ReplicationOptions>>();
            var interest = sp.GetRequiredService<IOptionsMonitor<InterestOptions>>();
            return new ReplicationModule(
                sp.GetRequiredService<WorldMirror>(),
                sp.GetRequiredService<InterestManager>(),
                () => replication.CurrentValue,
                () => interest.CurrentValue,
                sp.GetRequiredService<TimeProvider>(),
                logger: sp.GetService<ILogger<ReplicationModule>>());
        });
        services.AddSingleton<ISessionModule>(sp => sp.GetRequiredService<ReplicationModule>());
        return services;
    }
}
