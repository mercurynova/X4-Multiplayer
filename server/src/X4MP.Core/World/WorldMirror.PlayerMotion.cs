using X4MP.Protocol;

namespace X4MP.Core.World;

public sealed partial class WorldMirror
{
    /// <summary>
    /// Writes the server-derived velocity (m/s, protocol.md 13: the client sends none) into the mirror entity of the player's ship so
    /// replication carries it. <see cref="ApplyPlayerState"/> leaves the velocity alone; the relay (M1-10) derives it from three
    /// samples and calls this right after. Does nothing while the player's ship is not bound to a mirror entity. Components beyond the
    /// fine range are clamped (a ship never gets close: 8 km/s).
    /// </summary>
    public void ApplyPlayerVelocity(int playerId, double vxMps, double vyMps, double vzMps)
    {
        if (!_players.TryGetValue(playerId, out var ship)
            || ship.NetId == 0
            || !_entities.TryGetValue(ship.NetId, out var entity)
            || entity.ControllerPlayer != playerId)
        {
            return;
        }

        short vx = Fine(vxMps);
        short vy = Fine(vyMps);
        short vz = Fine(vzMps);
        if (vx == entity.Vx && vy == entity.Vy && vz == entity.Vz)
        {
            return;
        }

        entity.Vx = vx;
        entity.Vy = vy;
        entity.Vz = vz;
        entity.Version++;
        var observers = _observers;
        for (int i = 0; i < observers.Length; i++)
        {
            observers[i].OnEntityStateChanged(entity, entity.Sector, StateChange.Velocity);
        }
    }

    private static short Fine(double metresPerSecond)
    {
        double clamped = double.IsFinite(metresPerSecond)
            ? Math.Clamp(metresPerSecond, -Quantize.MaxFineVelocityMps, Quantize.MaxFineVelocityMps)
            : 0.0;
        return Quantize.Velocity(clamped, coarse: false);
    }
}
