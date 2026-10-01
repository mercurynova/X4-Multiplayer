using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using X4MP.Persistence;

namespace X4MP.Server.Auth;

/// <summary>A row of <c>admin_users</c>.</summary>
public sealed record AdminUser(
    long Id, string Username, byte[] PasswordHash, byte[] PasswordSalt, int Iterations, string Role, bool MustChange);

/// <summary>An active (not revoked) row of <c>api_tokens</c>.</summary>
public sealed record ApiTokenInfo(long Id, string Name, string Role);

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
        "SELECT id, username, pw_hash, pw_salt, pw_iter, role, must_change FROM admin_users";

    private static AdminUser? ReadUser(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new AdminUser(
            reader.GetInt64(0), reader.GetString(1), (byte[])reader["pw_hash"], (byte[])reader["pw_salt"],
            reader.GetInt32(4), reader.GetString(5), reader.GetInt64(6) != 0);
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

    /// <summary>Stores a newly computed hash (password change or iteration upgrade).</summary>
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

    /// <summary>Creates an API token and returns its plaintext, which is never stored (only its SHA-256).</summary>
    public string CreateToken(string name, string role)
    {
        if (!AdminRoles.IsValid(role))
        {
            throw new ArgumentException("Unknown role.", nameof(role));
        }

        var plaintext = TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        using var db = connections.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO api_tokens (name, token_hash, role, created_at) VALUES ($n, $h, $r, $c)";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$h", HashToken(plaintext));
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$c", Now());
        cmd.ExecuteNonQuery();
        return plaintext;
    }

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

    public void RevokeToken(long id)
    {
        using var db = connections.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE api_tokens SET revoked_at=$now WHERE id=$id AND revoked_at IS NULL";
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
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
