using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using X4MP.Core.Economy;
using X4MP.Persistence;
using X4MP.Proto;
using X4MP.Server.Tests.Saves;

namespace X4MP.Server.Tests.Admin;

/// <summary>
/// The economy admin REST API (M1-E6) over the real server: happy and error paths of every endpoint, reversal links, the audit rows and the ledger
/// invariant. Players are made by connecting fake nodes (the economy registers a player when its node first attaches).
/// </summary>
public sealed class EconomyAdminApiTests(AdminServerFixture f) : IClassFixture<AdminServerFixture>
{
    private const string Base = "/api/v1/economy";

    private HttpClient Admin => f.Admin;

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private EconomyModule Module => f.Server.Service<EconomyModule>();

    private Task<T> OnActor<T>(Func<EconomyService, T> work) =>
        f.Server.Actor.CallAsync(() => work(Module.Service!));

    /// <summary>Per-player wallets, open donate/loan/trade scopes. Called first by every test (idempotent).</summary>
    private async Task OpenPolicyAsync()
    {
        using var response = await Admin.CallAsync(HttpMethod.Patch, Base + "/policy", new
        {
            creditMode = "PerPlayer", donateScope = "Anyone", loanScope = "Anyone", tradeScope = "Anyone", startingCredits = 100_000, maxLoanInterestBp = 5000,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<long> NewPlayerAsync(string prefix = "Econ")
    {
        await OpenPolicyAsync();
        var (node, id) = await f.JoinAsync(f.NextName(prefix));
        await node.DisposeAsync();
        await SaveServer.WaitUntilAsync(() => Module.Service?.IsKnown((int)id) == true, 10_000, "player known to the economy");
        return id;
    }

    /// <summary>Polls the audit log for a row of <paramref name="action"/> whose data mentions <paramref name="text"/> (the reason).</summary>
    private async Task<AuditRecord> WaitForAuditWithAsync(string action, string text)
    {
        AuditRecord? row = null;
        await SaveServer.WaitUntilAsync(
            () => (row = f.Server.Service<SqliteAdminQueries>().AuditEntries(2000).FirstOrDefault(r => r.Action == action && r.DataJson?.Contains(text, StringComparison.Ordinal) == true)) is not null,
            10_000, "audit row " + action + " with " + text);
        return row!;
    }

    private async Task<JsonElement> GetAsync(string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await f.Viewer.GetAsync(url);
        Assert.Equal(expected, response.StatusCode);
        return await response.JsonAsync();
    }

    private async Task<JsonElement> WalletAsync(long player) => await GetAsync($"{Base}/wallets/Player/{N(player)}");

    private async Task<long> BalanceAsync(long player) => (await WalletAsync(player)).GetProperty("wallet").GetProperty("balance").GetInt64();

    private async Task<JsonElement> AdjustAsync(long player, long amount, string reason = "test adjustment", bool force = false)
    {
        using var response = await Admin.PostJsonAsync($"{Base}/wallets/Player/{N(player)}/adjust", new { amount, reason, force });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.JsonAsync();
    }

    // ------------------------------------------------------------------ roles

    public static TheoryData<string, string, bool> Endpoints => new()
    {
        { "GET", Base + "/summary", false },
        { "GET", Base + "/policy", false },
        { "PATCH", Base + "/policy", true },
        { "GET", Base + "/wallets", false },
        { "GET", Base + "/wallets/Player/1", false },
        { "POST", Base + "/wallets/Player/1/adjust", true },
        { "POST", Base + "/wallets/Player/1/freeze", true },
        { "GET", Base + "/transactions", false },
        { "GET", Base + "/transactions/ABC", false },
        { "POST", Base + "/transactions/ABC/reverse", true },
        { "GET", Base + "/ledger.csv", true },
        { "GET", Base + "/loans", false },
        { "GET", Base + "/loans/1", false },
        { "POST", Base + "/loans/1/forgive", true },
        { "POST", Base + "/loans/1/cancel", true },
        { "GET", Base + "/trades", false },
        { "GET", Base + "/trades/1", false },
        { "POST", Base + "/trades/1/cancel", true },
        { "POST", Base + "/trades/1/resolve", true },
        { "GET", Base + "/events", false },
        { "POST", Base + "/audit", true },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task EveryEconomyEndpointAnswers401WithoutCredentialsAnd403ToAViewerWhereAdminOnly(string method, string url, bool adminOnly)
    {
        object? body = method is "POST" or "PATCH" ? new { } : null;
        using (var anonymous = await f.Anon.CallAsync(new HttpMethod(method), url, body))
        {
            await anonymous.AssertProblemAsync(HttpStatusCode.Unauthorized, "Unauthorized");
        }

        using var viewer = await f.Viewer.CallAsync(new HttpMethod(method), url, body);
        if (adminOnly)
        {
            await viewer.AssertProblemAsync(HttpStatusCode.Forbidden, "Forbidden");
        }
        else
        {
            Assert.NotEqual(HttpStatusCode.Forbidden, viewer.StatusCode);
            Assert.NotEqual(HttpStatusCode.Unauthorized, viewer.StatusCode);
        }
    }

    // ------------------------------------------------------------------ wallets

    [Fact]
    public async Task WalletsAreListedFilteredAndShownInDetail()
    {
        string name = f.NextName("Lister");
        await OpenPolicyAsync();
        var (node, id) = await f.JoinAsync(name);
        await node.DisposeAsync();
        await SaveServer.WaitUntilAsync(() => Module.Service?.IsKnown((int)id) == true, 10_000, "known");

        var all = await GetAsync(Base + "/wallets");
        var mine = all.EnumerateArray().Single(w => w.GetProperty("kind").GetString() == "Player" && w.GetProperty("ownerId").GetInt64() == id);
        Assert.Equal(name, mine.GetProperty("ownerName").GetString());
        Assert.Equal(100_000, mine.GetProperty("balance").GetInt64());
        Assert.False(mine.GetProperty("frozen").GetBoolean());
        Assert.False(mine.GetProperty("overdrawn").GetBoolean());
        Assert.Contains(all.EnumerateArray(), w => w.GetProperty("kind").GetString() == "World");

        var byName = await GetAsync($"{Base}/wallets?kind=player&q={name}");
        Assert.Single(byName.EnumerateArray());
        var worlds = await GetAsync(Base + "/wallets?kind=world");
        Assert.Equal("World", worlds.EnumerateArray().Single().GetProperty("ownerName").GetString());

        var detail = await WalletAsync(id);
        Assert.Equal(id, detail.GetProperty("wallet").GetProperty("ownerId").GetInt64());
        Assert.Contains(detail.GetProperty("recent").EnumerateArray(), t => t.GetProperty("kind").GetString() == "StartingCredits");
        Assert.Empty(detail.GetProperty("openLoans").EnumerateArray());
        Assert.Empty(detail.GetProperty("openTrades").EnumerateArray());
    }

    [Fact]
    public async Task WalletReadsRejectBadKindsAndUnknownWallets()
    {
        using (var response = await f.Viewer.GetAsync(Base + "/wallets?kind=purse"))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "kind");
        }

        using (var response = await f.Viewer.GetAsync(Base + "/wallets/purse/1"))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "kind");
        }

        await OpenPolicyAsync();
        using (var response = await f.Viewer.GetAsync(Base + "/wallets/Player/987654"))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await f.Viewer.GetAsync(Base + "/wallets/Player/notanumber"))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // the route needs a number
        }
    }

    // ------------------------------------------------------------------ adjust and freeze

    [Fact]
    public async Task AdjustCreditsAndDebitsAndWritesAnAuditRowWithTheReason()
    {
        long id = await NewPlayerAsync();

        var credit = await AdjustAsync(id, 5_000, "compensation for the lost Teladi");
        Assert.Equal("AdminAdjust", credit.GetProperty("kind").GetString());
        Assert.StartsWith("admin:test-", credit.GetProperty("actor").GetString(), StringComparison.Ordinal);
        Assert.Equal("compensation for the lost Teladi", credit.GetProperty("note").GetString());
        var entries = credit.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(0, entries.Sum(e => e.GetProperty("amount").GetInt64()));
        Assert.Equal(105_000, await BalanceAsync(id));

        await AdjustAsync(id, -2_000, "fine for ramming");
        Assert.Equal(103_000, await BalanceAsync(id));

        var row = await WaitForAuditWithAsync("economy.adjust", "compensation for the lost Teladi");
        Assert.Equal("wallet:Player:" + N(id), row.Target);
        Assert.StartsWith("admin:test-", row.Actor, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", row.DataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdjustErrorsAreProblemsPerKey()
    {
        long id = await NewPlayerAsync();
        string url = $"{Base}/wallets/Player/{N(id)}/adjust";

        using (var response = await Admin.PostJsonAsync(url, new { amount = 5 }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "reason");
        }

        using (var response = await Admin.PostJsonAsync(url, new { amount = 0, reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "amount");
        }

        using (var response = await Admin.PostJsonAsync(url, new { reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "amount");
        }

        using (var response = await Admin.PostJsonAsync($"{Base}/wallets/purse/1/adjust", new { amount = 5, reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "kind");
        }

        using (var response = await Admin.PostJsonAsync(url, new { amount = 5, reason = new string('x', 300) }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "reason");
        }

        using (var response = await Admin.CallAsync(HttpMethod.Post, url, "{ not json"))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "InvalidRequest");
        }

        using (var response = await Admin.PostJsonAsync($"{Base}/wallets/Player/987654/adjust", new { amount = 5, reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await Admin.PostJsonAsync($"{Base}/wallets/World/0/adjust", new { amount = 5, reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.Conflict, "NotAdjustable");
        }

        using (var response = await Admin.PostJsonAsync(url, new { amount = -9_999_999, reason = "too much" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.Conflict, "WouldOverdraw");
        }

        Assert.Equal(100_000, await BalanceAsync(id));
        var forced = await AdjustAsync(id, -150_000, "make the debt visible", force: true);
        Assert.Equal(-50_000, forced.GetProperty("entries").EnumerateArray().First(e => e.GetProperty("walletKind").GetString() == "Player").GetProperty("balanceAfter").GetInt64());
        Assert.True((await WalletAsync(id)).GetProperty("wallet").GetProperty("overdrawn").GetBoolean());
    }

    [Fact]
    public async Task AnIdempotencyKeyMakesAnAdjustHappenOnce()
    {
        long id = await NewPlayerAsync();
        string url = $"{Base}/wallets/Player/{N(id)}/adjust";
        async Task<HttpResponseMessage> Send(long amount, string key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { amount, reason = "bonus" }) };
            request.Headers.Add("Idempotency-Key", key);
            return await Admin.SendAsync(request);
        }

        using var first = await Send(1_000, "key-1");
        using var again = await Send(1_000, "key-1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal((await first.JsonAsync()).GetProperty("id").GetString(), (await again.JsonAsync()).GetProperty("id").GetString());
        Assert.Equal(101_000, await BalanceAsync(id));

        using var other = await Send(2_000, "key-1");
        await other.AssertProblemAsync(HttpStatusCode.Conflict, "RequestIdReuse");
        using var bad = await Send(1, "has a space");
        await bad.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "Idempotency-Key");
        Assert.Equal(101_000, await BalanceAsync(id));
    }

    [Fact]
    public async Task FreezingAWalletIsAuditedAndShownAndErrorsAreProblems()
    {
        long id = await NewPlayerAsync();
        string url = $"{Base}/wallets/Player/{N(id)}/freeze";

        using (var response = await Admin.PostJsonAsync(url, new { frozen = true, reason = "possible dupe" }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var wallet = await response.JsonAsync();
            Assert.True(wallet.GetProperty("frozen").GetBoolean());
            Assert.Equal("possible dupe", wallet.GetProperty("frozenReason").GetString());
        }

        var row = await Api.WaitForAuditAsync(f.Server, "economy.wallet.freeze", "wallet:Player:" + N(id));
        Assert.Contains("possible dupe", row.DataJson, StringComparison.Ordinal);
        Assert.True((await WalletAsync(id)).GetProperty("wallet").GetProperty("frozen").GetBoolean());

        // the frozen wallet rejects the player's own request, an admin adjust still works
        long other = await NewPlayerAsync();
        var rejected = await OnActor(s => s.Donate((int)id, "frozen-donation", (int)other, 10).Reason);
        Assert.Equal(EconomyReject.EconomyFrozen, rejected);
        var incoming = await OnActor(s => s.Donate((int)other, "frozen-incoming", (int)id, 10).Reason);
        Assert.Equal(EconomyReject.EconomyFrozen, incoming);
        await AdjustAsync(id, 10, "an admin may adjust a frozen wallet");

        using (var response = await Admin.PostJsonAsync(url, new { frozen = false, reason = "cleared" }))
        {
            Assert.False((await response.JsonAsync()).GetProperty("frozen").GetBoolean());
        }

        await Api.WaitForAuditAsync(f.Server, "economy.wallet.unfreeze", "wallet:Player:" + N(id));

        using (var response = await Admin.PostJsonAsync(url, new { reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "frozen");
        }

        using (var response = await Admin.PostJsonAsync(url, new { frozen = true }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "reason");
        }

        using (var response = await Admin.PostJsonAsync($"{Base}/wallets/Player/987654/freeze", new { frozen = true, reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await Admin.PostJsonAsync($"{Base}/wallets/Escrow/1/freeze", new { frozen = true, reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.Conflict, "NotAdjustable");
        }
    }

    // ------------------------------------------------------------------ transactions and reversal

    [Fact]
    public async Task TransactionsAreFilteredAndPagedAndOneCanBeFetched()
    {
        long id = await NewPlayerAsync();
        for (var i = 0; i < 5; i++)
        {
            await AdjustAsync(id, 10 + i, "page " + i);
        }

        var forWallet = await GetAsync($"{Base}/transactions?wallet=Player:{N(id)}&kind=AdminAdjust&limit=3");
        var page1 = forWallet.EnumerateArray().ToList();
        Assert.Equal(3, page1.Count);
        Assert.Equal("page 4", page1[0].GetProperty("note").GetString()); // newest first
        string cursor = page1[^1].GetProperty("id").GetString()!;

        var page2 = (await GetAsync($"{Base}/transactions?wallet=Player:{N(id)}&kind=AdminAdjust&before={cursor}&limit=10")).EnumerateArray().ToList();
        Assert.Equal(2, page2.Count);
        Assert.Equal("page 1", page2[0].GetProperty("note").GetString());

        var byActor = await GetAsync($"{Base}/transactions?wallet=Player:{N(id)}&actor=authority");
        Assert.Empty(byActor.EnumerateArray());
        var future = await GetAsync($"{Base}/transactions?wallet=Player:{N(id)}&since={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture))}");
        Assert.Empty(future.EnumerateArray());

        string txId = page1[0].GetProperty("id").GetString()!;
        var one = await GetAsync($"{Base}/transactions/{txId}");
        Assert.Equal(txId, one.GetProperty("id").GetString());
        Assert.Equal(2, one.GetProperty("entries").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, one.GetProperty("reversedBy").ValueKind);
        Assert.Equal(txId, (await GetAsync($"{Base}/transactions/{txId.ToLowerInvariant()}")).GetProperty("id").GetString());
    }

    [Fact]
    public async Task TransactionReadsRejectBadFiltersAndUnknownIds()
    {
        foreach (var (query, key) in new[]
        {
            ("wallet=nonsense", "wallet"), ("wallet=Purse:1", "wallet"), ("kind=Gift", "kind"), ("since=yesterday", "since"),
            ("before=not%20an%20id", "before"), ("limit=0", "limit"), ("limit=501", "limit"),
        })
        {
            using var response = await f.Viewer.GetAsync($"{Base}/transactions?{query}");
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", key);
        }

        await OpenPolicyAsync();
        using var missing = await f.Viewer.GetAsync(Base + "/transactions/01NOSUCHTRANSACTION00000000");
        await missing.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
    }

    [Fact]
    public async Task AReversalLinksBothTransactionsAndASecondOneIsAConflict()
    {
        long id = await NewPlayerAsync();
        var adjust = await AdjustAsync(id, 4_000, "mistaken bonus");
        string original = adjust.GetProperty("id").GetString()!;
        Assert.Equal(104_000, await BalanceAsync(id));

        using var reversed = await Admin.PostJsonAsync($"{Base}/transactions/{original}/reverse", new { reason = "that was a mistake" });
        Assert.Equal(HttpStatusCode.OK, reversed.StatusCode);
        var reversal = await reversed.JsonAsync();
        string reversalId = reversal.GetProperty("id").GetString()!;
        Assert.Equal("Reversal", reversal.GetProperty("kind").GetString());
        Assert.Equal(original, reversal.GetProperty("reverses").GetString());
        Assert.Equal("that was a mistake", reversal.GetProperty("note").GetString());
        Assert.Equal(100_000, await BalanceAsync(id));

        var originalAfter = await GetAsync($"{Base}/transactions/{original}");
        Assert.Equal(reversalId, originalAfter.GetProperty("reversedBy").GetString());
        var reversalAfter = await GetAsync($"{Base}/transactions/{reversalId}");
        Assert.Equal(original, reversalAfter.GetProperty("reverses").GetString());

        using (var second = await Admin.PostJsonAsync($"{Base}/transactions/{original}/reverse", new { reason = "again" }))
        {
            var problem = await second.AssertProblemAsync(HttpStatusCode.Conflict, "AlreadyReversed");
            Assert.Contains(reversalId, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }

        using (var third = await Admin.PostJsonAsync($"{Base}/transactions/{reversalId}/reverse", new { reason = "undo the undo" }))
        {
            await third.AssertProblemAsync(HttpStatusCode.Conflict, "NotReversible");
        }

        var row = await Api.WaitForAuditAsync(f.Server, "economy.reverse", "tx:" + original);
        Assert.Contains("that was a mistake", row.DataJson, StringComparison.Ordinal);
        Assert.Contains(reversalId, row.DataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReversalThatWouldOverdrawIsRefusedUnlessForced()
    {
        long id = await NewPlayerAsync();
        var adjust = await AdjustAsync(id, 1_000, "bonus");
        string original = adjust.GetProperty("id").GetString()!;
        await AdjustAsync(id, -100_500, "the player spent it"); // 500 left, the bonus cannot come back out of it

        using (var refused = await Admin.PostJsonAsync($"{Base}/transactions/{original}/reverse", new { reason = "claw back" }))
        {
            await refused.AssertProblemAsync(HttpStatusCode.Conflict, "WouldOverdraw");
        }

        Assert.Equal(500, await BalanceAsync(id)); // nothing moved
        Assert.Equal(JsonValueKind.Null, (await GetAsync($"{Base}/transactions/{original}")).GetProperty("reversedBy").ValueKind);

        using var forced = await Admin.PostJsonAsync($"{Base}/transactions/{original}/reverse", new { reason = "claw back", force = true });
        Assert.Equal(HttpStatusCode.OK, forced.StatusCode);
        Assert.Equal(-500, await BalanceAsync(id));
        Assert.True((await WalletAsync(id)).GetProperty("wallet").GetProperty("overdrawn").GetBoolean());
    }

    [Fact]
    public async Task ReverseErrorsAreProblems()
    {
        await OpenPolicyAsync();
        using (var response = await Admin.PostJsonAsync($"{Base}/transactions/01NOSUCHTRANSACTION00000000/reverse", new { reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        long id = await NewPlayerAsync();
        string tx = (await AdjustAsync(id, 5)).GetProperty("id").GetString()!;
        using (var response = await Admin.PostJsonAsync($"{Base}/transactions/{tx}/reverse", new { }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "reason");
        }

        using (var response = await Admin.PostJsonAsync($"{Base}/transactions/{tx}/reverse", new { reason = "x", returnAsset = true }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "returnAsset");
        }

        Assert.Equal(100_005, await BalanceAsync(id));
    }

    [Fact]
    public async Task AReversalWithAnIdempotencyKeyReplaysTheFirstResult()
    {
        long id = await NewPlayerAsync();
        string tx = (await AdjustAsync(id, 300, "bonus")).GetProperty("id").GetString()!;
        async Task<HttpResponseMessage> Send(string reason)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/transactions/{tx}/reverse") { Content = JsonContent.Create(new { reason }) };
            request.Headers.Add("Idempotency-Key", "rev-key");
            return await Admin.SendAsync(request);
        }

        using var first = await Send("oops");
        using var replay = await Send("oops");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal((await first.JsonAsync()).GetProperty("id").GetString(), (await replay.JsonAsync()).GetProperty("id").GetString());
        using var other = await Send("a different reason");
        await other.AssertProblemAsync(HttpStatusCode.Conflict, "RequestIdReuse");
        Assert.Equal(100_000, await BalanceAsync(id));
    }

    // ------------------------------------------------------------------ ledger.csv

    [Fact]
    public async Task TheLedgerExportIsCsvWithOneRowPerEntryAndNeutralisesFormulas()
    {
        long id = await NewPlayerAsync();
        await AdjustAsync(id, 77, "=HYPERLINK(\"http://evil\",\"click\"), \"quoted\"");

        using var response = await Admin.GetAsync(Base + "/ledger.csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/csv", response.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
        Assert.Equal("ledger.csv", response.Content.Headers.ContentDisposition?.FileName?.Trim((char)34));
        string text = await response.Content.ReadAsStringAsync();
        var lines = text.Split("\r\n".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("tx_id,time,kind,actor,request_id,ref_type,ref_id,reverses,reversed_by,note,wallet_kind,wallet_owner_id,wallet_name,amount,balance_after", lines[0]);
        Assert.Contains(lines, l => l.Contains(",StartingCredits,", StringComparison.Ordinal) && l.Contains(",Player," + N(id) + ",", StringComparison.Ordinal));
        string adjusted = lines.Single(l => l.Contains(",77,", StringComparison.Ordinal) && l.Contains("AdminAdjust", StringComparison.Ordinal) && l.Contains(",Player," + N(id) + ",", StringComparison.Ordinal));
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\",\"\"click\"\"), \"\"quoted\"\"\"", adjusted, StringComparison.Ordinal);

        // both entries of a transaction are rows; they sum to zero per transaction
        var sums = lines.Skip(1).Select(l => (Tx: l[..26], Amount: long.Parse(ParseCsv(l)[13], CultureInfo.InvariantCulture))).GroupBy(r => r.Tx);
        Assert.All(sums, g => Assert.Equal(0, g.Sum(r => r.Amount)));

        string since = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture));
        using var none = await Admin.GetAsync($"{Base}/ledger.csv?since={since}");
        Assert.Single((await none.Content.ReadAsStringAsync()).Split("\r\n".ToCharArray(), StringSplitOptions.RemoveEmptyEntries));

        using var bad = await Admin.GetAsync(Base + "/ledger.csv?since=yesterday&until=tomorrow");
        await bad.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "since");
        await Api.WaitForAuditAsync(f.Server, "economy.ledger.export");
    }

    private static List<string> ParseCsv(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }

    // ------------------------------------------------------------------ loans

    private async Task<long> ActiveLoanAsync(long lender, long borrower, long principal = 1_000, long total = 1_100)
    {
        var id = await OnActor(s =>
        {
            var offer = s.OfferLoan((int)lender, "offer-" + Guid.NewGuid().ToString("N"), (int)borrower, principal, total, 3600, 300, 0, "test");
            Assert.True(offer.Ok, offer.Reason + " " + offer.Detail);
            Assert.True(s.RespondLoan((int)borrower, "accept-" + Guid.NewGuid().ToString("N"), offer.Loan!.Id, true).Ok);
            return offer.Loan.Id;
        });
        return id;
    }

    [Fact]
    public async Task LoansAreListedAndShownInDetailAndCanBeForgiven()
    {
        long lender = await NewPlayerAsync("Lender");
        long borrower = await NewPlayerAsync("Borrower");
        long loan = await ActiveLoanAsync(lender, borrower);

        var list = await GetAsync($"{Base}/loans?state=Active&playerId={N(borrower)}");
        var row = list.EnumerateArray().Single(l => l.GetProperty("id").GetInt64() == loan);
        Assert.Equal(1_100, row.GetProperty("outstanding").GetInt64());
        Assert.Equal(1_000, row.GetProperty("principal").GetInt64());
        Assert.Equal(lender, row.GetProperty("lenderId").GetInt64());
        Assert.Equal("Active", row.GetProperty("state").GetString());
        Assert.False(row.GetProperty("overdue").GetBoolean());
        Assert.Empty((await GetAsync($"{Base}/loans?state=Repaid&playerId={N(borrower)}")).EnumerateArray());

        var detail = await GetAsync($"{Base}/loans/{N(loan)}");
        Assert.Equal(loan, detail.GetProperty("loan").GetProperty("id").GetInt64());
        Assert.Contains(detail.GetProperty("transactions").EnumerateArray(), t => t.GetProperty("kind").GetString() == "LoanDisburse");

        using (var forgiven = await Admin.PostJsonAsync($"{Base}/loans/{N(loan)}/forgive", new { reason = "the borrower was griefed" }))
        {
            Assert.Equal(HttpStatusCode.OK, forgiven.StatusCode);
            var dto = await forgiven.JsonAsync();
            Assert.Equal("Forgiven", dto.GetProperty("state").GetString());
            Assert.Equal(0, dto.GetProperty("outstanding").GetInt64());
        }

        var row2 = await Api.WaitForAuditAsync(f.Server, "economy.loan.forgive", "loan:" + N(loan));
        Assert.Contains("the borrower was griefed", row2.DataJson, StringComparison.Ordinal);

        using (var again = await Admin.PostJsonAsync($"{Base}/loans/{N(loan)}/forgive", new { reason = "again" }))
        {
            await again.AssertProblemAsync(HttpStatusCode.Conflict, "WrongState");
        }
    }

    [Fact]
    public async Task CancellingALoanCanReverseTheDisbursementAndRefusesWhenTheBorrowerSpentIt()
    {
        long lender = await NewPlayerAsync("Lender");
        long borrower = await NewPlayerAsync("Borrower");
        long loan = await ActiveLoanAsync(lender, borrower, 50_000, 55_000);
        Assert.Equal(150_000, await BalanceAsync(borrower));
        Assert.Equal(50_000, await BalanceAsync(lender));
        await AdjustAsync(borrower, -149_000, "spent");

        using (var refused = await Admin.PostJsonAsync($"{Base}/loans/{N(loan)}/cancel", new { reason = "bad loan", reverseDisbursement = true }))
        {
            await refused.AssertProblemAsync(HttpStatusCode.Conflict, "WouldOverdraw");
        }

        Assert.Equal("Active", (await GetAsync($"{Base}/loans/{N(loan)}")).GetProperty("loan").GetProperty("state").GetString());

        using (var forced = await Admin.PostJsonAsync($"{Base}/loans/{N(loan)}/cancel", new { reason = "bad loan", reverseDisbursement = true, force = true }))
        {
            Assert.Equal(HttpStatusCode.OK, forced.StatusCode);
            Assert.Equal("Cancelled", (await forced.JsonAsync()).GetProperty("state").GetString());
        }

        Assert.Equal(100_000, await BalanceAsync(lender));
        Assert.Equal(-49_000, await BalanceAsync(borrower));
        await Api.WaitForAuditAsync(f.Server, "economy.loan.cancel", "loan:" + N(loan));
        await Api.WaitForAuditAsync(f.Server, "economy.reverse");
    }

    [Fact]
    public async Task LoanErrorsAreProblems()
    {
        await OpenPolicyAsync();
        foreach (var (url, key) in new[] { ("state=Lost", "state") })
        {
            using var response = await f.Viewer.GetAsync($"{Base}/loans?{url}");
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", key);
        }

        using (var response = await f.Viewer.GetAsync(Base + "/loans/987654"))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await Admin.PostJsonAsync(Base + "/loans/987654/forgive", new { reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await Admin.PostJsonAsync(Base + "/loans/987654/cancel", new { reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await Admin.PostJsonAsync(Base + "/loans/1/forgive", new { }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "reason");
        }

        using (var response = await Admin.PostJsonAsync(Base + "/loans/1/cancel", new { }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "reason");
        }
    }

    // ------------------------------------------------------------------ trades

    /// <summary>A world that lets any ship be traded (the real mirror has no ships in this test).</summary>
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

    private static uint _ship = 5_000;

    /// <summary>Puts a trade into the given state through the real service: <c>InDoubt</c> after the authority "left", <c>Proposed</c> before the counterparty accepts.</summary>
    private async Task<long> TradeAsync(long seller, long buyer, long price, bool inDoubt)
    {
        return await OnActor(s =>
        {
            s.TradeWorld = new OpenWorld();
            s.SendTransferOrder = _ => true;
            s.AuthorityOnline = () => true;
            var ship = Interlocked.Increment(ref _ship);
            var proposed = s.ProposeTrade((int)seller, "p-" + ship, (int)buyer, [TradeItemModel.Ship(ship)], [TradeItemModel.Credits(price)], 300, "test");
            Assert.True(proposed.Ok, proposed.Reason + " " + proposed.Detail);
            if (inDoubt)
            {
                Assert.True(s.AcceptTrade((int)buyer, "a-" + ship, proposed.Trade!.Id, proposed.Trade.Version, 0).Ok);
                s.OnAuthorityGone();
                Assert.Equal(TradeState.InDoubt, proposed.Trade.State);
            }

            return proposed.Trade!.Id;
        });
    }

    [Fact]
    public async Task AnInDoubtTradeIsResolvedAsRefundOrComplete()
    {
        long seller = await NewPlayerAsync("Seller");
        long buyer = await NewPlayerAsync("Buyer");
        long refundTrade = await TradeAsync(seller, buyer, 12_000, inDoubt: true);
        Assert.Equal(88_000, await BalanceAsync(buyer)); // the price sits in escrow

        var list = await GetAsync($"{Base}/trades?state=InDoubt&playerId={N(buyer)}");
        var row = list.EnumerateArray().Single(t => t.GetProperty("id").GetInt64() == refundTrade);
        Assert.Equal(12_000, row.GetProperty("escrowAmount").GetInt64());
        Assert.Equal(seller, row.GetProperty("initiatorId").GetInt64());
        Assert.Equal("Ship", row.GetProperty("initiatorGives")[0].GetProperty("kind").GetString());
        Assert.Equal("Credits", row.GetProperty("counterpartyGives")[0].GetProperty("kind").GetString());

        var summary = await GetAsync(Base + "/summary");
        Assert.True(summary.GetProperty("inDoubtTrades").GetInt32() >= 1);
        Assert.True(summary.GetProperty("inEscrow").GetInt64() >= 12_000);

        var detail = await GetAsync($"{Base}/trades/{N(refundTrade)}");
        Assert.Equal("InDoubt", detail.GetProperty("trade").GetProperty("state").GetString());
        Assert.Contains(detail.GetProperty("transactions").EnumerateArray(), t => t.GetProperty("kind").GetString() == "TradeEscrow");

        using (var resolved = await Admin.PostJsonAsync($"{Base}/trades/{N(refundTrade)}/resolve", new { outcome = "refund", reason = "the ship never moved" }))
        {
            Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
            var dto = await resolved.JsonAsync();
            Assert.Equal("RolledBack", dto.GetProperty("state").GetString());
            Assert.Equal(0, dto.GetProperty("escrowAmount").GetInt64());
        }

        Assert.Equal(100_000, await BalanceAsync(buyer));
        var audit = await Api.WaitForAuditAsync(f.Server, "economy.trade.resolve_refund", "trade:" + N(refundTrade));
        Assert.Contains("the ship never moved", audit.DataJson, StringComparison.Ordinal);

        long completeTrade = await TradeAsync(seller, buyer, 7_000, inDoubt: true);
        using (var resolved = await Admin.PostJsonAsync($"{Base}/trades/{N(completeTrade)}/resolve", new { outcome = "complete", reason = "the ship moved" }))
        {
            Assert.Equal("Completed", (await resolved.JsonAsync()).GetProperty("state").GetString());
        }

        Assert.Equal(93_000, await BalanceAsync(buyer));
        Assert.Equal(107_000, await BalanceAsync(seller));
        await Api.WaitForAuditAsync(f.Server, "economy.trade.resolve_complete", "trade:" + N(completeTrade));
    }

    [Fact]
    public async Task ANegotiatingTradeCanBeCancelledAndTradeErrorsAreProblems()
    {
        long seller = await NewPlayerAsync("Seller");
        long buyer = await NewPlayerAsync("Buyer");
        long open = await TradeAsync(seller, buyer, 5_000, inDoubt: false);

        using (var wrong = await Admin.PostJsonAsync($"{Base}/trades/{N(open)}/resolve", new { outcome = "refund", reason = "x" }))
        {
            await wrong.AssertProblemAsync(HttpStatusCode.Conflict, "WrongState");
        }

        using (var cancelled = await Admin.PostJsonAsync($"{Base}/trades/{N(open)}/cancel", new { reason = "no longer wanted" }))
        {
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
            Assert.Equal("Cancelled", (await cancelled.JsonAsync()).GetProperty("state").GetString());
        }

        await Api.WaitForAuditAsync(f.Server, "economy.trade.cancel", "trade:" + N(open));
        using (var again = await Admin.PostJsonAsync($"{Base}/trades/{N(open)}/cancel", new { reason = "again" }))
        {
            await again.AssertProblemAsync(HttpStatusCode.Conflict, "WrongState");
        }

        using (var response = await f.Viewer.GetAsync(Base + "/trades?state=Lost"))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "state");
        }

        using (var response = await f.Viewer.GetAsync(Base + "/trades/987654"))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await Admin.PostJsonAsync(Base + "/trades/987654/cancel", new { reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await Admin.PostJsonAsync(Base + "/trades/987654/resolve", new { outcome = "refund", reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await Admin.PostJsonAsync($"{Base}/trades/{N(open)}/resolve", new { outcome = "maybe", reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "outcome");
        }

        using (var response = await Admin.PostJsonAsync($"{Base}/trades/{N(open)}/cancel", new { }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "reason");
        }
    }

    // ------------------------------------------------------------------ summary, events, policy, audit

    [Fact]
    public async Task TheSummaryShowsMoneySupplyEscrowAndTheAuditor()
    {
        long id = await NewPlayerAsync();
        await AdjustAsync(id, 1_000, "supply");
        var summary = await GetAsync(Base + "/summary");
        Assert.Equal("PerPlayer", summary.GetProperty("creditMode").GetString());
        Assert.Equal("PerPlayer", summary.GetProperty("effectiveCreditMode").GetString());
        Assert.True(summary.GetProperty("moneySupply").GetInt64() >= 101_000);
        Assert.False(summary.GetProperty("economyFrozen").GetBoolean());
        Assert.True(summary.GetProperty("lastAudit").GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Array, summary.GetProperty("lastAudit").GetProperty("violations").ValueKind);

        // moneySupply is exactly the negated world balance
        var world = (await GetAsync(Base + "/wallets?kind=World")).EnumerateArray().Single();
        Assert.Equal(-world.GetProperty("balance").GetInt64(), (await GetAsync(Base + "/summary")).GetProperty("moneySupply").GetInt64());
    }

    [Fact]
    public async Task TheEventLogShowsAdminActionsAndFiltersByTypeAndReference()
    {
        long lender = await NewPlayerAsync("Lender");
        long borrower = await NewPlayerAsync("Borrower");
        long loan = await ActiveLoanAsync(lender, borrower);
        using (var forgiven = await Admin.PostJsonAsync($"{Base}/loans/{N(loan)}/forgive", new { reason = "event test" }))
        {
            Assert.Equal(HttpStatusCode.OK, forgiven.StatusCode);
        }

        List<JsonElement> events = [];
        await SaveServer.WaitUntilAsync(() =>
        {
            using var response = f.Viewer.GetAsync($"{Base}/events?refType=loan&refId={N(loan)}").GetAwaiter().GetResult();
            events = [.. response.JsonAsync().GetAwaiter().GetResult().EnumerateArray()];
            return events.Any(e => e.GetProperty("type").GetString() == "economy.loan.forgive") && events.Any(e => e.GetProperty("toState").GetString() == "Forgiven");
        }, 15_000, "economy events persisted");

        var forgive = events.Single(e => e.GetProperty("type").GetString() == "economy.loan.forgive");
        Assert.Equal("event test", forgive.GetProperty("reason").GetString());
        Assert.StartsWith("admin:test-", forgive.GetProperty("actor").GetString(), StringComparison.Ordinal);
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "LoanStateChanged" && e.GetProperty("fromState").GetString() == "Offered" && e.GetProperty("toState").GetString() == "Active");
        Assert.True(events.Count >= 3);
        Assert.Equal(events.Select(e => e.GetProperty("at").GetDateTimeOffset()).Order(), events.Select(e => e.GetProperty("at").GetDateTimeOffset())); // oldest first

        var onlyAdmin = await GetAsync($"{Base}/events?type=economy.loan.forgive&refId={N(loan)}");
        Assert.Single(onlyAdmin.EnumerateArray());
        var limited = await GetAsync($"{Base}/events?limit=1");
        Assert.Single(limited.EnumerateArray());

        using (var response = await f.Viewer.GetAsync(Base + "/events?since=yesterday"))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "since");
        }

        using (var response = await f.Viewer.GetAsync(Base + "/events?limit=5000"))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "limit");
        }
    }

    [Fact]
    public async Task ThePolicyIsReadAndPatchedWithPerKeyErrors()
    {
        await OpenPolicyAsync();
        var policy = await GetAsync(Base + "/policy");
        Assert.Equal("PerPlayer", policy.GetProperty("creditMode").GetString());
        Assert.Equal("Anyone", policy.GetProperty("donateScope").GetString());

        using (var response = await Admin.CallAsync(HttpMethod.Patch, Base + "/policy", new { maxSingleTransfer = 250_000, poolWithdrawPolicy = "LeaderOnly", reason = "tighten up" }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var changed = await response.JsonAsync();
            Assert.Equal(250_000, changed.GetProperty("maxSingleTransfer").GetInt64());
            Assert.Equal("LeaderOnly", changed.GetProperty("poolWithdrawPolicy").GetString());
        }

        Assert.Equal(250_000, (await GetAsync(Base + "/policy")).GetProperty("maxSingleTransfer").GetInt64());
        AuditRecord? audit = null;
        await SaveServer.WaitUntilAsync(
            () => (audit = f.Server.Service<SqliteAdminQueries>().AuditEntries(2000).FirstOrDefault(r => r.Action == "economy.policy" && r.DataJson?.Contains("tighten up", StringComparison.Ordinal) == true)) is not null,
            10_000, "policy audit row");
        Assert.Contains("maxSingleTransfer", audit!.DataJson, StringComparison.Ordinal);

        using (var response = await Admin.CallAsync(HttpMethod.Patch, Base + "/policy", new { donateScope = "Everyone", maxLoanInterestBp = -5, tradeExecuteTimeoutSeconds = 100000 }))
        {
            var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "donateScope");
            Assert.True(problem.GetProperty("errors").TryGetProperty("maxLoanInterestBp", out _));
            Assert.True(problem.GetProperty("errors").TryGetProperty("tradeExecuteTimeoutSeconds", out _));
            Assert.Equal("UnknownValue", problem.GetProperty("errorCodes").GetProperty("donateScope").GetString());
        }

        Assert.Equal("Anyone", (await GetAsync(Base + "/policy")).GetProperty("donateScope").GetString()); // nothing changed

        using (var response = await Admin.CallAsync(HttpMethod.Patch, Base + "/policy", new { }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "body");
        }

        using (var response = await Admin.CallAsync(HttpMethod.Patch, Base + "/policy", new { confirm = true }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "body");
        }

        using (var response = await Admin.CallAsync(HttpMethod.Patch, Base + "/policy", new { startingCredits = 100_000, maxSingleTransfer = 1_000_000_000_000 }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task TheAuditEndpointRunsTheCheckAndAnAdminAcknowledgesABreach()
    {
        long id = await NewPlayerAsync();
        using (var ok = await Admin.PostJsonAsync(Base + "/audit", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            var report = await ok.JsonAsync();
            Assert.True(report.GetProperty("ok").GetBoolean());
            Assert.False(report.GetProperty("economyFrozen").GetBoolean());
        }

        // corrupt the stored balance behind the ledger's back: the next audit finds it and freezes the economy
        Corrupt(id, +1);
        using (var breach = await Admin.PostJsonAsync(Base + "/audit", new { }))
        {
            var report = await breach.JsonAsync();
            Assert.False(report.GetProperty("ok").GetBoolean());
            Assert.True(report.GetProperty("economyFrozen").GetBoolean());
            Assert.NotEmpty(report.GetProperty("violations").EnumerateArray());
        }

        Assert.True((await GetAsync(Base + "/summary")).GetProperty("economyFrozen").GetBoolean());
        using (var frozen = await Admin.PostJsonAsync($"{Base}/wallets/Player/{N(id)}/adjust", new { amount = 5, reason = "x" }))
        {
            await frozen.AssertProblemAsync(HttpStatusCode.Conflict, "EconomyFrozen");
        }

        // acknowledging without fixing the cause freezes again at once
        using (var still = await Admin.PostJsonAsync(Base + "/audit", new { acknowledge = true }))
        {
            Assert.True((await still.JsonAsync()).GetProperty("economyFrozen").GetBoolean());
        }

        Corrupt(id, -1);
        using (var fixedUp = await Admin.PostJsonAsync(Base + "/audit", new { acknowledge = true }))
        {
            var report = await fixedUp.JsonAsync();
            Assert.True(report.GetProperty("ok").GetBoolean());
            Assert.False(report.GetProperty("economyFrozen").GetBoolean());
        }

        await AdjustAsync(id, 5, "the economy is back");
        await Api.WaitForAuditAsync(f.Server, "economy.unfreeze");
    }

    private void Corrupt(long player, long delta)
    {
        var factory = f.Server.Service<SqliteConnectionFactory>();
        using var db = factory.Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE wallets SET balance = balance + $d WHERE kind = 'player' AND owner_id = $o";
        command.Parameters.AddWithValue("$d", delta);
        command.Parameters.AddWithValue("$o", player);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    // ------------------------------------------------------------------ the ledger stays sound

    [Fact]
    public async Task TheLedgerSumsToZeroAfterAdminAdjustReverseAndForcedOperations()
    {
        long a = await NewPlayerAsync("SumA");
        long b = await NewPlayerAsync("SumB");
        var transactions = new List<string>();
        foreach (var (player, amount, force) in new[] { (a, 3_000L, false), (b, -2_000L, false), (a, -500_000L, true), (b, 40_000L, false), (a, 9L, false) })
        {
            transactions.Add((await AdjustAsync(player, amount, "sum test", force)).GetProperty("id").GetString()!);
        }

        foreach (var (index, force) in new[] { (0, false), (2, true), (3, false), (1, true) })
        {
            using var response = await Admin.PostJsonAsync($"{Base}/transactions/{transactions[index]}/reverse", new { reason = "sum test", force });
            Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        }

        var wallets = await GetAsync(Base + "/wallets");
        Assert.Equal(0, wallets.EnumerateArray().Sum(w => w.GetProperty("balance").GetInt64()));

        // the ledger's own entries sum to zero too, and the auditor agrees
        string csv = await Admin.GetStringAsync(Base + "/ledger.csv");
        Assert.Equal(0, csv.Split("\r\n".ToCharArray(), StringSplitOptions.RemoveEmptyEntries).Skip(1).Sum(l => long.Parse(ParseCsv(l)[13], CultureInfo.InvariantCulture)));
        using var audit = await Admin.PostJsonAsync(Base + "/audit", new { });
        Assert.True((await audit.JsonAsync()).GetProperty("ok").GetBoolean());
    }

    // ------------------------------------------------------------------ audit coverage

    [Fact]
    public async Task EveryAdminMoneyActionWritesAnAuditRowWithTheReasonAndNoSecrets()
    {
        long a = await NewPlayerAsync("AuditA");
        long b = await NewPlayerAsync("AuditB");
        string tx = (await AdjustAsync(a, 100, "audit reason adjust")).GetProperty("id").GetString()!;
        using (await Admin.PostJsonAsync($"{Base}/wallets/Player/{N(a)}/freeze", new { frozen = true, reason = "audit reason freeze" })) { }
        using (await Admin.PostJsonAsync($"{Base}/wallets/Player/{N(a)}/freeze", new { frozen = false, reason = "audit reason unfreeze" })) { }
        using (await Admin.PostJsonAsync($"{Base}/transactions/{tx}/reverse", new { reason = "audit reason reverse" })) { }
        long loan = await ActiveLoanAsync(a, b);
        using (await Admin.PostJsonAsync($"{Base}/loans/{N(loan)}/forgive", new { reason = "audit reason forgive" })) { }
        long loan2 = await ActiveLoanAsync(a, b);
        using (await Admin.PostJsonAsync($"{Base}/loans/{N(loan2)}/cancel", new { reason = "audit reason cancel" })) { }
        long trade = await TradeAsync(a, b, 100, inDoubt: false);
        using (await Admin.PostJsonAsync($"{Base}/trades/{N(trade)}/cancel", new { reason = "audit reason trade" })) { }
        long doubt = await TradeAsync(a, b, 100, inDoubt: true);
        using (await Admin.PostJsonAsync($"{Base}/trades/{N(doubt)}/resolve", new { outcome = "refund", reason = "audit reason resolve" })) { }

        foreach (var (action, reason) in new[]
        {
            ("economy.adjust", "audit reason adjust"), ("economy.wallet.freeze", "audit reason freeze"), ("economy.wallet.unfreeze", "audit reason unfreeze"),
            ("economy.reverse", "audit reason reverse"), ("economy.loan.forgive", "audit reason forgive"), ("economy.loan.cancel", "audit reason cancel"),
            ("economy.trade.cancel", "audit reason trade"), ("economy.trade.resolve_refund", "audit reason resolve"),
        })
        {
            AuditRecord? row = null;
            await SaveServer.WaitUntilAsync(
                () => (row = f.Server.Service<SqliteAdminQueries>().AuditEntries(2000).FirstOrDefault(r => r.Action == action && r.DataJson?.Contains(reason, StringComparison.Ordinal) == true)) is not null,
                10_000, "audit row " + action);
            Assert.StartsWith("admin:test-", row!.Actor, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer", row.DataJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("x4mp_", row.DataJson, StringComparison.Ordinal); // an API token never lands in the audit trail
        }
    }
}
