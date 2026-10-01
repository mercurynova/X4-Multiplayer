using System.Net;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.Sqlite;

namespace X4MP.Persistence.Tests;

/// <summary>A migrated temporary database with a write-behind writer and three bound players (for the team tests).</summary>
public abstract class TeamDbFixture : IDisposable
{
    protected static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] PlayerNames = ["Alice", "Bob", "Cleo"];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-teamstore-" + Guid.NewGuid().ToString("N"));

    protected TeamDbFixture()
    {
        var options = new PersistenceOptions { DataDir = _dir };
        Factory = new SqliteConnectionFactory(options);
        new MigrationRunner(Factory).Migrate();
        Writer = new PersistenceWriter(Factory, options);
        var nodes = new SqliteNodeStore(Factory);
        Players =
        [
            .. PlayerNames.Select(name =>
                nodes.BindAsync(name, SHA256.HashData(RandomNumberGenerator.GetBytes(32)), IPAddress.Loopback, T0, default).AsTask().GetAwaiter().GetResult().PlayerId),
        ];
    }

    protected SqliteConnectionFactory Factory { get; }

    protected PersistenceWriter Writer { get; }

    /// <summary>Alice, Bob and Cleo (<c>players.id</c>).</summary>
    protected int[] Players { get; }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
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

    protected T Scalar<T>(string sql, object? args = null)
    {
        using var c = Factory.Open();
        return c.ExecuteScalar<T>(sql, args)!;
    }

    protected async Task<long> NewSessionAsync(string name = "S") =>
        await new SqliteSessionStore(Factory, Writer).BeginSessionAsync(name, Guid.NewGuid(), T0, default);
}
