using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Net.Http.Headers;
using X4MP.Core.Events;
using X4MP.Core.Saves;
using X4MP.Proto;
using X4MP.Server.Admin;
using X4MP.Server.Api;
using X4MP.Server.Auth;
using X4MP.Server.Hosting;

namespace X4MP.Server.Saves;

/// <summary>One resumable admin upload (server-design 3.4): the part file is the state, so a restart loses nothing.</summary>
public sealed record HttpUpload(string Id, string Sha256, long Size, string FileName, string DisplayName);

/// <summary>The admin uploads in progress, by upload id.</summary>
public sealed class HttpUploads
{
    private readonly ConcurrentDictionary<string, HttpUpload> _byId = new(StringComparer.Ordinal);

    public void Add(HttpUpload upload) => _byId[upload.Id] = upload;

    public bool TryGet(string id, out HttpUpload upload) => _byId.TryGetValue(id, out upload!);

    public bool Remove(string id) => _byId.TryRemove(id, out _);

    public bool IsBusy(string sha256) => _byId.Values.Any(u => u.Sha256 == sha256);
}

/// <summary>
/// The save endpoints of the HTTP side (server-design 3.3/3.4, protocol.md 6.4). <c>GET /files/saves/{sha}</c> is the node fallback:
/// <c>Authorization: X4MP-Download &lt;token&gt;</c> (a per-node token from <c>SessionSaveInfo</c>) or an admin/viewer credential, with
/// <c>Range</c>, <c>If-Range</c> and <c>ETag = "&lt;sha256&gt;"</c>. <c>/api/v1/saves/*</c> is the admin Saves screen: resumable upload
/// (non-gzip or a wrong hash answers 422), list, download, rename/pin and delete. The save's SHA-256 is its id in the routes.
/// </summary>
public static class SaveEndpoints
{
    public const int HttpChunkSize = 8 * 1024 * 1024;
    public const string DownloadScheme = "X4MP-Download";

    public static IEndpointRouteBuilder MapSaveApi(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapMethods("/files/saves/{sha}", ["GET", "HEAD"], DownloadFallbackAsync).AllowAnonymous();

        var api = routes.MapGroup("/api/v1/saves");
        api.MapGet("", List).RequireAuthorization(AdminPolicies.Viewer);
        api.MapGet("/{sha}/download", AdminDownload).RequireAuthorization(AdminPolicies.Viewer);
        api.MapPatch("/{sha}", PatchAsync).RequireAuthorization(AdminPolicies.Admin);
        api.MapDelete("/{sha}", Delete).RequireAuthorization(AdminPolicies.Admin);
        api.MapPost("/uploads", BeginUploadAsync).RequireAuthorization(AdminPolicies.Admin);
        api.MapGet("/uploads/{id}", UploadProgress).RequireAuthorization(AdminPolicies.Admin);
        api.MapPut("/uploads/{id}", PutChunkAsync).RequireAuthorization(AdminPolicies.Admin);
        api.MapPost("/uploads/{id}/complete", CompleteAsync).RequireAuthorization(AdminPolicies.Admin);
        api.MapDelete("/uploads/{id}", AbortUpload).RequireAuthorization(AdminPolicies.Admin);
        return routes;
    }

    /// <summary>An RFC 7807 problem whose <c>code</c> is <paramref name="error"/> (<c>receivedBytes</c> is the resume point of a failed chunk).</summary>
    private static IResult Error(int status, string error, string? detail = null, long? received = null) =>
        Problems.Result(
            status,
            error,
            status switch
            {
                StatusCodes.Status400BadRequest => "The request is invalid.",
                StatusCodes.Status404NotFound => "Not found.",
                StatusCodes.Status409Conflict => "Conflict.",
                StatusCodes.Status413PayloadTooLarge => "The upload is too large.",
                StatusCodes.Status422UnprocessableEntity => "The file is not an acceptable save.",
                _ => "The request failed.",
            },
            detail,
            receivedBytes: received);

    // ------------------------------------------------------------------ downloads

    private static async Task<IResult> DownloadFallbackAsync(string sha, HttpContext ctx, SaveService saves, IAuthorizationService authorization)
    {
        string? header = ctx.Request.Headers.Authorization;
        bool allowed;
        if (header is not null && header.StartsWith(DownloadScheme + " ", StringComparison.OrdinalIgnoreCase))
        {
            allowed = saves.Tokens.TryValidate(header[(DownloadScheme.Length + 1)..].Trim(), out _);
        }
        else
        {
            allowed = (await authorization.AuthorizeAsync(ctx.User, null, AdminPolicies.Viewer).ConfigureAwait(false)).Succeeded;
        }

        if (!allowed)
        {
            ctx.Response.Headers.WWWAuthenticate = DownloadScheme;
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        return ServeFile(sha, saves);
    }

    private static IResult AdminDownload(string sha, SaveService saves) => ServeFile(sha, saves);

    private static IResult ServeFile(string sha, SaveService saves)
    {
        sha = sha.ToLowerInvariant();
        if (saves.Files.Find(sha) is not { } file)
        {
            return Results.NotFound();
        }

        return Results.File(
            file.Path,
            file.Kind == UploadKind.Save ? "application/gzip" : "application/octet-stream",
            fileDownloadName: null,
            lastModified: file.ModifiedAt,
            entityTag: new EntityTagHeaderValue("\"" + sha + "\""),
            enableRangeProcessing: true);
    }

    // ------------------------------------------------------------------ catalog

    private static IResult List(SaveService saves)
    {
        string? current = saves.Status.CurrentSha256;
        return Results.Json([.. saves.Catalog.List().Select(s => SaveDto.From(s, current))], SavesJsonContext.Default.ListSaveDto);
    }

    private static async Task<IResult> PatchAsync(string sha, HttpContext ctx, SaveService saves, IEventPublisher events, TimeProvider time)
    {
        sha = sha.ToLowerInvariant();
        var body = await ReadBodyAsync(ctx, SavesJsonContext.Default.PatchSaveRequest).ConfigureAwait(false);
        if (body is null || (body.DisplayName is null && body.Pinned is null))
        {
            return Problems.Validation("body", "Send displayName (1 to 128 characters) and/or pinned.");
        }

        if (body.DisplayName is { Length: 0 or > 128 })
        {
            return Problems.Validation("displayName", "The display name must be 1 to 128 characters.");
        }

        if (!saves.Catalog.Update(sha, body.DisplayName, body.Pinned))
        {
            return Error(StatusCodes.Status404NotFound, "NotFound");
        }

        Audit(ctx, events, time, "save.update", sha);
        var updated = saves.Catalog.Find(sha);
        return updated is null ? Results.NoContent() : Results.Json(SaveDto.From(updated with
        {
            DisplayName = body.DisplayName ?? updated.DisplayName,
            Pinned = body.Pinned ?? updated.Pinned,
        }, saves.Status.CurrentSha256), SavesJsonContext.Default.SaveDto);
    }

    private static IResult Delete(
        string sha, HttpContext ctx, SaveService saves, HttpUploads uploads, AdminSessions sessions, IEventPublisher events, TimeProvider time)
    {
        sha = sha.ToLowerInvariant();
        if (!SaveFileStore.IsValidSha(sha))
        {
            return Problems.Validation("sha", "Not a SHA-256.");
        }

        if (saves.Catalog.Find(sha) is null)
        {
            return Problems.NotFound("The save");
        }

        if (saves.ProtectedSha256().Contains(sha) || saves.Catalog.ReferencedSha256().Contains(sha) || uploads.IsBusy(sha) || sessions.IsSelected(sha))
        {
            return Error(StatusCodes.Status409Conflict, "InUse", "A session uses this save.");
        }

        foreach (var manifest in saves.Catalog.Delete(sha))
        {
            saves.Files.Delete(manifest, UploadKind.Manifest);
        }

        saves.Files.Delete(sha, UploadKind.Save);
        Audit(ctx, events, time, "save.delete", sha);
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ resumable upload

    private static async Task<IResult> BeginUploadAsync(HttpContext ctx, SaveService saves, HttpUploads uploads, Microsoft.Extensions.Options.IOptionsMonitor<SaveOptions> options)
    {
        var body = await ReadBodyAsync(ctx, SavesJsonContext.Default.BeginUploadRequest).ConfigureAwait(false);
        string sha = body?.Sha256?.ToLowerInvariant() ?? string.Empty;
        var invalid = new Dictionary<string, string[]>();
        if (body is null)
        {
            invalid["body"] = ["Send fileName, size and sha256."];
        }
        else
        {
            if (!SaveFileStore.IsValidSha(sha))
            {
                invalid["sha256"] = ["A 64-digit hex SHA-256 is required."];
            }

            if (body.Size <= 0)
            {
                invalid["size"] = ["The size must be positive."];
            }
        }

        if (invalid.Count > 0)
        {
            return Problems.Validation(invalid);
        }

        if (body is null)
        {
            return Problems.Validation("body", "Send fileName, size and sha256.");
        }

        if (body.Size > options.CurrentValue.MaxSaveBytes)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "TooLarge", $"The limit is {options.CurrentValue.MaxSaveBytes} bytes.");
        }

        string fileName = Path.GetFileName(body.FileName ?? string.Empty);
        string display = fileName.Length == 0 ? "upload " + sha[..12] : fileName.EndsWith(".xml.gz", StringComparison.OrdinalIgnoreCase) ? fileName[..^7] : fileName;
        string id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        var upload = new HttpUpload(id, sha, body.Size, fileName, display);
        uploads.Add(upload);
        long received = saves.Files.SizeOf(sha, UploadKind.Save) == body.Size ? body.Size : PartLength(saves.Files, upload);
        return Results.Json(new UploadStartedDto(id, HttpChunkSize, received), SavesJsonContext.Default.UploadStartedDto, "application/json", StatusCodes.Status201Created);
    }

    private static long PartLength(SaveFileStore files, HttpUpload upload)
    {
        var info = new FileInfo(files.PartPathOf(upload.Sha256, UploadKind.Save));
        return info.Exists && info.Length <= upload.Size ? info.Length : 0;
    }

    private static IResult UploadProgress(string id, SaveService saves, HttpUploads uploads)
    {
        if (!uploads.TryGet(id, out var upload))
        {
            return Error(StatusCodes.Status404NotFound, "NotFound");
        }

        long received = saves.Files.SizeOf(upload.Sha256, UploadKind.Save) == upload.Size ? upload.Size : PartLength(saves.Files, upload);
        return Results.Json(new UploadProgressDto(received, upload.Size), SavesJsonContext.Default.UploadProgressDto);
    }

    private static async Task<IResult> PutChunkAsync(string id, HttpContext ctx, SaveService saves, HttpUploads uploads)
    {
        if (!uploads.TryGet(id, out var upload))
        {
            return Error(StatusCodes.Status404NotFound, "NotFound");
        }

        if (!TryParseContentRange(ctx.Request.Headers.ContentRange.ToString(), out long start, out long end, out long total) || total != upload.Size || end >= total)
        {
            return Problems.Validation("Content-Range", "Send Content-Range: bytes a-b/size matching the announced size.");
        }

        string part = saves.Files.PartPathOf(upload.Sha256, UploadKind.Save);
        long have = PartLength(saves.Files, upload);
        if (start != have)
        {
            return Error(StatusCodes.Status409Conflict, "OffsetMismatch", $"The server has {have} bytes; continue there.", have);
        }

        long expected = end - start + 1;
        long written = 0;
        await using (var file = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1 << 16, FileOptions.Asynchronous))
        {
            file.SetLength(start);
            file.Position = start;
            var buffer = new byte[1 << 16];
            int n;
            while ((n = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted).ConfigureAwait(false)) > 0)
            {
                written += n;
                if (written > expected)
                {
                    break;
                }

                await file.WriteAsync(buffer.AsMemory(0, n), ctx.RequestAborted).ConfigureAwait(false);
            }
        }

        if (written != expected)
        {
            // keep what arrived: the client asks GET /uploads/{id} and continues
            return Error(StatusCodes.Status400BadRequest, "BodyMismatch", $"The body had {written} bytes, Content-Range says {expected}.", PartLength(saves.Files, upload));
        }

        return Results.Json(new UploadProgressDto(start + written, upload.Size), SavesJsonContext.Default.UploadProgressDto);
    }

    private static async Task<IResult> CompleteAsync(
        string id, HttpContext ctx, SaveService saves, HttpUploads uploads, IEventPublisher events, TimeProvider time)
    {
        if (!uploads.TryGet(id, out var upload))
        {
            return Error(StatusCodes.Status404NotFound, "NotFound");
        }

        var files = saves.Files;
        string part = files.PartPathOf(upload.Sha256, UploadKind.Save);
        bool already = files.SizeOf(upload.Sha256, UploadKind.Save) == upload.Size;
        SaveMeta meta = SaveMeta.Empty;
        if (!already)
        {
            long have = PartLength(files, upload);
            if (have != upload.Size)
            {
                return Error(StatusCodes.Status400BadRequest, "Incomplete", $"{have} of {upload.Size} bytes received.", have);
            }

            string actual;
            await using (var file = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ctx.RequestAborted).ConfigureAwait(false));
            }

            if (!string.Equals(actual, upload.Sha256, StringComparison.Ordinal))
            {
                SaveFileStore.TryDelete(part);
                uploads.Remove(id);
                return Error(StatusCodes.Status422UnprocessableEntity, "HashMismatch", $"sha256 is {actual}, announced {upload.Sha256}");
            }

            if (SaveSniffer.Check(part, UploadKind.Save, out meta) != SniffResult.Ok)
            {
                SaveFileStore.TryDelete(part);
                uploads.Remove(id);
                return Error(StatusCodes.Status422UnprocessableEntity, "NotASave", "Not a gzip file whose root element is <savegame>.");
            }

            files.Promote(part, upload.Sha256, UploadKind.Save);
        }

        var record = new SaveRecord(
            upload.Sha256, upload.Size, upload.DisplayName, "admin-upload", ctx.User.Identity?.Name, time.GetUtcNow(), meta,
            GhostsCleaned: true, Pinned: false, OriginalFileName: upload.FileName);
        saves.Catalog.AddSave(record);
        uploads.Remove(id);
        events.Publish(new X4MP.Core.Events.SaveStored(time.GetUtcNow(), null, upload.Sha256, upload.Size, "admin-upload"));
        Audit(ctx, events, time, "save.upload", upload.Sha256);
        return Results.Json(SaveDto.From(record, saves.Status.CurrentSha256), SavesJsonContext.Default.SaveDto, "application/json", StatusCodes.Status201Created);
    }

    private static IResult AbortUpload(string id, SaveService saves, HttpUploads uploads)
    {
        if (!uploads.TryGet(id, out var upload))
        {
            return Error(StatusCodes.Status404NotFound, "NotFound");
        }

        uploads.Remove(id);
        if (!saves.IsPartInUse(saves.Files.PartPathOf(upload.Sha256, UploadKind.Save)))
        {
            SaveFileStore.TryDelete(saves.Files.PartPathOf(upload.Sha256, UploadKind.Save));
        }

        return Results.NoContent();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Parses <c>bytes a-b/total</c>.</summary>
    internal static bool TryParseContentRange(string header, out long start, out long end, out long total)
    {
        start = end = total = 0;
        const string prefix = "bytes ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var span = header.AsSpan(prefix.Length);
        int dash = span.IndexOf('-');
        int slash = span.IndexOf('/');
        return dash > 0 && slash > dash
            && long.TryParse(span[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out start)
            && long.TryParse(span[(dash + 1)..slash], NumberStyles.None, CultureInfo.InvariantCulture, out end)
            && long.TryParse(span[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out total)
            && end >= start;
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpContext ctx, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(ctx.Request.Body, info, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Audit(HttpContext ctx, IEventPublisher events, TimeProvider time, string action, string target) =>
        events.Publish(new AdminActionTaken(
            time.GetUtcNow(), null, ctx.User.Identity?.Name ?? "unknown", action, target, null, ctx.Connection.RemoteIpAddress?.ToString()));
}
