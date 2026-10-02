using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using X4MP.Core.Events;
using X4MP.Proto;

namespace X4MP.Core.Economy;

/// <summary>One item of a trade side (<c>TradeItem</c> on the wire): credits, wares out of a container, or a whole ship.</summary>
/// <param name="Kind">Credits, Ware, Ship or Station (Station is rejected in M1).</param>
/// <param name="Amount">Credits, or the ware amount.</param>
/// <param name="WareRef">String-table index of the ware (Ware only).</param>
/// <param name="Asset">Ship net id, or the source container of the ware.</param>
public sealed record TradeItemModel(TradeItemKind Kind, long Amount, uint WareRef, uint Asset)
{
    /// <summary>True for items that move through the authority (everything except credits).</summary>
    [JsonIgnore]
    public bool IsAsset => Kind != TradeItemKind.Credits;

    public static TradeItemModel Credits(long amount) => new(TradeItemKind.Credits, amount, 0, 0);

    public static TradeItemModel Ship(uint asset) => new(TradeItemKind.Ship, 0, 0, asset);

    public static TradeItemModel Ware(uint source, uint wareRef, long amount) => new(TradeItemKind.Ware, amount, wareRef, source);

    public static TradeItemModel FromWire(TradeItemT item) => new(item.Kind, item.Amount, item.WareRef, item.Asset);

    public TradeItemT ToWire() => new() { Kind = Kind, Amount = Amount, WareRef = WareRef, Asset = Asset };

    internal string Canonical() => string.Create(CultureInfo.InvariantCulture, $"{(int)Kind}:{Amount}:{WareRef}:{Asset}");
}

/// <summary>A request key a trade has already processed, kept so a replay returns the same answer.</summary>
public sealed record TradeRequestMark(int Player, string Key, string Hash);

/// <summary>
/// One escrowed trade (server-design 2.14, protocol.md 15.6). Mutated only by <see cref="EconomyService"/> on the actor
/// thread; persisted as one JSON document through <see cref="ITradeStore"/>.
/// </summary>
public sealed class TradeRecord
{
    public long Id { get; set; }

    public int Initiator { get; set; }

    public int Counterparty { get; set; }

    /// <summary>Starts at 1 and goes up with every counter. An accept names exactly one version.</summary>
    public uint Version { get; set; } = 1;

    public TradeState State { get; set; } = TradeState.Proposed;

    public List<TradeItemModel> InitiatorGives { get; set; } = [];

    public List<TradeItemModel> CounterpartyGives { get; set; } = [];

    /// <summary>The version each side accepted (0 = nothing). The initiator accepts its own proposal, a counter's sender its own counter.</summary>
    public uint InitiatorAccepted { get; set; }

    public uint CounterpartyAccepted { get; set; }

    /// <summary>Where a side wants incoming wares (<c>TradeAccept.receive_into_asset</c>; 0 = its current ship).</summary>
    public uint InitiatorReceiveInto { get; set; }

    public uint CounterpartyReceiveInto { get; set; }

    public string? Memo { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the <c>AssetTransferOrder</c> went out (null before).</summary>
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>The next time the timeline looks at a transferring trade (first the execute timeout, then one query interval each).</summary>
    public DateTimeOffset? NextCheckAt { get; set; }

    /// <summary><c>TradeQuery</c>s sent so far for the current wait.</summary>
    public int QueryAttempts { get; set; }

    /// <summary>The player whose credits are escrowed (0 = no credits moved yet).</summary>
    public int Payer { get; set; }

    /// <summary>Credits held in the escrow wallet while the asset transfer is open.</summary>
    public long EscrowAmount { get; set; }

    /// <summary>Why the trade ended badly (None for Completed and for open trades).</summary>
    public EconomyReject Reason { get; set; }

    public string? Detail { get; set; }

    public string? ResolvedBy { get; set; }

    public List<TradeRequestMark> Requests { get; set; } = [];

    public uint AcceptedBy(int player) => player == Initiator ? InitiatorAccepted : player == Counterparty ? CounterpartyAccepted : 0;

    public List<TradeItemModel> GivesOf(int player) => player == Initiator ? InitiatorGives : CounterpartyGives;

    public int Other(int player) => player == Initiator ? Counterparty : Initiator;

    /// <summary>Not final: it holds locks and counts against the per-player limit.</summary>
    [JsonIgnore]
    public bool IsOpen => State is TradeState.Proposed or TradeState.Countered or TradeState.Accepted or TradeState.Escrowed
        or TradeState.Transferring or TradeState.InDoubt;

    /// <summary>Open and nothing was moved yet: cancel, expiry and counters are still possible.</summary>
    [JsonIgnore]
    public bool IsNegotiating => State is TradeState.Proposed or TradeState.Countered;

    /// <summary>Net ids this trade locks while it is open (ship items and the source containers of ware items).</summary>
    [JsonIgnore]
    public IReadOnlyList<uint> LockedAssets =>
        [.. InitiatorGives.Concat(CounterpartyGives).Where(i => i.IsAsset && i.Asset != 0).Select(i => i.Asset).Distinct()];

    /// <summary>
    /// Escrow wallet owner ids of trades start here, so they never meet the loan escrows (which start at 2^40): a trade's escrow wallet is
    /// <c>Escrow(EscrowBase + id)</c>.
    /// </summary>
    public const long EscrowBase = 1L << 41;

    public static WalletId EscrowOf(long tradeId) => WalletId.Escrow(EscrowBase + tradeId);

    /// <summary>The wallet that holds the escrow.</summary>
    [JsonIgnore]
    public WalletId EscrowWallet => EscrowOf(Id);

    internal static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static TradeRecord FromJson(string json) =>
        JsonSerializer.Deserialize<TradeRecord>(json, Json) ?? throw new JsonException("empty trade document");

    public TradeRecord Clone() => FromJson(ToJson());
}

/// <summary>Outcome of a trade request: a wire reason (None = success) and the trade it concerns.</summary>
public sealed record TradeActionResult(EconomyReject Reason, string? Detail, TradeRecord? Trade, PostOutcome? Outcome = null)
{
    public bool Ok => Reason == EconomyReject.None;

    public static TradeActionResult Rejected(EconomyReject reason, string? detail = null, TradeRecord? trade = null) => new(reason, detail, trade);
}

/// <summary>What <c>AssetTransferConfirm</c> did to the trade.</summary>
public enum TradeConfirmOutcome
{
    /// <summary>Credits released, ownership updated.</summary>
    Settled,

    /// <summary>Escrow refunded and assets unlocked.</summary>
    RolledBack,

    /// <summary>The authority applied part of the order and did not undo it; an admin has to look.</summary>
    InDoubt,

    /// <summary>The trade is not waiting for a confirmation (a duplicate or late one): nothing happened.</summary>
    Ignored,

    UnknownTrade,
}

/// <summary>A trade changed state (audit trail and the GUI's trade feed).</summary>
public sealed record TradeStateChanged(
    DateTimeOffset At, long? Session, long TradeId, TradeState From, TradeState To, EconomyReject Reason, string Actor, string? Detail)
    : DomainEvent(At, Session);

/// <summary>An asset a trade names, as the world mirror shows it.</summary>
public readonly record struct TradeAssetInfo(uint NetId, EntityKind Kind, int OwnerTeam, int OwnerPlayer, ushort Sector, bool IsPlayerShip)
{
    public bool IsShip => Kind is EntityKind.ShipXS or EntityKind.ShipS or EntityKind.ShipM or EntityKind.ShipL or EntityKind.ShipXL;
}

/// <summary>What the trade engine needs from the world mirror and the asset permission rules (implemented by <see cref="MirrorTradeWorld"/>).</summary>
public interface ITradeWorld
{
    bool TryGetAsset(uint netId, out TradeAssetInfo asset);

    /// <summary>The ship a player flies and its sector (false while unknown).</summary>
    bool TryGetPlayerShip(int playerId, out uint netId, out ushort sector);

    /// <summary>Amount of a ware in a container, or null when no cargo snapshot is known (the authority prechecks then).</summary>
    long? CargoAmount(uint container, uint wareRef);

    /// <summary>Null when <paramref name="player"/> may give the asset away (own or team asset under the asset policy; ships across teams also need <c>allow_asset_transfer</c>), else why not.</summary>
    string? DenyGive(int player, in TradeAssetInfo asset, bool crossTeam, bool shipTransfer);

    /// <summary>Null when <paramref name="player"/> may receive wares into this container (it is the player's or the team's), else why not.</summary>
    string? DenyReceive(int player, in TradeAssetInfo asset);

    /// <summary>Sets the owner team and player of an entity after a settled ship trade (also emitted by the authority as <c>EntityChange</c>).</summary>
    void SetOwner(uint netId, int team, int player, Id128T cause);
}

/// <summary>
/// Persistence seam of the trades (<c>trades</c> and <c>trade_locks</c>). Like <see cref="IEconomyStore"/> it is synchronous and
/// called on the actor thread. <see cref="Save"/> writes the trade and replaces its asset locks in one transaction; the unique key on
/// the lock table is what makes "an asset is in at most one open trade" hold even if the engine had a bug.
/// </summary>
public interface ITradeStore
{
    /// <summary>All open trades of the session plus up to <paramref name="recentTerminal"/> of the newest finished ones.</summary>
    IReadOnlyList<TradeRecord> Load(long sessionId, int recentTerminal);

    /// <summary>The highest trade id ever stored for the session (0 = none).</summary>
    long MaxId(long sessionId);

    /// <summary>Upserts the trade and sets its locks to <paramref name="locks"/> (empty when the trade is final). Throws <see cref="TradeLockConflictException"/> when another open trade holds one.</summary>
    void Save(long sessionId, TradeRecord trade, IReadOnlyCollection<uint> locks);
}

/// <summary>An asset is already locked by another open trade (the unique index fired).</summary>
public sealed class TradeLockConflictException(uint asset, long holder)
    : InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"asset {asset} is locked by trade {holder}"))
{
    public uint Asset { get; } = asset;

    public long Holder { get; } = holder;
}

/// <summary>An <see cref="ITradeStore"/> in memory (running without persistence, and tests). It enforces the lock uniqueness like the database does.</summary>
public sealed class InMemoryTradeStore : ITradeStore
{
    private readonly Dictionary<(long Session, long Id), string> _trades = [];
    private readonly Dictionary<(long Session, uint Asset), long> _locks = [];

    /// <summary>When true, <see cref="Save"/> throws (a failing disk).</summary>
    public bool FailSaves { get; set; }

    public IReadOnlyDictionary<(long Session, uint Asset), long> Locks => _locks;

    public IReadOnlyList<TradeRecord> Load(long sessionId, int recentTerminal)
    {
        var all = _trades.Where(kv => kv.Key.Session == sessionId).Select(kv => TradeRecord.FromJson(kv.Value)).OrderBy(t => t.Id).ToList();
        var terminal = all.Where(t => !t.IsOpen).OrderByDescending(t => t.Id).Take(recentTerminal).ToHashSet();
        return [.. all.Where(t => t.IsOpen || terminal.Contains(t))];
    }

    public long MaxId(long sessionId) => _trades.Keys.Where(k => k.Session == sessionId).Select(k => k.Id).DefaultIfEmpty(0).Max();

    public void Save(long sessionId, TradeRecord trade, IReadOnlyCollection<uint> locks)
    {
        ArgumentNullException.ThrowIfNull(trade);
        ArgumentNullException.ThrowIfNull(locks);
        if (FailSaves)
        {
            throw new IOException("simulated trade store failure");
        }

        foreach (var asset in locks)
        {
            if (_locks.TryGetValue((sessionId, asset), out var holder) && holder != trade.Id)
            {
                throw new TradeLockConflictException(asset, holder);
            }
        }

        foreach (var key in _locks.Where(kv => kv.Key.Session == sessionId && kv.Value == trade.Id).Select(kv => kv.Key).ToList())
        {
            _locks.Remove(key);
        }

        foreach (var asset in locks)
        {
            _locks[(sessionId, asset)] = trade.Id;
        }

        _trades[(sessionId, trade.Id)] = trade.ToJson();
    }
}
