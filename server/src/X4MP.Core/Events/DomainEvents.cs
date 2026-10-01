namespace X4MP.Core.Events;

public enum AlertSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>
/// Base of every event on the bus (server-design 2.7). <see cref="Session"/> is the <c>sessions.id</c> the event
/// belongs to, or null for server-wide events. Events are immutable records; publishers use the injected
/// <see cref="TimeProvider"/> for <see cref="At"/>.
/// </summary>
public abstract record DomainEvent(DateTimeOffset At, long? Session);

/// <summary>An event that concerns one player (persisted into <c>session_events.player_id</c>).</summary>
public interface IPlayerScoped
{
    long? PlayerId { get; }
}

/// <summary>An event that happened in one sector (persisted into <c>session_events.sector_id</c>).</summary>
public interface ISectorScoped
{
    long? SectorId { get; }
}

public sealed record NodeConnected(DateTimeOffset At, long? Session, long ConnectionId, string? RemoteAddress) : DomainEvent(At, Session);

public sealed record NodeDisconnected(DateTimeOffset At, long? Session, long ConnectionId, string? Player, string Reason) : DomainEvent(At, Session);

public sealed record PlayerJoined(DateTimeOffset At, long? Session, long PlayerId, string Name, string Roles) : DomainEvent(At, Session), IPlayerScoped
{
    long? IPlayerScoped.PlayerId => PlayerId;
}

public sealed record PlayerLeft(DateTimeOffset At, long? Session, long PlayerId, string Name, string Reason) : DomainEvent(At, Session), IPlayerScoped
{
    long? IPlayerScoped.PlayerId => PlayerId;
}

public sealed record SessionStateChanged(DateTimeOffset At, long? Session, string From, string To, string? Reason) : DomainEvent(At, Session);

public sealed record AuthorityChanged(DateTimeOffset At, long? Session, long? FromPlayerId, long? ToPlayerId, string Reason) : DomainEvent(At, Session);

public sealed record ChatPosted(DateTimeOffset At, long? Session, long? FromPlayerId, string? FromAdmin, string Channel, string Text) : DomainEvent(At, Session), IPlayerScoped
{
    long? IPlayerScoped.PlayerId => FromPlayerId;
}

/// <summary>A gameplay event: kill, capture, trade, build. <paramref name="DataJson"/> carries the details.</summary>
public sealed record GameEventOccurred(DateTimeOffset At, long? Session, string Kind, long? PlayerId, long? SectorId, string? DataJson)
    : DomainEvent(At, Session), IPlayerScoped, ISectorScoped
{
    long? IPlayerScoped.PlayerId => PlayerId;

    long? ISectorScoped.SectorId => SectorId;
}

/// <summary>An admin action. The audit forwarder turns it into an <c>audit_log</c> row; never put secrets in <paramref name="Data"/>.</summary>
public sealed record AdminActionTaken(
    DateTimeOffset At, long? Session, string Actor, string Action, string? Target, IReadOnlyDictionary<string, string?>? Data, string? RemoteIp)
    : DomainEvent(At, Session);

public sealed record SaveStored(DateTimeOffset At, long? Session, string Sha256, long SizeBytes, string Source) : DomainEvent(At, Session);

/// <summary>A protocol or policy violation by a node. (Named to avoid a clash with <c>X4MP.Protocol.ProtocolViolation</c>.)</summary>
public sealed record ProtocolViolationOccurred(DateTimeOffset At, long? Session, long ConnectionId, string? Player, string Code) : DomainEvent(At, Session);

/// <summary>A node's periodic <c>NodeStats</c> telemetry, reduced to what the alert rules use. Not persisted (too frequent).</summary>
public sealed record NodeStatsReported(DateTimeOffset At, long? Session, long PlayerId, string Player, bool IsAuthority, double Fps)
    : DomainEvent(At, Session), IPlayerScoped
{
    long? IPlayerScoped.PlayerId => PlayerId;
}

public sealed record AlertRaised(DateTimeOffset At, long? Session, AlertSeverity Severity, string Code, string Text) : DomainEvent(At, Session);

/// <summary>The condition behind an earlier <see cref="AlertRaised"/> with the same <paramref name="Code"/> is over.</summary>
public sealed record AlertCleared(DateTimeOffset At, long? Session, string Code) : DomainEvent(At, Session);
