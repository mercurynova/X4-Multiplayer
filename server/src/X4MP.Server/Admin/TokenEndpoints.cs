using System.Globalization;
using X4MP.Persistence;
using X4MP.Server.Api;
using X4MP.Server.Auth;

namespace X4MP.Server.Admin;

/// <summary><c>/api/v1/tokens</c> (API token management) and <c>/api/v1/audit</c>.</summary>
internal static class TokenEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        var tokens = routes.MapGroup("/api/v1/tokens");
        tokens.MapGet("", List).RequireAuthorization(AdminPolicies.Admin);
        tokens.MapPost("", Create).RequireAuthorization(AdminPolicies.Admin);
        tokens.MapDelete("/{id:long}", Revoke).RequireAuthorization(AdminPolicies.Admin);
        routes.MapGet("/api/v1/audit", Audit).RequireAuthorization(AdminPolicies.Admin);
    }

    private static IResult List(AdminStore store) =>
        Results.Json(
            [.. store.ListTokens().Select(t => new ApiTokenDto(t.Id, t.Name, t.Role, t.CreatedAt, t.LastUsedAt, t.RevokedAt is not null, t.OwnerName))],
            ApiJsonContext.Default.ListApiTokenDto);

    private static IResult Create(CreateTokenRequest? body, HttpContext context, AdminStore store)
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

        string? role = body?.Role;
        if (!AdminRoles.IsValid(role))
        {
            errors["role"] = [$"Use {AdminRoles.Admin} or {AdminRoles.Viewer}."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        // A token inherits the password dependency of whoever minted it, so changing that password revokes it.
        long? owner = AdminPrincipal.UserId(context.User)
            ?? (long.TryParse(context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value?.Replace("token:", string.Empty, StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out var presenting)
                ? store.TokenOwner(presenting)
                : null);
        var (id, plaintext) = store.CreateTokenWithInfo(name!, role!, owner);

        // The audit row names the token (id, name, role); the token itself is never recorded or logged.
        AdminApi.Audit(context, store, "token.create", id.ToString(CultureInfo.InvariantCulture), null, new() { ["name"] = name, ["role"] = role });
        return Results.Json(new ApiTokenCreatedDto(id, name!, role!, plaintext), ApiJsonContext.Default.ApiTokenCreatedDto, statusCode: StatusCodes.Status201Created);
    }

    private static IResult Revoke(long id, HttpContext context, AdminStore store)
    {
        if (!store.ListTokens().Any(t => t.Id == id))
        {
            return Problems.NotFound("The token");
        }

        if (!store.RevokeToken(id))
        {
            return Problems.Conflict("AlreadyRevoked", "The token was already revoked.");
        }

        AdminApi.Audit(context, store, "token.revoke", id.ToString(CultureInfo.InvariantCulture));
        return Results.NoContent();
    }

    private static IResult Audit(int? limit, SqliteAdminQueries queries)
    {
        var errors = new Dictionary<string, string[]>();
        int count = AdminApi.Limit(limit, 200, 1000, errors);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        return Results.Json([.. queries.AuditEntries(count).Select(AdminMapping.ToDto)], ApiJsonContext.Default.ListAuditEntryDto);
    }
}
