using Microsoft.Data.Sqlite;

namespace X4MP.Persistence;

/// <summary>
/// Opens connections to the server database with the standard pragmas (server-design 2.8):
/// WAL journal, synchronous=NORMAL, foreign_keys=ON, busy_timeout. Connections come from the
/// Microsoft.Data.Sqlite pool, so short-lived read connections are cheap.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;
    private readonly int _busyTimeoutMs;

    public SqliteConnectionFactory(PersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        DatabasePath = options.DatabasePath;
        _busyTimeoutMs = options.BusyTimeoutMs;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>Opens a connection and applies the pragmas. The caller disposes it.</summary>
    public SqliteConnection Open()
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // journal_mode is persistent in the file; the rest are per-connection.
            // BusyTimeoutMs is an int from options, so interpolation cannot inject SQL.
            command.CommandText =
                "PRAGMA journal_mode=WAL;" +
                "PRAGMA synchronous=NORMAL;" +
                "PRAGMA foreign_keys=ON;" +
                "PRAGMA temp_store=MEMORY;" +
                $"PRAGMA busy_timeout={_busyTimeoutMs};";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
