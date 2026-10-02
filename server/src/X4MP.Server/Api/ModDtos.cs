namespace X4MP.Server.Api;

// DTOs of the mod-management admin API (task M1-X4, docs/mod-management.md) and the hub's mods topic. Conventions as in AdminDtos: strings for enums
// (the schema names), ISO-8601 timestamps, hashes as lowercase hex. Everything the GUI shows about a mod's links is already derived here
// (Workshop https and steam:// URLs), so the page never builds a URL.

/// <summary>
/// One mod of the session mod list. <c>Rule</c>: Required | Allowed | Blocked. <c>Enabled</c> is the admin's on/off switch (a disabled Required mod acts as Blocked).
/// <c>ClassOverride</c> (Unknown = none) is the admin's classification, <c>Class</c> the effective one (Dlc | Sim | ClientOnly; the allowlist wins, then the override,
/// then the node's hint, Unknown counts as Sim). <c>VersionRule</c>: Exact | AtLeast | Any. <c>ContentHash</c> is hex or null.
/// <c>WorkshopUrl</c> and <c>WorkshopSteamUrl</c> are derived from <c>WorkshopId</c> (null for 0). <c>IsLibrary</c> marks the shipped client-only allowlist.
/// <c>HasNativeDll</c>, <c>ReplacesBasegame</c> and <c>SaveDependent</c> come from the newest report that carries the mod (the authority's first); null while no report has it.
/// <c>PlayersEnabled</c>, <c>PlayersDisabled</c> and <c>PlayersMissing</c> count the players whose newest report has the mod enabled, installed but disabled, or not at all;
/// they are 0 while the caller may not see players' mod lists (<see cref="ModsStateDto.PlayersHidden"/>).
/// </summary>
[TsContract]
public sealed record ModEntryDto(
    string Id, string Name, string Rule, bool Enabled, string ClassOverride, string Class, string VersionRule, string Version, string? ContentHash,
    string? NexusUrl, long WorkshopId, string? WorkshopUrl, string? WorkshopSteamUrl, string Notes, bool IsLibrary,
    bool? HasNativeDll, bool? ReplacesBasegame, bool? SaveDependent, int PlayersEnabled, int PlayersDisabled, int PlayersMissing);

/// <summary>
/// The session mod policy. <c>Version</c> goes up with every change (it is also what nodes see in <c>ServerHello.mod_policy_version</c>).
/// <c>SourceMode</c>: AuthorityDefines | AdminList. <c>UnknownDefault</c>: AllowClientOnly | Block | AllowAll. <c>Enforcement</c>: Strict | Warn.
/// <c>ModListVisibility</c> (read-only here, a setting of <c>X4MP:Mods</c>): AdminsOnly | AdminsAndViewers | AllPlayers.
/// <c>AuthorityPlayerId</c>/<c>AuthorityReportedAt</c> describe the report "Import from authority" would use (null when there is none).
/// </summary>
[TsContract]
public sealed record ModPolicyDto(
    long Version, string SourceMode, string UnknownDefault, string Enforcement, string ModListVisibility, List<ModEntryDto> Entries,
    DateTimeOffset? UpdatedAt, string? UpdatedBy, long? AuthorityPlayerId, DateTimeOffset? AuthorityReportedAt);

/// <summary>One mod named in a violation. <c>Version</c> is what the session wants ("" = any), <c>HaveVersion</c> what the player has ("" = not installed).</summary>
[TsContract]
public sealed record ModRefDto(
    string Id, string Name, string Version, string HaveVersion, string? NexusUrl, long WorkshopId, string? WorkshopUrl, string? WorkshopSteamUrl, string Notes);

/// <summary>What a player must do to match the policy: install (missing), enable (installed but off), disable (blocked or not part of the session), update (wrong version or hash).</summary>
[TsContract]
public sealed record ModViolationDto(
    long PolicyVersion, List<ModRefDto> Install, List<ModRefDto> Enable, List<ModRefDto> Disable, List<ModRefDto> Update);

/// <summary>
/// One player's mod status against the <em>current</em> policy. <c>Status</c>: Matches | Violates | NoReport.
/// <c>Outcome</c> is how the newest report was judged when it arrived (Admitted | Warned | Rejected | None); <c>ReportPolicyVersion</c> the policy version it was judged by.
/// <c>Violation</c> is null for Matches and NoReport; it lists what differs now. <c>Online</c> and <c>IsAuthority</c> describe the live session.
/// </summary>
[TsContract]
public sealed record PlayerModStatusDto(
    long PlayerId, string Name, bool Online, bool IsAuthority, string Status, string Outcome, DateTimeOffset? ReportedAt, long ReportPolicyVersion,
    int ExtensionCount, ModViolationDto? Violation);

/// <summary>
/// Everything the Mods page needs in one call (<c>GET /api/v1/mods</c>, the hub's <c>SubscribeMods</c> result).
/// <c>CanEdit</c> is true for Admin and ModEditor. <c>PlayersHidden</c> is true when <c>ModListVisibility</c> hides players' lists from this caller:
/// <c>Players</c> is then empty and the per-mod player counts are 0. <c>SaveRequirementsAvailable</c> is false while the server cannot read a save's required mods
/// (<c>GET /api/v1/mods/save-requirements</c> answers 501).
/// </summary>
[TsContract]
public sealed record ModsStateDto(
    ModPolicyDto Policy, List<PlayerModStatusDto> Players, bool CanEdit, bool PlayersHidden, bool SaveRequirementsAvailable);

/// <summary>A dependency a mod declares in its content.xml.</summary>
[TsContract]
public sealed record ExtensionDependencyDto(string Id, bool Optional);

/// <summary>
/// One extension of a player's report. <c>Source</c>: Dlc | Install | User | Workshop. <c>HashKind</c>: None | CatIndex | Files. <c>ClassHint</c> is the node's
/// heuristic, <c>EffectiveClass</c> what the policy treats it as (Dlc | Sim | ClientOnly). <c>InPolicy</c> is true when the session list has an entry for it.
/// </summary>
[TsContract]
public sealed record ExtensionDto(
    string Id, string Name, string Version, string Source, bool Enabled, bool Egosoft, long WorkshopId, string? WorkshopUrl, string? ContentHash, string HashKind,
    bool HasNativeDll, bool ReplacesBasegame, bool SaveDependent, string ClassHint, string EffectiveClass, string Error, string Warning,
    List<ExtensionDependencyDto> Dependencies, bool InPolicy);

/// <summary>One stored report. <c>CurrentViolation</c> is what differs from the current policy now (null when it matches); <c>Violation</c> is what differed when it arrived.</summary>
[TsContract]
public sealed record ExtensionReportDto(
    long PlayerId, DateTimeOffset At, string Outcome, long PolicyVersion, string? ExtensionsHash, List<ExtensionDto> Items, ModViolationDto? Violation,
    ModViolationDto? CurrentViolation);

/// <summary>A report without its list (the history rows).</summary>
[TsContract]
public sealed record ExtensionReportSummaryDto(
    DateTimeOffset At, string Outcome, long PolicyVersion, string? ExtensionsHash, int ExtensionCount, int EnabledCount, ModViolationDto? Violation);

/// <summary>Response of <c>GET /api/v1/players/{id}/extensions</c>: the newest report in full and the history (newest first, at most <c>limit</c>, default and maximum 20).</summary>
[TsContract]
public sealed record PlayerExtensionsDto(long PlayerId, string Name, ExtensionReportDto? Latest, List<ExtensionReportSummaryDto> History);

/// <summary>A mod the server knows: from reports (name, Workshop id) and from admin edits (links, class override, notes). <c>InPolicy</c>: the session list has an entry.</summary>
[TsContract]
public sealed record ModCatalogEntryDto(
    string Id, string Name, string? NexusUrl, long WorkshopId, string? WorkshopUrl, string? WorkshopSteamUrl, string ClassOverride, string? Notes, DateTimeOffset UpdatedAt,
    bool InPolicy, bool IsLibrary);

/// <summary>Body of <c>PATCH /api/v1/mods/policy</c>. Omitted fields stay as they are. Enum values are the schema names.</summary>
[TsContract]
public sealed record PatchModPolicyRequest(string? SourceMode, string? UnknownDefault, string? Enforcement);

/// <summary>
/// Body of <c>PUT /api/v1/mods/entries/{extId}</c> (create or update). <c>Rule</c> is required for a new entry. Omitted fields keep their value (a new entry gets: enabled, no class
/// override, Exact for Required and Any for Allowed/Blocked, name from the catalog or the id). <c>NexusUrl</c> must be a nexusmods.com/x4foundations/mods/&lt;n&gt; page ("" clears it);
/// <c>WorkshopId</c> of 0 clears it (a <c>ws_&lt;n&gt;</c> id derives it). <c>ContentHash</c> is hex ("" clears it).
/// </summary>
[TsContract]
public sealed record PutModEntryRequest(
    string? Name, string? Rule, bool? Enabled, string? ClassOverride, string? VersionRule, string? Version, string? ContentHash, string? NexusUrl, long? WorkshopId, string? Notes);

/// <summary>Body of <c>POST /api/v1/mods/import-from-authority</c>. <c>Merge</c> (default true) keeps the admin's entries, links and notes; false replaces the list.</summary>
[TsContract]
public sealed record ImportModsRequest(bool? Merge);

/// <summary>Body of <c>PUT /api/v1/mods/catalog/{extId}</c>. Omitted fields stay. Same link rules as <see cref="PutModEntryRequest"/>.</summary>
[TsContract]
public sealed record PutModCatalogRequest(string? Name, string? NexusUrl, long? WorkshopId, string? ClassOverride, string? Notes);

/// <summary>One extension a save file needs (its <c>&lt;patches&gt;</c>). For the day <c>GET /api/v1/mods/save-requirements</c> is implemented.</summary>
[TsContract]
public sealed record SavePatchDto(string Extension, string Name, string Version, bool InPolicy, bool Blocked);

/// <summary>The mods the session save needs (see <see cref="SavePatchDto"/>).</summary>
[TsContract]
public sealed record SaveRequirementsDto(string? SaveSha256, List<SavePatchDto> Patches);

/// <summary>
/// A refused connection whose key is bound to no player (it never claimed its name), newest attempt per key (<c>GET /api/v1/mods/rejections</c>). <c>KeyId</c> is the
/// first 12 hex digits of the key hash; <c>AttemptedName</c> is what it asked for. If that key is later admitted, its attempts move to the player's history.
/// </summary>
[TsContract]
public sealed record UnboundRejectionDto(
    string KeyId, string AttemptedName, DateTimeOffset At, long PolicyVersion, int ExtensionCount, ModViolationDto? Violation);
