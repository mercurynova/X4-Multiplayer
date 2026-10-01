using System.Text.Json;
using X4MP.Core.Settings;
using X4MP.Server.Api;
using X4MP.Server.Auth;

namespace X4MP.Server.Settings;

/// <summary>An options class registered as a settings section, plus how to read its effective value.</summary>
public sealed record SettingsSectionRegistration(Type OptionsType, Func<IServiceProvider, object> Current);

/// <summary>Outcome of <see cref="SettingsService.PatchAsync"/>.</summary>
public sealed record SettingsPatchResult(IReadOnlyList<SettingErrorDto> Errors)
{
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// Schema, current values and PATCH for the runtime settings (server-design 2.9). A PATCH is atomic: every key is
/// validated first, and either all overrides are written (then the configuration provider reloads, so
/// <c>IOptionsMonitor</c> consumers see the values at once) or none.
/// </summary>
public sealed partial class SettingsService(
    SettingsRegistry registry,
    SettingsOverridesStore store,
    SqliteOverridesConfigurationProvider provider,
    IEnumerable<SettingsSectionRegistration> sections,
    IServiceProvider services,
    ISessionSettingsPusher pusher,
    AdminStore audit,
    ILogger<SettingsService> logger)
{
    public const string Masked = "***";

    private readonly Dictionary<Type, SettingsSectionRegistration> _sections = sections.ToDictionary(s => s.OptionsType);
    private readonly Lock _patchGate = new();
    private long _version;

    public SettingsRegistry Registry => registry;

    public SettingsSchemaDto GetSchema() => new(registry.Descriptors.Select(d => new SettingSchemaDto(
        d.Key,
        d.Section,
        d.Name,
        d.Category,
        d.Description,
        SettingsRegistry.TypeName(d.Kind),
        d.Scope == SettingScope.Boot,
        d.Min,
        d.Max,
        d.MaxLength,
        d.EnumValues?.ToList(),
        d.Secret,
        d.PushToNodes,
        d.Secret ? null : SettingsRegistry.DefaultValue(d))).ToList());

    /// <summary>The effective value of a setting (the unmasked value; never send it to a client when secret).</summary>
    public JsonElement CurrentValue(SettingDescriptor setting) =>
        SettingsRegistry.ReadValue(setting, _sections[setting.OptionsType].Current(services));

    public SettingsDto GetValues()
    {
        var result = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
        foreach (var d in registry.Descriptors)
        {
            if (!result.TryGetValue(d.Section, out var section))
            {
                result[d.Section] = section = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            }

            section[d.Name] = Display(d, CurrentValue(d));
        }

        var overrides = store.ReadAll().Keys.Where(k => registry.TryGet(k, out _)).Order(StringComparer.Ordinal).ToList();
        return new SettingsDto(result, overrides);
    }

    /// <summary>Snapshot of the settings that nodes receive (<see cref="SettingAttribute.PushToNodes"/>).</summary>
    public SessionSettingsSnapshot GetSessionSettings() => BuildSnapshot(Interlocked.Read(ref _version));

    private SessionSettingsSnapshot BuildSnapshot(long version) => new(
        version,
        registry.Descriptors.Where(d => d.PushToNodes && !d.Secret).ToDictionary(d => d.Key, CurrentValue, StringComparer.Ordinal));

    /// <summary>Validates and applies a PATCH. A JSON null resets a key to its configured default.</summary>
    public async Task<SettingsPatchResult> PatchAsync(
        IReadOnlyDictionary<string, JsonElement> changes, string actor, string? remoteIp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var errors = new List<SettingErrorDto>();
        var accepted = new List<(SettingDescriptor Setting, string? ValueJson)>();

        foreach (var (key, value) in changes)
        {
            if (!registry.TryGet(key, out var setting))
            {
                errors.Add(new SettingErrorDto(key, "UnknownKey", $"{key} is not a known setting."));
                continue;
            }

            if (setting.Scope == SettingScope.Boot)
            {
                errors.Add(new SettingErrorDto(
                    setting.Key, "RestartRequired",
                    $"{setting.Key} is a boot setting: edit appsettings.json and restart the server (restart required)."));
                continue;
            }

            if (value.ValueKind == JsonValueKind.Null)
            {
                accepted.Add((setting, null));
                continue;
            }

            var check = SettingsRegistry.Validate(setting, value);
            if (check.IsValid)
            {
                accepted.Add((setting, check.Normalized.GetRawText()));
            }
            else
            {
                errors.Add(new SettingErrorDto(setting.Key, check.ErrorCode!, check.ErrorMessage!));
            }
        }

        if (errors.Count > 0 || accepted.Count == 0)
        {
            return new SettingsPatchResult(errors);
        }

        SessionSettingsSnapshot? push = null;
        lock (_patchGate)
        {
            var before = accepted.ToDictionary(a => a.Setting.Key, a => CurrentValue(a.Setting), StringComparer.Ordinal);
            store.Apply(accepted.ToDictionary(a => a.Setting.Key, a => a.ValueJson, StringComparer.Ordinal), actor);
            provider.Reload();

            foreach (var (setting, valueJson) in accepted)
            {
                var after = CurrentValue(setting);
                audit.Audit(actor, "settings.update", setting.Key, remoteIp, new Dictionary<string, string?>
                {
                    ["old"] = Redact(setting, before[setting.Key]),
                    ["new"] = Redact(setting, after),
                    ["reset"] = valueJson is null ? "true" : null,
                });
                LogChanged(setting.Key, actor);
            }

            if (accepted.Any(a => a.Setting.PushToNodes))
            {
                push = BuildSnapshot(Interlocked.Increment(ref _version));
            }
        }

        if (push is not null)
        {
            try
            {
                await pusher.PushAsync(push, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPushFailed(ex);
            }
        }

        return new SettingsPatchResult([]);
    }

    private static string Redact(SettingDescriptor setting, JsonElement value) =>
        setting.Secret ? Masked : value.GetRawText();

    private static JsonElement Display(SettingDescriptor setting, JsonElement value)
    {
        if (!setting.Secret)
        {
            return value;
        }

        var empty = value.ValueKind == JsonValueKind.Null || (value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 0);
        return empty ? value : JsonSerializer.SerializeToElement(Masked);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "setting {Key} changed by {Actor}")]
    private partial void LogChanged(string key, string actor);

    [LoggerMessage(Level = LogLevel.Error, Message = "pushing session settings to nodes failed")]
    private partial void LogPushFailed(Exception ex);
}
