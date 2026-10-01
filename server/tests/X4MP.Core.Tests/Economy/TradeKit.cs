using X4MP.Core.Economy;
using X4MP.Core.Teams;
using X4MP.Proto;
using TeamRelation = X4MP.Core.Teams.TeamRelation;

namespace X4MP.Core.Tests.Economy;

/// <summary>An <see cref="ITradeWorld"/> over plain dictionaries: assets, player ships, cargo and the owner changes it was told about.</summary>
public sealed class FakeTradeWorld(FakeTeamDirectory teams) : ITradeWorld
{
    public Dictionary<uint, TradeAssetInfo> Assets { get; } = [];

    public Dictionary<int, (uint NetId, ushort Sector)> PlayerShips { get; } = [];

    public Dictionary<(uint Container, uint Ware), long> Cargo { get; } = [];

    public List<(uint NetId, int Team, int Player, Id128T Cause)> OwnerChanges { get; } = [];

    public bool AllowAssetTransfer { get; set; }

    public bool TryGetAsset(uint netId, out TradeAssetInfo asset) => Assets.TryGetValue(netId, out asset);

    public bool TryGetPlayerShip(int playerId, out uint netId, out ushort sector)
    {
        if (PlayerShips.TryGetValue(playerId, out var ship))
        {
            (netId, sector) = ship;
            return true;
        }

        netId = 0;
        sector = 0;
        return false;
    }

    public long? CargoAmount(uint container, uint wareRef) => Cargo.TryGetValue((container, wareRef), out var amount) ? amount : null;

    public string? DenyGive(int player, in TradeAssetInfo asset, bool crossTeam, bool shipTransfer)
    {
        if (asset.OwnerTeam != teams.TeamOf(player))
        {
            return "not your asset";
        }

        return shipTransfer && crossTeam && !AllowAssetTransfer ? "asset transfers are disabled" : null;
    }

    public string? DenyReceive(int player, in TradeAssetInfo asset) =>
        asset.OwnerTeam == teams.TeamOf(player) ? null : "not the receiver's container";

    public void SetOwner(uint netId, int team, int player, Id128T cause)
    {
        OwnerChanges.Add((netId, team, player, cause));
        if (Assets.TryGetValue(netId, out var asset))
        {
            Assets[netId] = asset with { OwnerTeam = team, OwnerPlayer = player };
        }
    }
}

/// <summary>An economy with trades: four players, a fake world with a few ships, and a fake authority that records the orders it gets.</summary>
public sealed class TradeKit
{
    // 1 and 2 = team 1; 3 = team 2 (allied to 1); 4 = team 3 (neutral to 1); 5 = team 4 (hostile to 1).
    public const uint ShipOfOne = 100;
    public const uint CommonShip = 101;
    public const uint ShipOfTwo = 102;
    public const uint ForeignShip = 200;
    public const uint ShipOfThree = 300;
    public const uint CargoShipOfOne = 110;
    public const uint ShipOfFour = 400;

    public TradeKit(Action<EconomyOptions>? configure = null)
    {
        var options = new EconomyOptions { StartingCredits = 10_000, TradeScope = EconomyScope.Allied, TradeExecuteTimeoutSeconds = 30, TradeQueryIntervalSeconds = 10 };
        configure?.Invoke(options);
        Kit = new EconomyKit(options);
        Kit.Teams.Set([1, 2, 3, 4], new Dictionary<int, int?> { [1] = 1, [2] = 1, [3] = 2, [4] = 3, [5] = 4 });
        Kit.Teams.SetRelation(1, 2, TeamRelation.Allied);
        Kit.Teams.SetRelation(1, 3, TeamRelation.Neutral);
        Kit.Teams.SetRelation(1, 4, TeamRelation.Hostile);
        foreach (var player in new[] { 1, 2, 3, 4, 5 })
        {
            Kit.Service.EnsurePlayer(player);
        }

        World = new FakeTradeWorld(Kit.Teams);
        Add(ShipOfOne, 1, 1, 5);
        Add(CommonShip, 1, 0, 5);
        Add(ShipOfTwo, 1, 2, 5);
        Add(CargoShipOfOne, 1, 1, 5);
        Add(ForeignShip, 2, 3, 5);
        Add(ShipOfThree, 3, 4, 5);
        Add(ShipOfFour, 4, 5, 5);
        World.PlayerShips[1] = (900, 5);
        World.PlayerShips[2] = (901, 5);
        World.PlayerShips[3] = (902, 5);
        World.PlayerShips[4] = (903, 5);
        World.PlayerShips[5] = (904, 5);
        World.Assets[900] = new TradeAssetInfo(900, EntityKind.ShipM, 1, 1, 5, true);
        World.Assets[901] = new TradeAssetInfo(901, EntityKind.ShipM, 1, 2, 5, true);
        World.Assets[902] = new TradeAssetInfo(902, EntityKind.ShipM, 2, 3, 5, true);
        World.Assets[903] = new TradeAssetInfo(903, EntityKind.ShipM, 3, 4, 5, true);
        World.Assets[904] = new TradeAssetInfo(904, EntityKind.ShipM, 4, 5, 5, true);

        Service.TradeWorld = World;
        Service.TradeStore = Store;
        Service.SendTransferOrder = order =>
        {
            Orders.Add(order);
            return AuthorityUp;
        };
        Service.SendTradeQuery = id =>
        {
            Queries.Add(id);
            return AuthorityUp;
        };
        Service.AuthorityOnline = () => AuthorityUp;
        Service.TradeChanged = (trade, previous) => Changes.Add((trade.Id, previous, trade.State));
        Service.LoadTrades();
        Kit.Auditor.AddCheck(ledger => Service.AuditTrades(ledger));
    }

    public EconomyKit Kit { get; }

    public EconomyService Service => Kit.Service;

    public FakeTradeWorld World { get; }

    public InMemoryTradeStore Store { get; } = new();

    public bool AuthorityUp { get; set; } = true;

    public List<AssetTransferOrderT> Orders { get; } = [];

    public List<long> Queries { get; } = [];

    public List<(long Id, TradeState From, TradeState To)> Changes { get; } = [];

    private int _key;

    public string Key() => "k" + ++_key;

    public static Id128T Wire(long id) => new() { Lo = (ulong)id };

    public long Balance(int player) => Kit.Balance(WalletId.Player(player));

    public long Escrow(long trade) => Kit.Balance(TradeRecord.EscrowOf(trade));

    private void Add(uint id, int team, int player, ushort sector) =>
        World.Assets[id] = new TradeAssetInfo(id, EntityKind.ShipM, team, player, sector, false);

    /// <summary>Player <paramref name="from"/> offers a ship for credits; the credits item is on the other side.</summary>
    public TradeActionResult Sell(int from, int to, uint ship, long price, string? key = null) =>
        Service.ProposeTrade(from, key ?? Key(), to, [TradeItemModel.Ship(ship)], [TradeItemModel.Credits(price)], 300, "t");

    /// <summary>Player <paramref name="buyer"/> offers credits for a ship of <paramref name="seller"/>.</summary>
    public TradeActionResult Buy(int buyer, int seller, uint ship, long price, string? key = null) =>
        Service.ProposeTrade(buyer, key ?? Key(), seller, [TradeItemModel.Credits(price)], [TradeItemModel.Ship(ship)], 300, "t");

    /// <summary>Proposes and lets the counterparty accept: the trade is then Transferring.</summary>
    public TradeRecord Run(int seller, int buyer, uint ship, long price)
    {
        var proposed = Sell(seller, buyer, ship, price);
        Assert.True(proposed.Ok, proposed.Reason + " " + proposed.Detail);
        var accepted = Service.AcceptTrade(buyer, Key(), proposed.Trade!.Id, proposed.Trade.Version, 0);
        Assert.True(accepted.Ok, accepted.Reason + " " + accepted.Detail);
        Assert.Equal(TradeState.Transferring, proposed.Trade.State);
        return proposed.Trade;
    }

    public TradeConfirmOutcome Confirm(long trade, bool ok = true, short failedLine = -1, bool compensated = false, string? error = null) =>
        Service.OnAssetTransferConfirm(new AssetTransferConfirmT { TradeId = Wire(trade), Ok = ok, FailedLine = failedLine, Compensated = compensated, Error = error ?? string.Empty });

    /// <summary>The ledger is sound and nothing is locked or escrowed for a final trade.</summary>
    public void AssertSound()
    {
        Assert.Equal(0, Kit.Ledger.TotalBalance());
        var report = Kit.Auditor.RunNow();
        Assert.True(report.Ok, string.Join("; ", report.Violations));
        foreach (var trade in Service.Trades.Where(t => !t.IsOpen))
        {
            Assert.Equal(0, Escrow(trade.Id));
        }
    }
}
