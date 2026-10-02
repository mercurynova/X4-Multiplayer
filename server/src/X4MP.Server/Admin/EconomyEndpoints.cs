using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using X4MP.Core.Economy;
using X4MP.Core.Session;
using X4MP.Core.Settings;
using X4MP.Persistence;
using X4MP.Proto;
using WalletKind = X4MP.Core.Economy.WalletKind;
using LoanState = X4MP.Core.Economy.LoanState;
using X4MP.Server.Api;
using X4MP.Server.Auth;
using X4MP.Server.Economy;
using X4MP.Server.Settings;

namespace X4MP.Server.Admin;

/// <summary>
/// <c>/api/v1/economy/...</c> (server-design 4.4, task M1-E6): wallets, ledger, adjustments, freezes, reversals, loans, trades, summary, events,
/// policy and the invariant check. Reads and mutations run on the session actor's thread (the economy is single-threaded by design). Every money-changing
/// action is audited by the economy service with the actor and the reason (<c>audit_log</c>); nothing here logs or audits a secret.
/// </summary>
internal static class EconomyEndpoints
{
    private const int MaxIdempotencyKeyLength = 128;
    private const int CsvChunk = 500;

    public static void Map(IEndpointRouteBuilder routes)
    {
        var g = routes.MapGroup("/api/v1/economy");
        g.MapGet("/summary", SummaryAsync).RequireAuthorization(AdminPolicies.Viewer);
        g.MapGet("/policy", PolicyAsync).RequireAuthorization(AdminPolicies.Viewer);
        g.MapPatch("/policy", PatchPolicyAsync).RequireAuthorization(AdminPolicies.Admin);

        g.MapGet("/wallets", WalletsAsync).RequireAuthorization(AdminPolicies.Viewer);
        g.MapGet("/wallets/{kind}/{ownerId:long}", WalletAsync).RequireAuthorization(AdminPolicies.Viewer);
        g.MapPost("/wallets/{kind}/{ownerId:long}/adjust", AdjustAsync).RequireAuthorization(AdminPolicies.Admin);
        g.MapPost("/wallets/{kind}/{ownerId:long}/freeze", FreezeAsync).RequireAuthorization(AdminPolicies.Admin);

        g.MapGet("/transactions", TransactionsAsync).RequireAuthorization(AdminPolicies.Viewer);
        g.MapGet("/transactions/{txId}", TransactionAsync).RequireAuthorization(AdminPolicies.Viewer);
        g.MapPost("/transactions/{txId}/reverse", ReverseAsync).RequireAuthorization(AdminPolicies.Admin);
        g.MapGet("/ledger.csv", LedgerCsvAsync).RequireAuthorization(AdminPolicies.Admin);

        g.MapGet("/loans", LoansAsync).RequireAuthorization(AdminPolicies.Viewer);
        g.MapGet("/loans/{id:long}", LoanAsync).RequireAuthorization(AdminPolicies.Viewer);
        g.MapPost("/loans/{id:long}/forgive", ForgiveLoanAsync).RequireAuthorization(AdminPolicies.Admin);
        g.MapPost("/loans/{id:long}/cancel", CancelLoanAsync).RequireAuthorization(AdminPolicies.Admin);

        g.MapGet("/trades", TradesAsync).RequireAuthorization(AdminPolicies.Viewer);
        g.MapGet("/trades/{id:long}", TradeAsync).RequireAuthorization(AdminPolicies.Viewer);
        g.MapPost("/trades/{id:long}/cancel", CancelTradeAsync).RequireAuthorization(AdminPolicies.Admin);
        g.MapPost("/trades/{id:long}/resolve", ResolveTradeAsync).RequireAuthorization(AdminPolicies.Admin);

        g.MapGet("/events", EventsAsync).RequireAuthorization(AdminPolicies.Viewer);
        g.MapPost("/audit", AuditAsync).RequireAuthorization(AdminPolicies.Admin);
    }

    // ------------------------------------------------------------------ plumbing

    private static IResult NoSession() =>
        Problems.Conflict("NoSession", "No session exists yet: the economy starts with the first player or the first start.");

    /// <summary>Runs <paramref name="work"/> on the actor thread with the economy service, or answers 409 <c>NoSession</c>.</summary>
    private static Task<IResult> OnActor(EconomyModule module, AdminSessions sessions, Func<EconomyService, IResult> work) =>
        sessions.Actor.CallAsync<IResult>(() => module.Service is { } service ? work(service) : NoSession());

    private static string Who(HttpContext context) => "admin:" + AdminApi.Actor(context);

    private static IResult FromAdmin(EconomyAdminResult result, string what) => result.Error switch
    {
        EconomyAdminError.UnknownWallet => Problems.NotFound("The wallet"),
        EconomyAdminError.UnknownTransaction => Problems.NotFound("The transaction"),
        EconomyAdminError.UnknownLoan => Problems.NotFound("The loan"),
        EconomyAdminError.InvalidAmount => Problems.Validation("amount", result.Detail ?? "The amount is not valid."),
        EconomyAdminError.NotAdjustable => Problems.Conflict("NotAdjustable", result.Detail ?? "That wallet cannot be changed by hand."),
        EconomyAdminError.WouldOverdraw => Problems.Conflict("WouldOverdraw", $"{what} would take {result.Detail ?? "a wallet"} below zero. Use force to allow it on a player or shared wallet."),
        EconomyAdminError.AlreadyReversed => Problems.Conflict("AlreadyReversed", "The transaction was already reversed (" + result.Detail + ")."),
        EconomyAdminError.NotReversible => Problems.Conflict("NotReversible", result.Detail ?? "The transaction cannot be reversed."),
        EconomyAdminError.WrongState => Problems.Conflict("WrongState", "The loan is " + result.Detail + ", which does not allow that."),
        EconomyAdminError.EconomyFrozen => Problems.Conflict("EconomyFrozen", "The economy is frozen" + (result.Detail is null ? "." : ": " + result.Detail)),
        EconomyAdminError.RequestIdReuse => Problems.Conflict("RequestIdReuse", "That Idempotency-Key was used with a different request."),
        _ => Problems.Result(StatusCodes.Status500InternalServerError, "StoreFailure", "The change could not be saved.", result.Detail),
    };

    private static IResult FromReject(EconomyReject reason, string? detail, string what) => reason switch
    {
        EconomyReject.UnknownLoan => Problems.NotFound("The loan"),
        EconomyReject.UnknownTrade => Problems.NotFound("The trade"),
        EconomyReject.WrongState => Problems.Conflict("WrongState", $"{what} is {detail}, which does not allow that."),
        EconomyReject.EconomyFrozen => Problems.Conflict("EconomyFrozen", "The economy is frozen" + (detail is null ? "." : ": " + detail)),
        _ => Problems.Conflict(reason.ToString(), detail ?? reason.ToString()),
    };

    private static bool TryReadKey(HttpContext context, Dictionary<string, string[]> errors, out string? key)
    {
        key = null;
        if (!context.Request.Headers.TryGetValue("Idempotency-Key", out var values) || values.Count == 0)
        {
            return true;
        }

        string text = values[0]?.Trim() ?? string.Empty;
        if (text.Length is 0 or > MaxIdempotencyKeyLength || text.Any(c => c < 0x21 || c > 0x7E))
        {
            errors["Idempotency-Key"] = [$"Use 1 to {MaxIdempotencyKeyLength} printable characters without spaces."];
            return false;
        }

        key = text;
        return true;
    }

    /// <summary>Parses a wallet kind (<c>player</c>, <c>teamshared</c>, <c>team_pool</c>, ...; case, <c>_</c> and <c>-</c> ignored).</summary>
    private static bool TryKind(string? text, out WalletKind kind)
    {
        kind = default;
        string normal = (text ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        return Enum.TryParse(normal, ignoreCase: true, out kind) && Enum.IsDefined(kind);
    }

    private static readonly string KindList = string.Join(", ", Enum.GetNames<WalletKind>());

    // ------------------------------------------------------------------ summary and policy

    private static Task<IResult> SummaryAsync(EconomyModule module, AdminSessions sessions, EconomyViews views) =>
        sessions.Actor.CallAsync<IResult>(() => Results.Json(
            module.Service is { } service ? views.Summary(service, module.Auditor) : views.EmptySummary(), ApiJsonContext.Default.EconomySummaryDto));

    private static Task<IResult> PolicyAsync(EconomyModule module, AdminSessions sessions, EconomyViews views) =>
        sessions.Actor.CallAsync<IResult>(() => Results.Json(views.Policy(module.Service), ApiJsonContext.Default.EconomyPolicyDto));

    private static async Task<IResult> PatchPolicyAsync(
        EconomyPolicyPatch? body, HttpContext context, EconomyModule module, AdminSessions sessions, EconomyViews views, SettingsService settings, AdminStore audit)
    {
        var errors = new Dictionary<string, string[]>();
        var codes = new Dictionary<string, string>();
        var changes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var fieldOfKey = new Dictionary<string, string>(StringComparer.Ordinal);
        string? reason = AdminApi.CheckReason(body?.Reason, errors, required: false);
        if (body is null)
        {
            errors["body"] = ["Send the policy fields to change."];
        }
        else
        {
            foreach (var property in typeof(EconomyPolicyPatch).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.Name is nameof(EconomyPolicyPatch.Confirm) or nameof(EconomyPolicyPatch.Reason) || property.GetValue(body) is not { } value)
                {
                    continue;
                }

                string field = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                string key = "Economy." + property.Name;
                if (!settings.Registry.TryGet(key, out var descriptor))
                {
                    errors[field] = ["Not a known setting."];
                    continue;
                }

                var check = SettingsRegistry.Validate(descriptor, JsonSerializer.SerializeToElement(value));
                if (check.IsValid)
                {
                    changes[key] = check.Normalized;
                    fieldOfKey[key] = field;
                }
                else
                {
                    errors[field] = [check.ErrorMessage!];
                    codes[field] = check.ErrorCode!;
                }
            }

            if (errors.Count == 0 && changes.Count == 0)
            {
                errors["body"] = ["Send at least one policy field to change."];
            }
        }

        if (errors.Count > 0)
        {
            return Problems.Result(StatusCodes.Status400BadRequest, "ValidationFailed", "One or more fields are invalid; nothing was changed.", null, errors, codes.Count == 0 ? null : codes);
        }

        string actor = Who(context);
        bool modeChange = false;
        if (changes.TryGetValue("Economy." + nameof(EconomyOptions.CreditMode), out var modeValue)
            && Enum.TryParse<CreditMode>(modeValue.GetString(), ignoreCase: true, out var mode) && mode != views.Options.CreditMode)
        {
            // A switch of the credit mode moves money: show what it would do and make a live session confirm it first.
            var gate = await sessions.Actor.CallAsync<IResult?>(() =>
            {
                if (module.Service is not { } service)
                {
                    return null;
                }

                var preview = service.PreviewMigration(mode);
                modeChange = preview.Needed;
                if (preview.Needed && preview.RequiresConfirm && service.IsLive && body!.Confirm != true)
                {
                    return Results.Json(
                        new MigrationConfirmProblem(
                            "urn:x4mp:problem:ConfirmationRequired", "Confirmation required.", StatusCodes.Status409Conflict, "ConfirmationRequired",
                            "Switching the credit mode while the session is live moves money between wallets. Review the migration preview and send confirm: true.",
                            views.Preview(preview)),
                        ApiJsonContext.Default.MigrationConfirmProblem, Problems.ContentType, StatusCodes.Status409Conflict);
                }

                return null;
            });
            if (gate is not null)
            {
                return gate;
            }
        }

        var result = await settings.PatchAsync(changes, actor, AdminApi.RemoteIp(context), context.RequestAborted);
        if (!result.Success)
        {
            return Problems.Result(
                StatusCodes.Status400BadRequest, "ValidationFailed", "One or more fields were rejected; nothing was changed.", null,
                result.Errors.ToDictionary(e => fieldOfKey.GetValueOrDefault(e.Key, e.Key), e => new[] { e.Message }),
                result.Errors.ToDictionary(e => fieldOfKey.GetValueOrDefault(e.Key, e.Key), e => e.Code));
        }

        AdminApi.Audit(context, audit, "economy.policy", null, reason, new() { ["fields"] = string.Join(',', fieldOfKey.Values.Order(StringComparer.Ordinal)) });

        if (modeChange)
        {
            var migrated = await sessions.Actor.CallAsync(() => module.Service?.Reconcile(confirm: true, actor));
            if (migrated is { Status: MigrationStatus.Failed })
            {
                return Problems.Conflict("MigrationFailed", "The setting was saved, but the balances could not be migrated: " + migrated.Outcome?.Reason);
            }
        }

        return await sessions.Actor.CallAsync<IResult>(() => Results.Json(views.Policy(module.Service), ApiJsonContext.Default.EconomyPolicyDto));
    }

    // ------------------------------------------------------------------ wallets

    private static Task<IResult> WalletsAsync(string? kind, string? q, EconomyModule module, AdminSessions sessions, EconomyViews views)
    {
        var errors = new Dictionary<string, string[]>();
        WalletKind? only = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (TryKind(kind, out var parsed))
            {
                only = parsed;
            }
            else
            {
                errors["kind"] = ["Use one of: " + KindList + "."];
            }
        }

        if (errors.Count > 0)
        {
            return Task.FromResult(Problems.Validation(errors));
        }

        return OnActor(module, sessions, service =>
        {
            var rows = service.Ledger.Wallets.AsEnumerable();
            if (only is { } k)
            {
                rows = rows.Where(w => w.Id.Kind == k);
            }

            var list = rows.OrderBy(w => w.Id.Kind).ThenBy(w => w.Id.OwnerId).Select(views.Wallet);
            if (!string.IsNullOrWhiteSpace(q))
            {
                string needle = q.Trim();
                list = list.Where(w => w.OwnerName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || w.OwnerId.ToString(CultureInfo.InvariantCulture) == needle);
            }

            return Results.Json(list.ToList(), ApiJsonContext.Default.ListWalletDto);
        });
    }

    private static Task<IResult> WalletAsync(string kind, long ownerId, EconomyModule module, AdminSessions sessions, EconomyViews views)
    {
        if (!TryKind(kind, out var walletKind))
        {
            return Task.FromResult(Problems.Validation("kind", "Use one of: " + KindList + "."));
        }

        var id = new WalletId(walletKind, ownerId);
        return OnActor(module, sessions, service =>
        {
            if (service.Ledger.Find(id) is not { } wallet)
            {
                return Problems.NotFound("The wallet");
            }

            var recent = service.Ledger.QueryTransactions(new LedgerQuery(Wallet: id, Limit: 50));
            var loans = new List<LoanDto>();
            var trades = new List<TradeOfferDto>();
            if (walletKind == WalletKind.Player)
            {
                loans.AddRange(service.Loans.Where(l => l.IsOpen && l.InvolvesPlayer((int)ownerId)).OrderBy(l => l.Id).Select(views.Loan));
                trades.AddRange(service.Trades.Where(t => t.IsOpen && (t.Initiator == ownerId || t.Counterparty == ownerId)).OrderBy(t => t.Id).Select(t => views.Trade(t, service)));
            }

            return Results.Json(
                new WalletDetailDto(views.Wallet(wallet), [.. recent.Select(views.Tx)], loans, trades), ApiJsonContext.Default.WalletDetailDto);
        });
    }

    private static Task<IResult> AdjustAsync(
        string kind, long ownerId, AdjustWalletRequest? body, HttpContext context, EconomyModule module, AdminSessions sessions, EconomyViews views)
    {
        var errors = new Dictionary<string, string[]>();
        string? reason = AdminApi.CheckReason(body?.Reason, errors);
        if (!TryKind(kind, out var walletKind))
        {
            errors["kind"] = ["Use one of: " + KindList + "."];
        }

        if (body?.Amount is not { } amount || amount == 0 || amount == long.MinValue || Math.Abs(amount) > EconomyLedger.MaxAmount)
        {
            errors["amount"] = ["Give a non-zero whole number of credits (positive credits the wallet, negative debits it), at most 10^15."];
        }

        TryReadKey(context, errors, out var key);
        if (errors.Count > 0)
        {
            return Task.FromResult(Problems.Validation(errors));
        }

        string actor = Who(context);
        var id = new WalletId(walletKind, ownerId);
        return OnActor(module, sessions, service =>
        {
            var result = service.AdminAdjust(actor, id, body!.Amount!.Value, reason!, body.Force == true, key);
            return result.Ok ? Results.Json(views.Tx(result.Transaction!), ApiJsonContext.Default.LedgerTxDto) : FromAdmin(result, "The adjustment");
        });
    }

    private static Task<IResult> FreezeAsync(
        string kind, long ownerId, FreezeWalletRequest? body, HttpContext context, EconomyModule module, AdminSessions sessions, EconomyViews views)
    {
        var errors = new Dictionary<string, string[]>();
        string? reason = AdminApi.CheckReason(body?.Reason, errors);
        if (!TryKind(kind, out var walletKind))
        {
            errors["kind"] = ["Use one of: " + KindList + "."];
        }

        if (body?.Frozen is null)
        {
            errors["frozen"] = ["Send frozen: true or false."];
        }

        if (errors.Count > 0)
        {
            return Task.FromResult(Problems.Validation(errors));
        }

        string actor = Who(context);
        var id = new WalletId(walletKind, ownerId);
        return OnActor(module, sessions, service =>
        {
            var result = service.AdminSetFrozen(actor, id, body!.Frozen!.Value, reason!);
            return result.Ok ? Results.Json(views.Wallet(result.Wallet!), ApiJsonContext.Default.WalletDto) : FromAdmin(result, "The change");
        });
    }

    // ------------------------------------------------------------------ ledger

    private static Task<IResult> TransactionsAsync(
        string? wallet, string? kind, string? actor, string? refType, long? refId, string? since, string? before, int? limit,
        EconomyModule module, AdminSessions sessions, EconomyViews views)
    {
        var errors = new Dictionary<string, string[]>();
        WalletId? walletFilter = null;
        if (!string.IsNullOrWhiteSpace(wallet))
        {
            var parts = wallet.Split(':');
            if (parts.Length == 2 && TryKind(parts[0], out var k) && long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var owner))
            {
                walletFilter = new WalletId(k, owner);
            }
            else
            {
                errors["wallet"] = ["Use kind:ownerId, for example Player:3."];
            }
        }

        TxKind? txKind = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (Enum.TryParse<TxKind>(kind.Trim(), ignoreCase: true, out var parsedKind) && Enum.IsDefined(parsedKind))
            {
                txKind = parsedKind;
            }
            else
            {
                errors["kind"] = ["Use one of: " + string.Join(", ", Enum.GetNames<TxKind>()) + "."];
            }
        }

        DateTimeOffset? from = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            if (AdminApi.TryParseTime(since, out var parsed))
            {
                from = parsed;
            }
            else
            {
                errors["since"] = ["Use an ISO-8601 timestamp."];
            }
        }

        if (before is not null && (before.Length is 0 or > 26 || !before.All(char.IsAsciiLetterOrDigit)))
        {
            errors["before"] = ["Use the id of a transaction (the last one of the previous page)."];
        }

        int count = AdminApi.Limit(limit, 100, 500, errors);
        if (errors.Count > 0)
        {
            return Task.FromResult(Problems.Validation(errors));
        }

        var query = new LedgerQuery(walletFilter, txKind, string.IsNullOrWhiteSpace(actor) ? null : actor.Trim(), string.IsNullOrWhiteSpace(refType) ? null : refType.Trim(),
            refId, from, null, before?.ToUpperInvariant(), null, false, count);
        return OnActor(module, sessions, service =>
            Results.Json([.. service.Ledger.QueryTransactions(query).Select(views.Tx)], ApiJsonContext.Default.ListLedgerTxDto));
    }

    private static Task<IResult> TransactionAsync(string txId, EconomyModule module, AdminSessions sessions, EconomyViews views) =>
        OnActor(module, sessions, service => service.Ledger.FindTransaction(txId.ToUpperInvariant()) is { } tx
            ? Results.Json(views.Tx(tx), ApiJsonContext.Default.LedgerTxDto)
            : Problems.NotFound("The transaction"));

    private static Task<IResult> ReverseAsync(
        string txId, ReverseTransactionRequest? body, HttpContext context, EconomyModule module, AdminSessions sessions, EconomyViews views)
    {
        var errors = new Dictionary<string, string[]>();
        string? reason = AdminApi.CheckReason(body?.Reason, errors);
        if (body?.ReturnAsset == true)
        {
            errors["returnAsset"] = ["Sending the assets of a trade back is not available yet; reverse the credits and return the asset in game."];
        }

        TryReadKey(context, errors, out var key);
        if (errors.Count > 0)
        {
            return Task.FromResult(Problems.Validation(errors));
        }

        string actor = Who(context);
        return OnActor(module, sessions, service =>
        {
            var result = service.AdminReverse(actor, txId.ToUpperInvariant(), reason!, body!.Force == true, key);
            return result.Ok ? Results.Json(views.Tx(result.Transaction!), ApiJsonContext.Default.LedgerTxDto) : FromAdmin(result, "The reversal");
        });
    }

    private static async Task<IResult> LedgerCsvAsync(
        string? since, string? until, HttpContext context, EconomyModule module, AdminSessions sessions, EconomyViews views, AdminStore audit)
    {
        var errors = new Dictionary<string, string[]>();
        DateTimeOffset? from = null;
        DateTimeOffset? to = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            if (AdminApi.TryParseTime(since, out var a))
            {
                from = a;
            }
            else
            {
                errors["since"] = ["Use an ISO-8601 timestamp."];
            }
        }

        if (!string.IsNullOrWhiteSpace(until))
        {
            if (AdminApi.TryParseTime(until, out var b))
            {
                to = b;
            }
            else
            {
                errors["until"] = ["Use an ISO-8601 timestamp."];
            }
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        if (await sessions.Actor.CallAsync(() => module.Service is not null) is false)
        {
            return NoSession();
        }

        AdminApi.Audit(context, audit, "economy.ledger.export", null, null, new()
        {
            ["since"] = from?.ToString("O", CultureInfo.InvariantCulture),
            ["until"] = to?.ToString("O", CultureInfo.InvariantCulture),
        });
        return Results.Stream(async stream =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteLineAsync("tx_id,time,kind,actor,request_id,ref_type,ref_id,reverses,reversed_by,note,wallet_kind,wallet_owner_id,wallet_name,amount,balance_after");
            string? after = null;
            while (true)
            {
                var cursor = after;
                var page = await sessions.Actor.CallAsync(() => module.Service is { } service
                    ? [.. service.Ledger.QueryTransactions(new LedgerQuery(Since: from, Until: to, After: cursor, Ascending: true, Limit: CsvChunk)).Select(views.Tx)]
                    : new List<LedgerTxDto>());
                foreach (var tx in page)
                {
                    foreach (var entry in tx.Entries)
                    {
                        await writer.WriteLineAsync(string.Join(',', Csv(tx.Id), Csv(tx.At.ToString("O", CultureInfo.InvariantCulture)), Csv(tx.Kind), Csv(tx.Actor),
                            Csv(tx.RequestId), Csv(tx.RefType), Csv(tx.RefId?.ToString(CultureInfo.InvariantCulture)), Csv(tx.Reverses), Csv(tx.ReversedBy), Csv(tx.Note),
                            Csv(entry.WalletKind), entry.WalletOwnerId.ToString(CultureInfo.InvariantCulture), Csv(entry.WalletName),
                            entry.Amount.ToString(CultureInfo.InvariantCulture), entry.BalanceAfter.ToString(CultureInfo.InvariantCulture)));
                    }
                }

                if (page.Count < CsvChunk)
                {
                    break;
                }

                after = page[^1].Id;
            }
        }, "text/csv; charset=utf-8", "ledger.csv");
    }

    /// <summary>Quotes a CSV field and defuses spreadsheet formulas (a text field that starts with = + - @ gets a leading apostrophe).</summary>
    internal static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    // ------------------------------------------------------------------ loans

    private static Task<IResult> LoansAsync(string? state, long? playerId, EconomyModule module, AdminSessions sessions, EconomyViews views)
    {
        var errors = new Dictionary<string, string[]>();
        LoanState? wanted = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            if (Enum.TryParse<LoanState>(state.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            {
                wanted = parsed;
            }
            else
            {
                errors["state"] = ["Use one of: " + string.Join(", ", Enum.GetNames<LoanState>()) + "."];
            }
        }

        if (errors.Count > 0)
        {
            return Task.FromResult(Problems.Validation(errors));
        }

        return OnActor(module, sessions, service => Results.Json(
            [.. service.Loans
                .Where(l => (wanted is null || l.State == wanted) && (playerId is null || l.InvolvesPlayer((int)playerId.Value)))
                .OrderByDescending(l => l.Id)
                .Select(views.Loan)],
            ApiJsonContext.Default.ListLoanDto));
    }

    private static Task<IResult> LoanAsync(long id, EconomyModule module, AdminSessions sessions, EconomyViews views, SqliteAdminQueries queries) =>
        OnActor(module, sessions, service =>
        {
            if (service.FindLoan(id) is not { } loan)
            {
                return Problems.NotFound("The loan");
            }

            var txs = service.Ledger.QueryTransactions(new LedgerQuery(RefType: "Loan", RefId: id, Ascending: true, Limit: 500));
            return Results.Json(
                new LoanDetailDto(views.Loan(loan), [.. txs.Select(views.Tx)], EventsOf(queries, views, service.Ledger.SessionId, "loan", id)),
                ApiJsonContext.Default.LoanDetailDto);
        });

    private static Task<IResult> ForgiveLoanAsync(
        long id, ForgiveLoanRequest? body, HttpContext context, EconomyModule module, AdminSessions sessions, EconomyViews views)
    {
        var errors = new Dictionary<string, string[]>();
        string? reason = AdminApi.CheckReason(body?.Reason, errors);
        if (errors.Count > 0)
        {
            return Task.FromResult(Problems.Validation(errors));
        }

        string actor = Who(context);
        return OnActor(module, sessions, service =>
        {
            var result = service.AdminForgiveLoan(actor, id, reason);
            return result.Ok ? Results.Json(views.Loan(result.Loan!), ApiJsonContext.Default.LoanDto) : FromReject(result.Reason, result.Detail, "The loan");
        });
    }

    private static Task<IResult> CancelLoanAsync(
        long id, CancelLoanRequest? body, HttpContext context, EconomyModule module, AdminSessions sessions, EconomyViews views)
    {
        var errors = new Dictionary<string, string[]>();
        string? reason = AdminApi.CheckReason(body?.Reason, errors);
        if (errors.Count > 0)
        {
            return Task.FromResult(Problems.Validation(errors));
        }

        string actor = Who(context);
        return OnActor(module, sessions, service =>
        {
            var result = service.AdminCancelLoan(actor, id, reason!, body!.ReverseDisbursement == true, body.Force == true);
            return result.Ok ? Results.Json(views.Loan(result.Loan!), ApiJsonContext.Default.LoanDto) : FromAdmin(result, "Taking the principal back");
        });
    }

    // ------------------------------------------------------------------ trades

    private static Task<IResult> TradesAsync(string? state, long? playerId, EconomyModule module, AdminSessions sessions, EconomyViews views)
    {
        var errors = new Dictionary<string, string[]>();
        TradeState? wanted = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            if (Enum.TryParse<TradeState>(state.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            {
                wanted = parsed;
            }
            else
            {
                errors["state"] = ["Use one of: " + string.Join(", ", Enum.GetNames<TradeState>()) + "."];
            }
        }

        if (errors.Count > 0)
        {
            return Task.FromResult(Problems.Validation(errors));
        }

        return OnActor(module, sessions, service => Results.Json(
            [.. service.Trades
                .Where(t => (wanted is null || t.State == wanted) && (playerId is null || t.Initiator == playerId || t.Counterparty == playerId))
                .OrderByDescending(t => t.Id)
                .Select(t => views.Trade(t, service))],
            ApiJsonContext.Default.ListTradeOfferDto));
    }

    private static Task<IResult> TradeAsync(long id, EconomyModule module, AdminSessions sessions, EconomyViews views, SqliteAdminQueries queries) =>
        OnActor(module, sessions, service =>
        {
            if (service.FindTrade(id) is not { } trade)
            {
                return Problems.NotFound("The trade");
            }

            var txs = service.Ledger.QueryTransactions(new LedgerQuery(RefType: "trade", RefId: id, Ascending: true, Limit: 500));
            return Results.Json(
                new TradeDetailDto(views.Trade(trade, service), [.. txs.Select(views.Tx)], EventsOf(queries, views, service.Ledger.SessionId, "trade", id)),
                ApiJsonContext.Default.TradeDetailDto);
        });

    private static Task<IResult> CancelTradeAsync(
        long id, CancelTradeRequest? body, HttpContext context, EconomyModule module, AdminSessions sessions, EconomyViews views)
    {
        var errors = new Dictionary<string, string[]>();
        string? reason = AdminApi.CheckReason(body?.Reason, errors);
        if (errors.Count > 0)
        {
            return Task.FromResult(Problems.Validation(errors));
        }

        string actor = Who(context);
        return OnActor(module, sessions, service =>
        {
            var result = service.AdminCancelTrade(id, actor, reason);
            return result.Ok ? Results.Json(views.Trade(result.Trade!, service), ApiJsonContext.Default.TradeOfferDto) : FromReject(result.Reason, result.Detail, "The trade");
        });
    }

    private static Task<IResult> ResolveTradeAsync(
        long id, ResolveTradeRequest? body, HttpContext context, EconomyModule module, AdminSessions sessions, EconomyViews views)
    {
        var errors = new Dictionary<string, string[]>();
        string? reason = AdminApi.CheckReason(body?.Reason, errors);
        string outcome = body?.Outcome?.Trim().ToLowerInvariant() ?? string.Empty;
        if (outcome is not ("complete" or "refund"))
        {
            errors["outcome"] = ["Use \"complete\" (the authority did apply the transfer) or \"refund\"."];
        }

        if (errors.Count > 0)
        {
            return Task.FromResult(Problems.Validation(errors));
        }

        string actor = Who(context);
        return OnActor(module, sessions, service =>
        {
            var result = service.AdminResolveTrade(id, outcome == "complete", actor, reason);
            return result.Ok ? Results.Json(views.Trade(result.Trade!, service), ApiJsonContext.Default.TradeOfferDto) : FromReject(result.Reason, result.Detail, "The trade");
        });
    }

    // ------------------------------------------------------------------ events and audit

    private static List<EconomyEventDto> EventsOf(SqliteAdminQueries queries, EconomyViews views, long sessionId, string refType, long refId) =>
    [
        .. queries.EconomyEvents(sessionId, null, 2000)
            .Select(EconomyViews.FromRow)
            .OfType<EconomyEventDto>()
            .Where(e => e.RefType == refType && e.RefId == refId)
            .Reverse(),
    ];

    private static Task<IResult> EventsAsync(
        string? type, string? refType, long? refId, string? since, int? limit,
        EconomyModule module, AdminSessions sessions, EconomyViews views, SqliteAdminQueries queries)
    {
        var errors = new Dictionary<string, string[]>();
        DateTimeOffset? from = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            if (AdminApi.TryParseTime(since, out var parsed))
            {
                from = parsed;
            }
            else
            {
                errors["since"] = ["Use an ISO-8601 timestamp."];
            }
        }

        int count = AdminApi.Limit(limit, 200, 1000, errors);
        if (errors.Count > 0)
        {
            return Task.FromResult(Problems.Validation(errors));
        }

        return OnActor(module, sessions, service =>
        {
            var rows = queries.EconomyEvents(service.Ledger.SessionId, from, 5000)
                .Select(EconomyViews.FromRow)
                .OfType<EconomyEventDto>()
                .Where(e => (string.IsNullOrWhiteSpace(type) || string.Equals(e.Type, type.Trim(), StringComparison.OrdinalIgnoreCase))
                    && (string.IsNullOrWhiteSpace(refType) || string.Equals(e.RefType, refType.Trim(), StringComparison.OrdinalIgnoreCase))
                    && (refId is null || e.RefId == refId))
                .Take(count)
                .Reverse()
                .ToList();
            return Results.Json(rows, ApiJsonContext.Default.ListEconomyEventDto);
        });
    }

    private static Task<IResult> AuditAsync(
        AuditEconomyRequest? body, HttpContext context, EconomyModule module, AdminSessions sessions, EconomyViews views)
    {
        string actor = Who(context);
        return OnActor(module, sessions, service =>
        {
            var report = module.RunAudit(actor, body?.Acknowledge == true);
            return Results.Json(views.Audit(report, service), ApiJsonContext.Default.AuditorReportDto);
        });
    }
}
