using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Interest;

/// <summary>Counters of what the manager did (diagnostics, tests, the admin API later).</summary>
public sealed class InterestStats
{
    public long SpawnsSent { get; internal set; }

    public long DespawnsSent { get; internal set; }

    public long InterestUpdatesSent { get; internal set; }

    public long SectorCompletesSent { get; internal set; }

    public long CaptureSetsSent { get; internal set; }

    /// <summary><c>SectorComplete</c> from the authority that did not match a captured sector or its epoch.</summary>
    public long StaleSectorCompletes { get; internal set; }

    public long HintsAccepted { get; internal set; }

    public long HintsIgnored { get; internal set; }
}

/// <summary>
/// The interest manager (server-design 2.5, protocol.md 12, ADR-011, M1-07): decides, per client, which sectors it follows and at
/// which tier, and turns that into messages.
/// <list type="bullet">
/// <item><b>Tiers.</b> Sector (the player's current sector; the Near region inside it is entity-level, served by the per-sector
/// <see cref="NearGrid"/>), Adjacent (sectors within <c>PrefetchDepth</c> hops on the <see cref="SectorGraph"/>, plus hinted
/// jumps), Linger (a sector the player left, held for <c>LingerSeconds</c>).</item>
/// <item><b>To the client</b> (Control lane): <c>InterestUpdate</c> (diffs; a full one first), <c>EntitySpawn</c> batches for
/// entering sectors, <c>SectorComplete</c> after each sector's spawns, <c>EntityDespawn{OutOfInterest}</c> for leaving ones, plus live spawns, despawns,
/// changes and cargo of what it holds. Persistent entities are forwarded to everybody with the journal sequence stamped.
/// Crossing a gate only promotes the subscription: the promoted sector is never despawned or respawned.</item>
/// <item><b>To the authority:</b> the <c>CaptureSet</c>, the union of every client's sectors plus admin map views, with a focus
/// sphere around each player, at most once per <c>CaptureSetMinIntervalMs</c>. A sector nobody needs stays captured for
/// <c>CaptureEvictSeconds</c> and is then dropped and evicted from the mirror.</item>
/// <item><b>Gating.</b> A sector that is not captured and complete is forwarded to a client only after the authority's
/// <c>SectorComplete</c> for a matching capture epoch (the client suppresses its local NPCs only after the marker, so it must never
/// see a marker for an incomplete sector).</item>
/// <item><b>Budget.</b> <see cref="GhostBudgetPolicy"/> (<c>MaxGhosts</c>, default 250): over budget it drops XS/S/M from Adjacent
/// first, then unpredicted Adjacent sectors, then the rest of the prefetch, then caps the current sector.</item>
/// <item><b>Fog of war hook.</b> <see cref="VisibilityFilter"/> (ADR-038), a no-op in v1.</item>
/// </list>
/// It is an <see cref="ISessionModule"/> (register with <c>AddInterestManager()</c> after the world mirror) and an
/// <see cref="IWorldObserver"/> of the <see cref="WorldMirror"/>. Everything runs on the actor thread; all state is lock-free.
/// Replication (M1-08) reads the per-client sets through <see cref="HeldBy"/>, <see cref="TierOf"/> and <see cref="Subscriptions"/>.
/// </summary>
public sealed partial class InterestManager : ISessionModule, IWorldObserver
{
    private readonly WorldMirror _mirror;
    private readonly Func<InterestOptions> _options;
    private readonly TimeProvider _time;
    private readonly IInterestTransport _transport;
    private readonly Dictionary<int, SessionNode> _nodes = [];
    private readonly Dictionary<int, ClientInterest> _clients = [];
    private readonly Dictionary<string, ushort[]> _adminViews = new(StringComparer.Ordinal);
    private readonly NearGrid _grid;
    private readonly Dictionary<ushort, int> _gridRefs = [];
    private IVisibilityFilter _visibility = AllVisible.Instance;
    private IGhostObserver[] _ghostObservers = [];
    private int _nearRadiusApplied;

    public InterestManager(
        WorldMirror mirror,
        Func<InterestOptions> options,
        TimeProvider? time = null,
        IInterestTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentNullException.ThrowIfNull(options);
        _mirror = mirror;
        _options = options;
        _time = time ?? TimeProvider.System;
        _transport = transport ?? new NodeTransport(_nodes);
        _nearRadiusApplied = options().NearRadiusM;
        _grid = new NearGrid(_nearRadiusApplied);
        _mirror.AddObserver(this);
    }

    public InterestManager(WorldMirror mirror, InterestOptions options, TimeProvider? time = null, IInterestTransport? transport = null)
        : this(mirror, () => options, time, transport)
    {
    }

    public InterestStats Stats { get; } = new();

    /// <summary>Hears every ghost the manager adds to or removes from a client's held set (replication, M1-08).</summary>
    public void AddGhostObserver(IGhostObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        if (Array.IndexOf(_ghostObservers, observer) < 0)
        {
            _ghostObservers = [.. _ghostObservers, observer];
        }
    }

    /// <summary>The fog-of-war hook (ADR-038). The default shows everything to everyone; set it to hide entities per viewer.</summary>
    public IVisibilityFilter VisibilityFilter
    {
        get => _visibility;
        set
        {
            _visibility = value ?? AllVisible.Instance;
            foreach (var client in _clients.Values)
            {
                if (!client.ReceivesSpawns)
                {
                    continue;
                }

                foreach (var sub in client.Subs.Values.ToArray())
                {
                    if (sub.Delivery != SectorDelivery.Pending)
                    {
                        SyncSector(client, sub);
                    }
                }
            }
        }
    }

    private InterestOptions Opt => _options();

    private long Now => _time.GetTimestamp();

    private long Seconds(double seconds) => (long)(seconds * _time.TimestampFrequency);

    // ------------------------------------------------------------------ queries (replication, GUI, tests)

    public int ClientCount => _clients.Count;

    public bool IsActive(int playerId) => _clients.ContainsKey(playerId);

    /// <summary>The client's current subscriptions (empty when unknown).</summary>
    public IReadOnlyCollection<SectorSubscription> Subscriptions(int playerId) =>
        _clients.TryGetValue(playerId, out var c) ? c.Subs.Values : [];

    public InterestTier GetSectorTier(int playerId, ushort sector) =>
        _clients.TryGetValue(playerId, out var c) && c.Subs.TryGetValue(sector, out var sub) ? sub.Tier : InterestTier.None;

    public SectorDelivery? GetDelivery(int playerId, ushort sector) =>
        _clients.TryGetValue(playerId, out var c) && c.Subs.TryGetValue(sector, out var sub) ? sub.Delivery : null;

    /// <summary>The entities the client holds as ghosts (what the server told it about and has not despawned).</summary>
    public IReadOnlyCollection<uint> HeldBy(int playerId) =>
        _clients.TryGetValue(playerId, out var c) ? c.Held.Keys : [];

    public bool IsHeld(int playerId, uint netId) => _clients.TryGetValue(playerId, out var c) && c.Held.ContainsKey(netId);

    /// <summary>Ghosts counted against the budget (player ships are exempt).</summary>
    public int GhostCount(int playerId) => _clients.TryGetValue(playerId, out var c) ? c.Ghosts : 0;

    public BudgetLevel LevelOf(int playerId) => _clients.TryGetValue(playerId, out var c) ? c.Level : BudgetLevel.Full;

    /// <summary>Epoch of the last <c>InterestUpdate</c> sent to the client.</summary>
    public uint EpochOf(int playerId) => _clients.TryGetValue(playerId, out var c) ? c.Epoch : 0;

    /// <summary>
    /// The tier of one entity for a client: Near inside <c>NearRadius</c> of the player ship in the current sector, else the tier
    /// of its sector; player ships (galaxy-wide, at least 2 Hz) report Adjacent when their sector is not otherwise followed.
    /// </summary>
    public InterestTier TierOf(int playerId, MirrorEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!_clients.TryGetValue(playerId, out var c))
        {
            return InterestTier.None;
        }

        if (c.Subs.TryGetValue(entity.Sector, out var sub))
        {
            if (sub.Tier == InterestTier.Sector && entity.Sector == c.Sector
                && NearGrid.DistanceSquared(entity, c.Px, c.Py, c.Pz) <= NearRadiusSquared(Opt.NearRadiusM))
            {
                return InterestTier.Near;
            }

            return sub.Tier;
        }

        return entity.IsPlayerShip ? InterestTier.Adjacent : InterestTier.None;
    }

    /// <summary>Fills <paramref name="results"/> with the transient entities within <c>NearRadius</c> of the player (via the grid). False when the player has no sector.</summary>
    public bool QueryNear(int playerId, List<MirrorEntity> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return _clients.TryGetValue(playerId, out var c)
            && c.Sector != 0
            && _grid.Query(c.Sector, c.Px, c.Py, c.Pz, Opt.NearRadiusM, results);
    }

    public int ActiveGridSectors => _grid.ActiveSectorCount;

    private static long NearRadiusSquared(int radiusM)
    {
        long r = (long)radiusM * NearGrid.UnitsPerMetre;
        return r * r;
    }

    // ------------------------------------------------------------------ client lifecycle (primitive API)

    /// <summary>
    /// A client node entered <see cref="NodePhase.InGame"/> (or resumed there): start following it. A fresh state, a full
    /// <c>InterestUpdate</c> and the galaxy-wide player ships go out as soon as its position is known.
    /// </summary>
    public void ClientActivated(int playerId, ulong caps, bool isAuthority)
    {
        DropClient(playerId);
        var client = new ClientInterest(playerId, caps, isAuthority) { FramesLeft = Opt.SpawnFramesPerTick };
        _clients[playerId] = client;
        if (_mirror.TryGetPlayerShip(playerId, out var ship))
        {
            client.Sector = ship.Sector;
            client.Px = ship.Px;
            client.Py = ship.Py;
            client.Pz = ship.Pz;
        }

        if (client.ReceivesSpawns)
        {
            var ids = new List<uint>();
            foreach (var entity in _mirror.All)
            {
                if (entity.IsPlayerShip && !entity.IsPersistent)
                {
                    ids.Add(entity.NetId);
                }
            }

            if (ids.Count > 0)
            {
                client.Jobs.Add(new SpawnJob(0, ids, sendComplete: false));
            }
        }

        Recompute(client, Now);
        PumpJobs(client);
    }

    /// <summary>The client left <see cref="NodePhase.InGame"/>, detached or left: forget everything about it (a resume starts fresh).</summary>
    public void ClientDeactivated(int playerId) => DropClient(playerId);

    private void DropClient(int playerId)
    {
        if (_clients.Remove(playerId, out var old))
        {
            SetGridSector(old, 0);
            if (old.Held.Count > 0)
            {
                foreach (var observer in _ghostObservers)
                {
                    observer.OnGhostsReset(playerId);
                }
            }
        }
    }

    // ------------------------------------------------------------------ admin map views (API stub)

    /// <summary>
    /// Subscribes an admin map view to <paramref name="sectors"/> (captured at the Adjacent rate, no entities are sent to anybody:
    /// the GUI reads the mirror). An empty list removes the view. This is the seam the GUI's galaxy-map task (M1-W*) calls; there is
    /// no REST endpoint for it yet.
    /// </summary>
    public void SetAdminView(string viewId, IReadOnlyCollection<ushort> sectors)
    {
        ArgumentException.ThrowIfNullOrEmpty(viewId);
        ArgumentNullException.ThrowIfNull(sectors);
        if (sectors.Count == 0)
        {
            _adminViews.Remove(viewId);
        }
        else
        {
            _adminViews[viewId] = [.. sectors.Distinct()];
        }
    }

    public int AdminViewCount => _adminViews.Count;

    // ------------------------------------------------------------------ module callbacks

    public void OnNodeAttached(SessionNode node, bool resumed)
    {
        _nodes[node.PlayerId] = node;
        if (resumed)
        {
            DropClient(node.PlayerId); // baselines are reset on resume (protocol.md 6.6): a fresh delivery follows InGame
        }

        if (node.IsAuthority)
        {
            if (!resumed)
            {
                ResetCaptureCompletion();
            }

            _forceCapture = true;
        }

        if (node.Phase == NodePhase.InGame)
        {
            ActivateNode(node);
        }
    }

    public void OnNodePhaseChanged(SessionNode node, NodePhase previous, NodePhase current)
    {
        _nodes[node.PlayerId] = node;
        if (current == NodePhase.InGame)
        {
            if (node.IsAuthority)
            {
                _forceCapture = true;
            }

            ActivateNode(node);
        }
        else if (previous == NodePhase.InGame)
        {
            DropClient(node.PlayerId);
        }
    }

    public void OnNodeDetached(SessionNode node, DetachReason reason) => DropClient(node.PlayerId);

    public void OnNodeLeft(SessionNode node, string reason)
    {
        DropClient(node.PlayerId);
        _nodes.Remove(node.PlayerId);
    }

    public void OnSessionPhaseChanged(SessionPhase previous, SessionPhase current)
    {
        if (current == SessionPhase.Ended)
        {
            ClearAll();
        }
    }

    public bool OnMessage(SessionNode node, InboundFrame frame)
    {
        switch (frame.Type)
        {
            case MsgType.SectorComplete when node.IsAuthority:
                HandleSectorComplete(MessageRegistry.Default.Decode<SectorComplete>(frame.Frame));
                return true;
            case MsgType.InterestHint:
                HandleHint(node.PlayerId, MessageRegistry.Default.Decode<InterestHint>(frame.Frame));
                return true;
            default:
                return false;
        }
    }

    public void OnTick(long timestamp) => Tick(timestamp);

    private void ActivateNode(SessionNode node)
    {
        if ((node.Roles & Role.Client) == 0 || _clients.ContainsKey(node.PlayerId))
        {
            return;
        }

        ClientActivated(node.PlayerId, node.Attached?.NegotiatedCaps ?? 0, node.IsAuthority);
    }

    private void ClearAll()
    {
        foreach (var client in _clients.Values)
        {
            SetGridSector(client, 0);
        }

        _clients.Clear();
        _adminViews.Clear();
        _grid.Clear();
        _gridRefs.Clear();
        _capture.Clear();
        _lastSectors.Clear();
        _lastFocus.Clear();
        _everSent = false;
    }

    // ------------------------------------------------------------------ tick

    /// <summary>
    /// Periodic work at <paramref name="timestamp"/> (a <see cref="TimeProvider.GetTimestamp"/> value): linger and hint expiry,
    /// budget re-evaluation, spawn batches that were deferred, near-grid reconfiguration and the capture set.
    /// </summary>
    public void Tick(long timestamp)
    {
        var options = Opt;
        if (options.NearRadiusM != _nearRadiusApplied)
        {
            _nearRadiusApplied = options.NearRadiusM;
            _grid.Clear();
            _gridRefs.Clear();
            foreach (var client in _clients.Values)
            {
                client.GridSector = 0;
            }
        }

        foreach (var client in _clients.Values.ToArray())
        {
            if (!_clients.ContainsKey(client.PlayerId))
            {
                continue;
            }

            client.FramesLeft = options.SpawnFramesPerTick;
            Recompute(client, timestamp);
            PumpJobs(client);
        }

        MaybeSendCaptureSet(timestamp);
    }
}
