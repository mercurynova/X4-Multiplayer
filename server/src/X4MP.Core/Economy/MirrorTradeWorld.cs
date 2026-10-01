using Google.FlatBuffers;
using X4MP.Core.Permissions;
using X4MP.Core.Teams;
using X4MP.Core.World;
using X4MP.Proto;

namespace X4MP.Core.Economy;

/// <summary>
/// <see cref="ITradeWorld"/> over the <see cref="WorldMirror"/>: ownership as mirrored (<c>owner_team</c>/<c>owner_player</c>), positions of
/// assets and player ships, and the asset-policy rules of <see cref="AssetPermissionPolicy"/> for who may give an asset away.
/// </summary>
public sealed class MirrorTradeWorld(
    WorldMirror mirror,
    ITeamDirectory? teams,
    Func<TeamOptions> teamOptions,
    Func<int, int?>? leaderOf = null) : ITradeWorld
{
    public bool TryGetAsset(uint netId, out TradeAssetInfo asset)
    {
        if (mirror.TryGet(netId, out var entity))
        {
            asset = new TradeAssetInfo(netId, entity.Kind, entity.OwnerTeam, entity.OwnerPlayer, entity.Sector, entity.IsPlayerShip);
            return true;
        }

        asset = default;
        return false;
    }

    public bool TryGetPlayerShip(int playerId, out uint netId, out ushort sector)
    {
        if (mirror.TryGetPlayerShip(playerId, out var ship) && ship.NetId != 0)
        {
            netId = ship.NetId;
            sector = ship.Sector;
            return true;
        }

        netId = 0;
        sector = 0;
        return false;
    }

    public long? CargoAmount(uint container, uint wareRef)
    {
        if (!mirror.TryGet(container, out var entity) || entity.Cargo is not { } cargo)
        {
            return null;
        }

        long total = 0;
        foreach (var ware in cargo)
        {
            if (ware.WareRef == wareRef)
            {
                total += ware.Amount;
            }
        }

        return total;
    }

    public string? DenyGive(int player, in TradeAssetInfo asset, bool crossTeam, bool shipTransfer)
    {
        var settings = Settings();
        var verdict = AssetPermissionPolicy.Owns(settings, Actor(player), new PermissionSubject(asset.OwnerTeam, asset.OwnerPlayer));
        if (!verdict.Allowed)
        {
            return verdict.Detail ?? "not your asset";
        }

        return shipTransfer && crossTeam && !settings.AllowAssetTransfer ? "ships cannot change teams (asset transfers are disabled)" : null;
    }

    public string? DenyReceive(int player, in TradeAssetInfo asset)
    {
        var verdict = AssetPermissionPolicy.Owns(Settings(), Actor(player), new PermissionSubject(asset.OwnerTeam, asset.OwnerPlayer));
        return verdict.Allowed ? null : "the destination container is not the receiver's: " + verdict.Detail;
    }

    public void SetOwner(uint netId, int team, int player, Id128T cause)
    {
        var fbb = new FlatBufferBuilder(96);
        var change = new EntityChangeT
        {
            NetId = netId,
            Fields = ChangeField.OwnerTeam | ChangeField.OwnerPlayer,
            OwnerTeam = (ushort)team,
            OwnerPlayer = (ushort)player,
            CauseTradeId = cause,
        };
        fbb.Finish(EntityChange.Pack(fbb, change).Value);
        mirror.ApplyChange(EntityChange.GetRootAsEntityChange(fbb.DataBuffer));
    }

    private AssetPermissionSettings Settings()
    {
        var options = teamOptions();
        return new AssetPermissionSettings(options.AssetPolicy, options.AllowFriendlyFire, options.AllowAssetTransfer);
    }

    private PermissionActor Actor(int player)
    {
        var team = teams?.TeamOf(player) ?? 0;
        return new PermissionActor(player, team, team != 0 && leaderOf?.Invoke(team) == player);
    }
}
