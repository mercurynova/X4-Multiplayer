using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using X4MP.Core.Interest;
using X4MP.Core.Session;
using X4MP.Core.Teams;
using X4MP.Core.World;

namespace X4MP.Server.World;

/// <summary>Registers the interest manager (M1-07).</summary>
public static class InterestManagerExtensions
{
    /// <summary>
    /// Adds the <see cref="InterestManager"/> as a singleton and as an <see cref="ISessionModule"/>. It watches the
    /// <see cref="WorldMirror"/>, so call <see cref="WorldMirrorExtensions.AddWorldMirror"/> first (the module order is the
    /// registration order). Options bind from <c>X4MP:Interest</c> and follow <see cref="IOptionsMonitor{T}"/>, so the Live settings
    /// take effect on the next tick.
    /// </summary>
    public static IServiceCollection AddInterestManager(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<InterestOptions>().BindConfiguration(InterestOptions.SectionName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<InterestOptions>>();
            var manager = new InterestManager(sp.GetRequiredService<WorldMirror>(), () => monitor.CurrentValue, sp.GetRequiredService<TimeProvider>());
            // M17: radar fog. The filter needs the viewer's team and position, and the team directory is an optional module (a module
            // test may register no teams at all), so both are properties rather than constructor arguments - they re-sync on change.
            manager.ViewerTeams = sp.GetService<ITeamDirectory>();
            manager.VisibilityFilter = new RadarVisibilityFilter();
            return manager;
        });
        services.AddSingleton<ISessionModule>(sp => sp.GetRequiredService<InterestManager>());
        return services;
    }
}
