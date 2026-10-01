using X4MP.Core.Settings;
using X4MP.Proto;

namespace X4MP.Core.Teams;

/// <summary>
/// Team settings (server-design 2.13, defaults from ADR-017). All of them are Live: the team module reads them through
/// a provider on every use. The ones that are part of the wire <c>TeamPolicy</c> are flagged <c>PushToNodes</c>.
/// </summary>
[SettingsSection(SectionName, "Teams")]
public sealed record TeamOptions
{
    public const string SectionName = "X4MP:Teams";

    /// <summary>The number of X4 faction slots the mod ships (<c>x4mp_team_1</c> .. <c>x4mp_team_8</c>, ADR-014).</summary>
    public const int MaxFactionSlots = 8;

    [Setting("How a joining player gets a team: Auto, Lobby (the player picks) or AdminAssign (waits for an admin)",
        Scope = SettingScope.Live, PushToNodes = true)]
    public TeamJoinMode JoinMode { get; set; } = TeamJoinMode.Auto;

    [Setting("Auto-assign strategy: SingleTeam, Balance or NewTeamPerPlayer", Scope = SettingScope.Live, PushToNodes = true)]
    public AutoAssignStrategy AutoAssign { get; set; } = AutoAssignStrategy.SingleTeam;

    [Setting("Lobby: let a joining player create a team", Scope = SettingScope.Live, PushToNodes = true)]
    public bool AllowCreateInLobby { get; set; }

    [Setting("Lobby: fall back to Auto after this many seconds without a choice", Scope = SettingScope.Live, Min = 1, Max = 86400)]
    public int LobbyTimeoutSeconds { get; set; } = 300;

    [Setting("Most teams the session may have (at most the 8 faction slots)", Scope = SettingScope.Live, PushToNodes = true, Min = 1, Max = MaxFactionSlots)]
    public int MaxTeams { get; set; } = MaxFactionSlots;

    [Setting("Relation between two teams nobody set explicitly", Scope = SettingScope.Live, PushToNodes = true)]
    public TeamRelation DefaultRelation { get; set; } = TeamRelation.Neutral;

    [Setting("Who may command a team's assets: SharedCommand, OwnerOnly or OwnerAndLeader", Scope = SettingScope.Live, PushToNodes = true)]
    public TeamAssetPolicy AssetPolicy { get; set; } = TeamAssetPolicy.SharedCommand;

    [Setting("Allow attacking assets of Allied or Neutral teams", Scope = SettingScope.Live, PushToNodes = true)]
    public bool AllowFriendlyFire { get; set; }

    [Setting("Allow asset gifts and trades across teams", Scope = SettingScope.Live, PushToNodes = true)]
    public bool AllowAssetTransfer { get; set; }

    [Setting("What moves with a player who changes team: ShipOnly, AllOwned or None", Scope = SettingScope.Live, PushToNodes = true)]
    public MoveAssetsScope MoveAssetsWithPlayer { get; set; } = MoveAssetsScope.ShipOnly;

    [Setting("Let players change team themselves during the session (default: admin-only moves)", Scope = SettingScope.Live, PushToNodes = true)]
    public bool AllowSelfTeamChange { get; set; }

    [Setting("Who may change inter-team relations at runtime: AdminOnly, LeadersMutualAlly or LeadersUnilateral", Scope = SettingScope.Live, PushToNodes = true)]
    public RelationChangePolicy RelationChangePolicy { get; set; } = RelationChangePolicy.AdminOnly;
}
