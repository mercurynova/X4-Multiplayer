using System.Globalization;

namespace X4MP.Core.Economy;

/// <summary>Wallet kinds of the ledger (server-design 2.14). Wire kinds (<c>X4MP.Proto.WalletKind</c>) have no World.</summary>
public enum WalletKind : byte
{
    Player,
    TeamShared,
    TeamPool,
    Escrow,
    World,
}

/// <summary>A wallet: owner id is the player id, team id, escrow reference (loan/trade id) or 0 for <see cref="WalletKind.World"/>.</summary>
public readonly record struct WalletId(WalletKind Kind, long OwnerId)
{
    /// <summary>The counter-party of all game income and spend. The only wallet that may be negative without a flag.</summary>
    public static WalletId World { get; } = new(WalletKind.World, 0);

    public static WalletId Player(int playerId) => new(WalletKind.Player, playerId);

    public static WalletId TeamShared(int teamId) => new(WalletKind.TeamShared, teamId);

    public static WalletId TeamPool(int teamId) => new(WalletKind.TeamPool, teamId);

    public static WalletId Escrow(long reference) => new(WalletKind.Escrow, reference);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Kind}:{OwnerId}");
}

/// <summary>What a ledger transaction is for. Stored by name.</summary>
public enum TxKind : byte
{
    Donate,
    PoolDeposit,
    PoolWithdraw,
    LoanEscrow,
    LoanDisburse,
    LoanRepay,
    LoanAutoRepay,
    LoanRefund,
    TradeEscrow,
    TradeSettle,
    TradeRefund,
    GameIncome,
    GameSpend,
    AdminAdjust,
    Reversal,
    ModeMigration,
    TeamMove,
    StartingCredits,
    SaveMoney,
    Transfer,
}

/// <summary>One signed line of a transaction; <see cref="BalanceAfter"/> is the wallet balance after this line.</summary>
public sealed record LedgerEntry(WalletId Wallet, long Amount, long BalanceAfter);

/// <summary>A committed transaction. Entries always sum to zero.</summary>
public sealed record LedgerTransaction(
    string Id,
    long SessionId,
    DateTimeOffset At,
    TxKind Kind,
    string Actor,
    string? RequestId,
    string? RefType,
    long? RefId,
    string? Reverses,
    string? Note,
    IReadOnlyList<LedgerEntry> Entries);

/// <summary>The cached state of one wallet. Mutated only by <see cref="EconomyLedger"/> on the actor thread.</summary>
public sealed class WalletState
{
    public WalletState(WalletId id) => Id = id;

    /// <summary>Rebuilds a persisted wallet (store implementations).</summary>
    public WalletState(WalletId id, long balance, long version, bool frozen, string? frozenReason)
        : this(id)
    {
        Balance = balance;
        Version = version;
        Frozen = frozen;
        FrozenReason = frozenReason;
    }

    public WalletId Id { get; }

    public long Balance { get; internal set; }

    /// <summary>Increments with every committed change (a ledger sequence for the wallet; the wire <c>WalletBalance.version</c>).</summary>
    public long Version { get; internal set; }

    public bool Frozen { get; internal set; }

    public string? FrozenReason { get; internal set; }

    /// <summary>The ledger balance is negative (debt). In game the balance clamps at 0; the debt lives only here.</summary>
    public bool Overdrawn => Balance < 0 && Id.Kind != WalletKind.World;

    internal WalletState Clone() => new(Id)
    {
        Balance = Balance,
        Version = Version,
        Frozen = Frozen,
        FrozenReason = FrozenReason,
    };
}

/// <summary>The balance of one wallet after an operation.</summary>
public sealed record WalletBalanceAfter(WalletId Wallet, long Balance, long Version);

/// <summary>Credit layout the balances currently follow: the applied mode and each known player's team.</summary>
public sealed record EconomyLayout(string? AppliedMode, IReadOnlyDictionary<int, int?> PlayerTeams);

/// <summary>A stored idempotent request (<c>economy_requests</c>).</summary>
public sealed record EconomyRequestRecord(
    long SessionId, int PlayerId, string RequestId, string Type, byte[] PayloadHash, string ResultJson, DateTimeOffset At);

/// <summary>Everything the store persists atomically for one operation.</summary>
public sealed record EconomyCommit(
    long SessionId,
    LedgerTransaction? Transaction,
    IReadOnlyList<WalletState> Wallets,
    EconomyRequestRecord? Request,
    (int PlayerId, ulong Seq)? DeltaSeq,
    EconomyLayout? Layout,
    DateTimeOffset At,
    IReadOnlyList<LoanRecord>? Loans = null);

/// <summary>What <see cref="IEconomyStore.Load"/> returns.</summary>
public sealed record EconomyLoad(
    IReadOnlyList<WalletState> Wallets,
    IReadOnlyDictionary<int, ulong> DeltaSeqs,
    EconomyLayout Layout,
    IReadOnlyList<LoanRecord>? Loans = null);

/// <summary>The store's own view of the ledger, for the auditor. Computed from the persisted rows, not from the cache.</summary>
public sealed record LedgerAuditData(
    IReadOnlyList<string> UnbalancedTransactions,
    IReadOnlyDictionary<WalletId, long> EntrySums,
    IReadOnlyDictionary<WalletId, long> StoredBalances);
