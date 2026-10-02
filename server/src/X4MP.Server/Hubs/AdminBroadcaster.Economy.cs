using System.Collections.Concurrent;
using System.Globalization;
using X4MP.Core.Economy;
using X4MP.Core.Events;
using X4MP.Proto;
using X4MP.Server.Api;
using X4MP.Server.Economy;
using WalletKind = X4MP.Core.Economy.WalletKind;

namespace X4MP.Server.Hubs;

/// <summary>
/// The economy topic of the admin hub (M1-E6). The economy service lives on the session actor; the broadcaster attaches to its change events when the
/// service appears and, as everywhere else, builds nothing while the topic has no subscribers (<see cref="PayloadsBuilt"/> is the spy). Wallet changes
/// are collected and sent once per wallet every <c>EconomyWalletIntervalMs</c>; transactions go out at once; loan and trade states are keyed, so a busy
/// connection keeps the latest state of each; events are limited to <c>EconomyEventsPerSecond</c>.
/// </summary>
public sealed partial class AdminBroadcaster
{
    private readonly ConcurrentDictionary<WalletId, WalletDto> _pendingWallets = new();
    private readonly Lock _economyGate = new();
    private EconomyService? _attached;
    private long _economyEventId;
    private long _economyEventWindow;
    private int _economyEventsInWindow;

    private void AttachEconomy()
    {
        _economy.ServiceStarted += OnEconomyStarted;
        if (_economy.Service is { } existing)
        {
            OnEconomyStarted(existing);
        }
    }

    private void DetachEconomy()
    {
        _economy.ServiceStarted -= OnEconomyStarted;
        lock (_economyGate)
        {
            Detach(_attached);
            _attached = null;
        }
    }

    private void OnEconomyStarted(EconomyService service)
    {
        lock (_economyGate)
        {
            if (ReferenceEquals(_attached, service))
            {
                return;
            }

            Detach(_attached);
            _attached = service;
            service.Ledger.Committed += OnLedgerCommitted;
            service.Ledger.LoanCommitted += OnLoanCommitted;
            service.TradeObserved += OnTradeObserved;
        }
    }

    private void Detach(EconomyService? service)
    {
        if (service is null)
        {
            return;
        }

        service.Ledger.Committed -= OnLedgerCommitted;
        service.Ledger.LoanCommitted -= OnLoanCommitted;
        service.TradeObserved -= OnTradeObserved;
    }

    // ------------------------------------------------------------------ handlers (actor thread)

    private void OnLedgerCommitted(LedgerCommit commit)
    {
        if (_subs.Count(HubTopic.Economy) == 0)
        {
            return;
        }

        foreach (var wallet in commit.Wallets)
        {
            if (wallet.Id.Kind != WalletKind.World)
            {
                Built("wallet");
                _pendingWallets[wallet.Id] = _economyViews.Wallet(wallet);
            }
        }

        if (commit.Transaction is { } tx)
        {
            var dto = _economyViews.Tx(tx);
            Built("ledger");
            PostTo(_subs.In(HubTopic.Economy), c => c.LedgerPosted(dto));
        }
    }

    private void OnLoanCommitted(LoanRecord loan, LoanRecord? previous)
    {
        _ = previous;
        if (_subs.Count(HubTopic.Economy) == 0)
        {
            return;
        }

        var dto = _economyViews.Loan(loan);
        Built("loan");
        PostTo(_subs.In(HubTopic.Economy), c => c.LoanChanged(dto), "loan:" + loan.Id.ToString(CultureInfo.InvariantCulture));
    }

    private void OnTradeObserved(TradeRecord trade, TradeState previous)
    {
        _ = previous;
        if (_subs.Count(HubTopic.Economy) == 0 || _attached is not { } service)
        {
            return;
        }

        var dto = _economyViews.Trade(trade, service);
        Built("trade");
        PostTo(_subs.In(HubTopic.Economy), c => c.TradeChanged(dto), "trade:" + trade.Id.ToString(CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------ events (bus thread)

    private void PushEconomyEvent(DomainEvent domainEvent)
    {
        if (!AllowEconomyEvent())
        {
            return;
        }

        var dto = EconomyViews.Live(domainEvent, -Interlocked.Increment(ref _economyEventId));
        if (dto is null)
        {
            return;
        }

        Built("economy-event");
        PostTo(_subs.In(HubTopic.Economy), c => c.EconomyEvent(dto));
    }

    private bool AllowEconomyEvent()
    {
        long now = _time.GetTimestamp();
        lock (_economyGate)
        {
            if (_economyEventWindow == 0 || _time.GetElapsedTime(_economyEventWindow, now) >= TimeSpan.FromSeconds(1))
            {
                _economyEventWindow = now;
                _economyEventsInWindow = 0;
            }

            return ++_economyEventsInWindow <= _options.CurrentValue.EconomyEventsPerSecond;
        }
    }

    // ------------------------------------------------------------------ timers

    private Task EconomyFlushAsync()
    {
        if (_pendingWallets.IsEmpty)
        {
            return Task.CompletedTask;
        }

        var clients = _subs.In(HubTopic.Economy).ToList();
        foreach (var id in _pendingWallets.Keys)
        {
            if (_pendingWallets.TryRemove(id, out var dto) && clients.Count > 0)
            {
                PostTo(clients, c => c.WalletChanged(dto), "wallet:" + id);
            }
        }

        return Task.CompletedTask;
    }

    private async Task EconomySummaryTickAsync()
    {
        if (_subs.Count(HubTopic.Economy) == 0)
        {
            return;
        }

        var summary = await _actor.CallAsync(() => _economy.Service is { } service
            ? _economyViews.Summary(service, _economy.Auditor)
            : _economyViews.EmptySummary()).ConfigureAwait(false);
        Built("economy-summary");
        PostTo(_subs.In(HubTopic.Economy), c => c.EconomySummary(summary), "economy-summary");
    }
}
