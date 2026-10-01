using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using X4MP.Server.Api;
using X4MP.Server.Hosting;

namespace X4MP.Server.Auth;

/// <summary><c>/api/auth/{login,logout,me,change-password}</c>. Every outcome is written to <c>audit_log</c>.</summary>
internal static class AuthEndpoints
{
    private const int MaxPasswordLength = 256;

    // Verified against when the user does not exist, so unknown and known usernames cost the same.
    private static readonly (byte[] Hash, byte[] Salt) DummyHash = AdminPasswordHasher.Hash("x4mp-dummy-password", AdminPasswordHasher.MinIterations);

    public static void Map(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/auth");
        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapPost("/logout", LogoutAsync).RequireAuthorization(AdminPolicies.Session);
        group.MapGet("/me", Me).RequireAuthorization(AdminPolicies.Authenticated);
        group.MapPost("/change-password", ChangePasswordAsync).RequireAuthorization(AdminPolicies.Session);
    }

    private static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "local";

    private static IResult TooManyRequests(HttpContext ctx, string code, TimeSpan retryAfter)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        ctx.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return AdminAuthExtensions.Problem(StatusCodes.Status429TooManyRequests, code, "Too many attempts. Try again later.");
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest? body, HttpContext ctx, AdminStore store, LoginThrottle throttle, AdminAuthOptions options)
    {
        var ip = Ip(ctx);
        if (body is null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrEmpty(body.Password)
            || body.Username.Length > 64 || body.Password.Length > MaxPasswordLength)
        {
            return AdminAuthExtensions.Problem(StatusCodes.Status400BadRequest, "ValidationFailed", "Username and password are required.");
        }

        var username = body.Username.Trim();
        if (!throttle.TryAcquireAttempt(ip, out var ipRetry))
        {
            store.Audit(username, "auth.login.throttled", username, ip);
            return TooManyRequests(ctx, "RateLimited", ipRetry);
        }

        if (throttle.IsLocked(username, out var lockRetry))
        {
            store.Audit(username, "auth.login.locked", username, ip);
            return TooManyRequests(ctx, "AccountLocked", lockRetry);
        }

        var user = store.FindUser(username);
        var ok = user is not null
            && AdminPasswordHasher.Verify(body.Password, user.PasswordSalt, user.PasswordHash, user.Iterations);
        if (user is null)
        {
            _ = AdminPasswordHasher.Verify(body.Password, DummyHash.Salt, DummyHash.Hash, AdminPasswordHasher.MinIterations);
        }

        if (!ok || user is null)
        {
            var failures = throttle.RecordFailure(username);
            store.Audit(username, "auth.login.failure", username, ip, new Dictionary<string, string?> { ["failures"] = failures.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            var delay = throttle.FailureDelay(failures);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ctx.RequestAborted);
            }

            return Results.Json(
                new ApiProblem("Invalid username or password.", StatusCodes.Status401Unauthorized, "InvalidCredentials", null),
                ApiJsonContext.Default.ApiProblem, "application/problem+json", StatusCodes.Status401Unauthorized);
        }

        throttle.RecordSuccess(username);
        if (user.Iterations < options.Pbkdf2Iterations)
        {
            store.SetPassword(user.Id, body.Password, options.Pbkdf2Iterations, user.MustChange); // upgrade work factor
        }

        await ctx.SignInAsync(AdminAuthExtensions.CookieScheme, AdminPrincipal.ForUser(user));
        store.Audit(user.Username, "auth.login.success", user.Username, ip, new Dictionary<string, string?> { ["mustChange"] = user.MustChange ? "true" : "false" });
        return Results.NoContent();
    }

    private static async Task<IResult> LogoutAsync(HttpContext ctx, AdminStore store)
    {
        store.Audit(ctx.User.Identity?.Name ?? "unknown", "auth.logout", null, Ip(ctx));
        await ctx.SignOutAsync(AdminAuthExtensions.CookieScheme);
        return Results.NoContent();
    }

    private static IResult Me(ClaimsPrincipal user) =>
        Results.Json(
            new MeDto(
                user.Identity?.Name ?? string.Empty,
                user.FindFirstValue(ClaimTypes.Role) ?? string.Empty,
                AdminPrincipal.MustChange(user)),
            ApiJsonContext.Default.MeDto);

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest? body, HttpContext ctx, AdminStore store, LoginThrottle throttle, AdminAuthOptions options, DataDirInfo dataDir)
    {
        var ip = Ip(ctx);
        if (body is null || string.IsNullOrEmpty(body.Current) || string.IsNullOrEmpty(body.New)
            || body.Current.Length > MaxPasswordLength || body.New.Length > MaxPasswordLength)
        {
            return AdminAuthExtensions.Problem(StatusCodes.Status400BadRequest, "ValidationFailed", "Current and new password are required.");
        }

        var user = AdminPrincipal.UserId(ctx.User) is { } id ? store.FindUser(id) : null;
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (throttle.IsLocked(user.Username, out var lockRetry))
        {
            store.Audit(user.Username, "auth.password.locked", user.Username, ip);
            return TooManyRequests(ctx, "AccountLocked", lockRetry);
        }

        if (!AdminPasswordHasher.Verify(body.Current, user.PasswordSalt, user.PasswordHash, user.Iterations))
        {
            var failures = throttle.RecordFailure(user.Username);
            store.Audit(user.Username, "auth.password.failure", user.Username, ip, new Dictionary<string, string?> { ["reason"] = "wrong current password" });
            var delay = throttle.FailureDelay(failures);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ctx.RequestAborted);
            }

            return AdminAuthExtensions.Problem(StatusCodes.Status400BadRequest, "InvalidCurrentPassword", "The current password is incorrect.");
        }

        if (body.New.Length < options.MinPasswordLength)
        {
            return AdminAuthExtensions.Problem(StatusCodes.Status400BadRequest, "ValidationFailed", $"The new password must be at least {options.MinPasswordLength} characters.");
        }

        if (body.New == body.Current)
        {
            return AdminAuthExtensions.Problem(StatusCodes.Status400BadRequest, "ValidationFailed", "The new password must differ from the current one.");
        }

        throttle.RecordSuccess(user.Username);
        store.SetPassword(user.Id, body.New, options.Pbkdf2Iterations, mustChange: false);
        AdminBootstrap.DeleteInitialPasswordFile(dataDir.Path);
        await ctx.SignInAsync(AdminAuthExtensions.CookieScheme, AdminPrincipal.ForUser(user with { MustChange = false }));
        store.Audit(user.Username, "auth.password.change", user.Username, ip, new Dictionary<string, string?> { ["forced"] = user.MustChange ? "true" : "false" });
        return Results.NoContent();
    }
}
