using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using X4MP.Core.Economy;
using X4MP.Core.Events;
using X4MP.Core.Session;
using X4MP.Core.Teams;
using X4MP.Persistence;

namespace X4MP.Server.Economy;

/// <summary>Registers the economy (the single hook <c>ServerHost</c> calls).</summary>
public static class EconomyExtensions
{
    /// <summary>
    /// Adds the <see cref="EconomyModule"/> as an <see cref="ISessionModule"/> (the <see cref="SessionActor"/> attaches it),
    /// backed by <see cref="SqliteEconomyStore"/> with its own synchronous connection. Options bind from
    /// <c>X4MP:Economy</c> (Live settings follow <see cref="IOptionsMonitor{T}"/>). Team membership comes from an
    /// <see cref="ITeamDirectory"/> when the Teams module registered one; without it everybody is one implicit team.
    /// </summary>
    public static IServiceCollection AddEconomy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<EconomyOptions>().BindConfiguration(EconomyOptions.SectionName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEconomyStore>(sp => new SqliteEconomyStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        services.AddSingleton(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<EconomyOptions>>();
            return new EconomyModule(
                () => monitor.CurrentValue,
                sp.GetRequiredService<IEconomyStore>(),
                sp.GetService<ITeamDirectory>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<IEventPublisher>(),
                sp.GetService<ILogger<EconomyModule>>());
        });
        services.AddSingleton<ISessionModule>(sp => sp.GetRequiredService<EconomyModule>());
        return services;
    }
}
