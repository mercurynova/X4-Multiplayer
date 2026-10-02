using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using X4MP.Core.Session;
using X4MP.Core.Settings;
using X4MP.Core.Teams;
using TeamRelation = X4MP.Core.Teams.TeamRelation;
using X4MP.Persistence;
using X4MP.Proto;
using X4MP.Server.Api;
using X4MP.Server.Auth;
using X4MP.Server.Settings;
using X4MP.Server.Teams;

namespace X4MP.Server.Admin;

/// <summary>
/// <c>/api/v1/teams/...</c> (server-design 4.4, task M1-T5): the teams, their members, the relation matrix, presets and the team settings. Reads
/// and mutations run on the session actor's thread (the team module is single-threaded by design). Every mutation is audited (actor, action,
/// target); a lobby password is never logged or audited.
/// </summary>
internal static partial class TeamEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/api/v1/teams");
        g.MapGet("", StateAsync).RequireAuthorization(AdminPolicies.Viewer);
        g.MapPost("", CreateAsync).RequireAuthorization(AdminPolicies.Admin);
        g.MapGet("/unassigned", UnassignedAsync).RequireAuthorization(AdminPolicies.Viewer);

        g.MapGet("/relations", RelationsAsync).RequireAuthorization(AdminPolicies.Viewer);
        g.MapPut("/relations", SetRelationsAsync).RequireAuthorization(AdminPolicies.Admin);
        g.MapPut("/relations/{teamA:long}/{teamB:long}", SetRelationAsync).RequireAuthorization(AdminPolicies.Admin);

        g.MapPut("/members", BulkAssignAsync).RequireAuthorization(AdminPolicies.Admin);
        g.MapPut("/members/{playerId:long}", AssignAsync).RequireAuthorization(AdminPolicies.Admin);

        g.MapGet("/preset/{preset}/preview", PreviewAsync).RequireAuthorization(AdminPolicies.Viewer);
        g.MapPost("/preset", ApplyPresetAsync).RequireAuthorization(AdminPolicies.Admin);

        g.MapGet("/policy", PolicyAsync).RequireAuthorization(AdminPolicies.Viewer);
        g.MapPatch("/policy", PatchPolicyAsync).RequireAuthorization(AdminPolicies.Admin);

        g.MapGet("/{id:long}", DetailAsync).RequireAuthorization(AdminPolicies.Viewer);
        g.MapPatch("/{id:long}", PatchAsync).RequireAuthorization(AdminPolicies.Admin);
        g.MapDelete("/{id:long}", DeleteAsync).RequireAuthorization(AdminPolicies.Admin);
    }

    // ------------------------------------------------------------------ plumbing

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorPattern();

    private static string Who(HttpContext context) => "admin:" + AdminApi.Actor(context);

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static bool IsId(long id) => id is >= 1 and <= int.MaxValue;

    private static IResult FromReject(TeamRejectReason reason, string? detail) => reason switch
    {
        TeamRejectReason.UnknownTeam => Problems.NotFound("The team"),
        TeamRejectReason.NameTaken => Problems.Conflict("NameTaken", detail ?? "That team name is taken."),
        TeamRejectReason.NoFactionSlot => Problems.Conflict("NoFactionSlot", detail ?? "No faction slot is free."),
        TeamRejectReason.SessionRunningRestricted =>
            Problems.Conflict("SessionRunningRestricted", detail ?? "The authority's player cannot change team while the session is running."),
        TeamRejectReason.NotPermitted => Problems.Validation("body", detail ?? "That change is not permitted."),
        _ => Problems.Conflict(reason.ToString(), detail ?? reason.ToString()),
    };

    private static bool TryRelation(string? text, out TeamRelation relation) =>
        Enum.TryParse(text, ignoreCase: true, out relation) && Enum.IsDefined(relation);

    private static bool TryPreset(string? text, out TeamPreset preset)
    {
        preset = default;
        string normal = (text ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        return Enum.TryParse(normal, ignoreCase: true, out preset) && Enum.IsDefined(preset);
    }

    private static readonly string PresetList = string.Join(", ", Enum.GetNames<TeamPreset>());

    private static Task<TeamsStateDto> State(AdminSessions sessions, TeamViews views) => sessions.Actor.CallAsync(views.State);

    private static async Task<IResult> TeamOrNotFound(long id, AdminSessions sessions, TeamViews views, int status = StatusCodes.Status200OK)
    {
        var dto = await sessions.Actor.CallAsync(() => views.State().Teams.FirstOrDefault(t => t.Id == id));
        return dto is null ? Problems.NotFound("The team") : Results.Json(dto, ApiJsonContext.Default.TeamDto, statusCode: status);
    }

    // ------------------------------------------------------------------ reads

    private static async Task<IResult> StateAsync(AdminSessions sessions, TeamViews views) =>
        Results.Json(await State(sessions, views), ApiJsonContext.Default.TeamsStateDto);

    private static async Task<IResult> DetailAsync(long id, AdminSessions sessions, TeamViews views)
    {
        var state = await State(sessions, views);
        if (state.Teams.FirstOrDefault(t => t.Id == id) is not { } team)
        {
            return Problems.NotFound("The team");
        }

        return Results.Json(new TeamDetailDto(team, [.. state.Members.Where(m => m.TeamId == id)]), ApiJsonContext.Default.TeamDetailDto);
    }

    private static async Task<IResult> UnassignedAsync(AdminSessions sessions, TeamViews views) =>
        Results.Json((await State(sessions, views)).Unassigned, ApiJsonContext.Default.ListTeamMemberDto);

    private static async Task<IResult> RelationsAsync(AdminSessions sessions, TeamViews views) =>
        Results.Json((await State(sessions, views)).Relations, ApiJsonContext.Default.TeamRelationsDto);

    private static async Task<IResult> PolicyAsync(AdminSessions sessions, TeamViews views) =>
        Results.Json((await State(sessions, views)).Policy, ApiJsonContext.Default.TeamPolicyDto);

    // ------------------------------------------------------------------ teams

    private static bool CheckColor(string? color, Dictionary<string, string[]> errors, out string? normalized)
    {
        normalized = null;
        if (color is null)
        {
            return true;
        }

        if (!ColorPattern().IsMatch(color))
        {
            errors["color"] = ["Use a colour like #3FA7FF."];
            return false;
        }

        normalized = color.ToUpperInvariant();
        return true;
    }

    private static void CheckMax(int? maxMembers, Dictionary<string, string[]> errors)
    {
        if (maxMembers is < 0 or > 64)
        {
            errors["maxMembers"] = ["Use a value from 1 to 64 (0 removes the limit)."];
        }
    }

    private static async Task<IResult> CreateAsync(
        CreateTeamRequest? body, HttpContext context, TeamModule module, AdminSessions sessions, TeamViews views, AdminStore audit)
    {
        var errors = new Dictionary<string, string[]>();
        if (body is null)
        {
            errors["body"] = ["Send the team to create."];
        }
        else
        {
            if (TeamRules.NormalizeName(body.Name) is null)
            {
                errors["name"] = [$"A team name is 1 to {TeamRules.MaxNameLength} characters."];
            }

            CheckColor(body.Color, errors, out _);
            CheckMax(body.MaxMembers, errors);
            if (body.FactionSlot is < 1 or > TeamOptions.MaxFactionSlots)
            {
                errors["factionSlot"] = [$"Use a slot from 1 to {TeamOptions.MaxFactionSlots}."];
            }
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        CheckColor(body!.Color, errors, out string? color);
        var result = await module.CreateTeamAsync(
            body.Name!, color, body.FactionSlot, body.Locked ?? false, body.MaxMembers is > 0 ? body.MaxMembers : null, body.Password);
        if (!result.Ok)
        {
            return FromReject(result.Reason, result.Detail);
        }

        AdminApi.Audit(context, audit, "teams.create", Id(result.Value!.Id), null, new()
        {
            ["name"] = result.Value.Name,
            ["slot"] = result.Value.FactionSlot.ToString(CultureInfo.InvariantCulture),
            ["password"] = body.Password is { Length: > 0 } ? "set" : null,
        });
        return await TeamOrNotFound(result.Value.Id, sessions, views, StatusCodes.Status201Created);
    }

    private static async Task<IResult> PatchAsync(
        long id, PatchTeamRequest? body, HttpContext context, TeamModule module, AdminSessions sessions, TeamViews views, AdminStore audit)
    {
        var errors = new Dictionary<string, string[]>();
        if (body is null || (body.Name is null && body.Color is null && body.FactionSlot is null && body.LeaderPlayerId is null
            && body.Locked is null && body.MaxMembers is null && body.Password is null))
        {
            errors["body"] = ["Send at least one field to change."];
        }
        else
        {
            if (body.Name is not null && TeamRules.NormalizeName(body.Name) is null)
            {
                errors["name"] = [$"A team name is 1 to {TeamRules.MaxNameLength} characters."];
            }

            CheckColor(body.Color, errors, out _);
            CheckMax(body.MaxMembers, errors);
            if (body.FactionSlot is < 1 or > TeamOptions.MaxFactionSlots)
            {
                errors["factionSlot"] = [$"Use a slot from 1 to {TeamOptions.MaxFactionSlots}."];
            }

            if (body.LeaderPlayerId is { } leader && (leader < 1 || leader > int.MaxValue))
            {
                errors["leaderPlayerId"] = ["Not a player id."];
            }
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        if (!IsId(id))
        {
            return Problems.NotFound("The team");
        }

        CheckColor(body!.Color, errors, out string? color);
        var patch = new TeamRegistry.TeamPatch(
            Name: body.Name, Color: color, Locked: body.Locked, MaxMembers: body.MaxMembers is > 0 ? body.MaxMembers : null,
            ClearMaxMembers: body.MaxMembers == 0, LeaderPlayerId: body.LeaderPlayerId is { } l ? (int)l : null, FactionSlot: body.FactionSlot);
        var result = await module.UpdateTeamAsync((int)id, patch, body.Password);
        if (!result.Ok)
        {
            return FromReject(result.Reason, result.Detail);
        }

        var fields = new List<string>();
        foreach (var (name, present) in new[]
        {
            ("name", body.Name is not null), ("color", body.Color is not null), ("factionSlot", body.FactionSlot is not null),
            ("leaderPlayerId", body.LeaderPlayerId is not null), ("locked", body.Locked is not null), ("maxMembers", body.MaxMembers is not null),
            ("password", body.Password is not null),
        })
        {
            if (present)
            {
                fields.Add(name);
            }
        }

        AdminApi.Audit(context, audit, "teams.update", Id(id), null, new() { ["fields"] = string.Join(',', fields) });
        return await TeamOrNotFound(id, sessions, views);
    }

    private static async Task<IResult> DeleteAsync(
        long id, long? moveMembersTo, HttpContext context, TeamModule module, AdminSessions sessions, TeamViews views, AdminStore audit)
    {
        if (!IsId(id))
        {
            return Problems.NotFound("The team");
        }

        var state = await State(sessions, views);
        if (state.Teams.All(t => t.Id != id))
        {
            return Problems.NotFound("The team");
        }

        if (moveMembersTo is { } target && (target == id || state.Teams.All(t => t.Id != target)))
        {
            return Problems.Validation("moveMembersTo", "Choose another existing team to move the members to.");
        }

        var result = await module.DeleteTeamAsync((int)id, moveMembersTo is { } to ? (int)to : null);
        if (!result.Ok)
        {
            return FromReject(result.Reason, result.Detail);
        }

        AdminApi.Audit(context, audit, "teams.delete", Id(id), null, new() { ["moveMembersTo"] = moveMembersTo?.ToString(CultureInfo.InvariantCulture) });
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ members

    private static bool TryRole(string? text, out TeamRole role)
    {
        role = TeamRole.Member;
        return text is null || (Enum.TryParse(text, ignoreCase: true, out role) && Enum.IsDefined(role));
    }

    /// <summary>Moves one player (or puts it back to Unassigned for a null team). Returns a failure result, or null when done.</summary>
    private static async Task<IResult?> MoveAsync(
        int playerId, int? teamId, TeamRole role, string actor, TeamModule module, AdminSessions sessions)
    {
        if (teamId is { } team)
        {
            var result = await module.AssignPlayerAsync(playerId, team, role, actor);
            return result.Ok ? null : FromReject(result.Reason, result.Detail);
        }

        if (await module.UnassignPlayerAsync(playerId))
        {
            return null;
        }

        // False means "had no membership" (fine) or "the authority cannot leave its team while Running".
        bool stillMember = await sessions.Actor.CallAsync(() => module.Snapshot().Members.Any(m => m.PlayerId == playerId));
        return stillMember ? FromReject(TeamRejectReason.SessionRunningRestricted, null) : null;
    }

    private static async Task<IResult> AssignAsync(
        long playerId, AssignMemberRequest? body, HttpContext context, TeamModule module, AdminSessions sessions, TeamViews views,
        SqliteAdminQueries queries, AdminStore audit)
    {
        var errors = new Dictionary<string, string[]>();
        if (body is null)
        {
            errors["body"] = ["Send teamId (null for Unassigned) and optionally role."];
        }
        else
        {
            if (!TryRole(body.Role, out _))
            {
                errors["role"] = ["Use Member or Leader."];
            }

            if (body.TeamId is { } t && !IsId(t))
            {
                errors["teamId"] = ["Not a team id."];
            }
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        if (!IsId(playerId) || queries.FindPlayer(playerId, sessions.Time.GetUtcNow()) is null)
        {
            return Problems.NotFound("The player");
        }

        TryRole(body!.Role, out var role);
        if (await MoveAsync((int)playerId, body.TeamId is { } team ? (int)team : null, role, Who(context), module, sessions) is { } failure)
        {
            return failure;
        }

        AdminApi.Audit(context, audit, body.TeamId is null ? "teams.unassign" : "teams.assign", Id(playerId), null, new()
        {
            ["teamId"] = body.TeamId?.ToString(CultureInfo.InvariantCulture),
            ["role"] = body.TeamId is null ? null : role.ToString(),
        });
        return Results.Json(await sessions.Actor.CallAsync(() => views.MemberOf((int)playerId)), ApiJsonContext.Default.TeamMemberDto);
    }

    private static async Task<IResult> BulkAssignAsync(
        BulkAssignRequest? body, HttpContext context, TeamModule module, AdminSessions sessions, TeamViews views, SqliteAdminQueries queries, AdminStore audit)
    {
        var errors = new Dictionary<string, string[]>();
        if (body?.Assignments is not { Count: > 0 } list)
        {
            return Problems.Validation("assignments", "Send at least one assignment.");
        }

        var state = await State(sessions, views);
        var now = sessions.Time.GetUtcNow();
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (!IsId(a.PlayerId) || queries.FindPlayer(a.PlayerId, now) is null)
            {
                errors[$"assignments[{i}].playerId"] = ["Unknown player."];
            }

            if (a.TeamId is { } t && state.Teams.All(x => x.Id != t))
            {
                errors[$"assignments[{i}].teamId"] = ["Unknown team."];
            }
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        string actor = Who(context);
        foreach (var a in list)
        {
            if (await MoveAsync((int)a.PlayerId, a.TeamId is { } t ? (int)t : null, TeamRole.Member, actor, module, sessions) is { } failure)
            {
                return failure;
            }
        }

        AdminApi.Audit(context, audit, "teams.bulk-assign", null, null, new()
        {
            ["moves"] = string.Join(',', list.Select(a => Id(a.PlayerId) + ">" + (a.TeamId is { } t ? Id(t) : "none"))),
        });
        return Results.Json(await State(sessions, views), ApiJsonContext.Default.TeamsStateDto);
    }

    // ------------------------------------------------------------------ relations

    private static async Task<IResult> SetRelationsAsync(
        SetRelationsRequest? body, HttpContext context, TeamModule module, AdminSessions sessions, TeamViews views, AdminStore audit)
    {
        if (body?.Entries is not { Count: > 0 } entries)
        {
            return Problems.Validation("entries", "Send at least one relation.");
        }

        var errors = new Dictionary<string, string[]>();
        var parsed = new List<(int, int, TeamRelation)>();
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (!TryRelation(e.Relation, out var relation))
            {
                errors[$"entries[{i}].relation"] = ["Use Allied, Neutral or Hostile."];
            }
            else if (!IsId(e.TeamA) || !IsId(e.TeamB) || e.TeamA == e.TeamB)
            {
                errors[$"entries[{i}]"] = ["Name two different teams."];
            }
            else
            {
                parsed.Add(((int)e.TeamA, (int)e.TeamB, relation));
            }
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var result = await module.SetRelationsAsync(parsed);
        if (!result.Ok)
        {
            return FromReject(result.Reason, result.Detail);
        }

        AdminApi.Audit(context, audit, "teams.relations", null, null, new()
        {
            ["entries"] = string.Join(',', parsed.Select(p => $"{p.Item1}-{p.Item2}:{p.Item3}")),
        });
        return Results.Json((await State(sessions, views)).Relations, ApiJsonContext.Default.TeamRelationsDto);
    }

    private static async Task<IResult> SetRelationAsync(
        long teamA, long teamB, SetRelationRequest? body, HttpContext context, TeamModule module, AdminSessions sessions, TeamViews views, AdminStore audit)
    {
        if (!TryRelation(body?.Relation, out var relation))
        {
            return Problems.Validation("relation", "Use Allied, Neutral or Hostile.");
        }

        if (!IsId(teamA) || !IsId(teamB))
        {
            return Problems.NotFound("The team");
        }

        if (teamA == teamB)
        {
            return Problems.Validation("teamB", "Name two different teams: a team is always allied with itself.");
        }

        var result = await module.SetRelationAsync((int)teamA, (int)teamB, relation);
        if (!result.Ok)
        {
            return FromReject(result.Reason, result.Detail);
        }

        AdminApi.Audit(context, audit, "teams.relation", $"{teamA}-{teamB}", null, new() { ["relation"] = relation.ToString() });
        return Results.Json((await State(sessions, views)).Relations, ApiJsonContext.Default.TeamRelationsDto);
    }

    // ------------------------------------------------------------------ presets

    private static async Task<IResult> PreviewAsync(string preset, TeamModule module, AdminSessions sessions, TeamViews views)
    {
        if (!TryPreset(preset, out var parsed))
        {
            return Problems.Validation("preset", $"Use one of {PresetList}.");
        }

        return Results.Json(await sessions.Actor.CallAsync(() => views.Preview(module.PreviewPreset(parsed))), ApiJsonContext.Default.TeamPresetPreviewDto);
    }

    private static async Task<IResult> ApplyPresetAsync(
        ApplyPresetRequest? body, HttpContext context, TeamModule module, AdminSessions sessions, TeamViews views, SettingsService settings, AdminStore audit)
    {
        if (!TryPreset(body?.Preset, out var preset))
        {
            return Problems.Validation("preset", $"Use one of {PresetList}.");
        }

        var preview = await sessions.Actor.CallAsync(() => views.Preview(module.PreviewPreset(preset)));
        if (preview.Blocked is { } blocked)
        {
            return Enum.TryParse<TeamRejectReason>(blocked, out var reason)
                ? FromReject(reason, preview.BlockedDetail)
                : Problems.Conflict(blocked, preview.BlockedDetail ?? blocked);
        }

        if (preview.RequiresConfirm && body!.Confirm != true)
        {
            return Results.Json(
                new PresetConfirmProblem(
                    "urn:x4mp:problem:ConfirmationRequired", "Confirmation required.", StatusCodes.Status409Conflict, "ConfirmationRequired",
                    "Applying a preset while the session is running moves players and replaces the teams. Review the preview and send confirm: true.",
                    preview),
                ApiJsonContext.Default.PresetConfirmProblem, Problems.ContentType, StatusCodes.Status409Conflict);
        }

        string actor = Who(context);
        var result = await module.ApplyPresetAsync(preset, actor);
        if (!result.Ok)
        {
            return FromReject(result.Reason, result.Detail);
        }

        // The preset goes with an auto-assign strategy (the setting a joining player is placed by).
        var changes = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["Teams." + nameof(TeamOptions.AutoAssign)] = JsonSerializer.SerializeToElement(result.Value!.AutoAssign.ToString()),
        };
        await settings.PatchAsync(changes, actor, AdminApi.RemoteIp(context), context.RequestAborted);

        AdminApi.Audit(context, audit, "teams.preset", preset.ToString(), null, new()
        {
            ["playersMoved"] = preview.PlayersMoved.ToString(CultureInfo.InvariantCulture),
            ["teams"] = preview.Teams.Count.ToString(CultureInfo.InvariantCulture),
            ["running"] = preview.Running ? "true" : null,
        });
        return Results.Json(await State(sessions, views), ApiJsonContext.Default.TeamsStateDto);
    }

    // ------------------------------------------------------------------ policy

    private static async Task<IResult> PatchPolicyAsync(
        PatchTeamPolicyRequest? body, HttpContext context, AdminSessions sessions, TeamViews views, SettingsService settings, AdminStore audit)
    {
        var errors = new Dictionary<string, string[]>();
        var codes = new Dictionary<string, string>();
        var changes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var fieldOfKey = new Dictionary<string, string>(StringComparer.Ordinal);
        if (body is null)
        {
            errors["body"] = ["Send the policy fields to change."];
        }
        else
        {
            foreach (var property in typeof(PatchTeamPolicyRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetValue(body) is not { } value)
                {
                    continue;
                }

                string field = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                string key = "Teams." + property.Name;
                if (!settings.Registry.TryGet(key, out var descriptor))
                {
                    errors[field] = ["Not a known setting."];
                    continue;
                }

                var check = SettingsRegistry.Validate(descriptor, JsonSerializer.SerializeToElement(value));
                if (check.IsValid)
                {
                    changes[key] = check.Normalized;
                    fieldOfKey[key] = field;
                }
                else
                {
                    errors[field] = [check.ErrorMessage!];
                    codes[field] = check.ErrorCode!;
                }
            }

            if (errors.Count == 0 && changes.Count == 0)
            {
                errors["body"] = ["Send at least one policy field to change."];
            }
        }

        if (errors.Count > 0)
        {
            return Problems.Result(StatusCodes.Status400BadRequest, "ValidationFailed", "One or more fields are invalid; nothing was changed.", null, errors, codes.Count == 0 ? null : codes);
        }

        var result = await settings.PatchAsync(changes, Who(context), AdminApi.RemoteIp(context), context.RequestAborted);
        if (!result.Success)
        {
            return Problems.Result(
                StatusCodes.Status400BadRequest, "ValidationFailed", "One or more fields were rejected; nothing was changed.", null,
                result.Errors.ToDictionary(e => fieldOfKey.GetValueOrDefault(e.Key, e.Key), e => new[] { e.Message }),
                result.Errors.ToDictionary(e => fieldOfKey.GetValueOrDefault(e.Key, e.Key), e => e.Code));
        }

        AdminApi.Audit(context, audit, "teams.policy", null, null, new() { ["fields"] = string.Join(',', fieldOfKey.Values.Order(StringComparer.Ordinal)) });
        return Results.Json((await State(sessions, views)).Policy, ApiJsonContext.Default.TeamPolicyDto);
    }
}
