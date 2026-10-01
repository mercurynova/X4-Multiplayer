namespace X4MP.Server.Api;

/// <summary>Body of <c>POST /api/auth/login</c>.</summary>
[TsContract]
public sealed record LoginRequest(string Username, string Password);

/// <summary>Body of <c>POST /api/auth/change-password</c>.</summary>
[TsContract]
public sealed record ChangePasswordRequest(string Current, string New);

/// <summary>Response of <c>GET /api/auth/me</c>.</summary>
[TsContract]
public sealed record MeDto(string Username, string Role, bool MustChangePassword);

/// <summary>RFC 7807-style error body with a machine-readable <c>code</c> (server-design 4.1).</summary>
[TsContract]
public sealed record ApiProblem(string Title, int Status, string Code, string? Detail);
