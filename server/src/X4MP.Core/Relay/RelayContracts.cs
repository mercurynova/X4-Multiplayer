using X4MP.Core.Events;
using X4MP.Core.Interest;
using X4MP.Core.Session;
using X4MP.Proto;

namespace X4MP.Core.Relay;

/// <summary>What the relay needs to know about a client's interest (the interest manager answers in production).</summary>
public interface IRelayInterest
{
    /// <summary>True when the player follows the sector (any tier), i.e. world events there are relevant to it.</summary>
    bool IsInterested(int playerId, ushort sector);

    /// <summary>True when the player holds the entity as a ghost (<c>KillClaim</c> targets must be in the sender's interest, protocol.md 16.2).</summary>
    bool IsHeld(int playerId, uint netId);
}

/// <summary><see cref="IRelayInterest"/> over the <see cref="InterestManager"/>.</summary>
public sealed class InterestManagerRelayInterest(InterestManager manager) : IRelayInterest
{
    public bool IsInterested(int playerId, ushort sector) => manager.GetSectorTier(playerId, sector) != InterestTier.None;

    public bool IsHeld(int playerId, uint netId) => manager.IsHeld(playerId, netId);
}

/// <summary>
/// An extra gate an intent passes before it is forwarded to the authority (the seam for the permission checks of protocol.md 16.2:
/// asset ownership, hostility, friendly fire; the team tasks plug them in). Runs on the actor thread.
/// </summary>
public interface IIntentValidator
{
    /// <summary>Null to let the intent through, else the reason it is answered with <c>Rejected</c> (and not forwarded).</summary>
    RejectReason? Validate(SessionNode sender, IntentT intent);
}

/// <summary>
/// What the admin side may do with chat (server-design 1.3 admin API, M1-W6). Every call runs on the session actor, so it is safe
/// from any thread. Muting is persisted and survives a restart.
/// </summary>
public interface IChatControl
{
    /// <summary>Mutes a player, online or not (null duration = until lifted). Audited as <c>AdminActionTaken</c>; the player is told.</summary>
    Task<bool> MuteAsync(int playerId, TimeSpan? duration = null, string actor = "system", string? reason = null);

    /// <summary>Lifts a mute; false when the player was not muted.</summary>
    Task<bool> UnmuteAsync(int playerId, string actor = "system");

    Task<bool> IsMutedAsync(int playerId);

    /// <summary>Players muted right now.</summary>
    Task<IReadOnlyList<MuteEntry>> MutedAsync();

    /// <summary>
    /// Sends an admin message (<see cref="ChatChannel.Admin"/> to everyone, or to <paramref name="toPlayer"/> only) and persists it.
    /// Returns the number of nodes it was queued for.
    /// </summary>
    Task<int> SendAsync(string adminName, string text, ChatChannel channel = ChatChannel.Admin, int? toPlayer = null);
}

/// <summary>The avatar the authority assigned to a player (<c>EntitySpawn{controller_player}</c>); the roster now carries it.</summary>
public sealed record PlayerShipAssigned(DateTimeOffset At, long? Session, long PlayerId, long ShipNetId, long PreviousShipNetId)
    : DomainEvent(At, Session), IPlayerScoped
{
    long? IPlayerScoped.PlayerId => PlayerId;
}

/// <summary>Counters of what the relay did (diagnostics, tests, the admin API later).</summary>
public sealed class RelayStats
{
    public long StatesReceived { get; internal set; }

    /// <summary>States forwarded to the authority (at most <see cref="RelayOptions.PlayerStateRelayHz"/> per player).</summary>
    public long StatesRelayed { get; internal set; }

    /// <summary>States that were replaced by a newer one before their turn (rate cap).</summary>
    public long StatesSuperseded { get; internal set; }

    public long PlayerShipsForwarded { get; internal set; }

    public long IntentsForwarded { get; internal set; }

    public long IntentsRejected { get; internal set; }

    public long IntentTimeouts { get; internal set; }

    public long IntentResultsRelayed { get; internal set; }

    /// <summary>Results the authority sent for an intent that is not (or no longer) pending: dropped.</summary>
    public long IntentResultsStale { get; internal set; }

    public long GameEventsReceived { get; internal set; }

    public long GameEventsDelivered { get; internal set; }

    public long ChatDelivered { get; internal set; }

    public long ChatMuted { get; internal set; }

    public long ChatRateLimited { get; internal set; }

    public long ChatRejected { get; internal set; }
}
