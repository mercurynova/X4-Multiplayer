using System.Globalization;
using System.Text.RegularExpressions;
using X4MP.Core.Mods;
using X4MP.Persistence;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Server.Api;
using X4MP.Server.Auth;
using X4MP.Server.Mods;

namespace X4MP.Server.Admin;

/// <summary>
/// <c>/api/v1/mods/...</c> and <c>/api/v1/players/{id}/extensions</c> (docs/mod-management.md 7, task M1-X4). The server runs one session, so the routes are flat like
/// <c>/api/v1/teams</c> (there is no <c>/sessions/{sid}</c> prefix). Reads need Viewer (a Viewer sees players' mod lists only when <c>ModListVisibility</c> allows it; Admin and
/// ModEditor always do); every edit needs Admin or ModEditor, is audited and bumps the policy version, which pushes <c>ModPolicyChanged</c> to the nodes and the hub's mods topic.
/// </summary>
internal static partial class ModEndpoints
{
    private const int MaxNameLength = 128;
    private const int MaxVersionLength = 64;
    private const int MaxNotesLength = 500;
    private const int MaxHashBytes = 64;

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtIdPattern();

    public static void Map(IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/api/v1/mods");
        g.MapGet("", StateAsync).RequireAuthorization(AdminPolicies.Viewer);
        g.MapPatch("/policy", PatchPolicyAsync).RequireAuthorization(AdminPolicies.ModEditor);
        g.MapPut("/entries/{extId}", PutEntryAsync).RequireAuthorization(AdminPolicies.ModEditor);
        g.MapDelete("/entries/{extId}", DeleteEntryAsync).RequireAuthorization(AdminPolicies.ModEditor);
        g.MapPost("/import-from-authority", ImportAsync).RequireAuthorization(AdminPolicies.ModEditor);
        g.MapGet("/save-requirements", SaveRequirements).RequireAuthorization(AdminPolicies.Viewer);
        g.MapGet("/catalog", CatalogAsync).RequireAuthorization(AdminPolicies.Viewer);
        g.MapPut("/catalog/{extId}", PutCatalogAsync).RequireAuthorization(AdminPolicies.ModEditor);

        routes.MapGet("/api/v1/players/{id:long}/extensions", PlayerExtensionsAsync).RequireAuthorization(AdminPolicies.Viewer);
    }

    // ------------------------------------------------------------------ helpers

    private static IResult Hidden() => Problems.Result(
        StatusCodes.Status403Forbidden, "ModListHidden", "Forbidden.", "Players' mod lists are hidden from your role by the ModListVisibility setting.");

    private static bool TryEnum<T>(string? text, out T value) where T : struct, Enum =>
        Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value);

    private static bool TryHex(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length % 2 != 0 || text.Length > MaxHashBytes * 2)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Validates a pasted Nexus URL ("" clears it) and a Workshop id; adds field errors, returns the normalised values.</summary>
    private static (string? Nexus, ulong? Workshop) CheckLinks(string? nexusUrl, long? workshopId, Dictionary<string, string[]> errors)
    {
        string? nexus = null;
        if (nexusUrl is not null)
        {
            if (nexusUrl.Trim().Length == 0)
            {
                nexus = string.Empty;
            }
            else if (ModLinks.TryNormaliseNexusUrl(nexusUrl, out var normal))
            {
                nexus = normal;
            }
            else
            {
                errors["nexusUrl"] = ["Use a link to a Nexus Mods page for X4: https://www.nexusmods.com/x4foundations/mods/<number>."];
            }
        }

        ulong? workshop = null;
        if (workshopId is { } ws)
        {
            if (ws < 0)
            {
                errors["workshopId"] = ["Use a Steam Workshop item id (a positive number), or 0 for none."];
            }
            else
            {
                workshop = (ulong)ws;
            }
        }

        return (nexus, workshop);
    }

    private static void CheckText(string? value, int max, string field, Dictionary<string, string[]> errors)
    {
        if (value is { } text && text.Length > max)
        {
            errors[field] = [$"At most {max} characters."];
        }
    }

    private static ModPolicyEntryT Copy(ModPolicyEntryT e) => new()
    {
        Id = e.Id, Name = e.Name, Rule = e.Rule, Enabled = e.Enabled, ModClass = e.ModClass, VersionRule = e.VersionRule, Version = e.Version,
        ContentHash = e.ContentHash is null ? null : [.. e.ContentHash], NexusUrl = e.NexusUrl, WorkshopId = e.WorkshopId, Notes = e.Notes,
    };

    private static string Who(HttpContext context) => "admin:" + AdminApi.Actor(context);

    // ------------------------------------------------------------------ reads

    private static IResult StateAsync(HttpContext context, ModViews views)
    {
        var access = views.Access(context.User);
        return Results.Json(views.State(access), ApiJsonContext.Default.ModsStateDto);
    }

    private static IResult SaveRequirements() => Problems.NotImplemented(
        "Reading the mods a save needs (its <patches> block) is not available in this build; the Mods page cannot flag mods required by the session save yet.");

    private static IResult CatalogAsync(HttpContext context, ModViews views)
    {
        if (!views.Access(context.User).CanSeePlayers)
        {
            return Hidden(); // the catalog is learned from players' reports
        }

        var current = views.Policy.Current;
        return Results.Json([.. views.Store.Catalog().Select(c => ModViews.CatalogEntry(c, current))], ApiJsonContext.Default.ListModCatalogEntryDto);
    }

    private static IResult PlayerExtensionsAsync(long id, int? limit, HttpContext context, ModViews views, SqliteAdminQueries queries, TimeProvider time)
    {
        if (!views.Access(context.User).CanSeePlayers)
        {
            return Hidden();
        }

        var errors = new Dictionary<string, string[]>();
        int count = AdminApi.Limit(limit, IModStore.ReportsPerPlayer, IModStore.ReportsPerPlayer, errors);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        if (id is < 1 or > int.MaxValue || queries.FindPlayer(id, time.GetUtcNow()) is not { } player)
        {
            return Problems.NotFound("The player");
        }

        return Results.Json(views.PlayerExtensions((int)id, player.Name, count), ApiJsonContext.Default.PlayerExtensionsDto);
    }

    // ------------------------------------------------------------------ policy

    private static IResult PatchPolicyAsync(PatchModPolicyRequest? body, HttpContext context, ModViews views, AdminStore audit)
    {
        var errors = new Dictionary<string, string[]>();
        ModSourceMode mode = default;
        UnknownModDefault unknown = default;
        ModEnforcement enforcement = default;
        if (body is null || (body.SourceMode is null && body.UnknownDefault is null && body.Enforcement is null))
        {
            errors["body"] = ["Send at least one of sourceMode, unknownDefault, enforcement."];
        }
        else
        {
            if (body.SourceMode is not null && !TryEnum(body.SourceMode, out mode))
            {
                errors["sourceMode"] = [$"Use {string.Join(" or ", Enum.GetNames<ModSourceMode>())}."];
            }

            if (body.UnknownDefault is not null && !TryEnum(body.UnknownDefault, out unknown))
            {
                errors["unknownDefault"] = [$"Use {string.Join(", ", Enum.GetNames<UnknownModDefault>())}."];
            }

            if (body.Enforcement is not null && !TryEnum(body.Enforcement, out enforcement))
            {
                errors["enforcement"] = [$"Use {string.Join(" or ", Enum.GetNames<ModEnforcement>())}."];
            }
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var before = views.Policy.Current;
        var after = views.Policy.Update(Who(context), p =>
        {
            if (body!.SourceMode is not null)
            {
                p.SourceMode = mode;
            }

            if (body.UnknownDefault is not null)
            {
                p.UnknownDefault = unknown;
            }

            if (body.Enforcement is not null)
            {
                p.Enforcement = enforcement;
            }
        });
        AdminApi.Audit(context, audit, "mods.policy", null, null, new()
        {
            ["sourceMode"] = $"{before.SourceMode}->{after.SourceMode}",
            ["unknownDefault"] = $"{before.UnknownDefault}->{after.UnknownDefault}",
            ["enforcement"] = $"{before.Enforcement}->{after.Enforcement}",
            ["version"] = after.Version.ToString(CultureInfo.InvariantCulture),
        });
        return Results.Json(views.PolicyDto(views.Access(context.User)), ApiJsonContext.Default.ModPolicyDto);
    }

    private static IResult PutEntryAsync(string extId, PutModEntryRequest? body, HttpContext context, ModViews views, AdminStore audit, TimeProvider time)
    {
        if (!ExtIdPattern().IsMatch(extId))
        {
            return Problems.Validation("extId", "An extension id is 1 to 128 letters, digits, underscores, dots or dashes.");
        }

        var errors = new Dictionary<string, string[]>();
        if (body is null)
        {
            return Problems.Validation("body", "Send the entry (rule, enabled, versionRule, version, nexusUrl, workshopId, notes, ...).");
        }

        var current = views.Policy.Current;
        var existing = (current.Entries ?? []).FirstOrDefault(e => e.Id == extId);
        ModRule rule = existing?.Rule ?? default;
        if (body.Rule is not null)
        {
            if (!TryEnum(body.Rule, out rule))
            {
                errors["rule"] = [$"Use {string.Join(", ", Enum.GetNames<ModRule>())}."];
            }
        }
        else if (existing is null)
        {
            errors["rule"] = [$"A new mod needs a rule: {string.Join(", ", Enum.GetNames<ModRule>())}."];
        }

        ExtensionClass cls = existing?.ModClass ?? ExtensionClass.Unknown;
        if (body.ClassOverride is not null && (!TryEnum(body.ClassOverride, out cls) || cls == ExtensionClass.Dlc))
        {
            errors["classOverride"] = ["Use Unknown (no override), Sim or ClientOnly."];
        }

        VersionRule versionRule = existing?.VersionRule ?? (rule == ModRule.Required ? VersionRule.Exact : VersionRule.Any);
        if (body.VersionRule is not null && !TryEnum(body.VersionRule, out versionRule))
        {
            errors["versionRule"] = [$"Use {string.Join(", ", Enum.GetNames<VersionRule>())}."];
        }

        byte[]? hash = null;
        if (body.ContentHash is { } hashText && hashText.Length > 0 && !TryHex(hashText, out hash))
        {
            errors["contentHash"] = [$"Use hexadecimal (at most {MaxHashBytes} bytes), or \"\" to clear it."];
        }

        CheckText(body.Name, MaxNameLength, "name", errors);
        CheckText(body.Version, MaxVersionLength, "version", errors);
        CheckText(body.Notes, MaxNotesLength, "notes", errors);
        var (nexus, workshop) = CheckLinks(body.NexusUrl, body.WorkshopId, errors);
        if (existing is null && (current.Entries?.Count ?? 0) >= ModPolicyConstants.MaxExtensionEntries)
        {
            errors["extId"] = [$"The list holds at most {ModPolicyConstants.MaxExtensionEntries} mods."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var known = views.Store.CatalogEntry(extId);
        var fields = new List<string>();
        var after = views.Policy.Update(Who(context), p =>
        {
            p.Entries ??= [];
            var entry = p.Entries.FirstOrDefault(e => e.Id == extId);
            if (entry is null)
            {
                entry = new ModPolicyEntryT
                {
                    Id = extId,
                    Name = known?.Name is { Length: > 0 } n ? n : extId,
                    Rule = rule,
                    Enabled = true,
                    ModClass = known?.ClassOverride ?? ExtensionClass.Unknown,
                    VersionRule = versionRule,
                    Version = string.Empty,
                    NexusUrl = known?.NexusUrl ?? string.Empty,
                    WorkshopId = known?.WorkshopId is { } w and not 0 ? w : ModLinks.WorkshopIdOf(extId),
                    Notes = known?.Notes ?? string.Empty,
                };
                p.Entries.Add(entry);
            }

            void Set(string field, bool present, Action apply)
            {
                if (present)
                {
                    apply();
                    fields.Add(field);
                }
            }

            Set("rule", body.Rule is not null, () => entry.Rule = rule);
            Set("enabled", body.Enabled is not null, () => entry.Enabled = body.Enabled!.Value);
            Set("classOverride", body.ClassOverride is not null, () => entry.ModClass = cls);
            Set("versionRule", body.VersionRule is not null, () => entry.VersionRule = versionRule);
            Set("name", body.Name is not null, () => entry.Name = body.Name!.Trim().Length == 0 ? extId : body.Name.Trim());
            Set("version", body.Version is not null, () => entry.Version = body.Version!.Trim());
            Set("contentHash", body.ContentHash is not null, () => entry.ContentHash = hash is { Length: > 0 } ? [.. hash] : null);
            Set("nexusUrl", nexus is not null, () => entry.NexusUrl = nexus!);
            Set("workshopId", workshop is not null, () => entry.WorkshopId = workshop!.Value != 0 ? workshop.Value : ModLinks.WorkshopIdOf(extId));
            Set("notes", body.Notes is not null, () => entry.Notes = body.Notes!.Trim());
        });

        var saved = after.Entries!.First(e => e.Id == extId);
        // What an admin types once is remembered for every later session (mod_catalog).
        views.Store.UpsertCatalog(new ModCatalogRecord(
            extId, saved.Name ?? string.Empty, string.IsNullOrEmpty(saved.NexusUrl) ? null : saved.NexusUrl, saved.WorkshopId, saved.ModClass,
            string.IsNullOrEmpty(saved.Notes) ? null : saved.Notes, time.GetUtcNow()));
        AdminApi.Audit(context, audit, existing is null ? "mods.entry.add" : "mods.entry.update", extId, null, new()
        {
            ["rule"] = saved.Rule.ToString(),
            ["enabled"] = saved.Enabled ? "true" : "false",
            ["fields"] = string.Join(',', fields),
            ["version"] = after.Version.ToString(CultureInfo.InvariantCulture),
        });
        var access = views.Access(context.User);
        var dto = ModViews.Entry(saved, views.Store.LatestReports(), views.AuthorityReport()?.Items, access);
        return Results.Json(dto, ApiJsonContext.Default.ModEntryDto, statusCode: existing is null ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }

    private static IResult DeleteEntryAsync(string extId, HttpContext context, ModViews views, AdminStore audit)
    {
        var current = views.Policy.Current;
        if (!ExtIdPattern().IsMatch(extId) || (current.Entries ?? []).All(e => e.Id != extId))
        {
            return Problems.NotFound("The mod entry");
        }

        var after = views.Policy.Update(Who(context), p => p.Entries?.RemoveAll(e => e.Id == extId));
        AdminApi.Audit(context, audit, "mods.entry.delete", extId, null, new() { ["version"] = after.Version.ToString(CultureInfo.InvariantCulture) });
        return Results.NoContent();
    }

    private static IResult ImportAsync(ImportModsRequest? body, HttpContext context, ModViews views, AdminStore audit)
    {
        var authority = views.AuthorityReport();
        if (authority is null)
        {
            return Problems.Conflict("NoAuthorityReport", "The authority has not reported its mods yet. Connect the authority first.");
        }

        bool merge = body?.Merge ?? true;
        var before = views.Policy.Current;
        int beforeCount = before.Entries?.Count ?? 0;
        var after = views.Policy.Update(Who(context), p =>
            p.Entries = ModPolicyImport.FromAuthority(authority.Value.Items, [.. (p.Entries ?? []).Select(Copy)], merge, views.Store.CatalogEntry));
        AdminApi.Audit(context, audit, "mods.import", authority.Value.PlayerId?.ToString(CultureInfo.InvariantCulture), null, new()
        {
            ["merge"] = merge ? "true" : "false",
            ["entriesBefore"] = beforeCount.ToString(CultureInfo.InvariantCulture),
            ["entriesAfter"] = (after.Entries?.Count ?? 0).ToString(CultureInfo.InvariantCulture),
            ["version"] = after.Version.ToString(CultureInfo.InvariantCulture),
        });
        return Results.Json(views.PolicyDto(views.Access(context.User)), ApiJsonContext.Default.ModPolicyDto);
    }

    // ------------------------------------------------------------------ catalog

    private static IResult PutCatalogAsync(string extId, PutModCatalogRequest? body, HttpContext context, ModViews views, AdminStore audit, TimeProvider time)
    {
        if (!ExtIdPattern().IsMatch(extId))
        {
            return Problems.Validation("extId", "An extension id is 1 to 128 letters, digits, underscores, dots or dashes.");
        }

        if (body is null)
        {
            return Problems.Validation("body", "Send name, nexusUrl, workshopId, classOverride or notes.");
        }

        var errors = new Dictionary<string, string[]>();
        ExtensionClass? cls = null;
        if (body.ClassOverride is not null)
        {
            if (TryEnum<ExtensionClass>(body.ClassOverride, out var parsed) && parsed != ExtensionClass.Dlc)
            {
                cls = parsed;
            }
            else
            {
                errors["classOverride"] = ["Use Unknown (no override), Sim or ClientOnly."];
            }
        }

        CheckText(body.Name, MaxNameLength, "name", errors);
        CheckText(body.Notes, MaxNotesLength, "notes", errors);
        var (nexus, workshop) = CheckLinks(body.NexusUrl, body.WorkshopId, errors);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var old = views.Store.CatalogEntry(extId);
        ulong ws = workshop ?? old?.WorkshopId ?? ModLinks.WorkshopIdOf(extId);
        if (ws == 0)
        {
            ws = ModLinks.WorkshopIdOf(extId);
        }

        var entry = new ModCatalogRecord(
            extId,
            body.Name is { } name ? name.Trim() : old?.Name ?? string.Empty,
            nexus is not null ? (nexus.Length == 0 ? null : nexus) : old?.NexusUrl,
            ws,
            cls ?? old?.ClassOverride ?? ExtensionClass.Unknown,
            body.Notes is { } notes ? (notes.Trim().Length == 0 ? null : notes.Trim()) : old?.Notes,
            time.GetUtcNow());
        views.Store.UpsertCatalog(entry);
        AdminApi.Audit(context, audit, "mods.catalog", extId, null, new()
        {
            ["fields"] = string.Join(',', new[] { ("name", body.Name), ("nexusUrl", body.NexusUrl), ("classOverride", body.ClassOverride), ("notes", body.Notes) }
                .Where(f => f.Item2 is not null).Select(f => f.Item1).Concat(body.WorkshopId is null ? [] : ["workshopId"])),
        });
        return Results.Json(ModViews.CatalogEntry(entry, views.Policy.Current), ApiJsonContext.Default.ModCatalogEntryDto);
    }
}
