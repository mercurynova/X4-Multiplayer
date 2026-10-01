using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using X4MP.Persistence;

namespace X4MP.Server.Auth;

/// <summary>A row of <c>admin_users</c>.</summary>
public sealed record AdminUser(
    long Id, string Username, byte[] PasswordHash, byte[] PasswordSalt, int Iterations, string Role, bool MustChange, long PwVersion = 0);

/// <summary>An active (not revoked) row of <c>api_tokens</c>.</summary>
public sealed record ApiTokenInfo(long Id, string Name, string Role);

/// <summary>A row of <c>api_tokens</c> as the token list shows it (never the token itself).</summary>
public sealed record ApiTokenRecord(long Id, string Name, string Role, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt, string? OwnerName);

/// <summary>
/// Synchronous SQLite access for admin users, API tokens and the audit log. Writes go straight to the database
/// (not through the write-behind queue) because a password change or audit row must be durable before the
/// HTTP response is sent. Volume is tiny, so pooled short-lived connections are fine.
/// </summary>
public sealed class AdminStore(SqliteConnectionFactory connections, TimeProvider time)
{
    public const string TokenPrefix = "x4mp_";

    private string Now() => time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    public long CountUsers()
    {
        using var db = connections.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM admin_users";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public AdminUser? FindUser(string username)
    {
        using var db = connections.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = UserSelect + " WHERE username = $u COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$u", username);
        return ReadUser(cmd);
    }

    public AdminUser? FindUser(long id)
    {
        using var db = connections.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = UserSelect + " WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return ReadUser(cmd);
    }

    private const string UserSelect =
        "SELECT id, username, pw_hash, pw_salt, pw_iter, role, must_change, pw_version FROM admin_users";

    private static AdminUser? ReadUser(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new AdminUser(
            reader.GetInt64(0), reader.GetString(1), (byte[])reader["pw_hash"], (byte[])reader["pw_salt"],
            reader.GetInt32(4), reader.GetString(5), reader.GetInt64(6) != 0, reader.GetInt64(7));
    }

    public long CreateUser(string username, string password, string role, bool mustChange, int iterations)
    {
        var (hash, salt) = AdminPasswordHasher.Hash(password, iterations);
        using var db = connections.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "INSERT INTO admin_users (username, pw_hash, pw_salt, pw_iter, role, created_at, must_change) " +
            "VALUES ($u, $h, $s, $i, $r, $c, $m); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$s", salt);
        cmd.Parameters.AddWithValue("$i", iterations);
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$c", Now());
        cmd.Parameters.AddWithValue("$m", mustChange ? 1 : 0);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Stores a newly computed hash without ending any session (the work-factor upgrade at login, bootstrap).</summary>
    public void SetPassword(long id, string password, int iterations, bool mustChange)
    {
        var (hash, salt) = AdminPasswordHasher.Hash(password, iterations);
        using var db = connections.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE admin_users SET pw_hash=$h, pw_salt=$s, pw_iter=$i, must_change=$m WHERE id=$id";
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$s", salt);
        cmd.Parameters.AddWithValue("$i", iterations);
        cmd.Parameters.AddWithValue("$m", mustChange ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// A password CHANGE: stores the new hash, clears <c>must_change</c>, bumps <c>pw_version</c> (every cookie issued before no longer
    /// validates) and revokes the API tokens this user minted, all in one transaction. Returns the updated user.
    /// </summary>
    public AdminUser ChangePassword(long id, string password, int iterations)
    {
        var (hash, salt) = AdminPasswordHasher.Hash(password, iterations);
        using var db = connections.Open();
        using var tx = db.BeginTransaction();
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "UPDATE admin_users SET pw_hash=$h, pw_salt=$s, pw_iter=$i, must_change=0, pw_version=pw_version+1, pw_changed_at=$now WHERE id=$id";
            cmd.Parameters.AddWithValue("$h", hash);
            cmd.Parameters.AddWithValue("$s", salt);
            cmd.Parameters.AddWithValue("$i", iterations);
            cmd.Parameters.AddWithValue("$now", Now());
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        using (var revoke = db.CreateCommand())
        {
            revoke.Transaction = tx;
            revoke.CommandText = "UPDATE api_tokens SET revoked_at=$now WHERE owner_id=$id AND revoked_at IS NULL";
            revoke.Parameters.AddWithValue("$now", Now());
            revoke.Parameters.AddWithValue("$id", id);
            revoke.ExecuteNonQuery();
        }

        tx.Commit();
        return FindUser(id) ?? throw new InvalidOperationException("The user vanished during a password change.");
    }

    /// <summary>Creates an API token and returns its plaintext, which is never stored (only its SHA-256).</summary>
    public string CreateToken(string name, string role, long? ownerId = null) => CreateTokenWithInfo(name, role, ownerId).Plaintext;

    /// <summary>Like <see cref="CreateToken"/>, but also returns the new row's id. <paramref name="ownerId"/> ties it to a user's password.</summary>
    public (long Id, string Plaintext) CreateTokenWithInfo(string name, string role, long? ownerId = null)
    {
        if (!AdminRoles.IsValid(role))
        {
            throw new ArgumentException("Unknown role.", nameof(role));
        }

        var plaintext = TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        using var db = connections.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "INSERT INTO api_tokens (name, token_hash, role, created_at, owner_id) VALUES ($n, $h, $r, $c, $o); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$h", HashToken(plaintext));
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$c", Now());
        cmd.Parameters.AddWithValue("$o", (object?)ownerId ?? DBNull.Value);
        return (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture), plaintext);
    }

    /// <summary>The user a token was minted by (null for an ownerless token or an unknown id).</summary>
    public long? TokenOwner(long tokenId)
    {
        using var db = connections.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT owner_id FROM api_tokens WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", tokenId);
        return cmd.ExecuteScalar() is long owner ? owner : null;
    }

    /// <summary>Every token, newest first (revoked ones included, flagged).</summary>
    public IReadOnlyList<ApiTokenRecord> ListTokens()
    {
        using var db = connections.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT t.id, t.name, t.role, t.created_at, t.last_used_at, t.revoked_at, u.username " +
            "FROM api_tokens t LEFT JOIN admin_users u ON u.id = t.owner_id ORDER BY t.id DESC";
        using var reader = cmd.ExecuteReader();
        var list = new List<ApiTokenRecord>();
        while (reader.Read())
        {
            list.Add(new ApiTokenRecord(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), ParseTime(reader.GetString(3)),
                reader.IsDBNull(4) ? null : ParseTime(reader.GetString(4)), reader.IsDBNull(5) ? null : ParseTime(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return list;
    }

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public ApiTokenInfo? FindToken(string plaintext)
    {
        using var db = connections.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, name, role FROM api_tokens WHERE token_hash = $h AND revoked_at IS NULL";
        cmd.Parameters.AddWithValue("$h", HashToken(plaintext));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var info = new ApiTokenInfo(reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
        reader.Close();

        // Touch at most once a minute so reads do not become a write per request.
        using var touch = db.CreateCommand();
        touch.CommandText = "UPDATE api_tokens SET last_used_at=$now WHERE id=$id AND (last_used_at IS NULL OR last_used_at < $cutoff)";
        touch.Parameters.AddWithValue("$now", Now());
        touch.Parameters.AddWithValue("$id", info.Id);
        touch.Parameters.AddWithValue("$cutoff", time.GetUtcNow().AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture));
        touch.ExecuteNonQuery();
        return info;
    }

    /// <summary>Revokes a token. False when it does not exist or was already revoked.</summary>
    public bool RevokeToken(long id)
    {
        using var db = connections.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE api_tokens SET revoked_at=$now WHERE id=$id AND revoked_at IS NULL";
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Appends an <c>audit_log</c> row. Never pass secrets in <paramref name="data"/>.</summary>
    public void Audit(string actor, string action, string? target, string? remoteIp, IReadOnlyDictionary<string, string?>? data = null)
    {
        using var db = connections.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO audit_log (ts, actor, action, target, data_json, remote_ip) VALUES ($ts, $a, $x, $t, $d, $ip)";
        cmd.Parameters.AddWithValue("$ts", Now());
        cmd.Parameters.AddWithValue("$a", actor);
        cmd.Parameters.AddWithValue("$x", action);
        cmd.Parameters.AddWithValue("$t", (object?)target ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", data is null ? DBNull.Value : JsonSerializer.Serialize(data));
        cmd.Parameters.AddWithValue("$ip", (object?)remoteIp ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public static byte[] HashToken(string plaintext) => SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
