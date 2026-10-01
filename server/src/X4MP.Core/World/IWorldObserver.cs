using X4MP.Proto;

namespace X4MP.Core.World;

/// <summary>
/// Hears every change the <see cref="WorldMirror"/> applies, on the actor thread, right after the mirror was updated (for a
/// despawn: just before the entity is released, so it is still readable). The interest manager (M1-07) and replication
/// (M1-08) implement it. Callbacks must not block, must not keep a <see cref="MirrorEntity"/> beyond the call and must not
/// change the mirror. All methods are optional.
/// </summary>
public interface IWorldObserver
{
    /// <summary>
    /// An <c>EntitySpawn</c> record was applied. <paramref name="isNew"/> is false when the net_id already existed (a refresh);
    /// <paramref name="previousSector"/> is its sector before the refresh (0 when new). <paramref name="journalSeq"/> is the journal
    /// sequence of a persistent entity (0 otherwise): stamp it on forwarded copies.
    /// </summary>
    void OnEntitySpawned(MirrorEntity entity, bool isNew, ushort previousSector, ulong journalSeq)
    {
    }

    /// <summary>
    /// A <c>WorldUpdate</c>, a player state or a status batch changed the entity. Only called when something differs from the
    /// stored state. <paramref name="previousSector"/> equals the current sector unless <see cref="StateChange.Sector"/> is set.
    /// </summary>
    void OnEntityStateChanged(MirrorEntity entity, ushort previousSector, StateChange change)
    {
    }

    /// <summary>An <c>EntityChange</c> was applied (owner, name, parent, macro, kind, controller). <paramref name="change"/> is valid only during the call.</summary>
    void OnEntityChanged(MirrorEntity entity, EntityChange change, ulong journalSeq)
    {
    }

    void OnEntityCargo(MirrorEntity entity, EntityCargo cargo, ulong journalSeq)
    {
    }

    /// <summary>The entity is about to be removed from the mirror.</summary>
    void OnEntityDespawned(MirrorEntity entity, DespawnReason reason, uint killerNetId, ulong journalSeq)
    {
    }

    /// <summary>A client's <c>PlayerState</c> was stored (also when its net_id is still 0).</summary>
    void OnPlayerShipUpdated(PlayerShipState ship)
    {
    }

    void OnPlayerShipRemoved(int playerId)
    {
    }

    /// <summary>Strings were added to the table (new indices only).</summary>
    void OnStringsAdded(IReadOnlyList<StringTableEntry> added)
    {
    }

    /// <summary>The current galaxy (sector table and graph) changed.</summary>
    void OnGalaxyChanged(GalaxyModel galaxy)
    {
    }

    /// <summary>A sector's transient entities were evicted from the mirror (nobody subscribed any more). No per-entity callbacks follow.</summary>
    void OnSectorEvicted(ushort sector, int removed)
    {
    }

    /// <summary>The whole mirror was cleared (session ended).</summary>
    void OnWorldCleared()
    {
    }
}
