using System.Security.Cryptography;
using System.Text;
using X4MP.Core.Net;
using X4MP.Proto;

namespace X4MP.Core.Session;

/// <summary>What the first admitted authority looked like. Later nodes must match it (ADR-004).</summary>
public sealed class AuthorityIdentity(string gameBuild, string modVersion, string modBuild, byte[] extensionsHash, IReadOnlyList<string> extensions)
{
    public string GameBuild { get; } = gameBuild;

    public string ModVersion { get; } = modVersion;

    public string ModBuild { get; } = modBuild;

    /// <summary>SHA-256 of the sorted <c>id@version</c> lines of the enabled extensions.</summary>
    public byte[] ExtensionsHash { get; } = extensionsHash;

    /// <summary>The same list, for the mismatch diff.</summary>
    public IReadOnlyList<string> Extensions { get; } = extensions;
}

/// <summary>
/// The live, mutable facts the gateway needs from the session (phase, authority identity, secrets, caps).
/// The SessionActor (M1-05) and the settings provider (M1-13) update it; the gateway only reads. Fields are
/// published with volatile semantics.
/// </summary>
public sealed class GatewayState
{
    private volatile string _serverName = "X4MP Server";
    private volatile byte[]? _joinPasswordHash;
    private volatile byte[]? _adminPasswordHash;
    private volatile AuthorityIdentity? _authority;
    private int _phase = (int)SessionPhase.Idle;
    private int _authorityLive;
    private int _designatedAuthorityPlayerId;
    private int _maxPlayers = 8;
    private long _serverCaps;

    public string ServerName
    {
        get => _serverName;
        set => _serverName = value;
    }

    public string ServerVersion { get; set; } = "0.1.0";

    public Guid SessionId { get; set; } = Guid.NewGuid();

    public SessionPhase Phase
    {
        get => (SessionPhase)Volatile.Read(ref _phase);
        set => Volatile.Write(ref _phase, (int)value);
    }

    public ulong ServerCaps
    {
        get => (ulong)Interlocked.Read(ref _serverCaps);
        set => Interlocked.Exchange(ref _serverCaps, (long)value);
    }

    public int MaxPlayers
    {
        get => Volatile.Read(ref _maxPlayers);
        set => Volatile.Write(ref _maxPlayers, value);
    }

    /// <summary>SHA-256 of the join password, or null for an open server.</summary>
    public byte[]? JoinPasswordHash
    {
        get => _joinPasswordHash;
        set => _joinPasswordHash = value;
    }

    /// <summary>SHA-256 of the admin password, or null if no node may claim Admin.</summary>
    public byte[]? AdminPasswordHash
    {
        get => _adminPasswordHash;
        set => _adminPasswordHash = value;
    }

    /// <summary>Identity of the session's authority (live or last known); null until one was admitted.</summary>
    public AuthorityIdentity? Authority
    {
        get => _authority;
        set => _authority = value;
    }

    /// <summary>True while an authority node is connected and holds the role.</summary>
    public bool AuthorityLive
    {
        get => Volatile.Read(ref _authorityLive) != 0;
        set => Volatile.Write(ref _authorityLive, value ? 1 : 0);
    }

    /// <summary>Player id allowed to claim Authority even while a live one exists (migration), or 0.</summary>
    public int DesignatedAuthorityPlayerId
    {
        get => Volatile.Read(ref _designatedAuthorityPlayerId);
        set => Volatile.Write(ref _designatedAuthorityPlayerId, value);
    }

    /// <summary>Initial state from boot options.</summary>
    public static GatewayState FromOptions(NetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new GatewayState
        {
            ServerName = options.ServerName,
            MaxPlayers = options.MaxPlayers,
            ServerCaps = options.ServerCaps,
            JoinPasswordHash = HashPassword(options.JoinPassword),
            AdminPasswordHash = HashPassword(options.AdminPassword),
        };
    }

    /// <summary>SHA-256 of the UTF-8 password; null for a null or empty password.</summary>
    public static byte[]? HashPassword(string? password) =>
        string.IsNullOrEmpty(password) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(password));

    /// <summary>
    /// <c>HMAC-SHA256(key = SHA256(password), msg = nonce || player_key)</c> (protocol.md 4.3). The same
    /// construction is used for the join password, the admin password and the team password.
    /// </summary>
    public static byte[] ComputeProof(byte[] passwordHash, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> playerKey)
    {
        ArgumentNullException.ThrowIfNull(passwordHash);
        var message = new byte[nonce.Length + playerKey.Length];
        nonce.CopyTo(message);
        playerKey.CopyTo(message.AsSpan(nonce.Length));
        return HMACSHA256.HashData(passwordHash, message);
    }
}
