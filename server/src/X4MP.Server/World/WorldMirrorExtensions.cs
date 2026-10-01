using Microsoft.Extensions.DependencyInjection.Extensions;
using X4MP.Core.Session;
using X4MP.Core.World;
using X4MP.Persistence;

namespace X4MP.Server.World;

/// <summary>Registers the world mirror (M1-06).</summary>
public static class WorldMirrorExtensions
{
    /// <summary>
    /// Adds the <see cref="WorldMirror"/> as a singleton and as an <see cref="ISessionModule"/> (the
    /// <see cref="SessionActor"/> attaches every registered module), with its galaxy cache, journal and string table persisted
    /// through <see cref="SqliteWorldStore"/>. Register it before modules that observe it (the interest manager).
    /// </summary>
    public static IServiceCollection AddWorldMirror(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IWorldStore>(sp => new SqliteWorldStore(
            sp.GetRequiredService<SqliteConnectionFactory>(),
            sp.GetRequiredService<PersistenceWriter>(),
            () => sp.GetRequiredService<GatewayState>().SessionId));
        services.TryAddSingleton(sp => new WorldMirror(
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IWorldStore>(),
            sp.GetService<ILogger<WorldMirror>>()));
        services.AddSingleton<ISessionModule>(sp => sp.GetRequiredService<WorldMirror>());
        return services;
    }
}
