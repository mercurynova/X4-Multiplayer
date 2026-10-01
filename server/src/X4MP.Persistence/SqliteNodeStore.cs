using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Dapper;
using Microsoft.Data.Sqlite;
using X4MP.Core.Session;

namespace X4MP.Persistence;

/// <summary>
/// SQLite implementation of <see cref="IPlayerStore"/> and <see cref="IBanStore"/> over the
/// <c>players</c> and <c>bans</c> tables of migration 0001 (no schema change needed). Identity binding is
/// a short synchronous transaction on a pooled connection (the gateway needs the player id before it can
/// send <c>Welcome</c>); everything else the session does goes through <see cref="PersistenceWriter"/>.
/// </summary>
public sealed class SqliteNodeStore(SqliteConnectionFactory factory) : IPlayerStore, IBanStore
{
    private static string Stamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    public ValueTask<PlayerBindResult> BindAsync(string name, ReadOnlyMemory<byte> keyHash, IPAddress? ip, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ct.ThrowIfCancellationRequested();
        var hash = keyHash.ToArray();
        string stamp = Stamp(now);
        string? address = ip?.ToString();

        using var connection = factory.Open();
        using var tx = connection.BeginTransaction(deferred: false);

        var byKey = connection.QuerySingleOrDefault<PlayerRow>(
            "SELECT id AS Id, name AS Name FROM players WHERE key_hash = @hash", new { hash }, tx);
        var byName = connection.QuerySingleOrDefault<PlayerRow>(
            "SELECT id AS Id, name AS Name FROM players WHERE name = @name COLLATE NOCASE", new { name }, tx);

        if (byName is not null && (byKey is null || byKey.Id != byName.Id))
        {
            return ValueTask.FromResult(new PlayerBindResult(PlayerBindStatus.NameTaken, 0, false)); // name bound to another key
        }

        if (byKey is not null)
        {
            var existing = byKey;
            connection.Execute(
                "UPDATE players SET name = @name, last_seen = @stamp, last_ip = @address WHERE id = @id",
                new { name, stamp, address, id = existing.Id }, tx);
            tx.Commit();
            return ValueTask.FromResult(new PlayerBindResult(PlayerBindStatus.Ok, (int)existing.Id, false));
        }

        long id = connection.ExecuteScalar<long>(
            "INSERT INTO players (name, key_hash, first_seen, last_seen, last_ip) VALUES (@name, @hash, @stamp, @stamp, @address); SELECT last_insert_rowid();",
            new { name, hash, stamp, address }, tx);
        tx.Commit();
        return ValueTask.FromResult(new PlayerBindResult(PlayerBindStatus.Ok, (int)id, true));
    }

    public ValueTask<BanInfo?> FindActiveBanAsync(ReadOnlyMemory<byte> keyHash, IPAddress? ip, DateTimeOffset now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string stamp = Stamp(now);
        using var connection = factory.Open();

        if (!keyHash.IsEmpty)
        {
            var hash = keyHash.ToArray();
            string? reason = connection.QueryFirstOrDefault<string?>(
                """
                SELECT b.reason FROM bans b JOIN players p ON p.id = b.player_id
                WHERE p.key_hash = @hash AND b.revoked_at IS NULL AND (b.expires_at IS NULL OR b.expires_at > @stamp)
                LIMIT 1
                """,
                new { hash, stamp });
            if (reason is not null)
            {
                return ValueTask.FromResult<BanInfo?>(new BanInfo(reason, false));
            }
        }

        if (ip is not null)
        {
            var networks = connection.Query<NetworkBanRow>(
                """
                SELECT ip_cidr AS Cidr, reason AS Reason FROM bans
                WHERE ip_cidr IS NOT NULL AND revoked_at IS NULL AND (expires_at IS NULL OR expires_at > @stamp)
                """,
                new { stamp });
            foreach (var row in networks)
            {
                if (TryParseNetwork(row.Cidr, out var network) && network.Contains(ip))
                {
                    return ValueTask.FromResult<BanInfo?>(new BanInfo(row.Reason, true));
                }
            }
        }

        return ValueTask.FromResult<BanInfo?>(null);
    }

    public ValueTask AddIpBanAsync(IPAddress ip, string reason, string createdBy, DateTimeOffset now, DateTimeOffset? expires, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ip);
        ct.ThrowIfCancellationRequested();
        int bits = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        using var connection = factory.Open();
        connection.Execute(
            "INSERT INTO bans (ip_cidr, reason, created_by, created_at, expires_at) VALUES (@cidr, @reason, @createdBy, @created, @expires)",
            new
            {
                cidr = $"{ip}/{bits}",
                reason,
                createdBy,
                created = Stamp(now),
                expires = expires is { } e ? Stamp(e) : null,
            });
        return ValueTask.CompletedTask;
    }

    /// <summary>Accepts <c>a.b.c.d</c>, <c>a.b.c.d/n</c> and the IPv6 equivalents.</summary>
    private static bool TryParseNetwork(string text, out IPNetwork network)
    {
        if (IPNetwork.TryParse(text, out network))
        {
            return true;
        }

        if (IPAddress.TryParse(text, out var single))
        {
            network = new IPNetwork(single, single.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }

        return false;
    }

    private sealed class PlayerRow
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class NetworkBanRow
    {
        public string Cidr { get; set; } = string.Empty;

        public string Reason { get; set; } = string.Empty;
    }
}
