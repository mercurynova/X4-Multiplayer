using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace X4MP.Persistence;

/// <summary>
/// Applies embedded <c>Migrations/NNNN_name.sql</c> scripts in order. Each script runs in its own
/// transaction together with its <c>schema_version</c> row, so a failure leaves the database at the
/// previous version. Re-running is a no-op once everything is applied.
/// </summary>
public sealed partial class MigrationRunner
{
    private readonly SqliteConnectionFactory _factory;
    private readonly IReadOnlyList<Migration> _migrations;

    public MigrationRunner(SqliteConnectionFactory factory)
        : this(factory, LoadEmbedded(typeof(MigrationRunner).Assembly))
    {
    }

    /// <summary>For tests and future callers that supply their own scripts.</summary>
    public MigrationRunner(SqliteConnectionFactory factory, IEnumerable<Migration> migrations)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(migrations);
        _factory = factory;
        _migrations = [.. migrations.OrderBy(m => m.Version)];

        for (var i = 0; i < _migrations.Count; i++)
        {
            if (_migrations[i].Version != i + 1)
            {
                throw new InvalidOperationException(
                    $"Migrations must be numbered contiguously from 1; found {_migrations[i].Version} at position {i + 1}.");
            }
        }
    }

    /// <summary>Highest version this build knows about.</summary>
    public int LatestVersion => _migrations.Count;

    /// <summary>Applies pending migrations and returns the resulting schema version.</summary>
    public int Migrate()
    {
        using var connection = _factory.Open();
        var current = GetCurrentVersion(connection);
        if (current > LatestVersion)
        {
            throw new InvalidOperationException(
                $"Database is at schema version {current}, newer than this server supports ({LatestVersion}).");
        }

        foreach (var migration in _migrations.Where(m => m.Version > current))
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                connection.Execute(migration.Sql, transaction: transaction);
                connection.Execute(
                    "INSERT INTO schema_version (version) VALUES (@Version)",
                    new { migration.Version },
                    transaction);
                transaction.Commit();
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                throw new InvalidOperationException(
                    $"Migration {migration.Version:0000}_{migration.Name} failed: {ex.Message}", ex);
            }
        }

        return GetCurrentVersion(connection);
    }

    /// <summary>Current schema version of the database, 0 if it has none.</summary>
    public static int GetCurrentVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var hasTable = connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_version'");
        return hasTable == 0
            ? 0
            : connection.ExecuteScalar<int?>("SELECT MAX(version) FROM schema_version") ?? 0;
    }

    private static List<Migration> LoadEmbedded(Assembly assembly)
    {
        var result = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = ResourceName().Match(resource);
            if (!match.Success)
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Missing embedded resource {resource}.");
            using var reader = new StreamReader(stream);
            result.Add(new Migration(
                int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture),
                match.Groups["name"].Value,
                reader.ReadToEnd()));
        }
        return result;
    }

    [GeneratedRegex(@"Migrations\.(?<version>\d{4})_(?<name>.+)\.sql$")]
    private static partial Regex ResourceName();
}

/// <summary>One schema migration script.</summary>
public sealed record Migration(int Version, string Name, string Sql);
