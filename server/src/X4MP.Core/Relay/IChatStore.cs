namespace X4MP.Core.Relay;

/// <summary>One persisted chat line (the <c>chat_messages</c> table).</summary>
/// <param name="FromPlayerId">The sending player, or null for the server and web admins.</param>
/// <param name="FromAdmin">The admin's name when an admin sent it.</param>
/// <param name="Channel">The <c>ChatChannel</c> name (All, Team, Whisper, Admin, System).</param>
public sealed record ChatLine(DateTimeOffset At, long SessionId, int? FromPlayerId, string? FromAdmin, string Channel, string Text);

/// <summary>A player's mute as stored (<c>players.is_muted</c> and <c>mute_until</c>).</summary>
/// <param name="Until">When the mute ends; null for an indefinite one.</param>
public sealed record MuteEntry(int PlayerId, DateTimeOffset? Until);

/// <summary>
/// Persistence seam of the relay's chat (the <c>chat_messages</c> table and the mute columns of <c>players</c>). Every method
/// must return without waiting for the database (write-behind) except <see cref="LoadMutes"/>, which runs once at start.
/// </summary>
public interface IChatStore
{
    /// <summary>Queues one chat line. False when it could not be queued (the writer queue is full): the line is lost, never the actor.</summary>
    bool Append(ChatLine line);

    /// <summary>Records a mute (<paramref name="entry"/>) or lifts it (null).</summary>
    bool SetMute(int playerId, MuteEntry? entry);

    /// <summary>Players that are muted right now (the previous run's mutes survive a restart). Called once, off the actor thread.</summary>
    IReadOnlyList<MuteEntry> LoadMutes();
}

/// <summary>Keeps nothing (running without persistence, tests).</summary>
public sealed class NullChatStore : IChatStore
{
    public static NullChatStore Instance { get; } = new();

    public bool Append(ChatLine line) => true;

    public bool SetMute(int playerId, MuteEntry? entry) => true;

    public IReadOnlyList<MuteEntry> LoadMutes() => [];
}
