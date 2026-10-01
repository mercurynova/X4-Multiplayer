using System.Net;

namespace X4MP.Core.Session;

public enum PlayerBindStatus
{
    /// <summary>The name is bound to this key (new or existing player row).</summary>
    Ok,

    /// <summary>The name is already bound to a different player key.</summary>
    NameTaken,
}

/// <summary>Result of binding a (name, key) pair to a persistent player row.</summary>
public readonly record struct PlayerBindResult(PlayerBindStatus Status, int PlayerId, bool IsNew);

/// <summary>Why a connection was refused by a ban.</summary>
public sealed record BanInfo(string Reason, bool IsIpBan);

/// <summary>Persistent player identity (name bound to the first key seen with it; protocol.md 4.3).</summary>
public interface IPlayerStore
{
    /// <summary>
    /// Finds or creates the player row for <paramref name="keyHash"/> (SHA-256 of the player key) and binds
    /// <paramref name="name"/> to it. A name stays bound to the first key that used it: a different key
    /// asking for it gets <see cref="PlayerBindStatus.NameTaken"/>. Updates last-seen and last-IP.
    /// </summary>
    ValueTask<PlayerBindResult> BindAsync(string name, ReadOnlyMemory<byte> keyHash, IPAddress? ip, DateTimeOffset now, CancellationToken ct);
}

/// <summary>Bans by player key hash and by IP or CIDR (temporary IP bans use an expiry).</summary>
public interface IBanStore
{
    /// <summary>Returns the first active (not revoked, not expired) ban matching the key hash or the address.</summary>
    ValueTask<BanInfo?> FindActiveBanAsync(ReadOnlyMemory<byte> keyHash, IPAddress? ip, DateTimeOffset now, CancellationToken ct);

    /// <summary>Adds an IP ban (single address stored as /32 or /128). <paramref name="expires"/> null = permanent.</summary>
    ValueTask AddIpBanAsync(IPAddress ip, string reason, string createdBy, DateTimeOffset now, DateTimeOffset? expires, CancellationToken ct);
}

/// <summary>In-memory stores for tests and for running the server without persistence.</summary>
public sealed class InMemoryNodeStore : IPlayerStore, IBanStore
{
    private readonly object _gate = new();
    private readonly List<(int Id, string Name, byte[] KeyHash)> _players = [];
    private readonly List<(byte[]? KeyHash, IPNetwork? Network, string Reason, DateTimeOffset? Expires)> _bans = [];

    public ValueTask<PlayerBindResult> BindAsync(string name, ReadOnlyMemory<byte> keyHash, IPAddress? ip, DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            var byName = _players.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            var byKey = _players.FindIndex(p => keyHash.Span.SequenceEqual(p.KeyHash));
            if (byName >= 0 && byName != byKey)
            {
                return ValueTask.FromResult(new PlayerBindResult(PlayerBindStatus.NameTaken, 0, false));
            }

            if (byKey >= 0)
            {
                var existing = _players[byKey];
                _players[byKey] = existing with { Name = name };
                return ValueTask.FromResult(new PlayerBindResult(PlayerBindStatus.Ok, existing.Id, false));
            }

            int id = _players.Count + 1;
            _players.Add((id, name, keyHash.ToArray()));
            return ValueTask.FromResult(new PlayerBindResult(PlayerBindStatus.Ok, id, true));
        }
    }

    /// <summary>Test helper: ban a key hash.</summary>
    public void BanKey(byte[] keyHash, string reason = "test")
    {
        lock (_gate)
        {
            _bans.Add((keyHash, null, reason, null));
        }
    }

    /// <summary>Test helper: ban an address or CIDR like <c>10.1.0.0/16</c>.</summary>
    public void BanNetwork(string cidr, string reason = "test")
    {
        lock (_gate)
        {
            _bans.Add((null, IPNetwork.Parse(cidr), reason, null));
        }
    }

    public ValueTask<BanInfo?> FindActiveBanAsync(ReadOnlyMemory<byte> keyHash, IPAddress? ip, DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var ban in _bans)
            {
                if (ban.Expires is { } expires && expires <= now)
                {
                    continue;
                }

                if (ban.KeyHash is not null && keyHash.Span.SequenceEqual(ban.KeyHash))
                {
                    return ValueTask.FromResult<BanInfo?>(new BanInfo(ban.Reason, false));
                }

                if (ban.Network is { } net && ip is not null && net.Contains(ip))
                {
                    return ValueTask.FromResult<BanInfo?>(new BanInfo(ban.Reason, true));
                }
            }
        }

        return ValueTask.FromResult<BanInfo?>(null);
    }

    public ValueTask AddIpBanAsync(IPAddress ip, string reason, string createdBy, DateTimeOffset now, DateTimeOffset? expires, CancellationToken ct)
    {
        lock (_gate)
        {
            _bans.Add((null, new IPNetwork(ip, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128), reason, expires));
        }

        return ValueTask.CompletedTask;
    }
}
