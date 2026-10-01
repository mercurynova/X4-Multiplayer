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

        // PlayerShip has no player field and the authority must know whose avatar it is: the server stamps the player id into the
        // key's high half (the low half stays the client's idempotency key).
        ship.RequestKey ??= new Id128T();
        ship.RequestKey.Hi = (ulong)node.PlayerId;
        _avatarRequests[node.PlayerId] = ship;
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

        foreach (var ship in _avatarRequests.Values)
        {
            ForwardAvatarRequest(authority, ship);
        }
    }

    // ------------------------------------------------------------------ the mirror tells us which ship is whose

    public void OnEntitySpawned(MirrorEntity entity, bool isNew, ushort previousSector, ulong journalSeq)
    {
        if (entity.ControllerPlayer == 0)
        {
            return;
        }

        int player = entity.ControllerPlayer;
        _avatarRequests.Remove(player);
        if (!_nodes.TryGetValue(player, out var node) || node.ShipNetId == entity.NetId)
        {
            return;
        }

        uint previous = node.ShipNetId;
        node.ShipNetId = entity.NetId;
        Publish(new PlayerShipAssigned(_time.GetUtcNow(), SessionId, player, entity.NetId, previous));
        BroadcastRoster(node);
    }

    public void OnEntityDespawned(MirrorEntity entity, DespawnReason reason, uint killerNetId, ulong journalSeq)
    {
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
}
