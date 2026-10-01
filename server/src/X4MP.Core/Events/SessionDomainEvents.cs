namespace X4MP.Core.Events;

/// <summary>
/// A node lost its socket (or sent <c>Disconnect{ClientReload}</c>) and its slot is kept for the resume grace.
/// This is not a leave: <see cref="PlayerLeft"/> follows only if the grace runs out.
/// </summary>
public sealed record PlayerDetached(DateTimeOffset At, long? Session, long PlayerId, string Name, string Reason)
    : DomainEvent(At, Session), IPlayerScoped
{
    long? IPlayerScoped.PlayerId => PlayerId;
}

/// <summary>
/// A detached node came back inside the grace with a valid resume token. This is not a join. Replication resets
/// the node's baselines when <paramref name="BaselineEpoch"/> changes.
/// </summary>
public sealed record PlayerResumed(DateTimeOffset At, long? Session, long PlayerId, string Name, int BaselineEpoch)
    : DomainEvent(At, Session), IPlayerScoped
{
    long? IPlayerScoped.PlayerId => PlayerId;
}

/// <summary>A node's server-side phase changed (<c>NodePhase</c> names).</summary>
public sealed record NodePhaseChanged(DateTimeOffset At, long? Session, long PlayerId, string Name, string From, string To)
    : DomainEvent(At, Session), IPlayerScoped
{
    long? IPlayerScoped.PlayerId => PlayerId;
}
