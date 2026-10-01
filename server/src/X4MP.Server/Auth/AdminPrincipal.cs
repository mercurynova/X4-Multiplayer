using System.Security.Claims;

namespace X4MP.Server.Auth;

/// <summary>Builds and reads the claims shared by the cookie and bearer schemes.</summary>
public static class AdminPrincipal
{
    public const string MustChangeClaim = "x4mp:must_change";
    public const string KindClaim = "x4mp:kind";
    public const string KindSession = "session";
    public const string KindToken = "token";

    public static ClaimsPrincipal Create(string scheme, string id, string name, string role, bool mustChange, string kind)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, id),
                new Claim(ClaimTypes.Name, name),
                new Claim(ClaimTypes.Role, role),
                new Claim(MustChangeClaim, mustChange ? "1" : "0"),
                new Claim(KindClaim, kind),
            ],
            scheme);
        return new ClaimsPrincipal(identity);
    }

    public static ClaimsPrincipal ForUser(AdminUser user) =>
        Create(AdminAuthExtensions.CookieScheme, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), user.Username, user.Role, user.MustChange, KindSession);

    public static bool MustChange(ClaimsPrincipal user) => user.HasClaim(MustChangeClaim, "1");

    public static long? UserId(ClaimsPrincipal user) =>
        user.HasClaim(KindClaim, KindSession)
        && long.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
}
