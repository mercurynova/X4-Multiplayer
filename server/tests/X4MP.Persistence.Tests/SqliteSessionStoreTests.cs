using System.Net;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.Sqlite;
using X4MP.Core.Session;
using X4MP.Proto;

namespace X4MP.Persistence.Tests;

public sealed class SqliteSessionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-sessionstore-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    private readonly PersistenceWriter _writer;
    private readonly SqliteSessionStore _store;
    private readonly int _playerId;
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public SqliteSessionStoreTests()
    {
        _factory = new SqliteConnectionFactory(new PersistenceOptions { DataDir = _dir });
        new MigrationRunner(_factory).Migrate();
        _writer = new PersistenceWriter(_factory, new PersistenceOptions { DataDir = _dir });
        _store = new SqliteSessionStore(_factory, _writer);
        var bind = new SqliteNodeStore(_factory)
            .BindAsync("Alice", SHA256.HashData(RandomNumberGenerator.GetBytes(32)), IPAddress.Loopback, T0, default)
            .AsTask().GetAwaiter().GetResult();
        _playerId = bind.PlayerId;
    }

    public void Dispose()
    {
        _writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        X4MP.Persistence.SqliteConnectionFactory.ClearPool(_dir);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort temp cleanup
        }
    }

    private T Scalar<T>(string sql, object? args = null)
    {
        using var c = _factory.Open();
        return c.ExecuteScalar<T>(sql, args)!;
    }

    [Fact]
    public async Task SessionRowFollowsThePhasesAndRecordsStartAndEnd()
    {
        var guid = Guid.NewGuid();
        long id = await _store.BeginSessionAsync("Friday", guid, T0, default);
        Assert.True(id > 0);
        Assert.Equal("Idle", Scalar<string>("SELECT state FROM sessions WHERE id = @id", new { id }));
        Assert.Contains(guid.ToString("D"), Scalar<string>("SELECT settings_json FROM sessions WHERE id = @id", new { id }));
        Assert.Null(Scalar<string?>("SELECT started_at FROM sessions WHERE id = @id", new { id }));

        _store.RecordPhase(id, SessionPhase.WaitingForAuthority, 0, T0.AddSeconds(1), "start");
        _store.RecordPhase(id, SessionPhase.AuthorityLoading, _playerId, T0.AddSeconds(2), "authority admitted");
        _store.RecordPhase(id, SessionPhase.Running, 0, T0.AddSeconds(3), null);
        await _writer.FlushAsync();

        Assert.Equal("Running", Scalar<string>("SELECT state FROM sessions WHERE id = @id", new { id }));
        Assert.Equal(_playerId, Scalar<int>("SELECT authority_player_id FROM sessions WHERE id = @id", new { id })); // kept when a later phase has none
        Assert.StartsWith("2026-10-01T12:00:01", Scalar<string>("SELECT started_at FROM sessions WHERE id = @id", new { id }));
        Assert.Null(Scalar<string?>("SELECT ended_at FROM sessions WHERE id = @id", new { id }));

        _store.RecordPhase(id, SessionPhase.Ended, 0, T0.AddMinutes(5), "authority did not return");
        await _writer.FlushAsync();
        Assert.Equal("Ended", Scalar<string>("SELECT state FROM sessions WHERE id = @id", new { id }));
        Assert.StartsWith("2026-10-01T12:05:00", Scalar<string>("SELECT ended_at FROM sessions WHERE id = @id", new { id }));
        Assert.Equal("authority did not return", Scalar<string>("SELECT end_reason FROM sessions WHERE id = @id", new { id }));
    }

    [Fact]
    public async Task PlayerRowsOpenOnJoinAndCloseOnLeaveInOrder()
    {
        long id = await _store.BeginSessionAsync("S", Guid.NewGuid(), T0, default);
        _store.PlayerJoined(id, _playerId, Role.Authority | Role.Client, T0.AddSeconds(1));
        _store.PlayerLeft(id, _playerId, T0.AddSeconds(30), "ResumeGraceExpired");
        _store.PlayerJoined(id, _playerId, Role.Client, T0.AddSeconds(40)); // rejoin: a second row
        await _writer.FlushAsync();

        Assert.Equal(2, Scalar<int>("SELECT COUNT(*) FROM session_players WHERE session_id = @id", new { id }));
        Assert.Equal("Authority, Client", Scalar<string>("SELECT role FROM session_players WHERE session_id = @id ORDER BY joined_at LIMIT 1", new { id }));
        Assert.Equal("ResumeGraceExpired", Scalar<string>("SELECT leave_reason FROM session_players WHERE session_id = @id AND left_at IS NOT NULL", new { id }));
        Assert.Equal(1, Scalar<int>("SELECT COUNT(*) FROM session_players WHERE session_id = @id AND left_at IS NULL", new { id }));
    }
}
