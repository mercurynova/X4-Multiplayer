using System.Text.Json;
using System.Text.Json.Serialization;

namespace X4MP.Server.Api;

/// <summary>Source-generated JSON metadata for admin API DTOs (camelCase, server-design 4.1).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HealthzResponse))]
internal sealed partial class ApiJsonContext : JsonSerializerContext;
