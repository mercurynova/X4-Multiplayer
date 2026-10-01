using System.Text.Json;

namespace X4MP.Server.Api;

/// <summary>One setting in <c>GET /api/v1/settings/schema</c>; the GUI renders its form from this.</summary>
[TsContract]
public sealed record SettingSchemaDto(
    string Key,
    string Section,
    string Name,
    string Category,
    string Description,
    string Type,
    bool RequiresRestart,
    double? Min,
    double? Max,
    int? MaxLength,
    List<string>? Values,
    bool Secret,
    bool PushToNodes,
    JsonElement? Default);

/// <summary>Response of <c>GET /api/v1/settings/schema</c>.</summary>
[TsContract]
public sealed record SettingsSchemaDto(List<SettingSchemaDto> Settings);

/// <summary>
/// Response of <c>GET</c> and <c>PATCH /api/v1/settings</c>: <c>sections[Section][Name]</c> is the effective value
/// (secrets masked). <c>Overrides</c> lists the keys that carry a GUI override.
/// </summary>
[TsContract]
public sealed record SettingsDto(Dictionary<string, Dictionary<string, JsonElement>> Sections, List<string> Overrides);

/// <summary>
/// One rejected key of a <c>PATCH /api/v1/settings</c>. The 400 response is an <see cref="ApiProblem"/> with <c>errors[key]</c> (the
/// message) and <c>errorCodes[key]</c> (this code); nothing was applied.
/// </summary>
public sealed record SettingErrorDto(string Key, string Code, string Message);
