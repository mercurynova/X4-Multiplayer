using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Economy;
using X4MP.Core.Session;
using X4MP.Core.Teams;
using X4MP.Core.Tests.Session;

namespace X4MP.Core.Tests.Economy;

/// <summary>Test double for the team module's directory (the real one is implemented by M1-T1).</summary>
public sealed class FakeTeamDirectory : ITeamDirectory
{
    private readonly Dictionary<int, int?> _teamOf = [];
    private readonly List<int> _teams = [];

    public IReadOnlyList<TeamInfo> Teams => [.. _teams.Order().Select(t => new TeamInfo(t, "Team " + t, t))];

    public int Version { get; private set; }

    public event Action<TeamDirectoryChanged>? Changed;

    public int? TeamOf(int playerId) => _teamOf.GetValueOrDefault(playerId);

    public IReadOnlyList<int> MembersOf(int teamId) => [.. _teamOf.Where(kv => kv.Value == teamId).Select(kv => kv.Key).Order()];

    private readonly Dictionary<(int, int), TeamRelation> _relations = [];

    public TeamRelation RelationBetween(int teamA, int teamB) =>
        teamA == teamB ? TeamRelation.Allied : _relations.GetValueOrDefault((Math.Min(teamA, teamB), Math.Max(teamA, teamB)), TeamRelation.Neutral);

    /// <summary>Sets the relation of two teams (symmetric) and raises <see cref="Changed"/>.</summary>
    public void SetRelation(int teamA, int teamB, TeamRelation relation)
    {
        _relations[(Math.Min(teamA, teamB), Math.Max(teamA, teamB))] = relation;
        Changed?.Invoke(new TeamDirectoryChanged(++Version));
    }

    /// <summary>Replaces the whole layout and raises <see cref="Changed"/>.</summary>
    public void Set(IEnumerable<int> teams, IReadOnlyDictionary<int, int?> members)
    {
        _teams.Clear();
        _teams.AddRange(teams);
        _teamOf.Clear();
        foreach (var (player, team) in members)
        {
            _teamOf[player] = team;
        }

        Changed?.Invoke(new TeamDirectoryChanged(++Version));
    }
}

/// <summary>An economy wired to the in-memory store, a fake clock and a fake directory.</summary>
public sealed class EconomyKit
{
    public const long Session = 7;

    public EconomyKit(
        EconomyOptions? options = null,
        InMemoryEconomyStore? store = null,
        FakeTeamDirectory? teams = null,
        bool start = true)
    {
        Options = options ?? new EconomyOptions();
        Store = store ?? new InMemoryEconomyStore();
        Teams = teams ?? new FakeTeamDirectory();
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        Events = new RecordingEvents();
        Ledger = new EconomyLedger(Session, Store, Time, Events);
        Service = new EconomyService(Ledger, Store, Teams, () => Options, Time, Events, () => Phase)
        {
            Changed = Changes.Add,
        };
        Auditor = new EconomyAuditor(Ledger, Store, Time, () => TimeSpan.FromSeconds(Options.AuditIntervalSeconds));
        Teams.Changed += _ => Service.Reconcile(confirm: false, actor: "system");
        if (start)
        {
            Service.Start();
        }
    }

    public EconomyOptions Options { get; }

    public InMemoryEconomyStore Store { get; }

    public FakeTeamDirectory Teams { get; }

    public FakeTimeProvider Time { get; }

    public RecordingEvents Events { get; }

    public EconomyLedger Ledger { get; }

    public EconomyService Service { get; }

    public EconomyAuditor Auditor { get; }

    public X4MP.Proto.SessionPhase Phase { get; set; } = X4MP.Proto.SessionPhase.WaitingForAuthority;

    public List<WalletChange> Changes { get; } = [];

    public long Balance(WalletId id) => Ledger.BalanceOf(id);

    /// <summary>Gives a player an exact balance in their effective wallet (through a game-income delta).</summary>
    public void Fund(int player, long amount, bool authority = false)
    {
        var delta = new X4MP.Proto.CreditDeltaT { Amount = amount, Seq = 0 };
        Assert.True(Service.BookCreditDelta(player, authority, delta).Booked);
    }

}
