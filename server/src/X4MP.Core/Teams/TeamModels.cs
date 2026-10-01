using System.Security.Cryptography;
using System.Text;
using X4MP.Proto;

namespace X4MP.Core.Teams;

/// <summary>
/// A team (server-design 2.13): one in-game faction (<c>x4mp_team_{FactionSlot}</c>) shared by its members.
/// Ids are small positive integers, unique within the session and stable across restarts.
/// </summary>
/// <param name="Id">Team id (the wire <c>team_id</c>, a ushort; 0 means none).</param>
/// <param name="Name">1..24 characters, unique per session (case-insensitive).</param>
/// <param name="Color">"#RRGGBB".</param>
/// <param name="FactionSlot">1..8.</param>
/// <param name="LeaderPlayerId">May command any team asset under <c>OwnerAndLeader</c>; null = none.</param>
/// <param name="Locked">Lobby: players cannot choose this team.</param>
/// <param name="MaxMembers">Null = unlimited.</param>
/// <param name="JoinPasswordHash">SHA-256 of the lobby password (the HMAC proof key); null = open.</param>
public sealed record Team(
    int Id,
    string Name,
    string Color,
    int FactionSlot,
    int? LeaderPlayerId,
    bool Locked,
    int? MaxMembers,
    byte[]? JoinPasswordHash,
    DateTimeOffset CreatedAt)
{
    public bool PasswordProtected => JoinPasswordHash is { Length: > 0 };

    public TeamInfo Info => new(Id, Name, FactionSlot);
}

/// <summary>A player's place in a team. Sticky: it survives reconnects, leaving and server restarts.</summary>
/// <param name="AssignedBy"><c>auto</c>, <c>lobby</c>, <c>preset</c> or <c>admin:&lt;user&gt;</c>.</param>
public sealed record TeamMembership(int PlayerId, int TeamId, TeamRole Role, DateTimeOffset Since, string AssignedBy);

/// <summary>The ready-made team layouts of server-design 2.13.</summary>
public enum TeamPreset
{
    /// <summary>Everyone in one team (one faction); <c>AutoAssign=SingleTeam</c>.</summary>
    CoOp,

    /// <summary>One team per player, all Allied; <c>AutoAssign=NewTeamPerPlayer</c>.</summary>
    AlliedSeparate,

    /// <summary>One team per player, all Hostile; <c>AutoAssign=NewTeamPerPlayer</c>.</summary>
    FreeForAll,

    /// <summary>Two Hostile teams; <c>AutoAssign=Balance</c>.</summary>
    TwoTeams,
}

/// <summary>What a preset prescribes besides the teams themselves (the settings it goes with).</summary>
public sealed record TeamPresetPlan(TeamPreset Preset, AutoAssignStrategy AutoAssign, TeamRelation? Relation, int? FixedTeamCount)
{
    public static TeamPresetPlan For(TeamPreset preset) => preset switch
    {
        TeamPreset.CoOp => new(preset, AutoAssignStrategy.SingleTeam, null, 1),
        TeamPreset.AlliedSeparate => new(preset, AutoAssignStrategy.NewTeamPerPlayer, TeamRelation.Allied, null),
        TeamPreset.FreeForAll => new(preset, AutoAssignStrategy.NewTeamPerPlayer, TeamRelation.Hostile, null),
        TeamPreset.TwoTeams => new(preset, AutoAssignStrategy.Balance, TeamRelation.Hostile, 2),
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };
}

/// <summary>A player the preset has to place (id and the name a per-player team is called).</summary>
public readonly record struct TeamPlayer(int PlayerId, string Name);

/// <summary>Everything the store keeps (one snapshot replaces the previous one).</summary>
public sealed record TeamStateSnapshot(
    TeamRelation DefaultRelation,
    IReadOnlyList<Team> Teams,
    IReadOnlyList<TeamMembership> Members,
    IReadOnlyList<(int TeamA, int TeamB, TeamRelation Relation)> Relations)
{
    public static TeamStateSnapshot Empty { get; } = new(TeamRelation.Neutral, [], [], []);
}

/// <summary>A failed team operation: the wire reason plus a short English detail for the admin.</summary>
public readonly record struct TeamResult(TeamRejectReason Reason, string? Detail = null)
{
    public bool Ok => Reason == TeamRejectReason.None;

    public static TeamResult Success { get; } = new(TeamRejectReason.None);
}

public readonly record struct TeamResult<T>(T? Value, TeamRejectReason Reason, string? Detail = null)
{
    public bool Ok => Reason == TeamRejectReason.None;
}

/// <summary>Builders for <see cref="TeamResult{T}"/>.</summary>
public static class TeamResults
{
    public static TeamResult<T> Success<T>(T value) => new(value, TeamRejectReason.None);

    public static TeamResult<T> Fail<T>(TeamRejectReason reason, string? detail = null) => new(default, reason, detail);
}

/// <summary>Colours and name rules shared by the registry and the module.</summary>
public static class TeamRules
{
    public const int MaxNameLength = 24;

    /// <summary>The colour of faction slot <c>i + 1</c> (HUD tint, GUI map).</summary>
    public static readonly IReadOnlyList<string> Palette =
    [
        "#3FA7FF", "#FF5A5A", "#4CD964", "#FFC83D", "#B26BFF", "#2EE6D6", "#FF8A3D", "#E8E8E8",
    ];

    public static string ColorForSlot(int slot) => Palette[Math.Clamp(slot - 1, 0, Palette.Count - 1)];

    public static string? NormalizeName(string? name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        return trimmed.Length is >= 1 and <= MaxNameLength && !trimmed.Any(char.IsControl) ? trimmed : null;
    }

    public static bool TryParseColor(uint rgb, out string color)
    {
        color = $"#{rgb & 0xFFFFFF:X6}";
        return rgb is not 0 and <= 0xFFFFFF;
    }

    public static uint ColorToRgb(string color) =>
        color.Length == 7 && color[0] == '#' && uint.TryParse(color.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint v) ? v : 0;

    /// <summary>What the server stores for a lobby password (the key of the <c>TeamChoice</c> HMAC proof).</summary>
    public static byte[] HashPassword(string password) => SHA256.HashData(Encoding.UTF8.GetBytes(password));
}
