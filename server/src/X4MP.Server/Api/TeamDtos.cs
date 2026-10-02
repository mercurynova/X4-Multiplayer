namespace X4MP.Server.Api;

// DTOs of the teams admin API and the hub's teams topic (server-design 2.13, 4.4 to 4.6, task M1-T5). Same conventions as AdminDtos:
// strings for enums ("Allied"|"Neutral"|"Hostile", "Member"|"Leader", ...), timestamps as ISO-8601.

/// <summary>One team. <c>Color</c> is "#RRGGBB"; <c>HasPassword</c> says a lobby password is set (the password itself is never returned).</summary>
[TsContract]
public sealed record TeamDto(
    long Id, string Name, string Color, int FactionSlot, long? LeaderPlayerId, bool Locked, int? MaxMembers, bool HasPassword, int MemberCount);

/// <summary>
/// A player's place in the teams. <c>TeamId</c> is null for an unassigned player (<c>Role</c> is then <c>Member</c>, <c>AssignedBy</c> empty).
/// <c>AssignedBy</c> is <c>auto</c>, <c>lobby</c>, <c>preset</c>, <c>self</c> or <c>admin:name</c>.
/// </summary>
[TsContract]
public sealed record TeamMemberDto(
    long PlayerId, string Name, long? TeamId, string Role, bool Online, bool IsAuthority, string AssignedBy, DateTimeOffset? Since);

/// <summary>One explicitly set relation between two teams (symmetric; <c>TeamA</c> is the lower id).</summary>
[TsContract]
public sealed record TeamRelationEntryDto(long TeamA, long TeamB, string Relation);

/// <summary>The relation matrix: the explicit pairs and the relation every other pair has. <c>Version</c> goes up with every change.</summary>
[TsContract]
public sealed record TeamRelationsDto(long Version, List<TeamRelationEntryDto> Entries, string DefaultRelation);

/// <summary>The team settings (<c>PATCH /api/v1/teams/policy</c>). Enum values are the schema names (<c>JoinMode</c>: Auto, Lobby, AdminAssign; ...).</summary>
[TsContract]
public sealed record TeamPolicyDto(
    string JoinMode, string AutoAssign, bool AllowCreateInLobby, int LobbyTimeoutSeconds, int MaxTeams, int MaxFactionSlots, string DefaultRelation,
    string AssetPolicy, bool AllowFriendlyFire, bool AllowAssetTransfer, string MoveAssetsWithPlayer, bool AllowSelfTeamChange, string RelationChangePolicy);

/// <summary>Response of <c>GET /api/v1/teams/{id}</c>: the team and its members.</summary>
[TsContract]
public sealed record TeamDetailDto(TeamDto Team, List<TeamMemberDto> Members);

/// <summary>Everything the Teams page shows: the table, the roster, the waiting players, the matrix and the settings.</summary>
[TsContract]
public sealed record TeamsStateDto(
    List<TeamDto> Teams, List<TeamMemberDto> Members, List<TeamMemberDto> Unassigned, TeamRelationsDto Relations, TeamPolicyDto Policy, string SessionPhase);

/// <summary>Body of <c>POST /api/v1/teams</c>. <c>Name</c> is required; the colour and slot default to the first free slot's.</summary>
[TsContract]
public sealed record CreateTeamRequest(string? Name, string? Color, int? FactionSlot, int? MaxMembers, bool? Locked, string? Password);

/// <summary>Body of <c>PATCH /api/v1/teams/{id}</c>. <c>MaxMembers</c> of 0 clears the limit; <c>Password</c> of "" clears the lobby password.</summary>
[TsContract]
public sealed record PatchTeamRequest(
    string? Name, string? Color, int? FactionSlot, long? LeaderPlayerId, bool? Locked, int? MaxMembers, string? Password);

/// <summary>Body of <c>PUT /api/v1/teams/members/{playerId}</c>: <c>TeamId</c> null puts the player back to Unassigned. <c>Role</c> is Member or Leader.</summary>
[TsContract]
public sealed record AssignMemberRequest(long? TeamId, string? Role);

/// <summary>One line of <see cref="BulkAssignRequest"/>.</summary>
[TsContract]
public sealed record BulkAssignmentDto(long PlayerId, long? TeamId);

/// <summary>Body of <c>PUT /api/v1/teams/members</c>: several moves, applied in order (a multi-select drag).</summary>
[TsContract]
public sealed record BulkAssignRequest(List<BulkAssignmentDto>? Assignments);

/// <summary>Body of <c>PUT /api/v1/teams/relations</c>: the pairs to set (a pair left out keeps its relation).</summary>
[TsContract]
public sealed record SetRelationsRequest(List<TeamRelationEntryDto>? Entries);

/// <summary>Body of <c>PUT /api/v1/teams/relations/{teamA}/{teamB}</c>.</summary>
[TsContract]
public sealed record SetRelationRequest(string? Relation);

/// <summary>Body of <c>POST /api/v1/teams/preset</c>. <c>Preset</c> is CoOp, AlliedSeparate, FreeForAll or TwoTeams; <c>Confirm</c> is required while the session is Running.</summary>
[TsContract]
public sealed record ApplyPresetRequest(string? Preset, bool? Confirm);

/// <summary>
/// What a preset would do (<c>GET /api/v1/teams/preset/{preset}/preview</c>, and the body of the 409 <c>ConfirmationRequired</c>). <c>Blocked</c> is
/// set when it would be refused (it is the reason code, e.g. SessionRunningRestricted).
/// </summary>
[TsContract]
public sealed record TeamPresetPreviewDto(
    string Preset, bool Running, bool RequiresConfirm, string? Blocked, string? BlockedDetail, List<TeamDto> Teams, List<TeamMemberDto> Members,
    int PlayersMoved, int TeamsRemoved, string AutoAssign, string? Relation);

/// <summary>The 409 body of <c>POST /api/v1/teams/preset</c> while Running without <c>confirm</c>: a problem plus the preview.</summary>
[TsContract]
public sealed record PresetConfirmProblem(
    string Type, string Title, int Status, string Code, string? Detail, TeamPresetPreviewDto Preview);

/// <summary>Body of <c>PATCH /api/v1/teams/policy</c>: only the fields sent change. Enum values as in <see cref="TeamPolicyDto"/>.</summary>
[TsContract]
public sealed record PatchTeamPolicyRequest(
    string? JoinMode, string? AutoAssign, bool? AllowCreateInLobby, int? LobbyTimeoutSeconds, int? MaxTeams, string? DefaultRelation, string? AssetPolicy,
    bool? AllowFriendlyFire, bool? AllowAssetTransfer, string? MoveAssetsWithPlayer, bool? AllowSelfTeamChange, string? RelationChangePolicy);
