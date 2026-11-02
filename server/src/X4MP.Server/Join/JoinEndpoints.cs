using X4MP.Core.Session;
using X4MP.Server.Api;

namespace X4MP.Server.Join;

/// <summary>
/// The two endpoints a launcher needs before it holds a node connection (mod-management 4.1): the nonce for the join-password proof and
/// the session's mod manifest. Both are anonymous; on a server with a join password the manifest itself asks for the proof.
/// </summary>
internal static class JoinEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var join = routes.MapGroup("/api/v1/join");

        join.MapGet("/nonce", (JoinNonceStore nonces, GatewayState gateway) =>
            Results.Json(
                new JoinNonceDto(Convert.ToHexStringLower(nonces.Issue()), gateway.JoinPasswordHash is not null, nonces.ExpiresInSeconds),
                ApiJsonContext.Default.JoinNonceDto))
            .AllowAnonymous();

        join.MapGet("/mod-manifest", (HttpRequest request, GatewayState gateway, JoinNonceStore nonces, JoinModManifestBuilder builder) =>
            JoinAuth.Check(request, gateway, nonces) ?? Results.Json(builder.Build(), ApiJsonContext.Default.JoinModManifestDto))
            .AllowAnonymous();
    }
}
