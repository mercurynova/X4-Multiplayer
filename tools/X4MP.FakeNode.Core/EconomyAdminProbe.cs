using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using X4MP.Proto;

namespace X4MP.FakeNode;

/// <summary>One wallet as the admin API reports it.</summary>
public sealed record AdminWallet(string Kind, long OwnerId, string Name, long Balance, ulong Version, bool Frozen);

/// <summary>One loan as the admin API reports it.</summary>
public sealed record AdminLoan(long Id, string State, bool Overdue, string Lender, string Borrower, long Outstanding);

/// <summary>One trade as the admin API reports it.</summary>
public sealed record AdminTrade(long Id, string State);

/// <summary>What a pass over the whole ledger found: requests booked more than once and what each node's <c>CreditDelta</c>s added up to.</summary>
public sealed record AdminLedgerScan(int Transactions, IReadOnlyList<string> DuplicateRequests, IReadOnlyDictionary<string, long> DeltaBooked);

/// <summary>The auditor's answer.</summary>
public sealed record AdminAudit(bool Ok, bool EconomyFrozen, IReadOnlyList<string> Violations);

/// <summary>
/// The admin REST API as the fake nodes use it at the end of a run (<c>--admin-url</c>, M1-F4): sign in, run the auditor, read wallets, loans and
/// trades, and scan the ledger for a request that was booked twice. Read-mostly: the only POST is the audit itself and the sign-in.
/// The session cookie is kept by hand because the server may mark it <c>Secure</c> and a plain-HTTP test server would then never get it back.
/// </summary>
public sealed class EconomyAdminProbe : IDisposable
{
    private readonly HttpClient _http;
    private string _cookie = string.Empty;

    private EconomyAdminProbe(string baseUrl)
    {
        _http = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(baseUrl + "/"), Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Add("X-X4MP", "1");
    }

    public static async Task<EconomyAdminProbe> SignInAsync(string baseUrl, string user, string password, CancellationToken ct)
    {
        var probe = new EconomyAdminProbe(baseUrl.TrimEnd('/'));
        try
        {
            using var response = await probe._http.PostAsJsonAsync("api/v1/auth/login", new { username = user, password }, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"admin sign-in failed: HTTP {(int)response.StatusCode}");
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                probe._cookie = string.Join("; ", cookies.Select(c => c.Split(';', 2)[0]));
            return probe;
        }
        catch
        {
            probe.Dispose();
            throw;
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (_cookie.Length > 0)
            request.Headers.Add("Cookie", _cookie);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{method} {path}: HTTP {(int)response.StatusCode} {text[..Math.Min(200, text.Length)]}");
        using var doc = JsonDocument.Parse(text.Length == 0 ? "null" : text);
        return doc.RootElement.Clone();
    }

    /// <summary>Runs the invariant check (it freezes the economy if it finds a breach, like the periodic one).</summary>
    public async Task<AdminAudit> AuditAsync(CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, "api/v1/economy/audit", new { }, ct).ConfigureAwait(false);
        return new AdminAudit(
            json.GetProperty("ok").GetBoolean(),
            json.GetProperty("economyFrozen").GetBoolean(),
            [.. json.GetProperty("violations").EnumerateArray().Select(v => v.GetString() ?? string.Empty)]);
    }

    public async Task<IReadOnlyList<AdminWallet>> WalletsAsync(CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "api/v1/economy/wallets", null, ct).ConfigureAwait(false);
        return [.. json.EnumerateArray().Select(w => new AdminWallet(
            w.GetProperty("kind").GetString() ?? string.Empty,
            w.GetProperty("ownerId").GetInt64(),
            w.GetProperty("ownerName").GetString() ?? string.Empty,
            w.GetProperty("balance").GetInt64(),
            w.GetProperty("version").GetUInt64(),
            w.GetProperty("frozen").GetBoolean()))];
    }

    public async Task<IReadOnlyList<AdminLoan>> LoansAsync(CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "api/v1/economy/loans", null, ct).ConfigureAwait(false);
        return [.. json.EnumerateArray().Select(l => new AdminLoan(
            l.GetProperty("id").GetInt64(),
            l.GetProperty("state").GetString() ?? string.Empty,
            l.GetProperty("overdue").GetBoolean(),
            l.GetProperty("lender").GetString() ?? string.Empty,
            l.GetProperty("borrower").GetString() ?? string.Empty,
            l.GetProperty("outstanding").GetInt64()))];
    }

    public async Task<IReadOnlyList<AdminTrade>> TradesAsync(CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "api/v1/economy/trades", null, ct).ConfigureAwait(false);
        return [.. json.EnumerateArray().Select(t => new AdminTrade(t.GetProperty("id").GetInt64(), t.GetProperty("state").GetString() ?? string.Empty))];
    }

    /// <summary>
    /// Walks the whole ledger (newest first, 500 per page). A request key that appears on more than one transaction of one actor was booked twice.
    /// The credits booked for <c>CreditDelta</c>s are summed per actor (<c>player:id</c>) over the non-World wallets, so the caller can compare them
    /// with what that node sent.
    /// </summary>
    public async Task<AdminLedgerScan> ScanLedgerAsync(CancellationToken ct)
    {
        var seen = new Dictionary<(string Actor, string Request), int>();
        var booked = new Dictionary<string, long>(StringComparer.Ordinal);
        int count = 0;
        string? before = null;
        while (true)
        {
            string path = "api/v1/economy/transactions?limit=500" + (before is null ? string.Empty : "&before=" + before);
            var page = await SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
            var items = page.EnumerateArray().ToList();
            foreach (var tx in items)
            {
                count++;
                string actor = tx.GetProperty("actor").GetString() ?? string.Empty;
                string kind = tx.GetProperty("kind").GetString() ?? string.Empty;
                if (Text(tx, "requestId") is { } request && request != "starting-credits")
                {
                    var key = (actor, request + " (" + kind + ")");
                    seen[key] = seen.GetValueOrDefault(key) + 1;
                }

                if (kind is "GameIncome" or "GameSpend" && Text(tx, "refType") == "CreditDelta")
                {
                    long net = tx.GetProperty("entries").EnumerateArray()
                        .Where(e => e.GetProperty("walletKind").GetString() != "World")
                        .Sum(e => e.GetProperty("amount").GetInt64());
                    booked[actor] = booked.GetValueOrDefault(actor) + net;
                }
            }

            if (items.Count < 500)
                break;
            before = items[^1].GetProperty("id").GetString();
        }

        var duplicates = seen.Where(s => s.Value > 1).Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Key.Actor} request {s.Key.Request} booked {s.Value} times")).ToList();
        return new AdminLedgerScan(count, duplicates, booked);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Maps the admin API's wallet kind to the wire kind (null for <c>World</c>).</summary>
    public static WalletKind? WireKind(string kind) => kind switch
    {
        "Player" => WalletKind.Player,
        "TeamShared" => WalletKind.TeamShared,
        "TeamPool" => WalletKind.TeamPool,
        "Escrow" => WalletKind.Escrow,
        _ => null,
    };
}
