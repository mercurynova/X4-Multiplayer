using System.Globalization;
using Microsoft.Data.Sqlite;
using X4MP.Core.Economy;

namespace X4MP.Persistence;

/// <summary>
/// Trades in SQLite (<c>0006_trades.sql</c>). Like <see cref="SqliteEconomyStore"/> it uses its own connection and is durable before it
/// returns (<c>synchronous=FULL</c>): a trade state change is acknowledged to players right after it. A trade and its lock set are one
/// transaction, and the primary key of <c>trade_locks</c> turns a second open trade on the same asset into a
/// <see cref="TradeLockConflictException"/>.
/// </summary>
public sealed class SqliteTradeStore(SqliteConnectionFactory factory, bool fullSync = true) : ITradeStore, IDisposable
{
    private const string OpenStates = "'Proposed','Countered','Accepted','Escrowed','Transferring','InDoubt'";

    private SqliteConnection? _connection;

    private SqliteConnection Db
    {
        get
        {
            if (_connection is { State: System.Data.ConnectionState.Open } open)
            {
                return open;
            }

            _connection?.Dispose();
            var connection = factory.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = fullSync ? "PRAGMA synchronous=FULL;" : "PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
            _connection = connection;
            return connection;
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }

    public IReadOnlyList<TradeRecord> Load(long sessionId, int recentTerminal)
    {
        var list = new List<TradeRecord>();
        using var cmd = Db.CreateCommand();
        cmd.CommandText =
            $"SELECT body_json FROM trades WHERE session_id = $s AND (state IN ({OpenStates}) " +
            $"OR id IN (SELECT id FROM trades WHERE session_id = $s AND state NOT IN ({OpenStates}) ORDER BY id DESC LIMIT $n)) ORDER BY id";
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$n", recentTerminal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(TradeRecord.FromJson(reader.GetString(0)));
        }

        return list;
    }

    public long MaxId(long sessionId)
    {
        using var cmd = Db.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(id), 0) FROM trades WHERE session_id = $s";
        cmd.Parameters.AddWithValue("$s", sessionId);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void Save(long sessionId, TradeRecord trade, IReadOnlyCollection<uint> locks)
    {
        ArgumentNullException.ThrowIfNull(trade);
        ArgumentNullException.ThrowIfNull(locks);
        var db = Db;
        using var transaction = db.BeginTransaction(deferred: false);
        try
        {
            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText =
                    "INSERT INTO trades (session_id, id, state, version, initiator, counterparty, body_json, created_at, updated_at) " +
                    "VALUES ($s, $id, $state, $v, $i, $c, $body, $created, $updated) " +
                    "ON CONFLICT(session_id, id) DO UPDATE SET state = excluded.state, version = excluded.version, body_json = excluded.body_json, updated_at = excluded.updated_at";
                cmd.Parameters.AddWithValue("$s", sessionId);
                cmd.Parameters.AddWithValue("$id", trade.Id);
                cmd.Parameters.AddWithValue("$state", trade.State.ToString());
                cmd.Parameters.AddWithValue("$v", (long)trade.Version);
                cmd.Parameters.AddWithValue("$i", trade.Initiator);
                cmd.Parameters.AddWithValue("$c", trade.Counterparty);
                cmd.Parameters.AddWithValue("$body", trade.ToJson());
                cmd.Parameters.AddWithValue("$created", Iso(trade.CreatedAt));
                cmd.Parameters.AddWithValue("$updated", Iso(trade.UpdatedAt));
                cmd.ExecuteNonQuery();
            }

            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM trade_locks WHERE session_id = $s AND trade_id = $id";
                cmd.Parameters.AddWithValue("$s", sessionId);
                cmd.Parameters.AddWithValue("$id", trade.Id);
                cmd.ExecuteNonQuery();
            }

            foreach (var asset in locks)
            {
                using var cmd = db.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "INSERT INTO trade_locks (session_id, entity_id, trade_id) VALUES ($s, $e, $id)";
                cmd.Parameters.AddWithValue("$s", sessionId);
                cmd.Parameters.AddWithValue("$e", (long)asset);
                cmd.Parameters.AddWithValue("$id", trade.Id);
                try
                {
                    cmd.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    throw new TradeLockConflictException(asset, HolderOf(db, transaction, sessionId, asset));
                }
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static long HolderOf(SqliteConnection db, SqliteTransaction transaction, long sessionId, uint asset)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT trade_id FROM trade_locks WHERE session_id = $s AND entity_id = $e";
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$e", (long)asset);
        return cmd.ExecuteScalar() is long holder ? holder : 0;
    }

    private static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
