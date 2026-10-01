using X4MP.Core.Teams;
using X4MP.Proto;
using TeamRelation = X4MP.Core.Teams.TeamRelation;

namespace X4MP.Core.Permissions;

/// <summary>What a client asks to do with an asset (server-design 2.13, protocol.md 16.2).</summary>
public enum AssetAction
{
    /// <summary><c>AssetOrder</c> that is not an attack (move, dock, trade, ...): own asset.</summary>
    Order,

    /// <summary><c>AssetOrder{Attack}</c>: own asset, and the target must be attackable.</summary>
    OrderAttack,

    Rename,

    Gift,

    /// <summary><c>StationBuildRequest</c>: the sender must be on a team.</summary>
    StationBuild,

    /// <summary><c>TradeReport</c>: own ship, a station that is NPC or not Hostile.</summary>
    TradeReport,

    /// <summary><c>KillClaim</c> / one <c>HitReport</c> entry: in range, NPC or Hostile target, unless friendly fire.</summary>
    KillOrHit,

    /// <summary><c>CaptureReport</c>: NPC or Hostile target.</summary>
    Capture,
}

/// <summary>The sender: player, team (0 = none yet) and whether it leads that team.</summary>
public readonly record struct PermissionActor(int PlayerId, int Team, bool IsLeader);

/// <summary>Owner of an entity as mirrored: team 0 = NPC, player 0 = team-common.</summary>
public readonly record struct PermissionSubject(int Team, int Player);

/// <summary>The settings the policy reads (a snapshot of <see cref="TeamOptions"/> plus the claim range).</summary>
public sealed record AssetPermissionSettings(
    TeamAssetPolicy AssetPolicy = TeamAssetPolicy.SharedCommand,
    bool AllowFriendlyFire = false,
    bool AllowAssetTransfer = false,
    double MaxClaimRangeMetres = 30_000);

/// <summary>Result of a check: <see cref="RejectReason.None"/> = allowed.</summary>
public readonly record struct PermissionVerdict(RejectReason Reason, string? Detail = null)
{
    public bool Allowed => Reason == RejectReason.None;

    public static PermissionVerdict Allow { get; } = new(RejectReason.None);
}

/// <summary>
/// The pure permission rules (server-design 2.13): no state, no I/O. The relay's gate looks the facts up (mirror, teams) and calls
/// <see cref="Evaluate"/>. Rejections are never forwarded to the authority.
/// </summary>
public static class AssetPermissionPolicy
{
    /// <summary>Does the sender command this asset under the team's asset policy?</summary>
    public static PermissionVerdict Owns(AssetPermissionSettings settings, PermissionActor actor, PermissionSubject asset)
    {
        if (actor.Team == 0)
        {
            return Deny(RejectReason.PolicyDenied, "you are not on a team");
        }

        if (asset.Team != actor.Team)
        {
            return Deny(RejectReason.NotYourAsset, "the asset belongs to another team");
        }

        bool ok = settings.AssetPolicy == TeamAssetPolicy.SharedCommand
            || asset.Player == 0
            || asset.Player == actor.PlayerId
            || (settings.AssetPolicy == TeamAssetPolicy.OwnerAndLeader && actor.IsLeader);
        return ok ? PermissionVerdict.Allow : Deny(RejectReason.NotYourAsset, "the asset belongs to a teammate (asset policy)");
    }

    /// <summary>
    /// Decides one action.
    /// </summary>
    /// <param name="asset">The sender's own asset (order, rename, gift, trade ship); null for the other actions.</param>
    /// <param name="other">The other party: attack target, trade station, kill/capture target, gift destination (player 0).</param>
    /// <param name="relation">Relation between the sender's team and <paramref name="other"/>'s team (ignored for NPCs).</param>
    /// <param name="distanceMetres">Sender ship to target, for <see cref="AssetAction.KillOrHit"/>; null = unknown (out of range).</param>
    public static PermissionVerdict Evaluate(
        AssetPermissionSettings settings,
        PermissionActor actor,
        AssetAction action,
        PermissionSubject? asset,
        PermissionSubject? other,
        TeamRelation relation,
        double? distanceMetres = null)
    {
        if (actor.Team == 0)
        {
            return Deny(RejectReason.PolicyDenied, "you are not on a team");
        }

        switch (action)
        {
            case AssetAction.StationBuild:
                return PermissionVerdict.Allow;

            case AssetAction.Order:
            case AssetAction.Rename:
                return Owns(settings, actor, asset ?? default);

            case AssetAction.OrderAttack:
                {
                    var owns = Owns(settings, actor, asset ?? default);
                    return !owns.Allowed ? owns : Hostility(settings, other ?? default, relation, "attack");
                }

            case AssetAction.Gift:
                {
                    var owns = Owns(settings, actor, asset ?? default);
                    if (!owns.Allowed)
                    {
                        return owns;
                    }

                    if (!settings.AllowAssetTransfer)
                    {
                        return Deny(RejectReason.PolicyDenied, "asset transfers are disabled");
                    }

                    return relation == TeamRelation.Allied ? PermissionVerdict.Allow : Deny(RejectReason.NotAllied, "the destination team is not allied");
                }

            case AssetAction.TradeReport:
                {
                    var owns = Owns(settings, actor, asset ?? default);
                    if (!owns.Allowed)
                    {
                        return owns;
                    }

                    return other is { Team: not 0 } && relation == TeamRelation.Hostile
                        ? Deny(RejectReason.PolicyDenied, "cannot trade at a hostile team's station")
                        : PermissionVerdict.Allow;
                }

            case AssetAction.KillOrHit:
                {
                    if (distanceMetres is not { } d || d > settings.MaxClaimRangeMetres)
                    {
                        return Deny(RejectReason.NotPermitted, "target out of range");
                    }

                    return Hostility(settings, other ?? default, relation, "kill or hit");
                }

            case AssetAction.Capture:
                {
                    var target = other ?? default;
                    return target.Team == 0 || relation == TeamRelation.Hostile
                        ? PermissionVerdict.Allow
                        : Deny(RejectReason.HostileRequired, "only NPC or hostile assets can be captured");
                }

            default:
                return Deny(RejectReason.Unsupported, "unknown action");
        }
    }

    /// <summary>NPC targets are fair game; teams must be Hostile, unless friendly fire is on.</summary>
    private static PermissionVerdict Hostility(AssetPermissionSettings settings, PermissionSubject target, TeamRelation relation, string verb)
    {
        if (target.Team == 0 || relation == TeamRelation.Hostile || settings.AllowFriendlyFire)
        {
            return PermissionVerdict.Allow;
        }

        return relation == TeamRelation.Neutral
            ? Deny(RejectReason.HostileRequired, $"cannot {verb} a neutral team's asset")
            : Deny(RejectReason.FriendlyFireDisabled, $"cannot {verb} an allied or teammate asset (friendly fire is off)");
    }

    private static PermissionVerdict Deny(RejectReason reason, string detail) => new(reason, detail);
}
