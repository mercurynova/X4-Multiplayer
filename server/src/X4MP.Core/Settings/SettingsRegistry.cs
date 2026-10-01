using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace X4MP.Core.Settings;

/// <summary>Outcome of validating one proposed value.</summary>
public readonly record struct SettingValidation(bool IsValid, string? ErrorCode, string? ErrorMessage, JsonElement Normalized)
{
    public static SettingValidation Ok(JsonElement normalized) => new(true, null, null, normalized);

    public static SettingValidation Fail(string code, string message) => new(false, code, message, default);
}

/// <summary>
/// Reflects <see cref="SettingAttribute"/>-marked options classes into <see cref="SettingDescriptor"/>s and
/// validates proposed values against them. No ASP.NET dependency: this lives in Core.
/// </summary>
public sealed class SettingsRegistry
{
    public const string KeyPrefix = "X4MP:";

    private readonly Dictionary<string, SettingDescriptor> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public SettingsRegistry(IEnumerable<Type> optionTypes)
    {
        ArgumentNullException.ThrowIfNull(optionTypes);
        var all = new List<SettingDescriptor>();
        foreach (var type in optionTypes.Distinct())
        {
            all.AddRange(Describe(type));
        }

        foreach (var d in all)
        {
            if (!_byKey.TryAdd(d.Key, d))
            {
                throw new InvalidOperationException($"Duplicate setting key '{d.Key}' ({_byKey[d.Key].OptionsType.Name} and {d.OptionsType.Name}).");
            }
        }

        Descriptors = all.OrderBy(d => d.Category, StringComparer.Ordinal).ThenBy(d => d.Key, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<SettingDescriptor> Descriptors { get; }

    public bool TryGet(string key, out SettingDescriptor descriptor) => _byKey.TryGetValue(key, out descriptor!);

    /// <summary>Descriptors declared by one options class.</summary>
    public static IEnumerable<SettingDescriptor> Describe(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var section = type.GetCustomAttribute<SettingsSectionAttribute>()
            ?? throw new InvalidOperationException($"{type.Name} has no [SettingsSection].");
        var path = section.ConfigPath;
        var sectionName = path.StartsWith(KeyPrefix, StringComparison.Ordinal) ? path[KeyPrefix.Length..] : path;
        sectionName = sectionName.Replace(':', '.');
        var defaults = Activator.CreateInstance(type)!;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attribute = property.GetCustomAttribute<SettingAttribute>();
            if (attribute is null)
            {
                continue;
            }

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var kind = KindOf(propertyType)
                ?? throw new InvalidOperationException($"{type.Name}.{property.Name}: unsupported setting type {propertyType.Name}.");
            yield return new SettingDescriptor
            {
                Key = sectionName + "." + property.Name,
                ConfigPath = path + ":" + property.Name,
                Name = property.Name,
                Section = sectionName,
                Category = attribute.Category ?? section.Category,
                Description = attribute.Description,
                Kind = kind,
                Scope = attribute.Scope,
                Min = double.IsNaN(attribute.Min) ? null : attribute.Min,
                Max = double.IsNaN(attribute.Max) ? null : attribute.Max,
                MaxLength = attribute.MaxLength > 0 ? attribute.MaxLength : null,
                EnumValues = kind == SettingKind.Choice ? Enum.GetNames(propertyType) : null,
                Secret = attribute.Secret,
                PushToNodes = attribute.PushToNodes,
                Default = property.GetValue(defaults),
                OptionsType = type,
                Property = property,
            };
        }
    }

    private static SettingKind? KindOf(Type type)
    {
        if (type == typeof(bool))
        {
            return SettingKind.Boolean;
        }

        if (type == typeof(int))
        {
            return SettingKind.WholeNumber;
        }

        if (type == typeof(long))
        {
            return SettingKind.WholeNumber64;
        }

        if (type == typeof(double) || type == typeof(float))
        {
            return SettingKind.Number;
        }

        if (type == typeof(string))
        {
            return SettingKind.Text;
        }

        if (type.IsEnum)
        {
            return SettingKind.Choice;
        }

        if (type == typeof(List<string>) || type == typeof(string[]))
        {
            return SettingKind.TextList;
        }

        return null;
    }

    /// <summary>Wire type name used by the schema endpoint.</summary>
    public static string TypeName(SettingKind kind) => kind switch
    {
        SettingKind.Boolean => "bool",
        SettingKind.WholeNumber => "int",
        SettingKind.WholeNumber64 => "long",
        SettingKind.Number => "double",
        SettingKind.Text => "string",
        SettingKind.Choice => "enum",
        _ => "stringList",
    };

    /// <summary>Validates a JSON value for a setting. A JSON <c>null</c> is not valid here: callers treat it as "reset".</summary>
    public static SettingValidation Validate(SettingDescriptor setting, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(setting);
        switch (setting.Kind)
        {
            case SettingKind.Boolean:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? Normalized(value)
                    : Fail(setting, "InvalidType", "must be true or false");

            case SettingKind.WholeNumber:
            case SettingKind.WholeNumber64:
            {
                if (value.ValueKind != JsonValueKind.Number)
                {
                    return Fail(setting, "InvalidType", "must be a whole number");
                }

                bool fits = setting.Kind == SettingKind.WholeNumber ? value.TryGetInt32(out _) : value.TryGetInt64(out _);
                if (!fits)
                {
                    return Fail(setting, "InvalidType", "must be a whole number in range for " + (setting.Kind == SettingKind.WholeNumber ? "a 32-bit integer" : "a 64-bit integer"));
                }

                return CheckRange(setting, value.GetDouble(), value);
            }

            case SettingKind.Number:
                return value.ValueKind == JsonValueKind.Number
                    ? CheckRange(setting, value.GetDouble(), value)
                    : Fail(setting, "InvalidType", "must be a number");

            case SettingKind.Text:
            {
                if (value.ValueKind != JsonValueKind.String)
                {
                    return Fail(setting, "InvalidType", "must be a string");
                }

                if (setting.MaxLength is { } max && value.GetString()!.Length > max)
                {
                    return Fail(setting, "TooLong", $"must be at most {max} characters");
                }

                return Normalized(value);
            }

            case SettingKind.Choice:
            {
                if (value.ValueKind != JsonValueKind.String)
                {
                    return Fail(setting, "InvalidType", "must be one of: " + string.Join(", ", setting.EnumValues!));
                }

                var text = value.GetString();
                var match = setting.EnumValues!.FirstOrDefault(v => string.Equals(v, text, StringComparison.OrdinalIgnoreCase));
                return match is null
                    ? Fail(setting, "UnknownValue", "must be one of: " + string.Join(", ", setting.EnumValues!))
                    : SettingValidation.Ok(JsonSerializer.SerializeToElement(match));
            }

            default:
            {
                if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
                {
                    return Fail(setting, "InvalidType", "must be an array of strings");
                }

                return Normalized(value);
            }
        }
    }

    private static SettingValidation CheckRange(SettingDescriptor setting, double number, JsonElement value)
    {
        if (setting.Min is { } min && number < min)
        {
            return Fail(setting, "OutOfRange", $"must be at least {Format(min)}");
        }

        if (setting.Max is { } max && number > max)
        {
            return Fail(setting, "OutOfRange", $"must be at most {Format(max)}");
        }

        return Normalized(value);
    }

    private static string Format(double number) => number.ToString("R", CultureInfo.InvariantCulture);

    // Clone detaches the element from the request document, which is disposed after the response.
    private static SettingValidation Normalized(JsonElement value) => SettingValidation.Ok(value.Clone());

    private static SettingValidation Fail(SettingDescriptor setting, string code, string message) =>
        SettingValidation.Fail(code, $"{setting.Key} {message}");

    /// <summary>Reads the current value of a setting from an options object as JSON (enum as its name).</summary>
    public static JsonElement ReadValue(SettingDescriptor setting, object options)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(options);
        return ToJson(setting, setting.Property.GetValue(options));
    }

    /// <summary>The default value as JSON.</summary>
    public static JsonElement DefaultValue(SettingDescriptor setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return ToJson(setting, setting.Default);
    }

    private static JsonElement ToJson(SettingDescriptor setting, object? value)
    {
        if (value is null)
        {
            return JsonSerializer.SerializeToElement<object?>(null);
        }

        return setting.Kind switch
        {
            SettingKind.Choice => JsonSerializer.SerializeToElement(value.ToString()),
            SettingKind.TextList => JsonSerializer.SerializeToElement(((IEnumerable<string>)value).ToArray()),
            _ => JsonSerializer.SerializeToElement(value, value.GetType()),
        };
    }
}
