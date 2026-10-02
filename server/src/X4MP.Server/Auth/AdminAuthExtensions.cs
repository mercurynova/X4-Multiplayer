using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using X4MP.Server.Api;

namespace X4MP.Server.Auth;

/// <summary>Single integration point for admin authentication: <c>AddAdminAuth</c> + <c>UseAdminAuth</c>.</summary>
public static class AdminAuthExtensions
{
    public const string CookieScheme = "x4mp.cookie";
    public const string CookieName = "x4mp_admin";
    public const string CsrfHeader = "X-X4MP";
    private const string SelectorScheme = "x4mp.admin";

    public static IServiceCollection AddAdminAuth(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new AdminAuthOptions();
        configuration.GetSection(AdminAuthOptions.SectionName).Bind(options);
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<NetworkAllowList>();
        services.AddSingleton<AdminStore>();
        services.AddSingleton<LoginThrottle>();
        services.AddHostedService<AdminBootstrap>();

        services.AddAuthentication(SelectorScheme)
            .AddPolicyScheme(SelectorScheme, displayName: null, o =>
                o.ForwardDefaultSelector = ctx => BearerTokenHandler.GetToken(ctx.Request) is null
                    ? CookieScheme
                    : BearerTokenHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, BearerTokenHandler>(BearerTokenHandler.SchemeName, _ => { })
            .AddCookie(CookieScheme, o =>
            {
                o.Cookie.Name = CookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(options.SessionHours);
                o.SlidingExpiration = true;
                o.Events = new CookieAuthenticationEvents
                {
                    // API clients get status codes, never redirects.
                    OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; },
                    OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; },
                    OnValidatePrincipal = ValidateSessionAsync,
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicies.Authenticated, p => p.RequireAuthenticatedUser())
            .AddPolicy(AdminPolicies.Session, p => p
                .AddAuthenticationSchemes(CookieScheme)
                .RequireAuthenticatedUser()
                .RequireClaim(AdminPrincipal.KindClaim, AdminPrincipal.KindSession))
            .AddPolicy(AdminPolicies.Viewer, p => p
                .RequireAuthenticatedUser()
                .RequireRole(AdminRoles.Admin, AdminRoles.Viewer, AdminRoles.ModEditor)
                .RequireAssertion(c => !AdminPrincipal.MustChange(c.User)))
            .AddPolicy(AdminPolicies.ModEditor, p => p
                .RequireAuthenticatedUser()
                .RequireRole(AdminRoles.Admin, AdminRoles.ModEditor)
                .RequireAssertion(c => !AdminPrincipal.MustChange(c.User)))
            .AddPolicy(AdminPolicies.Admin, p => p
                .RequireAuthenticatedUser()
                .RequireRole(AdminRoles.Admin)
                .RequireAssertion(c => !AdminPrincipal.MustChange(c.User)));
        return services;
    }

    /// <summary>
    /// Installs security headers, the IP allow-list, the CSRF header check, authentication/authorization and the
    /// <c>/api/v1/auth/*</c> endpoints. Call before the rest of the web pipeline is mapped.
    /// </summary>
    public static WebApplication UseAdminAuth(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var allowList = app.Services.GetRequiredService<NetworkAllowList>();

        app.Use(async (context, next) =>
        {
            SetSecurityHeaders(context.Response);
            if (!allowList.IsAllowed(context.Connection.RemoteIpAddress))
            {
                await Problem(StatusCodes.Status403Forbidden, "NetworkNotAllowed", "Requests from this network are not allowed.")
                    .ExecuteAsync(context);
                return;
            }

            if (IsStateChangingApiCall(context.Request) && !context.Request.Headers.ContainsKey(CsrfHeader)
                && BearerTokenHandler.GetToken(context.Request) is null)
            {
                await Problem(StatusCodes.Status400BadRequest, "CsrfHeaderMissing", $"State-changing requests need the {CsrfHeader} header.")
                    .ExecuteAsync(context);
                return;
            }

            await next(context);
        });

        app.UseAuthentication();
        app.UseAuthorization();
        AuthEndpoints.Map(app);
        return app;
    }

    internal static IResult Problem(int status, string code, string title, string? detail = null) =>
        Problems.Result(status, code, title, detail);

    private static bool IsStateChangingApiCall(HttpRequest request) =>
        request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);

    private static void SetSecurityHeaders(HttpResponse response)
    {
        var headers = response.Headers;
        headers.ContentSecurityPolicy =
            "default-src 'self'; connect-src 'self' ws: wss:; img-src 'self' data:; style-src 'self' 'unsafe-inline'";
        headers.XContentTypeOptions = "nosniff";
        headers.Append("Referrer-Policy", "no-referrer");
        headers.XFrameOptions = "DENY";
        var path = response.HttpContext.Request.Path;
        if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            headers.CacheControl = "no-store";
        }
    }

    private static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        var store = context.HttpContext.RequestServices.GetRequiredService<AdminStore>();
        var id = context.Principal is null ? null : AdminPrincipal.UserId(context.Principal);
        var user = id is null ? null : store.FindUser(id.Value);

        // A password change bumps pw_version: every cookie issued before it stops validating (the changing session is re-issued).
        if (user is null || !AdminPrincipal.HasPasswordVersion(context.Principal!, user.PwVersion))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieScheme);
            return;
        }

        var current = context.Principal!;
        if (user.Role != current.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value
            || user.MustChange != AdminPrincipal.MustChange(current))
        {
            context.ReplacePrincipal(AdminPrincipal.ForUser(user));
            context.ShouldRenew = true;
        }
    }
}
