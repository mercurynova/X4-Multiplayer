using System.Globalization;
using Dapper;
using X4MP.Core.Relay;

namespace X4MP.Persistence;

/// <summary>
/// SQLite implementation of <see cref="IChatStore"/>: chat lines go into <c>chat_messages</c> and mutes into the <c>is_muted</c> and
/// <c>mute_until</c> columns of <c>players</c>, both through the write-behind <see cref="PersistenceWriter"/> (a full queue drops the
/// write and returns false; the actor never waits). <see cref="LoadMutes"/> reads synchronously at start.
/// </summary>
public sealed class SqliteChatStore(SqliteConnectionFactory factory, PersistenceWriter writer) : IChatStore
{
    private const string AppendSql =
        "INSERT INTO chat_messages (session_id, ts, from_player_id, from_admin, channel, text) VALUES (@session, @ts, @player, @admin, @channel, @text)";

    private static string Stamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    public bool Append(ChatLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return writer.TryEnqueue(
            AppendSql,
            new
            {
                session = line.SessionId > 0 ? (long?)line.SessionId : null,
                ts = Stamp(line.At),
                player = line.FromPlayerId,
                admin = line.FromAdmin,
                channel = line.Channel,
                text = line.Text,
            });
    }

    public bool SetMute(int playerId, MuteEntry? entry) =>
        writer.TryEnqueue(
            "UPDATE players SET is_muted = @muted, mute_until = @until WHERE id = @id",
            new { id = playerId, muted = entry is null ? 0 : 1, until = entry?.Until is { } until ? Stamp(until) : null });

    public IReadOnlyList<MuteEntry> LoadMutes()
    {
        using var connection = factory.Open();
        var now = Stamp(DateTimeOffset.UtcNow);
        return
        [
            .. connection
                .Query<(long Id, string? Until)>(
                    "SELECT id AS Id, mute_until AS Until FROM players WHERE is_muted = 1 AND (mute_until IS NULL OR mute_until > @now)",
                    new { now })
                .Select(r => new MuteEntry(
                    (int)r.Id,
                    r.Until is null
                        ? null
                        : DateTimeOffset.Parse(r.Until, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal))),
        ];
    }
}
