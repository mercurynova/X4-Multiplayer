using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using X4MP.Core.Session;
using X4MP.Core.Teams;
using X4MP.Persistence;

namespace X4MP.Server.Teams;

/// <summary>Registers the Teams module (the single hook <c>ServerHost</c> calls).</summary>
public static class TeamsExtensions
{
    /// <summary>
    /// Adds the <see cref="TeamModule"/> as a singleton, as the <see cref="ISessionModule"/> the
    /// <see cref="SessionActor"/> attaches and as the <see cref="ITeamDirectory"/> other modules read. Teams persist
    /// through <see cref="SqliteTeamStore"/>. Call it before <c>AddSessionActor</c> so the module is first in line.
    /// The <see cref="TeamOptions"/> section itself is listed with the other settings sections (SettingsExtensions).
    /// </summary>
    public static IServiceCollection AddTeams(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<TeamOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ITeamStore>(sp => new SqliteTeamStore(sp.GetRequiredService<SqliteConnectionFactory>(), sp.GetRequiredService<PersistenceWriter>()));
        services.TryAddSingleton(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<TeamOptions>>();
            return new TeamModule(
                () => monitor.CurrentValue,
                sp.GetRequiredService<ITeamStore>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<ILogger<TeamModule>>());
        });
        services.AddSingleton<ISessionModule>(sp => sp.GetRequiredService<TeamModule>());
        services.AddSingleton<ITeamDirectory>(sp => sp.GetRequiredService<TeamModule>());
        return services;
    }
}
