using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Relay;

public sealed partial class RelayModule
{
    /// <summary>Per-player relay state: the velocity estimator and the newest state not yet fed to the authority.</summary>
    private sealed class PlayerFlow(int playerId)
    {
        public int PlayerId { get; } = playerId;

        public MotionEstimator Estimator { get; } = new();

        public PlayerStateT? Latest { get; set; }

        public bool Dirty { get; set; }

        public long NextFeed { get; set; }
    }

    private readonly Dictionary<int, PlayerShipT> _avatarRequests = [];

    // M3-29: the newest PlayerShip of every player, kept after the avatar was bound (_avatarRequests drops it then). When the world is rolled back
    // for a re-hosted authority the player's avatar is gone from the mirror while the player keeps flying: this is what the new authority is asked
    // to provision again. _reprovision = the players whose pending request is such a re-send (their pose is refreshed from the newest PlayerState).
    private readonly Dictionary<int, PlayerShipT> _lastShips = [];
    private readonly HashSet<int> _reprovision = [];
    private ITimer? _flushTimer;
    private int _flushArmed;

    /// <summary>The velocity the relay derived for a player (metres per second), for tests and the GUI; false before two samples.</summary>
    public bool TryGetVelocity(int playerId, out (double X, double Y, double Z) velocity)
    {
        if (_flows.TryGetValue(playerId, out var flow) && flow.Estimator.Samples >= 2)
        {
            velocity = flow.Estimator.Velocity;
            return true;
        }

        velocity = default;
        return false;
    }

    // ------------------------------------------------------------------ PlayerState

    private void OnPlayerState(SessionNode node, InboundFrame frame)
    {
        Stats.StatesReceived++;
        var state = MessageRegistry.Default.Decode<PlayerState>(frame.Frame).UnPack();
        if (!_flows.TryGetValue(node.PlayerId, out var flow))
        {
            flow = new PlayerFlow(node.PlayerId);
            _flows[node.PlayerId] = flow;
        }

        node.Sector = state.Sector;
        node.PosX = state.Px;
        node.PosY = state.Py;
        node.PosZ = state.Pz;
        var velocity = flow.Estimator.Add(state.Sector, state.Flags, state.SampleTimeUs, state.Px, state.Py, state.Pz);
        _mirror?.ApplyPlayerVelocity(node.PlayerId, velocity.X, velocity.Y, velocity.Z);

        if (node.IsAuthority)
        {
            return; // the authority's own ship is simulated there: nothing to feed back
        }

        if (node.ShipNetId != 0)
        {
            state.NetId = node.ShipNetId; // the server knows the avatar even when the client has not learnt its id yet
        }
        else if (state.NetId != 0 && _mirror is not null && !(_mirror.TryGet(state.NetId, out var claimed) && claimed.ControllerPlayer == node.PlayerId))
        {
            state.NetId = 0; // M3-29: the client still names an avatar a world rollback removed (or one that is not theirs): never feed that id to the authority
        }

        if (flow.Dirty)
        {
            Stats.StatesSuperseded++;
        }

        flow.Latest = state;
        flow.Dirty = true;
        Feed(flow, Now);
    }

    /// <summary>Sends the player's newest state to the authority when its 20 Hz slot is due, else arms a timer for the slot.</summary>
    private void Feed(PlayerFlow flow, long now)
    {
        if (!flow.Dirty || flow.Latest is not { } state)
        {
            return;
        }

        if (now < flow.NextFeed)
        {
            ArmFlush(flow.NextFeed - now);
            return;
        }

        flow.Dirty = false;
        if (Authority is not { } authority)
        {
            return; // nobody to feed; a stale pose is not worth keeping
        }

        var frame = Encode(
            MsgType.PlayerState,
            fbb => PlayerState.Pack(fbb, state),
            96,
            coalesceKey: ((ulong)MsgType.PlayerState << 32) | (uint)flow.PlayerId);
        TrySend(authority, frame);
        frame.Release();
        flow.NextFeed = now + FeedIntervalTicks;
        Stats.StatesRelayed++;
    }

    private long FeedIntervalTicks => _time.TimestampFrequency / Math.Max(1, Opt.PlayerStateRelayHz);

    private void FlushDueStates(long now)
    {
        long soonest = long.MaxValue;
        foreach (var flow in _flows.Values)
        {
            if (!flow.Dirty)
            {
                continue;
            }

            Feed(flow, now);
            if (flow.Dirty)
            {
                soonest = Math.Min(soonest, flow.NextFeed);
            }
        }

        if (soonest != long.MaxValue)
        {
            ArmFlush(Math.Max(1, soonest - now));
        }
    }

    private void ArmFlush(long delayTicks)
    {
        if (Interlocked.Exchange(ref _flushArmed, 1) != 0)
        {
            return;
        }

        var due = TimeSpan.FromSeconds(Math.Max(0.001, delayTicks / (double)_time.TimestampFrequency));
        _flushTimer?.Dispose();
        _flushTimer = _time.CreateTimer(static state => ((RelayModule)state!).OnFlushTimer(), this, due, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Timer thread: hop onto the actor to feed what is due.</summary>
    private void OnFlushTimer()
    {
        var driver = _driver;
        if (driver is null)
        {
            Volatile.Write(ref _flushArmed, 0);
            return;
        }

        _ = driver.CallAsync(() =>
        {
            Volatile.Write(ref _flushArmed, 0);
            FlushDueStates(Now);
            return true;
        }).ContinueWith(static _ => { }, TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ PlayerShip (the avatar flow)

    private void OnPlayerShip(SessionNode node, InboundFrame frame)
    {
        var ship = MessageRegistry.Default.Decode<PlayerShip>(frame.Frame).UnPack();

        // The authority must know whose avatar this is: the server stamps the sender's id (a client-supplied value is overwritten).
        // request_key stays a pure idempotency key.
        ship.PlayerId = (ushort)node.PlayerId;
        _avatarRequests[node.PlayerId] = ship;
        _lastShips[node.PlayerId] = ship;
        _reprovision.Remove(node.PlayerId); // the player asked itself: the request is theirs, with their pose
        if (Authority is { } authority)
        {
            ForwardAvatarRequest(authority, ship);
        }
    }

    private void ForwardAvatarRequest(SessionNode authority, PlayerShipT ship)
    {
        var frame = Encode(MsgType.PlayerShip, fbb => PlayerShip.Pack(fbb, ship), 256);
        TrySend(authority, frame);
        frame.Release();
        Stats.PlayerShipsForwarded++;
    }

    /// <summary>The authority is (back) in game: registration that waited, or whose answer may have been lost, goes out again (the authority returns an existing avatar).</summary>
    private void ForwardHeldAvatarRequests()
    {
        if (Authority is not { } authority)
        {
            return;
        }

        foreach (var (player, ship) in _avatarRequests)
        {
            if (_reprovision.Contains(player))
            {
                // M3-29: a re-send after a world rollback: the player kept flying, so the avatar starts where they are now (the authority
                // still picks a safe position around it), not at the spot of their first request.
                if (_flows.TryGetValue(player, out var flow) && flow.Latest is { Sector: not 0 } state)
                {
                    ship.Sector = state.Sector;
                    ship.Px = state.Px;
                    ship.Py = state.Py;
                    ship.Pz = state.Pz;
                    ship.Yaw = state.Yaw;
                    ship.Pitch = state.Pitch;
                    ship.Roll = state.Roll;
                }

                Stats.AvatarsReprovisioned++;
                LogReprovision(player, ship.Sector);
            }

            ForwardAvatarRequest(authority, ship);
        }
    }

    /// <summary>
    /// M3-29: a player's avatar left the mirror by a rollback (<see cref="DespawnReason.Removed"/>: the authority loads a save that does not contain it)
    /// while the player is still in the session: their last PlayerShip is held again, and goes to the authority as soon as it is in game
    /// (<see cref="ForwardHeldAvatarRequests"/>), so the player gets a new avatar instead of staying invisible. Not for the authority's own ship (it
    /// self-spawns), not when the player has another ship in the mirror (that one survived), not after an admin removal (those clear the last ship first).
    /// </summary>
    private void QueueReprovision(MirrorEntity entity, DespawnReason reason)
    {
        if (reason != DespawnReason.Removed || entity.Origin != EntityOrigin.PlayerShip || _mirror is null)
        {
            return;
        }

        int player = entity.ControllerPlayer != 0 ? entity.ControllerPlayer : entity.OwnerPlayer;
        if (player == 0
            || !_nodes.TryGetValue(player, out var node)
            || node.IsAuthority
            || !_lastShips.TryGetValue(player, out var last))
        {
            return;
        }

        foreach (var other in _mirror.All)
        {
            if (other.NetId != entity.NetId && other.Origin == EntityOrigin.PlayerShip && (other.ControllerPlayer == player || other.OwnerPlayer == player))
            {
                return;
            }
        }

        _avatarRequests[player] = last;
        _reprovision.Add(player);
    }

    // ------------------------------------------------------------------ the mirror tells us which ship is whose

    public void OnEntitySpawned(MirrorEntity entity, bool isNew, ushort previousSector, ulong journalSeq) => BindShip(entity);

    /// <summary>
    /// An <c>EntityChange</c> that sets <c>controller_player</c>: a rejoining player gets its parked avatar back (the authority may answer
    /// <c>PlayerShip</c> with a change instead of a new spawn), and when the authority clears it (the player left, M3 plan Q6) the avatar
    /// stays in the mirror as a parked ship and a node that still lists it drops it.
    /// </summary>
    public void OnEntityChanged(MirrorEntity entity, EntityChange change, ulong journalSeq)
    {
        if ((change.Fields & ChangeField.Controller) == 0)
        {
            return;
        }

        if (entity.ControllerPlayer != 0)
        {
            BindShip(entity);
            return;
        }

        foreach (var node in _nodes.Values)
        {
            if (node.ShipNetId == entity.NetId)
            {
                node.ShipNetId = 0;
                BroadcastRoster(node);
            }
        }
    }

    private void BindShip(MirrorEntity entity)
    {
        if (entity.ControllerPlayer == 0)
        {
            return;
        }

        int player = entity.ControllerPlayer;
        _avatarRequests.Remove(player);
        _reprovision.Remove(player);
        if (!_nodes.TryGetValue(player, out var node) || node.ShipNetId == entity.NetId)
        {
            return;
        }

        uint previous = node.ShipNetId;
        node.ShipNetId = entity.NetId;
        Publish(new PlayerShipAssigned(_time.GetUtcNow(), SessionId, player, entity.NetId, previous));
        BroadcastRoster(node);
    }

    // ------------------------------------------------------------------ removing a player's avatar (admin kick/ban option)

    /// <summary>
    /// Removes every avatar of a player from the session: the mirror drops it (so the server stops replicating it and the nodes that held
    /// it get <c>EntityDespawn{Removed}</c>) and the authority is told to remove the real ship with an <c>EntityDespawn{Removed}</c> of the
    /// same <c>net_id</c> (the only server-to-authority removal order; held until the authority is in game when it is not now).
    /// Returns the removed <c>net_id</c>s.
    /// </summary>
    public Task<IReadOnlyList<uint>> RemoveAvatarsAsync(int playerId)
    {
        var driver = _driver ?? throw new InvalidOperationException("the relay is not attached to a session");
        return driver.CallAsync<IReadOnlyList<uint>>(() => RemoveAvatars(playerId));
    }

    private List<uint> RemoveAvatars(int playerId)
    {
        if (_mirror is null)
        {
            return [];
        }

        var found = new List<uint>();
        foreach (var entity in _mirror.All)
        {
            if (entity.Origin == EntityOrigin.PlayerShip && (entity.OwnerPlayer == playerId || entity.ControllerPlayer == playerId))
            {
                found.Add(entity.NetId);
            }
        }

        foreach (var node in _nodes.Values)
        {
            if (node.PlayerId == playerId && node.ShipNetId != 0 && !found.Contains(node.ShipNetId))
            {
                found.Add(node.ShipNetId);
            }
        }

        _avatarRequests.Remove(playerId);
        _lastShips.Remove(playerId);
        _reprovision.Remove(playerId);
        foreach (uint netId in found)
        {
            _mirror.Remove(netId, DespawnReason.Removed, journal: false);
            if (Authority is { } authority)
            {
                var frame = Encode(
                    MsgType.EntityDespawn,
                    fbb => EntityDespawn.Pack(fbb, new EntityDespawnT { Entries = [new DespawnEntryT { NetId = netId, Reason = DespawnReason.Removed }] }),
                    64);
                TrySend(authority, frame);
                frame.Release();
            }

            Stats.AvatarsRemoved++;
        }

        return found;
    }

    public void OnEntityDespawned(MirrorEntity entity, DespawnReason reason, uint killerNetId, ulong journalSeq)
    {
        QueueReprovision(entity, reason);
        if (entity.ControllerPlayer != 0
            && _nodes.TryGetValue(entity.ControllerPlayer, out var node)
            && node.ShipNetId == entity.NetId)
        {
            node.ShipNetId = 0;
            BroadcastRoster(node);
        }
    }

    private void BroadcastRoster(SessionNode node)
    {
        var frame = Encode(
            MsgType.RosterUpdate,
            fbb => RosterUpdate.Pack(fbb, new RosterUpdateT { Full = false, Players = [SessionActor.PlayerInfo(node)], Removed = [] }),
            256);
        foreach (var other in _nodes.Values)
        {
            if (other.Announced)
            {
                TrySend(other, frame);
            }
        }

        frame.Release();
    }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "relay: player {PlayerId} lost its avatar to a world rollback: the authority is asked to provision a new one (sector {Sector}, the player's newest position)")]
    private partial void LogReprovision(int playerId, int sector);
}
