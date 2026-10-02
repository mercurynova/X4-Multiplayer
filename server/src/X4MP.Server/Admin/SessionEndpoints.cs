using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using X4MP.Core.Net;
using X4MP.Core.Saves;
using X4MP.Core.Session;
using X4MP.Persistence;
using X4MP.Proto;
using X4MP.Server.Api;
using X4MP.Server.Auth;
using X4MP.Server.Hosting;
using X4MP.Server.Settings;

namespace X4MP.Server.Admin;

/// <summary><c>/api/v1/server</c>, <c>/dashboard</c> and <c>/sessions</c> (server-design 4.4). Session commands go through the actor's mailbox.</summary>
internal static class SessionEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/server", GetServer).RequireAuthorization(AdminPolicies.Viewer);
        routes.MapGet("/api/v1/dashboard", GetDashboardAsync).RequireAuthorization(AdminPolicies.Viewer);

        var group = routes.MapGroup("/api/v1/sessions");
        group.MapGet("", ListAsync).RequireAuthorization(AdminPolicies.Viewer);
        group.MapGet("/current", CurrentAsync).RequireAuthorization(AdminPolicies.Viewer);
        group.MapPost("", CreateAsync).RequireAuthorization(AdminPolicies.Admin);
        group.MapPost("/{id:long}/start", StartAsync).RequireAuthorization(AdminPolicies.Admin);
        group.MapPost("/{id:long}/stop", StopAsync).RequireAuthorization(AdminPolicies.Admin);
        group.MapPost("/{id:long}/request-save", RequestSaveAsync).RequireAuthorization(AdminPolicies.Admin);
        group.MapPost("/{id:long}/promote", (long id) =>
            Problems.NotImplemented("Promoting another player to authority arrives with authority migration (a later milestone)."))
            .RequireAuthorization(AdminPolicies.Admin);
        group.MapGet("/{id:long}/events", EventsAsync).RequireAuthorization(AdminPolicies.Viewer);
    }

    // ------------------------------------------------------------------ server and dashboard

    private static IResult GetServer(IServiceProvider services, ServerInfo info, NetOptions net)
    {
        var addresses = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var endpoints = new List<string>();
        if (net.Enabled)
        {
            endpoints.Add("tcp://" + net.NodeTcpEndpoint);
            if (net.UdpPort > 0)
            {
                endpoints.Add("udp://:" + net.UdpPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        var urls = addresses.ToList();
        var dto = new ServerInfoDto(
            net.ServerName, info.Version, new ProtocolRangeDto(ServerInfo.ProtocolMin, ServerInfo.ProtocolMax), info.BuildHash,
            DateTimeOffset.UtcNow - info.Uptime, (long)info.Uptime.TotalSeconds, endpoints, urls,
            urls.Any(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)));
        return Results.Json(dto, ApiJsonContext.Default.ServerInfoDto);
    }

    private static async Task<IResult> GetDashboardAsync(DashboardBuilder dashboard) =>
        Results.Json(await dashboard.BuildAsync(), ApiJsonContext.Default.DashboardSnapshotDto);

    // ------------------------------------------------------------------ reads

    private static async Task<IResult> ListAsync(string? state, int? limit, AdminSessions sessions, SqliteAdminQueries queries)
    {
        var errors = new Dictionary<string, string[]>();
        string? wanted = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            if (!Enum.TryParse<SessionPhase>(state.Trim(), ignoreCase: true, out var phase))
            {
                errors["state"] = ["Use one of: " + string.Join(", ", Enum.GetNames<SessionPhase>()) + "."];
            }
            else
            {
                wanted = phase.ToString();
            }
        }

        int count = AdminApi.Limit(limit, 50, 500, errors);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var live = await sessions.GetLiveAsync();
        var rows = queries.ListSessions(null, 500);
        var list = new List<SessionSummaryDto>();
        foreach (var row in rows)
        {
            // The live session's phase is the actor's, not the write-behind row's.
            var dto = row.Id == live.DbId ? sessions.Summary(live) with { SaveName = row.SaveName, SaveSha256 = row.SaveSha256 } : sessions.Summary(row);
            if (wanted is null || string.Equals(dto.State, wanted, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(dto);
            }

            if (list.Count >= count)
            {
                break;
            }
        }

        return Results.Json(list, ApiJsonContext.Default.ListSessionSummaryDto);
    }

    private static async Task<IResult> CurrentAsync(AdminSessions sessions)
    {
        var live = await sessions.GetLiveAsync();
        return live.Exists
            ? Results.Json(sessions.Detail(live), ApiJsonContext.Default.SessionDetailDto)
            : Results.NoContent();
    }

    private static async Task<IResult> EventsAsync(
        long id, string? type, string? since, int? limit, AdminSessions sessions, SqliteAdminQueries queries)
    {
        var errors = new Dictionary<string, string[]>();
        DateTimeOffset? from = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            if (AdminApi.TryParseTime(since, out var parsed))
            {
                from = parsed;
            }
            else
            {
                errors["since"] = ["Use an ISO-8601 timestamp."];
            }
        }

        int count = AdminApi.Limit(limit, 200, 1000, errors);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        if (queries.FindSession(id) is null && (await sessions.GetLiveAsync()).DbId != id)
        {
            return Problems.NotFound("The session");
        }

        var events = queries.SessionEvents(id, string.IsNullOrWhiteSpace(type) ? null : type.Trim(), from, count)
            .Select(e => new SessionEventDto(e.Id, e.At, e.Type, e.PlayerId, e.SectorId, e.ServerSeq, e.DataJson))
            .ToList();
        return Results.Json(events, ApiJsonContext.Default.ListSessionEventDto);
    }

    // ------------------------------------------------------------------ commands

    private static async Task<IResult> CreateAsync(
        CreateSessionRequest? body, HttpContext context, AdminSessions sessions, SaveService saves, SettingsService settings, AdminStore audit)
    {
        var errors = new Dictionary<string, string[]>();
        string? name = body?.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            errors["name"] = ["A name is required."];
        }
        else if (name.Length > 64)
        {
            errors["name"] = ["The name can be at most 64 characters."];
        }

        string? sha = string.IsNullOrWhiteSpace(body?.SaveId) ? null : body.SaveId.Trim().ToLowerInvariant();
        if (sha is not null && !SaveFileStore.IsValidSha(sha))
        {
            errors["saveId"] = ["saveId is the save's SHA-256 (64 hex digits)."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        SaveRecord? save = sha is null ? null : saves.Catalog.Find(sha);
        if (sha is not null && save is null)
        {
            return Problems.NotFound("The save");
        }

        var live = await sessions.GetLiveAsync();
        if (live.Snapshot.Phase is not (SessionPhase.Idle or SessionPhase.Ended))
        {
            return Problems.Conflict("SessionActive", $"A session is already {live.Phase}; stop it before creating another.");
        }

        if (body!.Settings is { Count: > 0 } changes)
        {
            var patch = await settings.PatchAsync(changes, AdminApi.Actor(context), AdminApi.RemoteIp(context), context.RequestAborted);
            if (!patch.Success)
            {
                return Problems.Validation(
                    patch.Errors.ToDictionary(e => "settings." + e.Key, e => new[] { e.Message }),
                    "One or more session settings were rejected; nothing was changed.");
            }
        }

        if (live.Snapshot.Phase == SessionPhase.Ended && !(await sessions.Actor.ApplyAsync(SessionTrigger.Reset, "admin create")).Applied)
        {
            return Problems.Conflict("SessionActive", "The ended session could not be reset.");
        }

        var created = await sessions.Actor.CreateSessionAsync(name!);
        if (!created.Ok)
        {
            return Problems.Conflict("SessionActive", created.Error ?? "A session is already active.");
        }

        if (sha is not null && created.SessionId > 0)
        {
            saves.Catalog.SetSessionSave(created.SessionId, sha, initial: true);
        }

        sessions.SelectSave(sha);

        AdminApi.Audit(context, audit, "session.create", created.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture), null, new()
        {
            ["name"] = name,
            ["saveSha256"] = sha,
        });
        var detail = sessions.Detail(await sessions.GetLiveAsync());
        if (save is not null)
        {
            detail = detail with { SaveName = save.DisplayName, SaveSha256 = save.Sha256 };
        }

        return Results.Json(detail, ApiJsonContext.Default.SessionDetailDto, statusCode: StatusCodes.Status201Created);
    }

    /// <summary>The live session when <paramref name="id"/> is its id; else the problem to answer with.</summary>
    private static async Task<(LiveSession? Live, IResult? Error)> ResolveLiveAsync(long id, AdminSessions sessions, SqliteAdminQueries queries)
    {
        var live = await sessions.GetLiveAsync();
        if (live.DbId == id && live.Exists)
        {
            return (live, null);
        }

        return queries.FindSession(id) is null
            ? (null, Problems.NotFound("The session"))
            : (null, Problems.Conflict("NotCurrentSession", "That session is not the one the server is running."));
    }

    private static async Task<IResult> StartAsync(
        long id, StartSessionRequest? body, HttpContext context, AdminSessions sessions, SqliteAdminQueries queries, AdminStore audit)
    {
        var (live, error) = await ResolveLiveAsync(id, sessions, queries);
        if (error is not null)
        {
            return error;
        }

        if (body?.AuthorityPlayerId is { } authorityId && queries.FindPlayer(authorityId, sessions.Time.GetUtcNow()) is null)
        {
            return Problems.NotFound("The player");
        }

        var result = await sessions.Actor.StartAsync(null, (int?)body?.AuthorityPlayerId);
        if (!result.Applied)
        {
            return Problems.Conflict("SessionNotIdle", result.Error ?? $"The session is {live!.Phase}.");
        }

        AdminApi.Audit(context, audit, "session.start", id.ToString(System.Globalization.CultureInfo.InvariantCulture), null, new()
        {
            ["authorityPlayerId"] = body?.AuthorityPlayerId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        return Results.Json(sessions.Detail(await sessions.GetLiveAsync()), ApiJsonContext.Default.SessionDetailDto, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> StopAsync(
        long id, StopSessionRequest? body, HttpContext context, AdminSessions sessions, SqliteAdminQueries queries, AdminStore audit)
    {
        var (live, error) = await ResolveLiveAsync(id, sessions, queries);
        if (error is not null)
        {
            return error;
        }

        var errors = new Dictionary<string, string[]>();
        string? message = AdminApi.CheckReason(body?.Message, errors, required: false, field: "message");
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        bool finalSave = body?.RequestFinalSave ?? true;
        var result = await sessions.Actor.ApplyAsync(SessionTrigger.Stop, message ?? "admin stop");
        if (!result.Applied)
        {
            return Problems.Result(
                StatusCodes.Status409Conflict, "SessionNotRunning", "Conflict.", result.Error ?? $"The session is {live!.Phase}.");
        }

        if (!finalSave)
        {
            await sessions.Actor.ApplyAsync(SessionTrigger.StopCompleted, "stopped without a final save");
        }

        AdminApi.Audit(context, audit, "session.stop", id.ToString(System.Globalization.CultureInfo.InvariantCulture), message, new()
        {
            ["requestFinalSave"] = finalSave ? "true" : "false",
        });
        return Results.Json(sessions.Detail(await sessions.GetLiveAsync()), ApiJsonContext.Default.SessionDetailDto, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> RequestSaveAsync(
        long id, HttpContext context, AdminSessions sessions, SqliteAdminQueries queries, SaveService saves, AdminStore audit)
    {
        var (live, error) = await ResolveLiveAsync(id, sessions, queries);
        if (error is not null)
        {
            return error;
        }

        if (live!.Snapshot.Phase is not (SessionPhase.Running or SessionPhase.Paused))
        {
            return Problems.Result(StatusCodes.Status409Conflict, "SessionNotRunning", "Conflict.", $"The session is {live.Phase}; a save can be requested while it runs.");
        }

        if (!await saves.RequestSaveAsync(SaveReason.Admin))
        {
            return Problems.Conflict("SaveNotPossible", "The authority cannot take a save request right now (it is not connected or already saving).");
        }

        AdminApi.Audit(context, audit, "session.request-save", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Results.Accepted();
    }
}
