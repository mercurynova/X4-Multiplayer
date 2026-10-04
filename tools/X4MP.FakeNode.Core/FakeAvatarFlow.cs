using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

/// <summary>The client half of the avatar flow (m3-plan 4.3): what a fake client sends to get its avatar, and where it says it stands.</summary>
public static class FakeAvatarFlow
{
    /// <summary>
    /// Where every client "stands" when it joins: in the host's ship, which is where the save's player ship was. The same place for everybody (the
    /// authority self-spawns the host ship at the first request's position): the first sector with a gate, a spot a few km from its centre.
    /// </summary>
    public static (ushort Sector, Vec3 Position) HostStand(FakeGalaxy galaxy, Vec3? position = null)
    {
        ArgumentNullException.ThrowIfNull(galaxy);
        ushort sector = galaxy.PlayableSectors.Count > 0 ? galaxy.PlayableSectors[0] : (ushort)1;
        return (sector, position ?? new Vec3(1500, 0, -1200));
    }

    /// <summary>The <c>PlayerShip</c> request of <paramref name="playerId"/>: the ship it stands in (the host's), at the host stand.</summary>
    public static PlayerShipT BuildRequest(CliOptions options, FakeGalaxy galaxy, int playerId)
    {
        var (sector, pos) = HostStand(galaxy, options.HostStandPosition);
        return new PlayerShipT
        {
            RequestKey = new Id128T { Lo = (ulong)playerId, Hi = 0x4156415441520001UL },
            ShipMacro = options.HostShipMacro,
            Name = "Host ship",
            Idcode = "HST-001",
            Sector = sector,
            Px = Quantize.Position(pos.X),
            Py = Quantize.Position(pos.Y),
            Pz = Quantize.Position(pos.Z),
            Yaw = 0,
            Pitch = 0,
            Roll = 0,
            Hull = 255,
            Shield = 255,
            LocalComponentId = 0x1000UL + (ulong)playerId,
        };
    }

    /// <summary>The wingman target this client follows, or null when it flies by itself (no <c>--wingman</c>, or it is the target).</summary>
    public static string? WingmanTargetFor(CliOptions options, string ownName) =>
        options.Wingman is { } target && !string.Equals(target.Trim(), ownName, StringComparison.OrdinalIgnoreCase) ? target : null;
}
