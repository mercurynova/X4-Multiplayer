using System.Security.Claims;

namespace X4MP.Server.Auth;

/// <summary>Builds and reads the claims shared by the cookie and bearer schemes.</summary>
public static class AdminPrincipal
{
    public const string MustChangeClaim = "x4mp:must_change";
    public const string KindClaim = "x4mp:kind";
    public const string KindSession = "session";
    public const string KindToken = "token";

    /// <summary>The user's <c>pw_version</c> when the cookie was issued; a cookie whose version is stale (the password changed since) is rejected.</summary>
    public const string PasswordVersionClaim = "x4mp:pwv";

    public static ClaimsPrincipal Create(string scheme, string id, string name, string role, bool mustChange, string kind, long? passwordVersion = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, id),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.Role, role),
            new(MustChangeClaim, mustChange ? "1" : "0"),
            new(KindClaim, kind),
        };
        if (passwordVersion is { } version)
        {
            claims.Add(new Claim(PasswordVersionClaim, version.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }

    public static ClaimsPrincipal ForUser(AdminUser user) =>
        Create(AdminAuthExtensions.CookieScheme, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), user.Username, user.Role, user.MustChange, KindSession, user.PwVersion);

    /// <summary>True when the principal carries the user's current password version (a cookie without one predates the claim and is not valid).</summary>
    public static bool HasPasswordVersion(ClaimsPrincipal principal, long current) =>
        principal.HasClaim(PasswordVersionClaim, current.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static bool MustChange(ClaimsPrincipal user) => user.HasClaim(MustChangeClaim, "1");

    public static long? UserId(ClaimsPrincipal user) =>
        user.HasClaim(KindClaim, KindSession)
        && long.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
}
