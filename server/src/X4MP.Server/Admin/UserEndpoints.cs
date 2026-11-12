using System.Globalization;
using X4MP.Server.Api;
using X4MP.Server.Auth;

namespace X4MP.Server.Admin;

/// <summary>
/// <c>/api/v1/users</c> (account management). Admin only. Passwords are validated but never echoed, logged or audited; every change is
/// written to <c>audit_log</c> before the response.
/// </summary>
internal static class UserEndpoints
{
    private const int MaxPasswordLength = 256;

    public static void Map(IEndpointRouteBuilder routes)
    {
        var users = routes.MapGroup("/api/v1/users");
        users.MapGet("", List).RequireAuthorization(AdminPolicies.Admin);
        users.MapPost("", Create).RequireAuthorization(AdminPolicies.Admin);
        users.MapPatch("/{id:long}", Patch).RequireAuthorization(AdminPolicies.Admin);
        users.MapDelete("/{id:long}", Delete).RequireAuthorization(AdminPolicies.Admin);
    }

    private static IResult List(AdminStore store) =>
        Results.Json([.. store.ListUsers().Select(ToDto)], ApiJsonContext.Default.ListAdminUserDto);

    private static IResult Create(CreateUserRequest? body, HttpContext context, AdminStore store, AdminAuthOptions options)
    {
        var errors = new Dictionary<string, string[]>();
        string? username = body?.Username?.Trim();
        if (string.IsNullOrEmpty(username))
        {
            errors["username"] = ["A username is required."];
        }
        else if (username.Length > 64)
        {
            errors["username"] = ["The username can be at most 64 characters."];
        }

        string? role = body?.Role;
        if (!AdminRoles.IsValid(role))
        {
            errors["role"] = [$"Use {AdminRoles.Admin}, {AdminRoles.Viewer} or {AdminRoles.ModEditor}."];
        }

        CheckPassword(body?.Password, options, errors);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        if (store.FindUser(username!) is { } existing)
        {
            return Problems.Conflict("UsernameTaken", $"The username '{existing.Username}' is already taken.");
        }

        // must_change: the person who was given the account picks their own password at first login.
        long id = store.CreateUser(username!, body!.Password!, role!, mustChange: true, options.Pbkdf2Iterations);
        AdminApi.Audit(
            context, store, "user.create", id.ToString(CultureInfo.InvariantCulture), null,
            new() { ["username"] = username, ["role"] = role });
        return Results.Json(ToDto(store.ListUsers().Single(u => u.Id == id)), ApiJsonContext.Default.AdminUserDto, statusCode: StatusCodes.Status201Created);
    }

    private static IResult Patch(long id, PatchUserRequest? body, HttpContext context, AdminStore store, AdminAuthOptions options)
    {
        var user = store.FindUser(id);
        if (user is null)
        {
            return Problems.NotFound("The user");
        }

        var errors = new Dictionary<string, string[]>();
        string? role = body?.Role;
        if (role is not null && !AdminRoles.IsValid(role))
        {
            errors["role"] = [$"Use {AdminRoles.Admin}, {AdminRoles.Viewer} or {AdminRoles.ModEditor}."];
        }

        if (body?.Password is not null)
        {
            CheckPassword(body.Password, options, errors);
        }

        if (errors.Count == 0 && role is null && body?.Password is null)
        {
            errors["role"] = ["Send a role, a password, or both."];
        }

        bool demote = role is not null && role != user.Role && user.Role == AdminRoles.Admin;
        if (demote && store.CountAdmins() <= 1)
        {
            errors["role"] = ["The last admin cannot be demoted."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var actor = AdminApi.Actor(context);
        if (role is not null && role != user.Role)
        {
            store.SetRole(id, role);
            AdminApi.Audit(
                context, store, "user.role", id.ToString(CultureInfo.InvariantCulture), null,
                new() { ["username"] = user.Username, ["from"] = user.Role, ["to"] = role, ["actor"] = actor });
        }

        if (body?.Password is not null)
        {
            // Ends the user's sessions and revokes the tokens it minted; the account must pick a new password at next login.
            store.ResetPassword(id, body.Password, options.Pbkdf2Iterations);
            AdminApi.Audit(
                context, store, "user.password.reset", id.ToString(CultureInfo.InvariantCulture), null,
                new() { ["username"] = user.Username, ["actor"] = actor });
        }

        return Results.Json(ToDto(store.ListUsers().Single(u => u.Id == id)), ApiJsonContext.Default.AdminUserDto);
    }

    private static IResult Delete(long id, HttpContext context, AdminStore store)
    {
        var user = store.FindUser(id);
        if (user is null)
        {
            return Problems.NotFound("The user");
        }

        if (AdminPrincipal.UserId(context.User) == id)
        {
            return Problems.Conflict("CannotDeleteSelf", "You cannot delete the account you are signed in with.");
        }

        if (user.Role == AdminRoles.Admin && store.CountAdmins() <= 1)
        {
            return Problems.Conflict("LastAdmin", "The last admin cannot be deleted.");
        }

        if (!store.DeleteUser(id))
        {
            return Problems.NotFound("The user");
        }

        AdminApi.Audit(
            context, store, "user.delete", id.ToString(CultureInfo.InvariantCulture), null,
            new() { ["username"] = user.Username, ["role"] = user.Role });
        return Results.NoContent();
    }

    private static void CheckPassword(string? password, AdminAuthOptions options, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(password) || password.Length > MaxPasswordLength)
        {
            errors["password"] = [$"A password (up to {MaxPasswordLength} characters) is required."];
        }
        else if (password.Length < options.MinPasswordLength)
        {
            errors["password"] = [$"The password must be at least {options.MinPasswordLength} characters."];
        }
    }

    private static AdminUserDto ToDto(AdminUserRecord user) =>
        new(user.Id, user.Username, user.Role, user.MustChange, user.CreatedAt);
}
