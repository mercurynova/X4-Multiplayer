using X4MP.Proto;

namespace X4MP.Core.Mods;

/// <summary>
/// One entry of the session's effective mod list (docs/mod-management.md 3.4): the explicit policy entries plus the implicit ones the
/// authority's own extensions add. Enforcement and the join manifest both read this list, so a launcher can never be told something the
/// session actor does not enforce.
/// </summary>
/// <param name="Id">Extension id.</param>
/// <param name="Name">Display name; empty when the policy never named the extension.</param>
/// <param name="Rule">The rule that is actually applied: a Required entry the admin switched off arrives here as Blocked.</param>
/// <param name="Enabled">The admin switch. False only for an explicit entry that was switched off; implicit authority entries are on.</param>
/// <param name="Class">Extension class, already resolved for the authority's own entries.</param>
/// <param name="VersionRule">How <paramref name="Version"/> is compared.</param>
/// <param name="Version">Desired version, empty for any.</param>
/// <param name="Hash">Expected content hash, empty when the entry carries none.</param>
/// <param name="NexusUrl">Nexus link as stored, empty when there is none.</param>
/// <param name="WorkshopId">Steam Workshop id, 0 when the mod is not on the Workshop.</param>
/// <param name="Notes">Admin notes meant for the player, empty when there are none.</param>
public sealed record EffectiveModRule(
    string Id,
    string Name,
    ModRule Rule,
    bool Enabled,
    ExtensionClass Class,
    VersionRule VersionRule,
    string Version,
    byte[] Hash,
    string NexusUrl,
    ulong WorkshopId,
    string Notes);
