using System.Text.Json;
using System.Text.Json.Serialization;
using X4MP.Core.Saves;
using X4MP.Server.Api;

namespace X4MP.Server.Saves;

/// <summary>A stored save as the admin API shows it (<c>GET /api/v1/saves</c>). The SHA-256 is the identity.</summary>
[TsContract]
public sealed record SaveDto(
    string Sha256,
    long SizeBytes,
    string DisplayName,
    string Source,
    DateTimeOffset UploadedAt,
    string? GameVersion,
    string? SaveTime,
    string? PlayerName,
    bool Pinned,
    bool GhostsCleaned,
    bool Current)
{
    public static SaveDto From(SaveRecord s, string? currentSha256) => new(
        s.Sha256, s.SizeBytes, s.DisplayName, s.Source, s.UploadedAt, s.Meta.GameVersion, s.Meta.SaveTime, s.Meta.PlayerName,
        s.Pinned, s.GhostsCleaned, string.Equals(s.Sha256, currentSha256, StringComparison.Ordinal));
}

/// <summary>Body of <c>POST /api/v1/saves/uploads</c>.</summary>
[TsContract]
public sealed record BeginUploadRequest(string? FileName, long Size, string? Sha256);

/// <summary>Response of <c>POST /api/v1/saves/uploads</c>: the upload id, the chunk size to use and the resume point.</summary>
[TsContract]
public sealed record UploadStartedDto(string UploadId, int ChunkSize, long ReceivedBytes);

/// <summary>Response of a chunk <c>PUT</c> and of <c>GET /api/v1/saves/uploads/{id}</c>.</summary>
[TsContract]
public sealed record UploadProgressDto(long ReceivedBytes, long Size);

/// <summary>Body of <c>PATCH /api/v1/saves/{sha}</c>.</summary>
[TsContract]
public sealed record PatchSaveRequest(string? DisplayName, bool? Pinned);

/// <summary>Source-generated JSON metadata for the save endpoints (camelCase, like the other admin DTOs).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SaveDto))]
[JsonSerializable(typeof(List<SaveDto>))]
[JsonSerializable(typeof(BeginUploadRequest))]
[JsonSerializable(typeof(UploadStartedDto))]
[JsonSerializable(typeof(UploadProgressDto))]
[JsonSerializable(typeof(PatchSaveRequest))]
internal sealed partial class SavesJsonContext : JsonSerializerContext;
