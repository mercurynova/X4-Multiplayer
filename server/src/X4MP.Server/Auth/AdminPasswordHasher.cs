using System.Security.Cryptography;

namespace X4MP.Server.Auth;

/// <summary>
/// PBKDF2-HMAC-SHA256 via <see cref="Rfc2898DeriveBytes.Pbkdf2(string, byte[], int, HashAlgorithmName, int)"/>.
/// Chosen over ASP.NET Identity's PasswordHasher because the schema already stores hash, salt and iteration
/// count per row (so the work factor can be raised later and rows upgraded on login), and this avoids an extra
/// package for what is a thin wrapper over the same primitive. Verification is constant-time.
/// </summary>
public static class AdminPasswordHasher
{
    public const int SaltSize = 16;
    public const int HashSize = 32;
    public const int MinIterations = 600_000;

    public static (byte[] Hash, byte[] Salt) Hash(string password, int iterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, MinIterations);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        return (Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSize), salt);
    }

    public static bool Verify(string password, byte[] salt, byte[] expectedHash, int iterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(expectedHash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }
}
