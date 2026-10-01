using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using X4MP.Core.Economy;
using X4MP.Core.Session;
using X4MP.Proto;
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
            var module = new TeamModule(
                () => monitor.CurrentValue,
                sp.GetRequiredService<ITeamStore>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<ILogger<TeamModule>>(),
                loadStored: false); // the database is migrated by DatabaseStartup, which runs later; TeamsStartup loads
            // A moved player's view is delivered again; resolved lazily because replication is registered after the teams.
            module.ResyncPlayer = player => sp.GetService<X4MP.Core.Replication.ReplicationModule>()?.Resync(player, null) ?? false;
            // The economy half of SessionSettings: built from the live economy options (the Teams module does not know the economy).
            module.EconomySource = () => sp.GetService<IOptionsMonitor<EconomyOptions>>() is { } economy
                ? BuildEconomySettings(economy.CurrentValue, module.Teams.Count)
                : null;
            return module;
        });
        services.AddHostedService<TeamsStartup>(); // registered after DatabaseStartup, so it runs on a migrated database
        services.AddSingleton<ISessionModule>(sp => sp.GetRequiredService<TeamModule>());
        services.AddSingleton<ITeamDirectory>(sp => sp.GetRequiredService<TeamModule>());
        return services;
    }

    /// <summary>The wire <c>EconomySettings</c> for the options now: the configured credit mode and what <c>Auto</c> resolves to (one team = Shared).</summary>
    public static EconomySettingsT BuildEconomySettings(EconomyOptions options, int teamCount) => new()
    {
        CreditMode = options.CreditMode,
        EffectiveMode = options.CreditMode switch
        {
            CreditMode.Shared => EffectiveCreditMode.Shared,
            CreditMode.PerPlayer => EffectiveCreditMode.PerPlayer,
            _ => teamCount == 1 ? EffectiveCreditMode.Shared : EffectiveCreditMode.PerPlayer,
        },
        TeamPoolEnabled = options.TeamPoolEnabled,
        PoolWithdrawPolicy = options.PoolWithdrawPolicy,
        PoolWithdrawDailyLimit = options.PoolWithdrawDailyLimitPerPlayer,
        MaxTransferAmount = options.MaxSingleTransfer,
        AllowAlliedTransfers = options.AllowAlliedTransfers,
        DonateScope = options.DonateScope,
        LoanScope = options.LoanScope,
        MaxOpenLoansPerPlayer = (byte)Math.Clamp(options.MaxOpenLoansPerPlayer, 0, byte.MaxValue),
    };
}

/// <summary>Loads the stored teams once the database is migrated (the module is built before that happens).</summary>
internal sealed class TeamsStartup(TeamModule module) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        module.LoadStored(); // synchronous on purpose: the session actor is not running yet
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
