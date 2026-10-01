using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace X4MP.Server.Auth;

/// <summary>
/// <c>Authorization: Bearer x4mp_&lt;token&gt;</c> authentication for scripts and CI. The token is looked up by its
/// SHA-256 hash in <c>api_tokens</c>. The SignalR hub may also pass it as <c>?access_token=</c>, accepted on
/// <c>/hubs/admin</c> only (server-design 4.2).
/// </summary>
public sealed class BearerTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AdminStore store) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "x4mp.bearer";

    /// <summary>Extracts the presented bearer token, or null when the request carries none.</summary>
    public static string? GetToken(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? header = request.Headers.Authorization;
        const string prefix = "Bearer ";
        if (header is not null && header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return header[prefix.Length..].Trim();
        }

        if (request.Path.StartsWithSegments("/hubs/admin", StringComparison.OrdinalIgnoreCase)
            && request.Query.TryGetValue("access_token", out var q) && !string.IsNullOrEmpty(q))
        {
            return q.ToString();
        }

        return null;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = GetToken(Request);
        if (token is null)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var info = token.StartsWith(AdminStore.TokenPrefix, StringComparison.Ordinal) ? store.FindToken(token) : null;
        if (info is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API token."));
        }

        var principal = AdminPrincipal.Create(SchemeName, $"token:{info.Id}", info.Name, info.Role, mustChange: false, AdminPrincipal.KindToken);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
