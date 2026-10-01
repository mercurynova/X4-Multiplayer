using System.Globalization;
using Microsoft.Data.Sqlite;
using X4MP.Persistence;

namespace X4MP.Server.Settings;

/// <summary>
/// Reads and writes the <c>config_overrides</c> table (server-design 2.8/2.9). Keys are API keys such as
/// <c>Replication.TickRateHz</c>; values are JSON scalars or arrays. Writes are synchronous so a PATCH is durable
/// before the response is sent.
/// </summary>
public sealed class SettingsOverridesStore(SqliteConnectionFactory connections, TimeProvider time)
{
    /// <summary>All overrides, key to value JSON. Empty when the database or table does not exist yet.</summary>
    public IReadOnlyDictionary<string, string> ReadAll()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(connections.DatabasePath))
        {
            return result;
        }

        try
        {
            using var db = connections.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT key, value_json FROM config_overrides";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetString(0)] = reader.GetString(1);
            }
        }
        catch (SqliteException)
        {
            // schema not migrated yet: the provider reloads after DatabaseStartup
        }

        return result;
    }

    /// <summary>Upserts (value JSON) or deletes (null) overrides in one transaction.</summary>
    public void Apply(IReadOnlyDictionary<string, string?> changes, string actor)
    {
        ArgumentNullException.ThrowIfNull(changes);
        using var db = connections.Open();
        using var tx = db.BeginTransaction();
        var now = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        foreach (var (key, valueJson) in changes)
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            if (valueJson is null)
            {
                cmd.CommandText = "DELETE FROM config_overrides WHERE key = $k";
            }
            else
            {
                cmd.CommandText =
                    "INSERT INTO config_overrides (key, value_json, updated_at, updated_by) VALUES ($k, $v, $t, $a) " +
                    "ON CONFLICT(key) DO UPDATE SET value_json = $v, updated_at = $t, updated_by = $a";
                cmd.Parameters.AddWithValue("$v", valueJson);
                cmd.Parameters.AddWithValue("$t", now);
                cmd.Parameters.AddWithValue("$a", actor);
            }

            cmd.Parameters.AddWithValue("$k", key);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
