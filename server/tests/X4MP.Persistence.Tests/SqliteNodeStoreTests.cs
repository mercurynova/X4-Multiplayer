using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.Sqlite;
using X4MP.Core.Session;

namespace X4MP.Persistence.Tests;

public sealed class SqliteNodeStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-nodestore-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteNodeStore _store;
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public SqliteNodeStoreTests()
    {
        _factory = new SqliteConnectionFactory(new PersistenceOptions { DataDir = _dir });
        new MigrationRunner(_factory).Migrate();
        _store = new SqliteNodeStore(_factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort temp cleanup
        }
    }

    private static byte[] Key() => SHA256.HashData(RandomNumberGenerator.GetBytes(32));

    private static string Stamp(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private T Scalar<T>(string sql, object? args = null)
    {
        using var c = _factory.Open();
        return c.ExecuteScalar<T>(sql, args)!;
    }

    [Fact]
    public async Task BindCreatesPlayerThenReturnsSameIdForSameKey()
    {
        var key = Key();
        var first = await _store.BindAsync("Alice", key, IPAddress.Parse("192.168.1.5"), Now, default);
        Assert.Equal(PlayerBindStatus.Ok, first.Status);
        Assert.True(first.IsNew);
        Assert.Equal(1, first.PlayerId);

        var again = await _store.BindAsync("Alice", key, IPAddress.Parse("192.168.1.6"), Now.AddHours(1), default);
        Assert.Equal(first.PlayerId, again.PlayerId);
        Assert.False(again.IsNew);

        Assert.Equal("192.168.1.6", Scalar<string>("SELECT last_ip FROM players WHERE id = 1"));
        Assert.Equal(Stamp(Now), Scalar<string>("SELECT first_seen FROM players WHERE id = 1"));
        Assert.Equal(Stamp(Now.AddHours(1)), Scalar<string>("SELECT last_seen FROM players WHERE id = 1"));
        Assert.Equal(1, Scalar<int>("SELECT COUNT(*) FROM players"));
    }

    [Fact]
    public async Task NameStaysBoundToTheFirstKeyCaseInsensitively()
    {
        await _store.BindAsync("Alice", Key(), null, Now, default);
        var taken = await _store.BindAsync("aLiCe", Key(), null, Now, default);
        Assert.Equal(PlayerBindStatus.NameTaken, taken.Status);
        Assert.Equal(1, Scalar<int>("SELECT COUNT(*) FROM players")); // no row created for the loser
    }

    [Fact]
    public async Task KeyCanRenameToAFreeNameButNotToATakenOne()
    {
        var a = Key();
        var b = Key();
        var ida = (await _store.BindAsync("Alice", a, null, Now, default)).PlayerId;
        await _store.BindAsync("Bob", b, null, Now, default);

        var renamed = await _store.BindAsync("Alicia", a, null, Now, default);
        Assert.Equal(ida, renamed.PlayerId);
        Assert.Equal("Alicia", Scalar<string>("SELECT name FROM players WHERE id = @ida", new { ida }));

        Assert.Equal(PlayerBindStatus.NameTaken, (await _store.BindAsync("Bob", a, null, Now, default)).Status);
    }

    [Fact]
    public async Task IdentitySurvivesAServerRestart()
    {
        var key = Key();
        int id = (await _store.BindAsync("Alice", key, null, Now, default)).PlayerId;
        SqliteConnection.ClearAllPools();

        var restarted = new SqliteNodeStore(new SqliteConnectionFactory(new PersistenceOptions { DataDir = _dir }));
        Assert.Equal(id, (await restarted.BindAsync("Alice", key, null, Now, default)).PlayerId);
        Assert.Equal(PlayerBindStatus.NameTaken, (await restarted.BindAsync("Alice", Key(), null, Now, default)).Status);
    }

    private async Task<int> PlayerWithKeyAsync(byte[] key) => (await _store.BindAsync("p" + Guid.NewGuid().ToString("N")[..8], key, null, Now, default)).PlayerId;

    private void InsertBan(int? playerId, string? cidr, string reason, DateTimeOffset? expires = null, DateTimeOffset? revoked = null)
    {
        using var c = _factory.Open();
        c.Execute(
            "INSERT INTO bans (player_id, ip_cidr, reason, created_by, created_at, expires_at, revoked_at) VALUES (@playerId, @cidr, @reason, 'test', @created, @expires, @revoked)",
            new { playerId, cidr, reason, created = Stamp(Now), expires = expires is { } e ? Stamp(e) : null, revoked = revoked is { } r ? Stamp(r) : null });
    }

    [Fact]
    public async Task KeyBanMatchesThePlayersKeyHash()
    {
        var banned = Key();
        var other = Key();
        int id = await PlayerWithKeyAsync(banned);
        await PlayerWithKeyAsync(other);
        InsertBan(id, null, "cheating");

        var hit = await _store.FindActiveBanAsync(banned, null, Now, default);
        Assert.NotNull(hit);
        Assert.Equal("cheating", hit.Reason);
        Assert.False(hit.IsIpBan);
        Assert.Null(await _store.FindActiveBanAsync(other, null, Now, default));
        Assert.Null(await _store.FindActiveBanAsync(Key(), null, Now, default));
    }

    [Fact]
    public async Task ExpiredAndRevokedBansDoNotApply()
    {
        var key = Key();
        int id = await PlayerWithKeyAsync(key);
        InsertBan(id, null, "old", expires: Now.AddMinutes(-1));
        InsertBan(id, null, "lifted", revoked: Now.AddDays(-1));
        Assert.Null(await _store.FindActiveBanAsync(key, null, Now, default));

        InsertBan(id, null, "temp", expires: Now.AddMinutes(5));
        Assert.Equal("temp", (await _store.FindActiveBanAsync(key, null, Now, default))!.Reason);
        Assert.Null(await _store.FindActiveBanAsync(key, null, Now.AddMinutes(6), default)); // expired by then
    }

    [Fact]
    public async Task CidrBansMatchAddressesInsideTheRange()
    {
        InsertBan(null, "10.9.0.0/16", "bad lan");
        InsertBan(null, "203.0.113.7", "single host");
        InsertBan(null, "2001:db8::/32", "v6 range");

        Assert.Equal("bad lan", (await _store.FindActiveBanAsync(ReadOnlyMemory<byte>.Empty, IPAddress.Parse("10.9.200.1"), Now, default))!.Reason);
        Assert.Null(await _store.FindActiveBanAsync(ReadOnlyMemory<byte>.Empty, IPAddress.Parse("10.10.0.1"), Now, default));
        Assert.Equal("single host", (await _store.FindActiveBanAsync(ReadOnlyMemory<byte>.Empty, IPAddress.Parse("203.0.113.7"), Now, default))!.Reason);
        Assert.Null(await _store.FindActiveBanAsync(ReadOnlyMemory<byte>.Empty, IPAddress.Parse("203.0.113.8"), Now, default));
        var v6 = await _store.FindActiveBanAsync(ReadOnlyMemory<byte>.Empty, IPAddress.Parse("2001:db8:1::5"), Now, default);
        Assert.True(v6!.IsIpBan);
    }

    [Fact]
    public async Task AddIpBanIsReadBackUntilItExpires()
    {
        var ip = IPAddress.Parse("198.51.100.4");
        await _store.AddIpBanAsync(ip, "TooManyViolations", "system", Now, Now.AddMinutes(5), default);

        Assert.Equal("198.51.100.4/32", Scalar<string>("SELECT ip_cidr FROM bans"));
        Assert.Equal("system", Scalar<string>("SELECT created_by FROM bans"));
        Assert.NotNull(await _store.FindActiveBanAsync(ReadOnlyMemory<byte>.Empty, ip, Now.AddMinutes(4), default));
        Assert.Null(await _store.FindActiveBanAsync(ReadOnlyMemory<byte>.Empty, ip, Now.AddMinutes(5), default));
        Assert.Null(await _store.FindActiveBanAsync(ReadOnlyMemory<byte>.Empty, IPAddress.Parse("198.51.100.5"), Now, default));

        await _store.AddIpBanAsync(IPAddress.Parse("2001:db8::1"), "manual", "admin", Now, null, default); // permanent
        Assert.NotNull(await _store.FindActiveBanAsync(ReadOnlyMemory<byte>.Empty, IPAddress.Parse("2001:db8::1"), Now.AddYears(5), default));
    }
}
