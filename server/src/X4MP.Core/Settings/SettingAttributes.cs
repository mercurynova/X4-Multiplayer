namespace X4MP.Core.Settings;

/// <summary>Whether a setting can change while the server runs.</summary>
public enum SettingScope
{
    /// <summary>Read once at startup; shown read-only in the GUI. A PATCH is answered with "restart required".</summary>
    Boot = 0,

    /// <summary>Editable at runtime. Consumers must read it through <c>IOptionsMonitor&lt;T&gt;</c>.</summary>
    Live = 1,
}

/// <summary>
/// Marks an options class as a settings section (server-design 2.9). <paramref name="configPath"/> is the
/// configuration section, for example <c>X4MP:Replication</c>. The API key of a property is the path without the
/// <c>X4MP:</c> prefix, joined with the property name by a dot: <c>Replication.TickRateHz</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SettingsSectionAttribute(string configPath, string category) : Attribute
{
    public string ConfigPath { get; } = configPath;

    /// <summary>GUI grouping, for example "Replication".</summary>
    public string Category { get; } = category;
}

/// <summary>
/// Declares an options property as a setting that appears in <c>/api/v1/settings</c>. The GUI renders its form
/// from the schema this attribute produces, so a new setting needs no GUI change.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class SettingAttribute(string description) : Attribute
{
    public string Description { get; } = description;

    /// <summary>Default <see cref="SettingScope.Boot"/>: a setting is only <c>Live</c> if its consumers use <c>IOptionsMonitor</c>.</summary>
    public SettingScope Scope { get; set; } = SettingScope.Boot;

    /// <summary>Overrides the section's category.</summary>
    public string? Category { get; set; }

    /// <summary>Inclusive lower bound for numeric settings (NaN = none).</summary>
    public double Min { get; set; } = double.NaN;

    /// <summary>Inclusive upper bound for numeric settings (NaN = none).</summary>
    public double Max { get; set; } = double.NaN;

    /// <summary>Maximum length for string settings (0 = unlimited).</summary>
    public int MaxLength { get; set; }

    /// <summary>Masked in <c>GET</c> responses and in audit rows.</summary>
    public bool Secret { get; set; }

    /// <summary>
    /// Node-relevant: the setting is sent to every node in <c>ServerSettingsUpdate</c> (right after Welcome, and the full set again
    /// whenever one of these changes; <see cref="ISessionSettingsPusher"/>). Only meaningful for Live settings.
    /// </summary>
    public bool PushToNodes { get; set; }
}
