using System.Text.Json;
using System.Text.Json.Serialization;
using X4MP.Core.Saves;

namespace X4MP.Server.Saves;

/// <summary>A stored save as the admin API shows it (<c>GET /api/v1/saves</c>). The SHA-256 is the identity.</summary>
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
public sealed record BeginUploadRequest(string? FileName, long Size, string? Sha256);

public sealed record UploadStartedDto(string UploadId, int ChunkSize, long ReceivedBytes);

public sealed record UploadProgressDto(long ReceivedBytes, long Size);

/// <summary>Body of <c>PATCH /api/v1/saves/{sha}</c>.</summary>
public sealed record PatchSaveRequest(string? DisplayName, bool? Pinned);

/// <summary>422 body of <c>complete</c>: <c>HashMismatch</c> or <c>NotASave</c>; other errors use their own code.</summary>
public sealed record SaveErrorDto(string Error, string? Detail, long? ReceivedBytes = null);

/// <summary>Source-generated JSON metadata for the save endpoints (camelCase, like the other admin DTOs).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SaveDto))]
[JsonSerializable(typeof(List<SaveDto>))]
[JsonSerializable(typeof(BeginUploadRequest))]
[JsonSerializable(typeof(UploadStartedDto))]
[JsonSerializable(typeof(UploadProgressDto))]
[JsonSerializable(typeof(PatchSaveRequest))]
[JsonSerializable(typeof(SaveErrorDto))]
internal sealed partial class SavesJsonContext : JsonSerializerContext;
