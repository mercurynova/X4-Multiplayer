using System.Text.Json;
using System.Text.Json.Serialization;

namespace X4MP.Server.Api;

/// <summary>Source-generated JSON metadata for admin API DTOs (camelCase, server-design 4.1).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HealthzResponse))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(ChangePasswordRequest))]
[JsonSerializable(typeof(MeDto))]
[JsonSerializable(typeof(ApiProblem))]
[JsonSerializable(typeof(SettingsSchemaDto))]
[JsonSerializable(typeof(SettingsDto))]
[JsonSerializable(typeof(SettingsProblem))]
[JsonSerializable(typeof(Dictionary<string, System.Text.Json.JsonElement>))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;
