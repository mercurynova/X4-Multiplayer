using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.World;

/// <summary>Outcome of ingesting one <c>WorldUpdate</c> or status batch.</summary>
public readonly record struct IngestResult(int Applied, int Changed, int Unknown, bool Malformed);

/// <summary>Per-sector aggregate from <c>GalaxySummary</c> (GUI map only).</summary>
public readonly record struct SectorSummaryCounts(ushort Sector, ushort ShipsXs, ushort ShipsS, ushort ShipsM, ushort ShipsL, ushort ShipsXl, ushort Stations);

/// <summary>Per-sector counts of mirrored transient entities (the ghost budget works on these).</summary>
public readonly record struct SectorCounts(int Small, int Large)
{
    public int Total => Small + Large;
}

/// <summary>
/// The server's mirror of the authority's world (server-design 2.6, architecture 6): every persistent entity universe-wide,
/// the hot state of every transient entity in the captured sectors, and the player ships, keyed by the authority-assigned
/// <c>net_id</c>. It also owns the session <see cref="StringTable"/>, the <see cref="GalaxyMetadataCache"/> (with the
/// <see cref="SectorGraph"/>) and the persistent-mutation <see cref="Journal"/>.
/// <para>
/// It is an <see cref="ISessionModule"/>: register it with <c>AddWorldMirror()</c>. Everything runs on the actor thread, so
/// there are no locks. Records are pooled and the <c>WorldUpdate</c> path (<see cref="IngestWorldUpdate"/>) parses the payload in
/// place: steady-state ingestion allocates nothing. Other modules watch it through <see cref="IWorldObserver"/>.
/// </para>
/// </summary>
public sealed partial class WorldMirror : ISessionModule
{
    private sealed class SectorBucket
    {
        public readonly List<MirrorEntity> Transient = [];
        public int Small;
        public int Large;
    }

    private readonly TimeProvider _time;
    private readonly IWorldStore _store;
    private readonly ILogger _logger;
    private readonly Dictionary<uint, MirrorEntity> _entities;
    private readonly Dictionary<ushort, SectorBucket> _buckets = [];
    private readonly Dictionary<int, PlayerShipState> _players = [];
    private readonly Stack<MirrorEntity> _pool = new();
    private IWorldObserver[] _observers = [];
    private int _persistentCount;
    private int _transientCount;
    private readonly Dictionary<ushort, SectorSummaryCounts> _summary = [];

    public WorldMirror(TimeProvider? time = null, IWorldStore? store = null, ILogger<WorldMirror>? logger = null, int initialCapacity = 1024)
    {
        _time = time ?? TimeProvider.System;
        _store = store ?? NullWorldStore.Instance;
        _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _entities = new Dictionary<uint, MirrorEntity>(Math.Max(0, initialCapacity));
        Strings = new StringTable();
        Journal = new Journal(_store);
        Galaxy = new GalaxyMetadataCache(_store, _time);
        Galaxy.CurrentChanged += model =>
        {
            foreach (var observer in _observers)
            {
                observer.OnGalaxyChanged(model);
            }
        };
    }

    public StringTable Strings { get; }

    public Journal Journal { get; }

    public GalaxyMetadataCache Galaxy { get; }

    /// <summary>The graph of the current galaxy (empty until <c>GalaxyMetadata</c> arrived or was activated from the cache).</summary>
    public SectorGraph Graph => Galaxy.Graph;

    /// <summary>All mirrored entities.</summary>
    public int Count => _entities.Count;

    /// <summary>Persistent entities (stations, gates, ...).</summary>
    public int PersistentCount => _persistentCount;

    /// <summary>Transient entities in captured sectors (the hot set).</summary>
    public int HotCount => _transientCount;

    public int PlayerShipCount => _players.Count;

    public double AuthorityGameTime { get; private set; }

    /// <summary>
    /// True once a <c>WorldUpdate</c> was ingested in this session. Before that <see cref="AuthorityGameTime"/> is 0, a reference time no spawn
    /// state was sampled at, so replication holds its state entries back (M1-F2: a client already in interest while the authority's first
    /// update was still on its way, as under injected latency, got entries stamped game time 0).
    /// </summary>
    public bool HasWorldUpdate { get; private set; }

    public uint AuthorityTick { get; private set; }

    private bool _clockGuardArmed;

    /// <summary><c>WorldUpdate</c>s dropped because both their tick and their game time were older than the latest ingested one (UDP reordering).</summary>
    public long StaleWorldUpdates { get; private set; }

    /// <summary>
    /// The <c>capture_time_us</c> of the latest <c>WorldUpdate</c>: the authority's estimate of the server clock at the time
    /// <see cref="AuthorityGameTime"/> was sampled. The pair is the reference of every <c>Replication</c> frame.
    /// </summary>
    public ulong AuthorityCaptureTimeUs { get; private set; }

    /// <summary>The latest <c>GalaxySummary</c> game time (0 = none yet).</summary>
    public double SummaryGameTime { get; private set; }

    /// <summary><c>WorldUpdate</c> states for a net_id the mirror does not know (spawn lost, evicted or not yet sent).</summary>
    public long UnknownStateUpdates { get; private set; }

    public long StateUpdatesApplied { get; private set; }

    public long MalformedFrames { get; private set; }

    /// <summary>Raised after a <c>SaveStarted</c> marker was journaled (the save service, M1-12, uploads the checkpoint).</summary>
    public event Action<JournalMarker>? MarkerRecorded;

    public void AddObserver(IWorldObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        if (Array.IndexOf(_observers, observer) < 0)
        {
            _observers = [.. _observers, observer];
        }
    }

    public void RemoveObserver(IWorldObserver observer) => _observers = [.. _observers.Where(o => !ReferenceEquals(o, observer))];

    // ------------------------------------------------------------------ reads

    public bool TryGet(uint netId, out MirrorEntity entity)
    {
        if (_entities.TryGetValue(netId, out var found))
        {
            entity = found;
            return true;
        }

        entity = null!;
        return false;
    }

    public bool Contains(uint netId) => _entities.ContainsKey(netId);

    /// <summary>Every entity (enumerate on the actor thread only, and do not change the mirror while doing it).</summary>
    public IEnumerable<MirrorEntity> All => _entities.Values;

    /// <summary>The transient entities currently in a sector. Valid until the mirror changes.</summary>
    public ReadOnlySpan<MirrorEntity> TransientIn(ushort sector) =>
        _buckets.TryGetValue(sector, out var bucket) ? CollectionsMarshal.AsSpan(bucket.Transient) : [];

    public SectorCounts CountsIn(ushort sector) =>
        _buckets.TryGetValue(sector, out var bucket) ? new SectorCounts(bucket.Small, bucket.Large) : default;

    public bool TryGetPlayerShip(int playerId, out PlayerShipState ship)
    {
        if (_players.TryGetValue(playerId, out var found))
        {
            ship = found;
            return true;
        }

        ship = null!;
        return false;
    }

    public IEnumerable<PlayerShipState> PlayerShips => _players.Values;

    /// <summary>The latest per-sector summary the authority sent (<c>GalaxySummary</c>), for the GUI map.</summary>
    public IReadOnlyDictionary<ushort, SectorSummaryCounts> Summary => _summary;

    // ------------------------------------------------------------------ module callbacks

    public bool OnMessage(SessionNode node, InboundFrame frame)
    {
        try
        {
            return HandleFrame(frame, node.PlayerId, node.IsAuthority);
        }
        catch (ProtocolViolation violation)
        {
            MalformedFrames++;
            node.Connection?.Stats.AddViolation();
            node.Connection?.Close(DisconnectCode.MalformedMessage, violation.Code.ToString());
            return true;
        }
    }

    /// <summary>
    /// The message switch behind <see cref="OnMessage"/>, free of the node object (tests, replays). Authority-only messages from
    /// a non-authority sender are not consumed. Returns true when the frame was consumed; throws <see cref="ProtocolViolation"/>
    /// for a malformed payload.
    /// </summary>
    public bool HandleFrame(InboundFrame frame, int senderPlayerId, bool senderIsAuthority)
    {
        var f = frame.Frame;
        if (frame.Type == MsgType.EntityCargo)
        {
            ApplyCargo(MessageRegistry.Default.Decode<EntityCargo>(f), senderPlayerId, senderIsAuthority);
            return true;
        }

        if (frame.Type == MsgType.PlayerState)
        {
            ApplyPlayerState(senderPlayerId, MessageRegistry.Default.Decode<PlayerState>(f));
            return false; // the relay (M1-10) derives velocity and forwards it
        }

        if (!senderIsAuthority)
        {
            return false;
        }

        switch (frame.Type)
        {
            case MsgType.WorldUpdate:
                if (IngestWorldUpdate(f.Payload).Malformed)
                {
                    throw new ProtocolViolation(ViolationCode.MalformedPayload, "malformed WorldUpdate");
                }

                return true;
            case MsgType.EntityStatusBatch:
                if (IngestStatusBatch(f.Payload).Malformed)
                {
                    throw new ProtocolViolation(ViolationCode.MalformedPayload, "malformed EntityStatusBatch");
                }

                return true;
            case MsgType.EntitySpawn:
                ApplySpawn(MessageRegistry.Default.Decode<EntitySpawn>(f));
                return true;
            case MsgType.EntityDespawn:
                ApplyDespawn(MessageRegistry.Default.Decode<EntityDespawn>(f));
                return true;
            case MsgType.EntityChange:
                ApplyChange(MessageRegistry.Default.Decode<EntityChange>(f));
                return true;
            case MsgType.StringTableAdd:
                ApplyStrings(MessageRegistry.Default.Decode<StringTableAdd>(f));
                return true;
            case MsgType.GalaxyMetadata:
                ApplyGalaxy(f.Payload);
                return true;
            case MsgType.GalaxySummary:
                ApplySummary(MessageRegistry.Default.Decode<GalaxySummary>(f));
                return true;
            case MsgType.SaveStarted:
                ApplySaveStarted(MessageRegistry.Default.Decode<SaveStarted>(f));
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// A (re)attached authority starts a new datagram sequence (it may have reloaded a save: tick and game time can restart lower), so the
    /// stale-update guard of <see cref="IngestWorldUpdate"/> forgets the old clock.
    /// </summary>
    public void OnNodeAttached(SessionNode node, bool resumed)
    {
        if (node.IsAuthority)
        {
            ResetClockGuard();
        }
    }

    /// <summary>Lets the next <c>WorldUpdate</c> through whatever its tick and game time (the guard is re-armed by it).</summary>
    public void ResetClockGuard() => _clockGuardArmed = false;

    public void OnNodeLeft(SessionNode node, string reason)
    {
        if (_players.Remove(node.PlayerId))
        {
            foreach (var observer in _observers)
            {
                observer.OnPlayerShipRemoved(node.PlayerId);
            }
        }
    }

    public void OnSessionBegun(long sessionId) => _store.BindSession(sessionId);

    public void OnSessionPhaseChanged(SessionPhase previous, SessionPhase current)
    {
        if (current == SessionPhase.Ended)
        {
            Clear();
        }
    }

    // ------------------------------------------------------------------ authority world messages

    /// <summary>Applies a decoded <c>EntitySpawn</c>.</summary>
    public void ApplySpawn(EntitySpawn message)
    {
        double gameTime = message.GameTime;
        if (message.EntitiesLength is > 0 and <= 4 && _logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information))
        {
            // Small spawns only (the authority's self-spawn, single ships): M2 criterion 11 reads the game_time the authority stamped on them from the log.
            LogSmallSpawn(message.EntitiesLength, message.Entities(0)?.NetId ?? 0, gameTime);
        }

        for (int i = 0; i < message.EntitiesLength; i++)
        {
            if (message.Entities(i) is { } record)
            {
                ApplyRecord(record, gameTime);
            }
        }
    }

    /// <summary>Adds or refreshes one entity from its full record; returns the mirror entity (null for an invalid net_id).</summary>
    public MirrorEntity? ApplyRecord(EntityRecord record, double gameTime = 0)
    {
        uint netId = record.NetId;
        if (netId is 0 or ReplicationCodec.ReservedNetId)
        {
            MalformedFrames++;
            return null;
        }

        long now = _time.GetTimestamp();
        bool isNew = !_entities.TryGetValue(netId, out var entity);
        ushort previousSector = 0;
        if (isNew)
        {
            entity = Rent();
            entity.NetId = netId;
            _entities[netId] = entity;
        }
        else
        {
            previousSector = entity!.Sector;
            Unbucket(entity);
            if (entity.IsPersistent)
            {
                _persistentCount--;
            }
            else
            {
                _transientCount--;
            }
        }

        entity!.Kind = record.Kind;
        entity.Origin = record.Origin;
        entity.MacroRef = record.MacroRef;
        entity.OwnerRef = record.OwnerRef;
        entity.OwnerTeam = record.OwnerTeam;
        entity.OwnerPlayer = record.OwnerPlayer;
        entity.ParentNetId = record.ParentNetId;
        entity.ControllerPlayer = record.ControllerPlayer;
        entity.Name = record.Name;
        entity.IdCode = record.Idcode;
        entity.Hull = record.Hull;
        entity.Shield = record.Shield;
        entity.IsPersistent = EntityKinds.IsPersistent(record.Kind);
        if (record.State is { } s)
        {
            entity.Sector = s.Sector;
            entity.Flags = s.Flags;
            entity.Px = s.Px;
            entity.Py = s.Py;
            entity.Pz = s.Pz;
            entity.Yaw = s.Yaw;
            entity.Pitch = s.Pitch;
            entity.Roll = s.Roll;
            entity.Vx = s.Vx;
            entity.Vy = s.Vy;
            entity.Vz = s.Vz;
        }

        entity.Version++;
        entity.LastUpdateTick = now;
        // The sender's capture time of the state; 0 (an old sender) falls back to the latest world update: as fresh as that.
        entity.SampleGameTime = gameTime > 0 ? gameTime : AuthorityGameTime;
        if (entity.IsPersistent)
        {
            _persistentCount++;
        }
        else
        {
            _transientCount++;
            Bucket(entity);
        }

        ulong seq = 0;
        if (entity.IsPersistent)
        {
            seq = Journal.Append(MsgType.EntitySpawn, netId, entity.Sector, JournalCodec.EncodeSpawn(record), _time.GetUtcNow());
        }

        foreach (var observer in _observers)
        {
            observer.OnEntitySpawned(entity, isNew, previousSector, seq);
        }

        return entity;
    }

    public void ApplyDespawn(EntityDespawn message)
    {
        for (int i = 0; i < message.EntriesLength; i++)
        {
            if (message.Entries(i) is { } entry)
            {
                Remove(entry.NetId, entry.Reason, entry.KillerNetId, journal: true);
            }
        }
    }

    /// <summary>Removes an entity (a despawn from the authority). Returns false when the net_id is unknown.</summary>
    public bool Remove(uint netId, DespawnReason reason, uint killerNetId = 0, bool journal = true)
    {
        if (!_entities.TryGetValue(netId, out var entity))
        {
            return false;
        }

        ulong seq = 0;
        if (entity.IsPersistent && journal)
        {
            seq = Journal.Append(MsgType.EntityDespawn, netId, entity.Sector, JournalCodec.EncodeDespawn(netId, killerNetId, reason), _time.GetUtcNow());
        }

        foreach (var observer in _observers)
        {
            observer.OnEntityDespawned(entity, reason, killerNetId, seq);
        }

        Release(entity);
        return true;
    }

    public void ApplyChange(EntityChange change)
    {
        if (!_entities.TryGetValue(change.NetId, out var entity))
        {
            return;
        }

        var fields = change.Fields;
        Unbucket(entity);
        if ((fields & ChangeField.Owner) != 0)
        {
            entity.OwnerRef = change.OwnerRef;
        }

        if ((fields & ChangeField.OwnerTeam) != 0)
        {
            entity.OwnerTeam = change.OwnerTeam;
        }

        if ((fields & ChangeField.OwnerPlayer) != 0)
        {
            entity.OwnerPlayer = change.OwnerPlayer;
        }

        if ((fields & ChangeField.Name) != 0)
        {
            entity.Name = change.Name;
        }

        if ((fields & ChangeField.Parent) != 0)
        {
            entity.ParentNetId = change.ParentNetId;
        }

        if ((fields & ChangeField.Macro) != 0)
        {
            entity.MacroRef = change.MacroRef;
        }

        if ((fields & ChangeField.Kind) != 0)
        {
            ReclassifyKind(entity, change.Kind);
        }

        if ((fields & ChangeField.Controller) != 0)
        {
            entity.ControllerPlayer = change.ControllerPlayer;
        }

        entity.Version++;
        if (!entity.IsPersistent)
        {
            Bucket(entity);
        }

        ulong seq = 0;
        if (entity.IsPersistent)
        {
            seq = Journal.Append(MsgType.EntityChange, entity.NetId, entity.Sector, JournalCodec.EncodeChange(change), _time.GetUtcNow());
        }

        foreach (var observer in _observers)
        {
            observer.OnEntityChanged(entity, change, seq);
        }
    }

    private void ReclassifyKind(MirrorEntity entity, EntityKind kind)
    {
        bool persistent = EntityKinds.IsPersistent(kind);
        if (persistent != entity.IsPersistent)
        {
            if (entity.IsPersistent)
            {
                _persistentCount--;
                _transientCount++;
            }
            else
            {
                _transientCount--;
                _persistentCount++;
            }

            entity.IsPersistent = persistent;
        }

        entity.Kind = kind;
    }

    public void ApplyCargo(EntityCargo cargo, int senderPlayerId = 0, bool senderIsAuthority = true)
    {
        if (!_entities.TryGetValue(cargo.NetId, out var entity))
        {
            return;
        }

        // A client may only report the cargo of the ship it pilots; the authority may report anything.
        if (!senderIsAuthority && entity.ControllerPlayer != senderPlayerId)
        {
            return;
        }

        var wares = new WareAmount[cargo.WaresLength];
        for (int i = 0; i < wares.Length; i++)
        {
            wares[i] = cargo.Wares(i)!.Value;
        }

        entity.Cargo = wares;
        ulong seq = 0;
        if (entity.IsPersistent)
        {
            seq = Journal.Append(MsgType.EntityCargo, entity.NetId, entity.Sector, JournalCodec.EncodeCargo(cargo), _time.GetUtcNow());
        }

        foreach (var observer in _observers)
        {
            observer.OnEntityCargo(entity, cargo, seq);
        }
    }

    private void ApplyStrings(StringTableAdd message)
    {
        var added = Strings.Apply(message);
        if (added.Count == 0)
        {
            return;
        }

        _store.AppendStrings(added);
        foreach (var observer in _observers)
        {
            observer.OnStringsAdded(added);
        }
    }

    private void ApplyGalaxy(byte[] payload)
    {
        if (!Galaxy.TryIngest(payload, out var error))
        {
            MalformedFrames++;
            LogGalaxyRejected(error ?? "invalid");
        }
    }

    private void ApplySummary(GalaxySummary message)
    {
        SummaryGameTime = message.GameTime;
        _summary.Clear();
        for (int i = 0; i < message.SectorsLength; i++)
        {
            if (message.Sectors(i) is { } s)
            {
                _summary[s.Sector] = new SectorSummaryCounts(s.Sector, s.ShipsXs, s.ShipsS, s.ShipsM, s.ShipsL, s.ShipsXl, s.Stations);
            }
        }
    }

    private void ApplySaveStarted(SaveStarted message)
    {
        var marker = Journal.AppendMarker(message.RequestId, CheckpointId.From(message.CheckpointId), message.GameTime, message.NextNetId, _time.GetUtcNow());
        MarkerRecorded?.Invoke(marker);
    }

    // ------------------------------------------------------------------ hot state (zero allocation)

    /// <summary>
    /// Applies a <c>WorldUpdate</c> payload straight from its bytes. Allocates nothing (the states are read in place, records
    /// are pooled, the sector index only moves entries). Unknown net_ids are counted and skipped.
    /// </summary>
    public IngestResult IngestWorldUpdate(ReadOnlySpan<byte> payload)
    {
        if (!FlatTableReader.TryRoot(payload, out var table)
            || !table.TryGetStructVector(3, EntityStateLayout.Size, out var states, out int count))
        {
            return new IngestResult(0, 0, 0, true);
        }

        uint tick = table.GetUInt32(0);
        double gameTime = table.GetDouble(2);
        // A reordered datagram must not roll the clock back. Stale = older in BOTH tick (wrap-safe) and game time, so a reloaded save
        // (lower game time, tick continuing) is not mistaken for a late datagram; a reattached authority resets the guard.
        if (_clockGuardArmed && unchecked((int)(tick - AuthorityTick)) < 0 && gameTime < AuthorityGameTime)
        {
            StaleWorldUpdates++;
            return new IngestResult(0, 0, 0, false);
        }

        _clockGuardArmed = true;
        AuthorityTick = tick;
        AuthorityCaptureTimeUs = table.GetUInt64(1);
        AuthorityGameTime = gameTime;
        HasWorldUpdate = true;
        long now = _time.GetTimestamp();
        int applied = 0;
        int changed = 0;
        int unknown = 0;
        for (int i = 0; i < count; i++)
        {
            var s = states.Slice(i * EntityStateLayout.Size, EntityStateLayout.Size);
            uint netId = BinaryPrimitives.ReadUInt32LittleEndian(s);
            if (!_entities.TryGetValue(netId, out var entity))
            {
                unknown++;
                continue;
            }

            applied++;
            var change = ApplyState(
                entity,
                BinaryPrimitives.ReadUInt16LittleEndian(s[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(s[6..]),
                BinaryPrimitives.ReadInt32LittleEndian(s[8..]),
                BinaryPrimitives.ReadInt32LittleEndian(s[12..]),
                BinaryPrimitives.ReadInt32LittleEndian(s[16..]),
                BinaryPrimitives.ReadInt16LittleEndian(s[20..]),
                BinaryPrimitives.ReadInt16LittleEndian(s[22..]),
                BinaryPrimitives.ReadInt16LittleEndian(s[24..]),
                BinaryPrimitives.ReadInt16LittleEndian(s[26..]),
                BinaryPrimitives.ReadInt16LittleEndian(s[28..]),
                BinaryPrimitives.ReadInt16LittleEndian(s[30..]),
                applyVelocity: true,
                now,
                gameTime);
            if (change != StateChange.None)
            {
                changed++;
            }
        }

        StateUpdatesApplied += applied;
        UnknownStateUpdates += unknown;
        return new IngestResult(applied, changed, unknown, false);
    }

    /// <summary>Applies an <c>EntityStatusBatch</c> payload (hull and shield), allocation-free.</summary>
    public IngestResult IngestStatusBatch(ReadOnlySpan<byte> payload)
    {
        if (!FlatTableReader.TryRoot(payload, out var table)
            || !table.TryGetStructVector(1, EntityStatusLayout.Size, out var statuses, out int count))
        {
            return new IngestResult(0, 0, 0, true);
        }

        int applied = 0;
        int changed = 0;
        int unknown = 0;
        for (int i = 0; i < count; i++)
        {
            var s = statuses.Slice(i * EntityStatusLayout.Size, EntityStatusLayout.Size);
            if (!_entities.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(s), out var entity))
            {
                unknown++;
                continue;
            }

            applied++;
            byte hull = s[4];
            byte shield = s[5];
            if (hull != entity.Hull || shield != entity.Shield)
            {
                entity.Hull = hull;
                entity.Shield = shield;
                entity.Version++;
                changed++;
                foreach (var observer in _observers)
                {
                    observer.OnEntityStateChanged(entity, entity.Sector, StateChange.Status);
                }
            }
        }

        UnknownStateUpdates += unknown;
        return new IngestResult(applied, changed, unknown, false);
    }

    /// <summary>Writes one state sample into an entity and tells the observers when anything differs.</summary>
    private StateChange ApplyState(
        MirrorEntity e, ushort sector, ushort flags, int px, int py, int pz, short yaw, short pitch, short roll,
        short vx, short vy, short vz, bool applyVelocity, long now, double sampleGameTime)
    {
        e.LastUpdateTick = now;
        var change = StateChange.None;
        if (sector != e.Sector)
        {
            change |= StateChange.Sector;
        }

        if (px != e.Px || py != e.Py || pz != e.Pz)
        {
            change |= StateChange.Position;
        }

        if (yaw != e.Yaw || pitch != e.Pitch || roll != e.Roll)
        {
            change |= StateChange.Rotation;
        }

        if (applyVelocity && (vx != e.Vx || vy != e.Vy || vz != e.Vz))
        {
            change |= StateChange.Velocity;
        }

        if (flags != e.Flags)
        {
            change |= StateChange.Flags;
        }

        if (change == StateChange.None)
        {
            return change;
        }

        ushort previousSector = e.Sector;
        if ((change & StateChange.Sector) != 0 && !e.IsPersistent)
        {
            Unbucket(e);
            e.Sector = sector;
            Bucket(e);
        }
        else
        {
            e.Sector = sector;
        }

        e.Flags = flags;
        e.Px = px;
        e.Py = py;
        e.Pz = pz;
        e.Yaw = yaw;
        e.Pitch = pitch;
        e.Roll = roll;
        if (applyVelocity)
        {
            e.Vx = vx;
            e.Vy = vy;
            e.Vz = vz;
        }

        e.Version++;
        e.SampleGameTime = sampleGameTime;
        var observers = _observers;
        for (int i = 0; i < observers.Length; i++)
        {
            observers[i].OnEntityStateChanged(e, previousSector, change);
        }

        return change;
    }

    // ------------------------------------------------------------------ player ships

    /// <summary>Stores a player's <c>PlayerState</c> and drives the mirror entity of its ship when it is bound (net_id assigned and controlled by this player).</summary>
    public void ApplyPlayerState(int playerId, PlayerState state)
    {
        if (!_players.TryGetValue(playerId, out var ship))
        {
            ship = new PlayerShipState(playerId);
            _players[playerId] = ship;
        }
        else if (state.Seq != ship.Seq && !SeqNewer(state.Seq, ship.Seq))
        {
            return; // reordered or duplicate (UDP): keep the newer one
        }

        long now = _time.GetTimestamp();
        ship.Seq = state.Seq;
        ship.SampleTimeUs = state.SampleTimeUs;
        ship.NetId = state.NetId;
        ship.Sector = state.Sector;
        ship.Flags = state.Flags;
        ship.Px = state.Px;
        ship.Py = state.Py;
        ship.Pz = state.Pz;
        ship.Yaw = state.Yaw;
        ship.Pitch = state.Pitch;
        ship.Roll = state.Roll;
        ship.Hull = state.Hull;
        ship.Shield = state.Shield;
        ship.TargetNetId = state.TargetNetId;
        ship.UpdatedTick = now;

        if (state.NetId != 0 && _entities.TryGetValue(state.NetId, out var entity) && entity.ControllerPlayer == playerId)
        {
            ApplyState(entity, state.Sector, state.Flags, state.Px, state.Py, state.Pz, state.Yaw, state.Pitch, state.Roll, 0, 0, 0, applyVelocity: false, now, AuthorityGameTime);
            if (state.Hull != entity.Hull || state.Shield != entity.Shield)
            {
                entity.Hull = state.Hull;
                entity.Shield = state.Shield;
                entity.Version++;
            }
        }

        foreach (var observer in _observers)
        {
            observer.OnPlayerShipUpdated(ship);
        }
    }

    private static bool SeqNewer(uint incoming, uint known) => (int)(incoming - known) > 0;

    // ------------------------------------------------------------------ eviction and reset

    /// <summary>
    /// Drops every transient entity of a sector (nobody subscribes to it any more). No per-entity observer calls; observers get
    /// <see cref="IWorldObserver.OnSectorEvicted"/>. Returns how many were removed.
    /// </summary>
    public int EvictTransient(ushort sector)
    {
        if (!_buckets.TryGetValue(sector, out var bucket) || bucket.Transient.Count == 0)
        {
            return 0;
        }

        int removed = 0;
        for (int i = bucket.Transient.Count - 1; i >= 0; i--)
        {
            var entity = bucket.Transient[i];
            if (entity.IsPlayerShip)
            {
                continue; // player ships are mirrored galaxy-wide
            }

            Release(entity);
            removed++;
        }

        foreach (var observer in _observers)
        {
            observer.OnSectorEvicted(sector, removed);
        }

        return removed;
    }

    /// <summary>Drops everything (a session ended). Observers get <see cref="IWorldObserver.OnWorldCleared"/>.</summary>
    public void Clear()
    {
        foreach (var entity in _entities.Values)
        {
            _pool.Push(Reset(entity));
        }

        _entities.Clear();
        _buckets.Clear();
        _players.Clear();
        _summary.Clear();
        _persistentCount = _transientCount = 0;
        HasWorldUpdate = false;
        _clockGuardArmed = false;
        Strings.Clear();
        Journal.Clear();
        Galaxy.Reset();
        foreach (var observer in _observers)
        {
            observer.OnWorldCleared();
        }
    }

    /// <summary>
    /// Restores the string table and the journal of the session from the store (a resumed session). The save service calls it
    /// before it serves a <c>WorldCatchUp</c>.
    /// </summary>
    public void RestoreFromStore()
    {
        Strings.Clear();
        Strings.Load(_store.LoadStrings());
        Journal.Restore();
    }

    // ------------------------------------------------------------------ pool and index

    private MirrorEntity Rent() => _pool.TryPop(out var e) ? e : new MirrorEntity();

    private static MirrorEntity Reset(MirrorEntity entity)
    {
        entity.Reset();
        return entity;
    }

    private void Release(MirrorEntity entity)
    {
        Unbucket(entity);
        _entities.Remove(entity.NetId);
        if (entity.IsPersistent)
        {
            _persistentCount--;
        }
        else
        {
            _transientCount--;
        }

        _pool.Push(Reset(entity));
    }

    private void Bucket(MirrorEntity entity)
    {
        if (entity.IsPersistent || entity.Sector == 0)
        {
            return;
        }

        if (!_buckets.TryGetValue(entity.Sector, out var bucket))
        {
            bucket = new SectorBucket();
            _buckets[entity.Sector] = bucket;
        }

        entity.SectorSlot = bucket.Transient.Count;
        bucket.Transient.Add(entity);
        entity.BucketLarge = entity.Size == EntitySize.Large;
        if (entity.BucketLarge)
        {
            bucket.Large++;
        }
        else
        {
            bucket.Small++;
        }
    }

    private void Unbucket(MirrorEntity entity)
    {
        if (entity.SectorSlot < 0)
        {
            return;
        }

        if (_buckets.TryGetValue(entity.Sector, out var bucket))
        {
            var list = bucket.Transient;
            int slot = entity.SectorSlot;
            int last = list.Count - 1;
            if (slot <= last && ReferenceEquals(list[slot], entity))
            {
                var moved = list[last];
                list[slot] = moved;
                moved.SectorSlot = slot;
                list.RemoveAt(last);
                if (entity.BucketLarge)
                {
                    bucket.Large--;
                }
                else
                {
                    bucket.Small--;
                }
            }
        }

        entity.SectorSlot = -1;
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "world: EntitySpawn of {Count} entities, first net_id {NetId}, game_time {GameTime:F3}")]
    private partial void LogSmallSpawn(int count, uint netId, double gameTime);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "world mirror: GalaxyMetadata rejected: {Reason}")]
    private partial void LogGalaxyRejected(string reason);
}

/// <summary>Byte layout of the fixed-size wire structs (schema <c>EntityState</c> 32 B, <c>EntityStatus</c> 8 B).</summary>
internal static class EntityStateLayout
{
    public const int Size = 32;
}

internal static class EntityStatusLayout
{
    public const int Size = 8;
}
