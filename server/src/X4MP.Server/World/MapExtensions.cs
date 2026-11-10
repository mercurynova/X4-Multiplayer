using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using X4MP.Core.Session;
using X4MP.Core.World;

namespace X4MP.Server.World;

/// <summary>Registers the live strategic map module (T1.6). Call it after <c>AddWorldMirror</c>.</summary>
public static class MapExtensions
{
    /// <summary>
    /// Adds the <see cref="MapModule"/> as a singleton and as an <see cref="ISessionModule"/>. It reads the world
    /// mirror's galaxy cache and forwards both the cached <c>GalaxyMetadata</c> and the live map to nodes.
    /// </summary>
    public static IServiceCollection AddStrategicMap(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new MapModule(
            sp.GetRequiredService<WorldMirror>(),
            sp.GetService<ILogger<MapModule>>()));
        services.AddSingleton<ISessionModule>(sp => sp.GetRequiredService<MapModule>());
        return services;
    }
}
