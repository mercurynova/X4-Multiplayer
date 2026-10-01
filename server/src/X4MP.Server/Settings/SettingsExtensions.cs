using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using X4MP.Core.Economy;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Core.Settings;
using X4MP.Core.Teams;
using X4MP.Persistence;
using X4MP.Server.Api;
using X4MP.Server.Auth;

namespace X4MP.Server.Settings;

/// <summary>Wires the settings system (the hooks <c>ServerHost</c> calls) and maps <c>/api/v1/settings</c>.</summary>
public static class SettingsExtensions
{
    /// <summary>
    /// Adds the SQLite overrides configuration provider (last, so it wins) and registers the settings services and
    /// every settings section. Add new options classes in <see cref="AddSettingsSections"/>.
    /// </summary>
    public static WebApplicationBuilder AddServerSettings(this WebApplicationBuilder builder, PersistenceOptions persistence)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(persistence);

        var store = new SettingsOverridesStore(new SqliteConnectionFactory(persistence), TimeProvider.System);
        var provider = new SqliteOverridesConfigurationProvider(store);
        ((IConfigurationBuilder)builder.Configuration).Add(provider);

        var services = builder.Services;
        services.AddSingleton(store);
        services.AddSingleton(provider);
        services.TryAddSingleton<ISessionSettingsPusher, NullSessionSettingsPusher>();
        services.AddSettingsSections();
        services.AddSingleton(sp => new SettingsRegistry(sp.GetServices<SettingsSectionRegistration>().Select(s => s.OptionsType)));
        services.AddSingleton<SettingsService>();
        services.AddHostedService<SettingsReloadService>();
        return builder;
    }

    /// <summary>Every options class that carries <c>[Setting]</c> properties. One line per class.</summary>
    private static void AddSettingsSections(this IServiceCollection services)
    {
        services.AddSettingsSection<NetOptions>();
        services.AddSettingsSection<ReplicationOptions>();
        services.AddSettingsSection<AlertOptions>();
        services.AddSettingsSection<ModManagementOptions>();
        services.AddSettingsSection<SessionActorOptions>();
        services.AddSettingsSection<TeamOptions>();
        services.AddSettingsSection<EconomyOptions>();
    }

    /// <summary>
    /// Registers an options class with a <c>[SettingsSection]</c>: binds it from configuration (so
    /// <c>IOptionsMonitor&lt;T&gt;</c> follows overrides) and lists it in the schema.
    /// </summary>
    public static IServiceCollection AddSettingsSection<T>(this IServiceCollection services)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(services);
        var attribute = (SettingsSectionAttribute?)Attribute.GetCustomAttribute(typeof(T), typeof(SettingsSectionAttribute))
            ?? throw new InvalidOperationException($"{typeof(T).Name} has no [SettingsSection].");
        services.AddOptions<T>().BindConfiguration(attribute.ConfigPath);
        services.AddSingleton(new SettingsSectionRegistration(typeof(T), sp => sp.GetRequiredService<IOptionsMonitor<T>>().CurrentValue));
        return services;
    }

    public static IEndpointRouteBuilder MapSettingsApi(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var group = routes.MapGroup("/api/v1/settings");
        group.MapGet("/schema", (SettingsService settings) =>
            Results.Json(settings.GetSchema(), ApiJsonContext.Default.SettingsSchemaDto))
            .RequireAuthorization(AdminPolicies.Viewer);
        group.MapGet("", (SettingsService settings) =>
            Results.Json(settings.GetValues(), ApiJsonContext.Default.SettingsDto))
            .RequireAuthorization(AdminPolicies.Viewer);
        group.MapPatch("", PatchAsync).RequireAuthorization(AdminPolicies.Admin);
        return routes;
    }

    private static async Task<IResult> PatchAsync(
        Dictionary<string, JsonElement>? body, HttpContext context, SettingsService settings)
    {
        if (body is null || body.Count == 0)
        {
            return AdminAuthExtensions.Problem(StatusCodes.Status400BadRequest, "ValidationFailed", "Send a JSON object of setting keys and values.");
        }

        var actor = context.User.Identity?.Name ?? "unknown";
        var result = await settings.PatchAsync(body, actor, context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);
        if (!result.Success)
        {
            var problem = new SettingsProblem(
                "One or more settings were rejected; nothing was changed.",
                StatusCodes.Status400BadRequest,
                "ValidationFailed",
                null,
                result.Errors.ToList());
            return Results.Json(problem, ApiJsonContext.Default.SettingsProblem, "application/problem+json", StatusCodes.Status400BadRequest);
        }

        return Results.Json(settings.GetValues(), ApiJsonContext.Default.SettingsDto);
    }
}

/// <summary>Reloads the overrides once the database is migrated (the provider loads before the schema exists).</summary>
internal sealed class SettingsReloadService(SqliteOverridesConfigurationProvider provider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        provider.Reload();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
