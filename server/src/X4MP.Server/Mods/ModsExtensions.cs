using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using X4MP.Core.Mods;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Core.Settings;
using X4MP.Persistence;
using X4MP.Proto;

namespace X4MP.Server.Mods;

/// <summary>Registers mod management (task M1-X3/X4): the stored mod policy, the report store, the views and the push of policy changes to the nodes.</summary>
public static class ModsExtensions
{
    /// <summary>
    /// Adds <see cref="SqliteModStore"/>, the <see cref="PersistentModPolicyProvider"/> (as <see cref="IModPolicyProvider"/> and <see cref="IModPolicyEditor"/>; the knobs come from
    /// the live <c>X4MP:Mods</c> settings until the first edit) and <see cref="ModPolicyPusher"/>. Call it before <c>AddNodeNetworking</c> so the gateway gets the persistent provider.
    /// </summary>
    public static IServiceCollection AddModManagement(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IModStore>(sp => new SqliteModStore(sp.GetRequiredService<SqliteConnectionFactory>(), sp.GetRequiredService<PersistenceWriter>()));
        services.TryAddSingleton<IModPolicyEditor>(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<ModManagementOptions>>();
            var net = sp.GetRequiredService<NetOptions>();
            return new PersistentModPolicyProvider(
                sp.GetRequiredService<IModStore>(),
                () =>
                {
                    var o = monitor.CurrentValue;
                    // The legacy NetOptions switch still downgrades enforcement to Warn.
                    return net.ExtensionsMismatchIsWarning && o.Enforcement == ModEnforcement.Strict
                        ? new ModManagementOptions { ModListVisibility = o.ModListVisibility, SourceMode = o.SourceMode, UnknownDefault = o.UnknownDefault, Enforcement = ModEnforcement.Warn }
                        : o;
                },
                sp.GetRequiredService<TimeProvider>());
        });
        services.TryAddSingleton<IModPolicyProvider>(sp => sp.GetRequiredService<IModPolicyEditor>());
        services.TryAddSingleton(sp => new ModViews(
            sp.GetRequiredService<IModPolicyEditor>(),
            sp.GetRequiredService<IModStore>(),
            sp.GetRequiredService<SessionActor>(),
            sp.GetRequiredService<GatewayState>(),
            sp.GetRequiredService<SqliteAdminQueries>(),
            sp.GetRequiredService<IOptionsMonitor<ModManagementOptions>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService<ModPolicyPusher>();
        return services;
    }
}

/// <summary>
/// Sends <c>ModPolicyChanged</c> to the connected nodes whenever the policy changes (an admin edit or a change of the <c>X4MP:Mods</c> settings). Nobody is kicked:
/// a node that no longer matches is judged at its next join (MM3).
/// </summary>
internal sealed class ModPolicyPusher(IModPolicyEditor editor, SessionActor actor, IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        editor.Changed += OnChanged;
        if (services.GetService<X4MP.Server.Settings.SettingsService>() is { } settings)
        {
            settings.Changed += OnSettingsChanged;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        editor.Changed -= OnChanged;
        if (services.GetService<X4MP.Server.Settings.SettingsService>() is { } settings)
        {
            settings.Changed -= OnSettingsChanged;
        }

        return Task.CompletedTask;
    }

    // The newest policy, not the event's: two edits close together may raise their events out of order.
    private void OnChanged(ModPolicyT policy)
    {
        _ = policy;
        actor.PushModPolicy(editor.Current);
    }

    /// <summary>A knob of <c>X4MP:Mods</c> changed: reading the policy adopts it (version up) and raises <see cref="IModPolicyEditor.Changed"/>.</summary>
    private void OnSettingsChanged(IReadOnlyList<string> keys)
    {
        if (keys.Any(k => k.StartsWith("Mods.", StringComparison.Ordinal)))
        {
            _ = editor.Current;
        }
    }
}
