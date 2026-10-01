namespace X4MP.Server.Auth;

/// <summary>Role names stored in <c>admin_users.role</c> and <c>api_tokens.role</c>.</summary>
public static class AdminRoles
{
    public const string Admin = "Admin";
    public const string Viewer = "Viewer";

    public static bool IsValid(string? role) => role is Admin or Viewer;
}

/// <summary>
/// Authorization policy names. Later admin endpoints declare access with
/// <c>.RequireAuthorization(AdminPolicies.Admin)</c> (mutations) or <c>AdminPolicies.Viewer</c> (reads).
/// Both reject a user who still must change the initial password.
/// </summary>
public static class AdminPolicies
{
    /// <summary>Admin role (cookie or bearer), password already changed.</summary>
    public const string Admin = "Admin";

    /// <summary>Admin or Viewer role (cookie or bearer), password already changed.</summary>
    public const string Viewer = "Viewer";

    /// <summary>Any authenticated principal, even one that must change its password (auth endpoints only).</summary>
    public const string Authenticated = "Authenticated";

    /// <summary>Browser/cookie session only (logout, change-password).</summary>
    public const string Session = "Session";
}
