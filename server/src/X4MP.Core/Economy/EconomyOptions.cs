using X4MP.Core.Settings;
using X4MP.Proto;

namespace X4MP.Core.Economy;

/// <summary>Where the money stored in the save goes (ADR-033, ADR-039).</summary>
public enum SaveMoneyDistribution
{
    /// <summary>All of it to the inheriting team (its shared wallet, or its pool in PerPlayer mode).</summary>
    InheritTeam,

    /// <summary>Split evenly between the known players; the remainder goes to the inheriting team.</summary>
    SplitAmongPlayers,
}

/// <summary>Who may spend a team's shared wallet toward another team (server-design 2.14 validation rule 5).</summary>
public enum SharedWalletSpendPolicy
{
    AnyMember,
    LeaderOnly,
}

/// <summary>Economy settings (server-design 2.14 <c>EconomyOptions</c>). Scopes, loan and trade limits arrive with M1-E3..E5.</summary>
[SettingsSection(SectionName, "Economy")]
public sealed record EconomyOptions
{
    public const string SectionName = "X4MP:Economy";

    /// <summary>Auto = Shared if the session has exactly one team, else PerPlayer.</summary>
    [Setting("Credit mode (Auto: one shared wallet when the session has a single team)", Scope = SettingScope.Live, PushToNodes = true)]
    public CreditMode CreditMode { get; set; } = CreditMode.Auto;

    /// <summary>Credited once to each new player's effective wallet (ADR-039).</summary>
    [Setting("Starting credits for each new player", Scope = SettingScope.Live, Min = 0, Max = 1_000_000_000_000)]
    public long StartingCredits { get; set; } = 100_000;

    [Setting("Team pool enabled", Scope = SettingScope.Live, PushToNodes = true)]
    public bool TeamPoolEnabled { get; set; } = true;

    [Setting("Pool withdraw policy", Scope = SettingScope.Live, PushToNodes = true)]
    public PoolWithdrawPolicy PoolWithdrawPolicy { get; set; } = PoolWithdrawPolicy.AnyMember;

    /// <summary>Credits one player may take from the pool in any rolling 24 hours (0 = no limit).</summary>
    [Setting("Pool withdraw daily limit per player (0 = unlimited)", Scope = SettingScope.Live, PushToNodes = true, Min = 0, Max = 1_000_000_000_000)]
    public long PoolWithdrawDailyLimitPerPlayer { get; set; }

    /// <summary>Who may give from a team's shared wallet to another team (Shared mode).</summary>
    [Setting("Who may spend the shared wallet toward other teams", Scope = SettingScope.Live)]
    public SharedWalletSpendPolicy SharedWalletSpend { get; set; } = SharedWalletSpendPolicy.AnyMember;

    /// <summary>Who may receive a donation (Off, Teammates, Allied = same or allied team, Anyone).</summary>
    [Setting("Donation scope", Scope = SettingScope.Live, PushToNodes = true)]
    public EconomyScope DonateScope { get; set; } = EconomyScope.Teammates;

    /// <summary>Teammate transfers also work toward players of allied teams.</summary>
    [Setting("Teammate transfers also reach allied teams", Scope = SettingScope.Live, PushToNodes = true)]
    public bool AllowAlliedTransfers { get; set; }

    /// <summary>Who may be lent to (Off, Teammates, Allied = same or allied team, Anyone).</summary>
    [Setting("Loan scope", Scope = SettingScope.Live, PushToNodes = true)]
    public EconomyScope LoanScope { get; set; } = EconomyScope.Teammates;

    /// <summary>Open loans (offered, active or overdue) a player may be part of, as lender and borrower together.</summary>
    [Setting("Open loans per player (lender and borrower)", Scope = SettingScope.Live, PushToNodes = true, Min = 1, Max = 255)]
    public int MaxOpenLoansPerPlayer { get; set; } = 5;

    [Setting("Largest loan principal", Scope = SettingScope.Live, Min = 1, Max = 1_000_000_000_000)]
    public long MaxLoanPrincipal { get; set; } = 1_000_000_000_000;

    /// <summary>Largest flat interest on the principal, in basis points (5000 = 50%).</summary>
    [Setting("Largest loan interest (basis points of the principal)", Scope = SettingScope.Live, Min = 0, Max = 100_000)]
    public int MaxLoanInterestBp { get; set; } = 5000;

    /// <summary>Used when an offer carries no time to live.</summary>
    [Setting("Default offer lifetime (minutes)", Scope = SettingScope.Live, Min = 1, Max = 10_080)]
    public int OfferDefaultTtlMinutes { get; set; } = 30;

    [Setting("Largest single transfer or pool movement", Scope = SettingScope.Live, PushToNodes = true, Min = 1, Max = 1_000_000_000_000)]
    public long MaxSingleTransfer { get; set; } = 1_000_000_000_000;

    /// <summary>The team that inherits the save's money; 0 = the authority player's team (ADR-033).</summary>
    [Setting("Team that inherits the save's money (0 = the authority's team)", Min = 0, Max = 64)]
    public int InheritTeam { get; set; }

    [Setting("Save money goes to the inheriting team or is split among players", Scope = SettingScope.Live)]
    public SaveMoneyDistribution SaveMoneyDistribution { get; set; } = SaveMoneyDistribution.InheritTeam;

    [Setting("Ledger audit interval (s)", Scope = SettingScope.Live, Min = 1, Max = 3600)]
    public int AuditIntervalSeconds { get; set; } = 60;
}
