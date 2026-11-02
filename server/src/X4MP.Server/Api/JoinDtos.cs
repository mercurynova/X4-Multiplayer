namespace X4MP.Server.Api;

/// <summary>Response of <c>GET /api/v1/join/nonce</c>: a one-time challenge for the join-password proof (mod-management 4.1).</summary>
public sealed record JoinNonceDto(string Nonce, bool PasswordRequired, int ExpiresInSeconds);

/// <summary>
/// Response of <c>GET /api/v1/join/mod-manifest</c> (mod-management 4.1): what a client has to install to join this session. Enum-valued
/// fields are plain strings so a launcher can read them without the server's enum definitions.
/// </summary>
public sealed record JoinModManifestDto(string Session, long PolicyVersion, string GameBuild, string UnknownDefault, List<JoinModDto> Mods);

/// <summary>
/// One row of the manifest. <c>Rule</c> is the rule that is enforced, so a Required entry the admin switched off arrives as Blocked;
/// <c>Version</c> is empty when any version is accepted, and <c>WorkshopId</c> is 0 when the mod is not on the Workshop. Links are
/// re-validated by every consumer (mod-management 3.6).
/// </summary>
public sealed record JoinModDto(
    string Id, string Name, string Rule, bool Enabled, string Class, string Version, long WorkshopId, string? NexusUrl, string Notes);
