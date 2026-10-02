using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using X4MP.Core.Economy;
using X4MP.Core.Events;
using X4MP.Core.Teams;
using X4MP.Persistence;
using X4MP.Server.Api;

namespace X4MP.Server.Economy;

/// <summary>
/// Maps economy domain objects to the admin API DTOs (REST and hub), resolving wallet owners to names. Reads of live economy objects must happen on the
/// actor thread; the name lookups only use short-lived database connections and the team directory.
/// </summary>
internal sealed class EconomyViews(
    SqliteAdminQueries queries, ITeamDirectory? teams, IOptionsMonitor<EconomyOptions> options, TimeProvider time)
{
    private static readonly string[] EventTypes =
        ["LoanStateChanged", "TradeStateChanged", "EconomyActionCompleted", "EconomyMigrated", "EconomyFrozen", "EconomyUnfrozen"];

    private readonly ConcurrentDictionary<long, string> _players = new();

    public EconomyOptions Options => options.CurrentValue;

    public string PlayerName(long id)
    {
        if (_players.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var found = queries.FindPlayer(id, time.GetUtcNow())?.Name;
        if (found is null)
        {
            return "Player " + id.ToString(CultureInfo.InvariantCulture);
        }

        _players[id] = found;
        return found;
    }

    public string TeamName(long id) =>
        teams?.Teams.FirstOrDefault(t => t.TeamId == id)?.Name ?? "Team " + id.ToString(CultureInfo.InvariantCulture);

    public string WalletName(WalletId id) => id.Kind switch
    {
        WalletKind.Player => PlayerName(id.OwnerId),
        WalletKind.TeamShared => TeamName(id.OwnerId) + " (shared)",
        WalletKind.TeamPool => TeamName(id.OwnerId) + " (pool)",
        WalletKind.Escrow when id.OwnerId >= TradeRecord.EscrowBase => "Trade #" + (id.OwnerId - TradeRecord.EscrowBase).ToString(CultureInfo.InvariantCulture) + " escrow",
        WalletKind.Escrow when id.OwnerId >= LoanWallets.EscrowOffset => "Loan #" + (id.OwnerId - LoanWallets.EscrowOffset).ToString(CultureInfo.InvariantCulture) + " escrow",
        WalletKind.Escrow => "Escrow " + id.OwnerId.ToString(CultureInfo.InvariantCulture),
        _ => "World",
    };

    public WalletDto Wallet(WalletState wallet) => new(
        wallet.Id.Kind.ToString(), wallet.Id.OwnerId, WalletName(wallet.Id), wallet.Balance, wallet.Frozen, wallet.FrozenReason, wallet.Overdrawn, wallet.Version);

    public LedgerTxDto Tx(LedgerTransaction tx) => new(
        tx.Id, tx.At, tx.Kind.ToString(), tx.Actor, tx.RequestId, tx.RefType, tx.RefId, tx.Reverses, tx.ReversedBy, tx.Note,
        [.. tx.Entries.Select(e => new LedgerEntryDto(e.Wallet.Kind.ToString(), e.Wallet.OwnerId, WalletName(e.Wallet), e.Amount, e.BalanceAfter))]);

    public LoanDto Loan(LoanRecord loan) => new(
        loan.Id, loan.LenderId, PlayerName(loan.LenderId), loan.BorrowerId, PlayerName(loan.BorrowerId), loan.Principal, loan.InterestBasisPoints,
        loan.RepayTotal, loan.Outstanding, loan.Repaid, loan.Forgiven, loan.AutoRepayPercent, loan.DueInSeconds, loan.DueAt, loan.State.ToString(),
        loan.State == LoanState.Overdue, loan.CreatedAt, loan.OfferExpiresAt, loan.AcceptedAt, loan.ClosedAt, loan.CloseReason, loan.Memo);

    public TradeOfferDto Trade(TradeRecord trade, EconomyService service) => new(
        trade.Id, trade.Initiator, PlayerName(trade.Initiator), trade.Counterparty, PlayerName(trade.Counterparty),
        [.. trade.InitiatorGives.Select(Item)], [.. trade.CounterpartyGives.Select(Item)], trade.State.ToString(), trade.Version, trade.ExpiresAt,
        trade.QueryAttempts, service.Ledger.BalanceOf(trade.EscrowWallet),
        trade.Reason == X4MP.Proto.EconomyReject.None ? null : trade.Reason.ToString(), trade.Detail, trade.ResolvedBy, trade.Reversed,
        trade.CreatedAt, trade.UpdatedAt, trade.Memo);

    private static TradeItemDto Item(TradeItemModel item) => new(item.Kind.ToString(), item.Amount, item.WareRef, item.Asset);

    public MigrationPreviewDto Preview(MigrationPreview preview) => new(
        preview.Needed, preview.RequiresConfirm, preview.From.ToString(), preview.To.ToString(), preview.Kind.ToString(),
        [.. preview.Changes.Select(c => new WalletPreviewDto(c.Wallet.Kind.ToString(), c.Wallet.OwnerId, WalletName(c.Wallet), c.Before, c.After))],
        preview.TotalMoved, [.. preview.TeamMoves]);

    public AuditorReportDto Audit(AuditReport? report, EconomyService service) => report is null
        ? new AuditorReportDto(time.GetUtcNow(), true, service.Ledger.IsFrozen, [])
        : new AuditorReportDto(report.At, report.Ok, service.Ledger.IsFrozen, [.. report.Violations]);

    public EconomySummaryDto Summary(EconomyService service, EconomyAuditor? auditor)
    {
        var totals = service.Totals();
        var configured = options.CurrentValue.CreditMode;
        return new EconomySummaryDto(
            configured.ToString(), service.DesiredMode.ToString(), service.AppliedMode.ToString(), service.MigrationPending, totals.MoneySupply,
            totals.InEscrow, totals.OpenLoans, totals.OverdueLoans, totals.OutstandingDebt, totals.OpenTrades, totals.InDoubtTrades, totals.FrozenWallets,
            service.Ledger.IsFrozen, service.Ledger.FreezeReason, Audit(auditor?.LastReport, service));
    }

    /// <summary>The summary before any session exists: nothing booked, nothing frozen.</summary>
    public EconomySummaryDto EmptySummary()
    {
        var configured = options.CurrentValue.CreditMode;
        var effective = EconomyService.Resolve(configured, teams?.Teams.Count ?? 1).ToString();
        return new EconomySummaryDto(
            configured.ToString(), effective, effective, false, 0, 0, 0, 0, 0, 0, 0, 0, false, null, new AuditorReportDto(time.GetUtcNow(), true, false, []));
    }

    public EconomyPolicyDto Policy(EconomyService? service)
    {
        var o = options.CurrentValue;
        var effective = service?.DesiredMode ?? EconomyService.Resolve(o.CreditMode, teams?.Teams.Count ?? 1);
        return new EconomyPolicyDto(
            o.CreditMode.ToString(), effective.ToString(), o.StartingCredits, o.TeamPoolEnabled, o.PoolWithdrawPolicy.ToString(),
            o.PoolWithdrawDailyLimitPerPlayer, o.SharedWalletSpend.ToString(), o.DonateScope.ToString(), o.AllowAlliedTransfers, o.LoanScope.ToString(),
            o.TradeScope.ToString(), o.TradeShipsEnabled, o.MaxOpenTradesPerPlayer, o.TradeRequiresProximity, o.TradeExecuteTimeoutSeconds,
            o.TradeQueryIntervalSeconds, o.MaxOpenLoansPerPlayer, o.MaxLoanPrincipal, o.MaxLoanInterestBp, o.OfferDefaultTtlMinutes, o.MaxSingleTransfer,
            o.AuditIntervalSeconds);
    }

    // ------------------------------------------------------------------ events

    /// <summary>True for the domain events that belong in the economy event log.</summary>
    public static bool IsEconomyEvent(DomainEvent e) =>
        e is LoanStateChanged or TradeStateChanged or EconomyActionCompleted or EconomyMigrated or EconomyFrozen or EconomyUnfrozen
        || (e is AdminActionTaken a && a.Action.StartsWith("economy.", StringComparison.Ordinal));

    /// <summary>A live event for the hub (null when it is not an economy event). <paramref name="id"/> is a local number: live events have no database id yet.</summary>
    public static EconomyEventDto? Live(DomainEvent e, long id) => e switch
    {
        LoanStateChanged l => new(id, l.At, nameof(LoanStateChanged), "system", "loan", l.LoanId, l.From?.ToString(), l.To.ToString(), l.Reason),
        TradeStateChanged t => new(id, t.At, nameof(TradeStateChanged), t.Actor, "trade", t.TradeId, t.From.ToString(), t.To.ToString(),
            t.Detail ?? (t.Reason == X4MP.Proto.EconomyReject.None ? null : t.Reason.ToString())),
        EconomyActionCompleted a => new(id, a.At, nameof(EconomyActionCompleted), "player:" + a.FromPlayer.ToString(CultureInfo.InvariantCulture), "tx", null, null, null,
            a.Kind + " " + a.Amount.ToString(CultureInfo.InvariantCulture)),
        EconomyMigrated m => new(id, m.At, nameof(EconomyMigrated), m.Actor, "tx", null, m.From, m.To, "moved " + m.TotalMoved.ToString(CultureInfo.InvariantCulture)),
        EconomyFrozen f => new(id, f.At, nameof(EconomyFrozen), "system", null, null, null, null, f.Reason),
        EconomyUnfrozen u => new(id, u.At, nameof(EconomyUnfrozen), u.Actor, null, null, null, null, null),
        AdminActionTaken a when a.Action.StartsWith("economy.", StringComparison.Ordinal) => FromAdmin(id, a.At, a.Actor, a.Action, a.Target,
            a.Data is not null && a.Data.TryGetValue("reason", out var reason) ? reason : null),
        _ => null,
    };

    /// <summary>A stored event (<c>session_events</c> row) as a DTO (null when it does not parse).</summary>
    public static EconomyEventDto? FromRow(SessionEventRecord row)
    {
        if (row.DataJson is null || !EventTypes.Contains(row.Type, StringComparer.Ordinal) && row.Type != nameof(AdminActionTaken))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(row.DataJson);
            var json = doc.RootElement;
            string? Text(string name) =>
                json.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;
            long? Number(string name) =>
                json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

            return row.Type switch
            {
                nameof(LoanStateChanged) => new(row.Id, row.At, row.Type, "system", "loan", Number("loanId"), Text("from"), Text("to"), Text("reason")),
                nameof(TradeStateChanged) => new(row.Id, row.At, row.Type, Text("actor") ?? "system", "trade", Number("tradeId"), Text("from"), Text("to"),
                    Text("detail") ?? (Text("reason") is { } r && r != nameof(X4MP.Proto.EconomyReject.None) ? r : null)),
                nameof(EconomyActionCompleted) => new(row.Id, row.At, row.Type, "player:" + Text("fromPlayer"), "tx", null, null, null, Text("kind") + " " + Text("amount")),
                nameof(EconomyMigrated) => new(row.Id, row.At, row.Type, Text("actor") ?? "system", "tx", null, Text("from"), Text("to"), "moved " + Text("totalMoved")),
                nameof(EconomyFrozen) => new(row.Id, row.At, row.Type, "system", null, null, null, null, Text("reason")),
                nameof(EconomyUnfrozen) => new(row.Id, row.At, row.Type, Text("actor") ?? "system", null, null, null, null, null),
                _ => FromAdmin(
                    row.Id, row.At, Text("actor") ?? "unknown", Text("action") ?? row.Type, Text("target"),
                    json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("reason", out var reason)
                        && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static EconomyEventDto FromAdmin(long id, DateTimeOffset at, string actor, string action, string? target, string? reason)
    {
        string? refType = null;
        long? refId = null;
        if (target is not null)
        {
            var colon = target.IndexOf(':', StringComparison.Ordinal);
            refType = colon < 0 ? target : target[..colon];
            if (colon >= 0 && long.TryParse(target[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                refId = number;
            }
        }

        return new EconomyEventDto(id, at, action, actor, refType, refId, null, null, reason);
    }
}
