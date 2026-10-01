namespace X4MP.Server.Auth;

/// <summary>Admin authentication settings, bound from <c>X4MP:Admin</c> (server-design 4.2/4.3).</summary>
public sealed class AdminAuthOptions
{
    public const string SectionName = "X4MP:Admin";

    /// <summary>Extra CIDR ranges allowed to reach the admin API, in addition to loopback and (by default) private ranges.</summary>
    public string[] AllowedNetworks { get; set; } = [];

    /// <summary>Allow RFC1918, link-local, CGNAT (VPN) and IPv6 ULA/link-local clients. Loopback is always allowed.</summary>
    public bool AllowPrivateNetworks { get; set; } = true;

    /// <summary>PBKDF2-HMAC-SHA256 iterations for new hashes (OWASP: 600k). Stored per row.</summary>
    public int Pbkdf2Iterations { get; set; } = 600_000;

    /// <summary>Login attempts per IP per <see cref="LoginWindowSeconds"/>.</summary>
    public int LoginMaxPerWindow { get; set; } = 5;

    public int LoginWindowSeconds { get; set; } = 60;

    /// <summary>Consecutive failures for one username before it is locked.</summary>
    public int LockoutFailures { get; set; } = 10;

    public int LockoutMinutes { get; set; } = 5;

    /// <summary>Delay added to a failed attempt, multiplied by the (capped) consecutive failure count. 0 disables.</summary>
    public int FailureDelayMs { get; set; } = 500;

    public int SessionHours { get; set; } = 8;

    public int MinPasswordLength { get; set; } = 12;
}
