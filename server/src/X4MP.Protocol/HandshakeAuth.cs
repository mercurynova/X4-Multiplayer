using System.Security.Cryptography;
using System.Text;

namespace X4MP.Protocol;

/// <summary>
/// Challenge-response proofs (protocol.md 4.3). The password never crosses the wire:
/// <c>proof = HMAC-SHA256(key = SHA256(utf8(password)), msg = nonce || player_key)</c>.
/// </summary>
public static class HandshakeAuth
{
    public const int NonceLength = 32;
    public const int PlayerKeyLength = 32;
    public const int ProofLength = 32;

    /// <summary>SHA-256 of the UTF-8 password: what the server stores.</summary>
    public static byte[] HashPassword(string password) => SHA256.HashData(Encoding.UTF8.GetBytes(password));

    /// <summary>Proof for a plain password.</summary>
    public static byte[] ComputeProof(string password, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> playerKey) =>
        ComputeProofFromHash(HashPassword(password), nonce, playerKey);

    /// <summary>Proof from the stored password hash (server side).</summary>
    public static byte[] ComputeProofFromHash(ReadOnlySpan<byte> passwordHash, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> playerKey)
    {
        var msg = new byte[nonce.Length + playerKey.Length];
        nonce.CopyTo(msg);
        playerKey.CopyTo(msg.AsSpan(nonce.Length));
        return HMACSHA256.HashData(passwordHash, msg);
    }

    /// <summary>Constant-time proof comparison.</summary>
    public static bool ProofsEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => CryptographicOperations.FixedTimeEquals(a, b);
}
