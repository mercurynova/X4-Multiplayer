using X4MP.Core.Session;
using X4MP.Protocol;
using X4MP.Server.Api;

namespace X4MP.Server.Join;

/// <summary>
/// The join-password gate of the manifest (mod-management 4.1, protocol 4.3): the caller proves the session password over a nonce from
/// <c>GET /api/v1/join/nonce</c> with the same HMAC the node handshake uses. On a server without a join password the manifest is public.
/// The gate only keeps the mod list off an unauthenticated caller; it is not a session credential.
/// </summary>
internal static class JoinAuth
{
    public const string NonceHeader = "X-X4MP-Nonce";
    public const string PlayerKeyHeader = "X-X4MP-Player-Key";
    public const string ProofHeader = "X-X4MP-Proof";
    private const int HexLength = HandshakeAuth.ProofLength * 2;

    /// <summary>Null when the request may read the manifest, otherwise the 401 to answer with.</summary>
    public static IResult? Check(HttpRequest request, GatewayState gateway, JoinNonceStore nonces)
    {
        byte[]? hash = gateway.JoinPasswordHash;
        if (hash is null)
        {
            return null;
        }

        if (!TryRead(request, NonceHeader, out var nonce)
            || !TryRead(request, PlayerKeyHeader, out var playerKey)
            || !TryRead(request, ProofHeader, out var proof))
        {
            return Unauthorized(
                "JoinAuthRequired",
                "This session has a join password.",
                $"Send {NonceHeader}, {PlayerKeyHeader} and {ProofHeader}, all 32 bytes as 64 hex characters, from GET /api/v1/join/nonce.");
        }

        if (!nonces.TryConsume(nonce))
        {
            return Unauthorized(
                "JoinNonceExpired", "The nonce is unknown, already used or expired.", "Ask GET /api/v1/join/nonce for a new one and send the request again.");
        }

        var expected = HandshakeAuth.ComputeProofFromHash(hash, nonce, playerKey);
        return HandshakeAuth.ProofsEqual(expected, proof)
            ? null
            : Unauthorized("JoinAuthFailed", "The join password proof does not match.", "Check the password and compute the proof again.");
    }

    private static IResult Unauthorized(string code, string title, string detail) =>
        Problems.Result(StatusCodes.Status401Unauthorized, code, title, detail);

    private static bool TryRead(HttpRequest request, string name, out byte[] value)
    {
        value = [];
        string text = request.Headers[name].ToString();
        if (text.Length != HexLength)
        {
            return false;
        }

        try
        {
            value = Convert.FromHexString(text);
        }
        catch (FormatException)
        {
            return false;
        }

        return value.Length == HandshakeAuth.ProofLength;
    }
}
