namespace X4MP.Server.Api;

/// <summary>Response of <c>GET /healthz</c>.</summary>
public sealed record HealthzResponse(
    string Status,
    string Version,
    ProtocolRangeDto Protocol,
    long UptimeSeconds);

/// <summary>Range of wire-protocol versions this server accepts (placeholder until M1 negotiates it).</summary>
public sealed record ProtocolRangeDto(int Min, int Max);
