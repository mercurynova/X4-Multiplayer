using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using X4MP.Core.Economy;
using X4MP.Core.Events;
using X4MP.Core.Interest;
using X4MP.Core.Permissions;
using X4MP.Core.Relay;
using X4MP.Core.Session;
using X4MP.Core.Teams;
using X4MP.Core.World;
using X4MP.Persistence;

namespace X4MP.Server.Relay;

/// <summary>Registers the relay and chat (M1-10; the single hook <c>ServerHost</c> calls).</summary>
public static class RelayExtensions
{
    /// <summary>
    /// Adds the <see cref="RelayModule"/> as a singleton, as the <see cref="ISessionModule"/> the <see cref="SessionActor"/> attaches
    /// and as the <see cref="IChatControl"/> the admin API will use (mute, admin messages). Chat and mutes persist through
    /// <see cref="SqliteChatStore"/>. It reads player states after the <see cref="WorldMirror"/> applied them and follows the
    /// <see cref="InterestManager"/> for event fan-out, so call it after <c>AddWorldMirror</c> and <c>AddInterestManager</c> (the
    /// module order is the registration order). Options bind from <c>X4MP:Relay</c>; the Live settings follow
    /// <see cref="IOptionsMonitor{T}"/>.
    /// </summary>
    public static IServiceCollection AddRelay(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<RelayOptions>().BindConfiguration(RelayOptions.SectionName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IChatStore>(sp => new SqliteChatStore(sp.GetRequiredService<SqliteConnectionFactory>(), sp.GetRequiredService<PersistenceWriter>()));
        services.TryAddSingleton(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<RelayOptions>>();
            var interest = sp.GetService<InterestManager>();
            var relay = new RelayModule(
                () => monitor.CurrentValue,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<IEventPublisher>(),
                sp.GetRequiredService<IChatStore>(),
                sp.GetService<WorldMirror>(),
                interest is null ? null : new InterestManagerRelayInterest(interest),
                sp.GetService<ILogger<RelayModule>>());

            // Asset permissions (server-design 2.13) need the teams: without a Teams module nothing is enforced.
            var mirror = sp.GetService<WorldMirror>();
            var teams = sp.GetService<ITeamDirectory>();
            var teamOptions = sp.GetService<IOptionsMonitor<TeamOptions>>();
            if (mirror is not null && teams is not null && teamOptions is not null)
            {
                relay.AssetPermissions = new AssetPermissionGate(
                    mirror, teams, () => teamOptions.CurrentValue, () => monitor.CurrentValue.ClaimRangeMetres, teamId => (teams as TeamModule)?.LeaderOf(teamId),
                    netId => sp.GetService<EconomyModule>()?.Service?.IsAssetLocked(netId) == true); // an asset in an open trade takes no orders
            }

            return relay;
        });
        services.AddSingleton<ISessionModule>(sp => sp.GetRequiredService<RelayModule>());
        services.AddSingleton<IChatControl>(sp => sp.GetRequiredService<RelayModule>());
        return services;
    }
}
