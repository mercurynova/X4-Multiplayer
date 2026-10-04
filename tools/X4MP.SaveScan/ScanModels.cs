namespace X4MP.SaveScan;

/// <summary>Where an expected avatar must be owned in the save (checkpoint: a team faction; client quicksave: the player).</summary>
public enum AvatarOwner
{
    Any,
    Team,
    Player,
}

/// <summary>What the caller knows should be in the save. Everything X4MP-looking that is not covered is a leftover.</summary>
public sealed class ScanExpectations
{
    /// <summary>idcodes of the avatars that belong in the save (the checkpoint manifest's PlayerShip entries).</summary>
    public HashSet<string> AvatarIdcodes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Owner an expected avatar must have (the plan: avatars sit under x4mp_team_k in an authority checkpoint).</summary>
    public AvatarOwner AvatarOwner { get; set; } = AvatarOwner.Any;

    /// <summary>Team-owned ships without the "[MP] " prefix are not leftovers (later milestones give teams real assets).</summary>
    public bool AllowTeamOwned { get; set; }

    /// <summary>A listed avatar idcode that is not in the save is a problem (default true when idcodes were given).</summary>
    public bool RequireAvatars { get; set; } = true;
}

public static class ScanKinds
{
    public const string MpNamed = "mp_named";
    public const string TeamOwned = "team_owned";
    public const string Reference = "reference";
}

/// <summary>One object of the save that carries an X4MP trace.</summary>
public sealed record ScanObject
{
    public string Id { get; init; } = "";
    public string Class { get; init; } = "";
    public string Name { get; init; } = "";
    public string Macro { get; init; } = "";
    public string Idcode { get; init; } = "";
    public string Owner { get; init; } = "";
    public string Sector { get; init; } = "";

    /// <summary>Why it is listed: any of <see cref="ScanKinds"/>.</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];

    /// <summary>True when the expectations explain it (an expected avatar).</summary>
    public bool Expected { get; init; }
}

public sealed record FactionTrace(string Id, string? Active);

public sealed record CueTrace(string Name, IReadOnlyList<string> Variables);

public sealed record GameInfo(string Version, string Build, string SaveName);

public sealed record ScanSummary
{
    public int ObjectsScanned { get; init; }
    public int MpNamed { get; init; }
    public int TeamOwned { get; init; }
    public int ReferenceLeftovers { get; init; }
    public int ExpectedAvatars { get; init; }
    public int Unexpected { get; init; }
    public int Problems { get; init; }
    public bool Ok { get; init; }
}

public sealed record ScanReport
{
    public string File { get; init; } = "";
    public bool Gzip { get; init; }
    public GameInfo Game { get; init; } = new("", "", "");
    public IReadOnlyList<ScanObject> Objects { get; init; } = [];
    public IReadOnlyList<FactionTrace> TeamFactions { get; init; } = [];

    /// <summary>Factions of the reference mod (x4mp_host, x4mp_client_*): always leftovers.</summary>
    public IReadOnlyList<FactionTrace> ReferenceFactions { get; init; } = [];
    public IReadOnlyList<CueTrace> Cues { get; init; } = [];
    public IReadOnlyList<string> ExpectedAvatars { get; init; } = [];
    public IReadOnlyList<string> Problems { get; init; } = [];
    public ScanSummary Summary { get; init; } = new();
}
