namespace X4MP.Server.Auth;

/// <summary>Role names stored in <c>admin_users.role</c> and <c>api_tokens.role</c>.</summary>
public static class AdminRoles
{
    public const string Admin = "Admin";
    public const string Viewer = "Viewer";

    /// <summary>
    /// A non-admin account (ADR-045): reads everything a Viewer reads (always including players' mod lists) and may edit the session mod list.
    /// It can change nothing else.
    /// </summary>
    public const string ModEditor = "ModEditor";

    public static bool IsValid(string? role) => role is Admin or Viewer or ModEditor;
}

/// <summary>
/// Authorization policy names. Later admin endpoints declare access with
/// <c>.RequireAuthorization(AdminPolicies.Admin)</c> (mutations) or <c>AdminPolicies.Viewer</c> (reads).
/// All reject a user who still must change the initial password.
/// </summary>
public static class AdminPolicies
{
    /// <summary>Admin role (cookie or bearer), password already changed.</summary>
    public const string Admin = "Admin";

    /// <summary>Admin, ModEditor or Viewer role (cookie or bearer), password already changed.</summary>
    public const string Viewer = "Viewer";

    /// <summary>Admin or ModEditor role (cookie or bearer), password already changed: may edit the session mod list.</summary>
    public const string ModEditor = "ModEditor";

    /// <summary>Any authenticated principal, even one that must change its password (auth endpoints only).</summary>
    public const string Authenticated = "Authenticated";

    /// <summary>Browser/cookie session only (logout, change-password).</summary>
    public const string Session = "Session";
}
