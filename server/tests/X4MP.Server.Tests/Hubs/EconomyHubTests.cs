using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using X4MP.Core.Economy;
using X4MP.Persistence;
using X4MP.Proto;
using X4MP.Protocol.Client;
using X4MP.Server.Api;
using X4MP.Server.Auth;
using X4MP.Server.Tests.Admin;
using X4MP.Server.Tests.Saves;

namespace X4MP.Server.Tests.Hubs;

/// <summary>The economy topic of the admin hub (M1-E6) and the economy REST paths that need a live session or none.</summary>
public sealed class EconomyHubTests
{
    private const string Base = "/api/v1/economy";

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static readonly string[] EconomyPayloads = ["wallet", "ledger", "loan", "trade", "economy-summary", "economy-event", "economy-alert"];

    private static async Task<(TcpNodeClient Node, long Id)> JoinAsync(HubRig rig, string name)
    {
        var node = await rig.Server.ConnectAsync(name, Role.Client);
        await rig.Server.WaitForAsync(s => s.Nodes.Any(n => n.Name == name && n.Connected), 10_000, "join " + name);
        return (node, node.Welcome.PlayerId);
    }

    private static async Task OpenPolicyAsync(HttpClient admin)
    {
        using var response = await admin.CallAsync(HttpMethod.Patch, Base + "/policy", new
        {
            creditMode = "PerPlayer", donateScope = "Anyone", loanScope = "Anyone", tradeScope = "Anyone", startingCredits = 100_000,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task AdjustAsync(HttpClient admin, long player, long amount, string reason = "hub test")
    {
        using var response = await admin.PostJsonAsync($"{Base}/wallets/Player/{N(player)}/adjust", new { amount, reason });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private sealed class OpenWorld : ITradeWorld
    {
        public bool TryGetAsset(uint netId, out TradeAssetInfo asset)
        {
            asset = new TradeAssetInfo(netId, EntityKind.ShipM, 0, 0, 1, false);
            return true;
        }

        public bool TryGetPlayerShip(int playerId, out uint netId, out ushort sector)
        {
            netId = 9_000_000 + (uint)playerId;
            sector = 1;
            return true;
        }

        public long? CargoAmount(uint container, uint wareRef) => null;

        public string? DenyGive(int player, in TradeAssetInfo asset, bool crossTeam, bool shipTransfer) => null;

        public string? DenyReceive(int player, in TradeAssetInfo asset) => null;

        public void SetOwner(uint netId, int team, int player, Id128T cause)
        {
        }
    }

    // ------------------------------------------------------------------ pushes

    [Fact]
    public async Task ASubscriberReceivesWalletLedgerLoanTradeSummaryEventAndAlertPushes()
    {
        await using var rig = await HubRig.StartAsync("--X4MP:AdminHub:EconomyWalletIntervalMs=50", "--X4MP:AdminHub:EconomySummaryIntervalMs=100");
        using var admin = rig.Server.Http(rig.Server.AdminToken());
        await OpenPolicyAsync(admin);
        await rig.StartAuthorityAsync();
        var (nodeA, a) = await JoinAsync(rig, "EcoA");
        await using var _ = nodeA;
        var (nodeB, b) = await JoinAsync(rig, "EcoB");
        await using var __ = nodeB;
        var module = rig.Server.Service<EconomyModule>();
        await SaveServer.WaitUntilAsync(() => module.Service?.IsKnown((int)b) == true, 10_000, "players known");

        var (viewer, rec) = await rig.ConnectAsync(AdminRoles.Viewer); // a Viewer may subscribe
        var summary = await viewer.InvokeCoreAsync<EconomySummaryDto>(AdminHubMethods.SubscribeEconomy, []);
        Assert.Equal("PerPlayer", summary.EffectiveCreditMode);
        Assert.True(summary.MoneySupply >= 200_000);
        Assert.False(summary.EconomyFrozen);

        // wallet + ledger + event + summary
        await AdjustAsync(admin, a, 1_234, "pushed adjustment");
        var wallet = await rec.WaitAsync<WalletDto>("WalletChanged", w => w.Kind == "Player" && w.OwnerId == a && w.Balance == 101_234);
        Assert.Equal("EcoA", wallet.OwnerName);
        var tx = await rec.WaitAsync<LedgerTxDto>("LedgerPosted", t => t.Kind == "AdminAdjust" && t.Note == "pushed adjustment");
        Assert.Equal(2, tx.Entries.Count);
        Assert.StartsWith("admin:test-", tx.Actor, StringComparison.Ordinal);
        var evt = await rec.WaitAsync<EconomyEventDto>("EconomyEvent", e => e.Type == "economy.adjust");
        Assert.Equal("pushed adjustment", evt.Reason);
        Assert.True(evt.Id < 0); // live events carry a local number
        var pushed = await rec.WaitAsync<EconomySummaryDto>("EconomySummary", s => s.MoneySupply >= 201_234);
        Assert.True(pushed.LastAudit.Ok);

        // a reversal pushes the linked transactions
        using (var reversed = await admin.PostJsonAsync($"{Base}/transactions/{tx.Id}/reverse", new { reason = "push the reversal" }))
        {
            Assert.Equal(HttpStatusCode.OK, reversed.StatusCode);
        }

        var reversal = await rec.WaitAsync<LedgerTxDto>("LedgerPosted", t => t.Kind == "Reversal" && t.Reverses == tx.Id);
        Assert.Equal(100_000, (await rec.WaitAsync<WalletDto>("WalletChanged", w => w.OwnerId == a && w.Balance == 100_000)).Balance);
        Assert.NotNull(reversal);

        // a frozen wallet is pushed with its flag
        using (var frozen = await admin.PostJsonAsync($"{Base}/wallets/Player/{N(a)}/freeze", new { frozen = true, reason = "hub freeze" }))
        {
            Assert.Equal(HttpStatusCode.OK, frozen.StatusCode);
        }

        var frozenWallet = await rec.WaitAsync<WalletDto>("WalletChanged", w => w.OwnerId == a && w.Frozen);
        Assert.Equal("hub freeze", frozenWallet.FrozenReason);
        using (var unfreeze = await admin.PostJsonAsync($"{Base}/wallets/Player/{N(a)}/freeze", new { frozen = false, reason = "done" }))
        {
            Assert.Equal(HttpStatusCode.OK, unfreeze.StatusCode);
        }

        // loans: offered, accepted, forgiven
        var loanId = await rig.Server.Actor.CallAsync(() =>
        {
            var service = module.Service!;
            var offer = service.OfferLoan((int)b, "hub-offer", (int)a, 5_000, 5_500, 3600, 300, 0, "hub");
            Assert.True(offer.Ok, offer.Reason + " " + offer.Detail);
            return offer.Loan!.Id;
        });
        var offered = await rec.WaitAsync<LoanDto>("LoanChanged", l => l.Id == loanId && l.State == "Offered");
        Assert.Equal(b, offered.LenderId);
        Assert.Equal("EcoB", offered.Lender);
        await rig.Server.Actor.CallAsync(() => module.Service!.RespondLoan((int)a, "hub-accept", loanId, true).Ok);
        await rec.WaitAsync<LoanDto>("LoanChanged", l => l.Id == loanId && l.State == "Active" && l.Outstanding == 5_500);
        using (var forgiven = await admin.PostJsonAsync($"{Base}/loans/{N(loanId)}/forgive", new { reason = "hub forgive" }))
        {
            Assert.Equal(HttpStatusCode.OK, forgiven.StatusCode);
        }

        await rec.WaitAsync<LoanDto>("LoanChanged", l => l.Id == loanId && l.State == "Forgiven");
        await rec.WaitAsync<EconomyEventDto>("EconomyEvent", e => e.Type == "LoanStateChanged" && e.RefId == loanId);

        // trades: proposed, in doubt, resolved
        var tradeId = await rig.Server.Actor.CallAsync(() =>
        {
            var service = module.Service!;
            service.TradeWorld = new OpenWorld();
            service.SendTransferOrder = _ => true;
            service.AuthorityOnline = () => true;
            var proposed = service.ProposeTrade((int)a, "hub-trade", (int)b, [TradeItemModel.Ship(7_001)], [TradeItemModel.Credits(2_000)], 300, "hub");
            Assert.True(proposed.Ok, proposed.Reason + " " + proposed.Detail);
            Assert.True(service.AcceptTrade((int)b, "hub-trade-accept", proposed.Trade!.Id, proposed.Trade.Version, 0).Ok);
            service.OnAuthorityGone();
            return proposed.Trade.Id;
        });
        var doubt = await rec.WaitAsync<TradeOfferDto>("TradeChanged", t => t.Id == tradeId && t.State == "InDoubt");
        Assert.Equal(2_000, doubt.EscrowAmount);
        Assert.Equal("EcoA", doubt.Initiator);
        await rec.WaitAsync<AlertDto>("EconomyAlert", al => al.Code == "economy_trade_in_doubt_" + N(tradeId) && al.Active);
        using (var resolved = await admin.PostJsonAsync($"{Base}/trades/{N(tradeId)}/resolve", new { outcome = "refund", reason = "hub resolve" }))
        {
            Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        }

        await rec.WaitAsync<TradeOfferDto>("TradeChanged", t => t.Id == tradeId && t.State == "RolledBack" && t.EscrowAmount == 0);
        await rec.WaitAsync<AlertDto>("EconomyAlert", al => al.Code == "economy_trade_in_doubt_" + N(tradeId) && !al.Active);

        // an auditor breach: the alert, and the summary shows the frozen economy
        using (var db = rig.Server.Service<SqliteConnectionFactory>().Open())
        {
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE wallets SET balance = balance + 1 WHERE kind = 'player' AND owner_id = $o";
            command.Parameters.AddWithValue("$o", a);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        using (var audit = await admin.PostJsonAsync(Base + "/audit", new { }))
        {
            Assert.False((await audit.JsonAsync()).GetProperty("ok").GetBoolean());
        }

        var breach = await rec.WaitAsync<AlertDto>("EconomyAlert", al => al.Code == "economy_frozen" && al.Active);
        Assert.Equal("Critical", breach.Severity);
        var frozenSummary = await rec.WaitAsync<EconomySummaryDto>("EconomySummary", s => s.EconomyFrozen);
        Assert.False(frozenSummary.LastAudit.Ok);
        Assert.NotEmpty(frozenSummary.LastAudit.Violations);
    }

    [Fact]
    public async Task NoEconomyPayloadIsBuiltWithoutSubscribersAndUnsubscribingStopsThePushes()
    {
        await using var rig = await HubRig.StartAsync("--X4MP:AdminHub:EconomyWalletIntervalMs=50", "--X4MP:AdminHub:EconomySummaryIntervalMs=50");
        using var admin = rig.Server.Http(rig.Server.AdminToken());
        await OpenPolicyAsync(admin);
        var (node, id) = await JoinAsync(rig, "Quiet");
        await using var joined = node;
        var module = rig.Server.Service<EconomyModule>();
        await SaveServer.WaitUntilAsync(() => module.Service?.IsKnown((int)id) == true, 10_000, "known");
        var broadcaster = rig.Broadcaster;

        // nobody subscribed: money moves, ticks run, nothing economic is built
        for (var i = 0; i < 5; i++)
        {
            await AdjustAsync(admin, id, 10 + i);
        }

        await Task.Delay(400);
        Assert.Equal(0, rig.Subscriptions.Count(X4MP.Server.Hubs.HubTopic.Economy));
        Assert.All(EconomyPayloads, kind => Assert.False(broadcaster.PayloadsByKind.ContainsKey(kind), $"{kind} was built without a subscriber"));

        // a subscriber: payloads appear
        var (connection, rec) = await rig.ConnectAsync();
        await connection.InvokeCoreAsync<EconomySummaryDto>(AdminHubMethods.SubscribeEconomy, []);
        Assert.Equal(1, rig.Subscriptions.Count(X4MP.Server.Hubs.HubTopic.Economy));
        await AdjustAsync(admin, id, 1_000, "now watched");
        await rec.WaitAsync<LedgerTxDto>("LedgerPosted", t => t.Note == "now watched");
        await rec.WaitAsync<WalletDto>("WalletChanged", w => w.OwnerId == id);
        await rec.WaitAsync<EconomySummaryDto>("EconomySummary");
        Assert.True(broadcaster.PayloadsByKind["ledger"] >= 1);
        Assert.True(broadcaster.PayloadsByKind["economy-summary"] >= 1);

        // unsubscribed again: the counters stop
        await connection.InvokeAsync(AdminHubMethods.UnsubscribeEconomy);
        Assert.Equal(0, rig.Subscriptions.Count(X4MP.Server.Hubs.HubTopic.Economy));
        await Task.Delay(200); // anything already in flight lands
        var counters = EconomyPayloads.ToDictionary(k => k, k => broadcaster.PayloadsByKind.GetValueOrDefault(k));
        var received = rec.Count("LedgerPosted") + rec.Count("WalletChanged") + rec.Count("EconomySummary");
        await AdjustAsync(admin, id, 5, "unwatched again");
        await Task.Delay(400);
        Assert.All(EconomyPayloads, kind => Assert.Equal(counters[kind], broadcaster.PayloadsByKind.GetValueOrDefault(kind)));
        Assert.Equal(received, rec.Count("LedgerPosted") + rec.Count("WalletChanged") + rec.Count("EconomySummary"));
    }

    [Fact]
    public async Task EconomyEventPushesAreRateLimited()
    {
        await using var rig = await HubRig.StartAsync("--X4MP:AdminHub:EconomyEventsPerSecond=2");
        using var admin = rig.Server.Http(rig.Server.AdminToken());
        await OpenPolicyAsync(admin);
        var (node, id) = await JoinAsync(rig, "Flood");
        await using var joined = node;
        var module = rig.Server.Service<EconomyModule>();
        await SaveServer.WaitUntilAsync(() => module.Service?.IsKnown((int)id) == true, 10_000, "known");
        var (connection, rec) = await rig.ConnectAsync();
        await connection.InvokeCoreAsync<EconomySummaryDto>(AdminHubMethods.SubscribeEconomy, []);

        for (var i = 0; i < 12; i++)
        {
            await AdjustAsync(admin, id, 1 + i, "flood " + i);
        }

        await rec.WaitAsync<LedgerTxDto>("LedgerPosted", t => t.Note == "flood 11"); // transactions are never dropped
        Assert.Equal(12, rec.All<LedgerTxDto>("LedgerPosted").Count(t => t.Note?.StartsWith("flood", StringComparison.Ordinal) == true));
        await Task.Delay(300);
        Assert.InRange(rec.Count("EconomyEvent"), 1, 8); // 2 per second: at most a few windows
    }

    // ------------------------------------------------------------------ REST with a live session

    [Fact]
    public async Task SwitchingTheCreditModeOfALiveSessionNeedsAConfirmAfterAPreview()
    {
        await using var rig = await HubRig.StartAsync();
        using var admin = rig.Server.Http(rig.Server.AdminToken());
        await OpenPolicyAsync(admin);
        await rig.StartAuthorityAsync();
        var (nodeA, a) = await JoinAsync(rig, "ModeA");
        await using var _ = nodeA;
        var (nodeB, b) = await JoinAsync(rig, "ModeB");
        await using var __ = nodeB;
        var module = rig.Server.Service<EconomyModule>();
        await SaveServer.WaitUntilAsync(() => module.Service?.IsKnown((int)b) == true, 10_000, "known");
        Assert.True(await rig.Server.Actor.CallAsync(() => module.Service!.IsLive));

        using (var preview = await admin.CallAsync(HttpMethod.Patch, Base + "/policy", new { creditMode = "Shared", reason = "go shared" }))
        {
            string text = await preview.Content.ReadAsStringAsync();
            Assert.True(preview.StatusCode == HttpStatusCode.Conflict, text);
            Assert.Equal("application/problem+json", preview.Content.Headers.ContentType?.MediaType);
            var body = JsonDocument.Parse(text).RootElement;
            Assert.Equal("ConfirmationRequired", body.GetProperty("code").GetString());
            Assert.Equal("urn:x4mp:problem:ConfirmationRequired", body.GetProperty("type").GetString());
            Assert.Equal(409, body.GetProperty("status").GetInt32());
            var migration = body.GetProperty("migrationPreview");
            Assert.True(migration.GetProperty("needed").GetBoolean());
            Assert.True(migration.GetProperty("requiresConfirm").GetBoolean());
            Assert.Equal("PerPlayer", migration.GetProperty("from").GetString());
            Assert.Equal("Shared", migration.GetProperty("to").GetString());
            Assert.Equal("ModeMigration", migration.GetProperty("kind").GetString());
        }

        // the preview changed nothing: not the setting, not the mode, not a balance
        var policy = await (await admin.GetAsync(Base + "/policy")).JsonAsync();
        Assert.Equal("PerPlayer", policy.GetProperty("creditMode").GetString());
        var before = await (await admin.GetAsync($"{Base}/wallets/Player/{N(a)}")).JsonAsync();
        Assert.Equal(100_000, before.GetProperty("wallet").GetProperty("balance").GetInt64());
        Assert.Equal("PerPlayer", (await (await admin.GetAsync(Base + "/summary")).JsonAsync()).GetProperty("appliedCreditMode").GetString());

        using (var confirmed = await admin.CallAsync(HttpMethod.Patch, Base + "/policy", new { creditMode = "Shared", confirm = true, reason = "go shared" }))
        {
            Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
            var changed = await confirmed.JsonAsync();
            Assert.Equal("Shared", changed.GetProperty("creditMode").GetString());
            Assert.Equal("Shared", changed.GetProperty("effectiveCreditMode").GetString());
        }

        var summary = await (await admin.GetAsync(Base + "/summary")).JsonAsync();
        Assert.Equal("Shared", summary.GetProperty("appliedCreditMode").GetString());
        Assert.False(summary.GetProperty("migrationPending").GetBoolean());
        Assert.True((await (await admin.PostJsonAsync(Base + "/audit", new { })).JsonAsync()).GetProperty("ok").GetBoolean());

        // a change that is not a mode change needs no confirm
        using (var plain = await admin.CallAsync(HttpMethod.Patch, Base + "/policy", new { maxOpenLoansPerPlayer = 3 }))
        {
            Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        }
    }

    [Fact]
    public async Task WithoutASessionTheEconomyAnswersNoSessionButSummaryAndPolicyStillWork()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        using var admin = server.Http(server.AdminToken());

        foreach (var url in new[] { "/wallets", "/wallets/Player/1", "/transactions", "/transactions/ABC", "/loans", "/loans/1", "/trades", "/trades/1", "/ledger.csv" })
        {
            using var response = await admin.GetAsync(Base + url);
            await response.AssertProblemAsync(HttpStatusCode.Conflict, "NoSession");
        }

        foreach (var (url, body) in new (string, object)[]
        {
            ("/wallets/Player/1/adjust", new { amount = 5, reason = "x" }), ("/wallets/Player/1/freeze", new { frozen = true, reason = "x" }),
            ("/transactions/ABC/reverse", new { reason = "x" }), ("/loans/1/forgive", new { reason = "x" }), ("/loans/1/cancel", new { reason = "x" }),
            ("/trades/1/cancel", new { reason = "x" }), ("/trades/1/resolve", new { outcome = "refund", reason = "x" }), ("/audit", new { }),
        })
        {
            using var response = await admin.PostJsonAsync(Base + url, body);
            await response.AssertProblemAsync(HttpStatusCode.Conflict, "NoSession");
        }

        using (var summary = await admin.GetAsync(Base + "/summary"))
        {
            Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
            var body = await summary.JsonAsync();
            Assert.Equal(0, body.GetProperty("moneySupply").GetInt64());
            Assert.False(body.GetProperty("economyFrozen").GetBoolean());
        }

        using (var policy = await admin.GetAsync(Base + "/policy"))
        {
            Assert.Equal(HttpStatusCode.OK, policy.StatusCode);
        }

        using (var patched = await admin.CallAsync(HttpMethod.Patch, Base + "/policy", new { creditMode = "Shared" }))
        {
            Assert.Equal(HttpStatusCode.OK, patched.StatusCode); // no session: no migration, no confirm
            Assert.Equal("Shared", (await patched.JsonAsync()).GetProperty("creditMode").GetString());
        }
    }
}
