namespace X4MP.Server.Api;

/// <summary>Body of <c>POST /api/v1/auth/login</c>.</summary>
[TsContract]
public sealed record LoginRequest(string Username, string Password);

/// <summary>Body of <c>POST /api/v1/auth/change-password</c>.</summary>
[TsContract]
public sealed record ChangePasswordRequest(string Current, string New);

/// <summary>Response of <c>GET /api/v1/auth/me</c>.</summary>
[TsContract]
public sealed record MeDto(string Username, string Role, bool MustChangePassword);

/// <summary>
/// The body of every error response of the admin API: RFC 7807 <c>application/problem+json</c> with the extensions
/// <c>code</c> (machine-readable, stable: <c>NotFound</c>, <c>ValidationFailed</c>, <c>Conflict</c>, <c>SessionNotRunning</c>, ...)
/// and <c>errors</c> (validation failures, message list per field or setting key). <c>type</c> is <c>urn:x4mp:problem:&lt;code&gt;</c>.
/// <c>errorCodes</c> carries a machine code per key when a key-level code exists (the settings API); <c>receivedBytes</c> is the
/// resume point on a failed upload chunk.
/// </summary>
[TsContract]
public sealed record ApiProblem(
    string Type,
    string Title,
    int Status,
    string Code,
    string? Detail,
    Dictionary<string, string[]>? Errors = null,
    Dictionary<string, string>? ErrorCodes = null,
    long? ReceivedBytes = null);
