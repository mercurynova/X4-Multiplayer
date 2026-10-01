using X4MP.Core.Session;
using X4MP.Core.Teams;
using X4MP.Core.World;
using X4MP.Proto;
using TeamRelation = X4MP.Core.Teams.TeamRelation;

namespace X4MP.Core.Permissions;

/// <summary>
/// Looks up the facts for <see cref="AssetPermissionPolicy"/> (sender team and leadership, mirror ownership and positions,
/// relations) and judges one <c>Intent</c> (server-design 2.13). Runs on the actor thread. Intent kinds that do not command
/// or modify an asset (<c>PlayerDeath</c>) pass.
/// </summary>
public sealed class AssetPermissionGate(
    WorldMirror mirror,
    ITeamDirectory teams,
    Func<TeamOptions> teamOptions,
    Func<double> claimRangeMetres,
    Func<int, int?>? leaderOf = null,
    Func<uint, bool>? isLocked = null)
{
    private const double PositionScale = 64.0; // protocol.md 11: i32 = round(m * 64)

    /// <summary>The verdict plus what it was about (for the PermissionDenied event).</summary>
    public readonly record struct Result(PermissionVerdict Verdict, AssetAction Action, uint EntityId);

    public Result Check(SessionNode sender, IntentT intent)
    {
        var options = teamOptions();
        var settings = new AssetPermissionSettings(options.AssetPolicy, options.AllowFriendlyFire, options.AllowAssetTransfer, claimRangeMetres());
        int team = teams.TeamOf(sender.PlayerId) ?? 0;
        var actor = new PermissionActor(sender.PlayerId, team, team != 0 && leaderOf?.Invoke(team) == sender.PlayerId);
        var body = intent.Body;

        switch (body.Type)
        {
            case IntentBody.AssetOrder:
                {
                    var order = body.AsAssetOrder();
                    if (Locked(order.Asset, AssetAction.Order) is { } lockedOrder)
                    {
                        return lockedOrder;
                    }

                    if (!Subject(order.Asset, out var asset))
                    {
                        return Unknown(AssetAction.Order, order.Asset);
                    }

                    if (order.Order != OrderKind.Attack)
                    {
                        return Done(AssetPermissionPolicy.Evaluate(settings, actor, AssetAction.Order, asset, null, TeamRelation.Allied), AssetAction.Order, order.Asset);
                    }

                    if (!Subject(order.Target, out var target))
                    {
                        return Unknown(AssetAction.OrderAttack, order.Target);
                    }

                    return Done(Run(settings, actor, AssetAction.OrderAttack, asset, target), AssetAction.OrderAttack, order.Asset);
                }

            case IntentBody.AssetRename:
                {
                    var rename = body.AsAssetRename();
                    if (Locked(rename.Asset, AssetAction.Rename) is { } lockedRename)
                    {
                        return lockedRename;
                    }

                    return !Subject(rename.Asset, out var asset)
                        ? Unknown(AssetAction.Rename, rename.Asset)
                        : Done(AssetPermissionPolicy.Evaluate(settings, actor, AssetAction.Rename, asset, null, TeamRelation.Allied), AssetAction.Rename, rename.Asset);
                }

            case IntentBody.AssetGift:
                {
                    var gift = body.AsAssetGift();
                    if (Locked(gift.Asset, AssetAction.Gift) is { } lockedGift)
                    {
                        return lockedGift;
                    }

                    if (!Subject(gift.Asset, out var asset))
                    {
                        return Unknown(AssetAction.Gift, gift.Asset);
                    }

                    int toTeam = gift.ToTeam == 0 ? team : gift.ToTeam;
                    if (toTeam == 0 || !teams.Teams.Any(t => t.TeamId == toTeam) || (gift.ToPlayer != 0 && teams.TeamOf(gift.ToPlayer) != toTeam))
                    {
                        return Done(new PermissionVerdict(RejectReason.InvalidParameters, "unknown destination team or player"), AssetAction.Gift, gift.Asset);
                    }

                    return Done(
                        AssetPermissionPolicy.Evaluate(settings, actor, AssetAction.Gift, asset, new PermissionSubject(toTeam, gift.ToPlayer), Relation(team, toTeam)),
                        AssetAction.Gift,
                        gift.Asset);
                }

            case IntentBody.StationBuildRequest:
                return Done(AssetPermissionPolicy.Evaluate(settings, actor, AssetAction.StationBuild, null, null, TeamRelation.Allied), AssetAction.StationBuild, 0);

            case IntentBody.TradeReport:
                {
                    var trade = body.AsTradeReport();
                    if (Locked(trade.Ship, AssetAction.TradeReport) is { } lockedShip)
                    {
                        return lockedShip;
                    }

                    if (!Subject(trade.Ship, out var ship))
                    {
                        return Unknown(AssetAction.TradeReport, trade.Ship);
                    }

                    if (!Subject(trade.Station, out var station))
                    {
                        return Unknown(AssetAction.TradeReport, trade.Station);
                    }

                    return Done(Run(settings, actor, AssetAction.TradeReport, ship, station), AssetAction.TradeReport, trade.Ship);
                }

            case IntentBody.KillClaim:
                return Claim(settings, actor, sender.PlayerId, body.AsKillClaim().Target);

            case IntentBody.HitReport:
                {
                    foreach (var hit in body.AsHitReport().Hits ?? [])
                    {
                        var result = Claim(settings, actor, sender.PlayerId, hit.Target);
                        if (!result.Verdict.Allowed)
                        {
                            return result;
                        }
                    }

                    return new Result(PermissionVerdict.Allow, AssetAction.KillOrHit, 0);
                }

            case IntentBody.CaptureReport:
                {
                    var capture = body.AsCaptureReport();
                    return !Subject(capture.Target, out var target)
                        ? Unknown(AssetAction.Capture, capture.Target)
                        : Done(AssetPermissionPolicy.Evaluate(settings, actor, AssetAction.Capture, null, target, Relation(team, target.Team)), AssetAction.Capture, capture.Target);
                }

            default:
                return new Result(PermissionVerdict.Allow, AssetAction.Order, 0);
        }
    }

    private Result Claim(AssetPermissionSettings settings, PermissionActor actor, int playerId, uint targetId)
    {
        if (!Subject(targetId, out var target))
        {
            return Unknown(AssetAction.KillOrHit, targetId);
        }

        double? distance = null;
        if (mirror.TryGetPlayerShip(playerId, out var ship) && mirror.TryGet(targetId, out var entity) && ship.Sector == entity.Sector)
        {
            double dx = (ship.Px - entity.Px) / PositionScale;
            double dy = (ship.Py - entity.Py) / PositionScale;
            double dz = (ship.Pz - entity.Pz) / PositionScale;
            distance = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        return Done(
            AssetPermissionPolicy.Evaluate(settings, actor, AssetAction.KillOrHit, null, target, Relation(actor.Team, target.Team), distance),
            AssetAction.KillOrHit,
            targetId);
    }

    /// <summary>
    /// An entity that an open escrowed trade holds (M1-E5) takes no orders, renames, gifts or station trades until the trade ends,
    /// so it cannot be sold twice or moved while the authority transfers it.
    /// </summary>
    private Result? Locked(uint netId, AssetAction action) =>
        netId != 0 && isLocked?.Invoke(netId) == true
            ? new Result(new PermissionVerdict(RejectReason.Conflict, "the asset is part of an open trade"), action, netId)
            : null;

    private PermissionVerdict Run(AssetPermissionSettings settings, PermissionActor actor, AssetAction action, PermissionSubject asset, PermissionSubject other) =>
        AssetPermissionPolicy.Evaluate(settings, actor, action, asset, other, Relation(actor.Team, other.Team));

    private TeamRelation Relation(int teamA, int teamB) => teamA == 0 || teamB == 0 ? TeamRelation.Neutral : teams.RelationBetween(teamA, teamB);

    private bool Subject(uint netId, out PermissionSubject subject)
    {
        if (mirror.TryGet(netId, out var entity))
        {
            subject = new PermissionSubject(entity.OwnerTeam, entity.OwnerPlayer);
            return true;
        }

        subject = default;
        return false;
    }

    private static Result Unknown(AssetAction action, uint id) =>
        new(new PermissionVerdict(RejectReason.UnknownEntity, "unknown entity"), action, id);

    private static Result Done(PermissionVerdict verdict, AssetAction action, uint id) => new(verdict, action, id);
}
