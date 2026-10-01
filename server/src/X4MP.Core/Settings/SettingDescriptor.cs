using System.Reflection;

namespace X4MP.Core.Settings;

public enum SettingKind
{
    Boolean,
    WholeNumber,
    WholeNumber64,
    Number,
    Text,
    Choice,
    TextList,
}

/// <summary>Everything the schema, validation and the configuration provider need to know about one setting.</summary>
public sealed record SettingDescriptor
{
    /// <summary>API key, for example <c>Replication.TickRateHz</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Configuration path, for example <c>X4MP:Replication:TickRateHz</c>.</summary>
    public required string ConfigPath { get; init; }

    /// <summary>Key without its section: <c>TickRateHz</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Section part of the key: <c>Replication</c>.</summary>
    public required string Section { get; init; }

    public required string Category { get; init; }

    public required string Description { get; init; }

    public required SettingKind Kind { get; init; }

    public required SettingScope Scope { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    public int? MaxLength { get; init; }

    /// <summary>Enum member names, for <see cref="SettingKind.Choice"/>.</summary>
    public IReadOnlyList<string>? EnumValues { get; init; }

    public bool Secret { get; init; }

    public bool PushToNodes { get; init; }

    /// <summary>Default of the property on a freshly constructed options object.</summary>
    public object? Default { get; init; }

    public required Type OptionsType { get; init; }

    public required PropertyInfo Property { get; init; }
}
