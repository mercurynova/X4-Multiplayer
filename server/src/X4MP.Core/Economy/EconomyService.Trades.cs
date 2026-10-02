using System.Globalization;
using X4MP.Core.Events;
using X4MP.Proto;

namespace X4MP.Core.Economy;

/// <summary>
/// Escrowed trades (M1-E5; server-design 2.14, protocol.md 15.6): proposal, counter and accept on one version, credit escrow when both
/// sides accepted, asset locks, one <c>AssetTransferOrder</c> per trade to the authority, settle-once on <c>AssetTransferConfirm</c>,
/// the 30 s timeout with <c>TradeQuery</c> x3 then <c>InDoubt</c>, admin resolve and cancel, rollback. Runs on the actor thread.
/// </summary>
public sealed partial class EconomyService
{
    /// <summary>Queries a silent authority gets before the trade is declared InDoubt (design: "up to 3 times").</summary>
    public const int MaxTradeQueries = 3;

    private const int MaxTradeItemsPerSide = 8;
    private const uint DefaultTradeTtlSeconds = 300;
    private const uint MinTradeTtlSeconds = 10;
    private const uint MaxTradeTtlSeconds = 86_400;
    private const int RecentTerminalTrades = 200;

    private readonly Dictionary<long, TradeRecord> _trades = [];
    private readonly Dictionary<uint, long> _tradeLocks = [];
    private readonly Dictionary<(int Player, string Key), (string Hash, long Trade)> _tradeRequests = [];
    private long _nextTradeId = 1;

    /// <summary>Where trades persist; null keeps them in memory only.</summary>
    public ITradeStore? TradeStore { get; set; }

    /// <summary>The world mirror view; without it nothing that names an asset can be validated, so such trades are refused.</summary>
    public ITradeWorld? TradeWorld { get; set; }

    /// <summary>Hands an <c>AssetTransferOrder</c> to the authority; false when it cannot be sent (no authority connected).</summary>
    public Func<AssetTransferOrderT, bool>? SendTransferOrder { get; set; }

    /// <summary>Sends a <c>TradeQuery</c> to the authority; false when it cannot be sent.</summary>
    public Func<long, bool>? SendTradeQuery { get; set; }

    /// <summary>True while an authority is connected and can take an order. Default: true when <see cref="SendTransferOrder"/> is set.</summary>
    public Func<bool>? AuthorityOnline { get; set; }

    /// <summary>Raised after every change of a trade (state, version, acceptance): the module sends <c>TradeStatus</c> and, for final states, <c>TradeResult</c>. Arguments: the trade and its previous state.</summary>
    public Action<TradeRecord, TradeState>? TradeChanged { get; set; }

    /// <summary>Raised after every change of a trade, next to <see cref="TradeChanged"/> but multicast (the admin hub observes here). Handlers must be quick and must not throw.</summary>
    public event Action<TradeRecord, TradeState>? TradeObserved;

    private void RaiseTradeChanged(TradeRecord trade, TradeState previous)
    {
        TradeChanged?.Invoke(trade, previous);
        try
        {
            TradeObserved?.Invoke(trade, previous);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex; // an observer must never break the trade engine
        }
    }

    /// <summary>Confirmations that arrived for a trade that was not waiting for one (duplicates and late ones): ignored.</summary>
    public long DuplicateConfirms { get; private set; }

    /// <summary>Confirmations for a trade id the server does not know.</summary>
    public long UnknownConfirms { get; private set; }

    /// <summary>Every trade the engine holds (open ones and the most recent final ones).</summary>
    public IReadOnlyCollection<TradeRecord> Trades => _trades.Values;

    public TradeRecord? FindTrade(long id) => _trades.GetValueOrDefault(id);

    /// <summary>The trade that locks the asset, or null.</summary>
    public long? LockHolder(uint netId) => _tradeLocks.TryGetValue(netId, out var id) ? id : null;

    public bool IsAssetLocked(uint netId) => _tradeLocks.ContainsKey(netId);

    /// <summary>Open trades a player is a party to.</summary>
    public int OpenTradesOf(int player) => _trades.Values.Count(t => t.IsOpen && (t.Initiator == player || t.Counterparty == player));

    // ------------------------------------------------------------------ start and recovery

    /// <summary>
    /// Loads the stored trades and rebuilds the lock and request indexes. A trade that was caught between the escrow posting and its
    /// own save (still negotiating while its escrow wallet holds credits) is rolled back; transferring trades are queried when the
    /// authority is back.
    /// </summary>
    public void LoadTrades()
    {
        _trades.Clear();
        _tradeLocks.Clear();
        _tradeRequests.Clear();
        if (TradeStore is not { } store)
        {
            return;
        }

        foreach (var trade in store.Load(_ledger.SessionId, RecentTerminalTrades))
        {
            _trades[trade.Id] = trade;
            IndexRequests(trade);
            if (trade.IsOpen)
            {
                foreach (var asset in trade.LockedAssets)
                {
                    _tradeLocks[asset] = trade.Id;
                }
            }
        }

        _nextTradeId = Math.Max(store.MaxId(_ledger.SessionId), _trades.Keys.DefaultIfEmpty(0).Max()) + 1;
        foreach (var trade in _trades.Values.Where(t => t.IsOpen && t.State is not (TradeState.Transferring or TradeState.InDoubt)).ToList())
        {
            if (_ledger.BalanceOf(trade.EscrowWallet) > 0)
            {
                RollBack(trade, EconomyReject.Timeout, "system", "the server restarted while the trade was being accepted");
            }
        }
    }

    private void IndexRequests(TradeRecord trade)
    {
        foreach (var mark in trade.Requests)
        {
            _tradeRequests[(mark.Player, mark.Key)] = (mark.Hash, trade.Id);
        }
    }

    // ------------------------------------------------------------------ requests

    /// <summary>
    /// <c>TradeProposal</c>: validates everything (scope, shape, ownership, locks, proximity, limits) and opens version 1. The proposer
    /// accepts its own proposal implicitly. Nothing is escrowed before both sides accepted.
    /// </summary>
    public TradeActionResult ProposeTrade(
        int player, string requestKey, int counterparty, IReadOnlyList<TradeItemModel> give, IReadOnlyList<TradeItemModel> want, uint ttlSeconds, string? memo)
    {
        ArgumentNullException.ThrowIfNull(give);
        ArgumentNullException.ThrowIfNull(want);
        var hash = Hex(PayloadHasher.Hash(
            "TradeProposal", counterparty, string.Join(',', give.Select(i => i.Canonical())), string.Join(',', want.Select(i => i.Canonical())), ttlSeconds, memo ?? string.Empty));
        var replay = ReplayTrade(player, requestKey, hash);
        if (replay is not null)
        {
            return replay;
        }

        if (_ledger.IsFrozen)
        {
            return TradeActionResult.Rejected(EconomyReject.EconomyFrozen, _ledger.FreezeReason);
        }

        var options = _options();
        var now = _time.GetUtcNow();
        var ttl = Math.Clamp(ttlSeconds == 0 ? DefaultTradeTtlSeconds : ttlSeconds, MinTradeTtlSeconds, MaxTradeTtlSeconds);
        var trade = new TradeRecord
        {
            Id = _nextTradeId,
            Initiator = player,
            Counterparty = counterparty,
            Version = 1,
            State = TradeState.Proposed,
            InitiatorGives = [.. give],
            CounterpartyGives = [.. want],
            InitiatorAccepted = 1,
            Memo = memo is { Length: > MaxMemoLength } ? memo[..MaxMemoLength] : memo,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now + TimeSpan.FromSeconds(ttl),
        };

        var rejected = ValidateTrade(trade, trade.InitiatorReceiveInto, trade.CounterpartyReceiveInto, checkOutgoing: false);
        if (rejected is not null)
        {
            return rejected;
        }

        foreach (var party in new[] { player, counterparty })
        {
            if (OpenTradesOf(party) >= options.MaxOpenTradesPerPlayer)
            {
                return TradeActionResult.Rejected(
                    EconomyReject.TooManyOpen, party == player ? "you have too many open trades" : "the counterparty has too many open trades");
            }
        }

        trade.Requests.Add(new TradeRequestMark(player, requestKey, hash));
        var saved = Persist(trade);
        if (saved is not null)
        {
            return saved;
        }

        _nextTradeId++;
        _trades[trade.Id] = trade;
        IndexRequests(trade);
        LockAll(trade);
        PublishTrade(trade, TradeState.Proposed, TradeState.Proposed, EconomyReject.None, Actor(player), "proposed");
        RaiseTradeChanged(trade, TradeState.Proposed);
        return new TradeActionResult(EconomyReject.None, null, trade);
    }

    /// <summary>
    /// <c>TradeCounter</c>: replaces both item lists of the trade as seen from the sender, bumps the version, and resets the acceptances
    /// (the sender accepts its own counter). A <paramref name="baseVersion"/> that is not the current version is <see cref="EconomyReject.StaleVersion"/>.
    /// </summary>
    public TradeActionResult CounterTrade(
        int player, string requestKey, long tradeId, uint baseVersion, IReadOnlyList<TradeItemModel> give, IReadOnlyList<TradeItemModel> want)
    {
        ArgumentNullException.ThrowIfNull(give);
        ArgumentNullException.ThrowIfNull(want);
        var hash = Hex(PayloadHasher.Hash(
            "TradeCounter", tradeId, baseVersion, string.Join(',', give.Select(i => i.Canonical())), string.Join(',', want.Select(i => i.Canonical()))));
        var replay = ReplayTrade(player, requestKey, hash);
        if (replay is not null)
        {
            return replay;
        }

        var gate = PartyGate(player, tradeId, out var trade);
        if (gate is not null)
        {
            return gate;
        }

        if (!trade!.IsNegotiating)
        {
            return TradeActionResult.Rejected(EconomyReject.WrongState, trade.State.ToString(), trade);
        }

        if (baseVersion != trade.Version)
        {
            return TradeActionResult.Rejected(EconomyReject.StaleVersion, $"the trade is at version {trade.Version}", trade);
        }

        var candidate = trade.Clone();
        candidate.Version = trade.Version + 1;
        candidate.State = TradeState.Countered;
        candidate.UpdatedAt = _time.GetUtcNow();
        candidate.InitiatorReceiveInto = candidate.CounterpartyReceiveInto = 0;
        candidate.InitiatorGives = [.. player == trade.Initiator ? give : want];
        candidate.CounterpartyGives = [.. player == trade.Initiator ? want : give];
        candidate.InitiatorAccepted = player == trade.Initiator ? candidate.Version : 0;
        candidate.CounterpartyAccepted = player == trade.Counterparty ? candidate.Version : 0;
        var rejected = ValidateTrade(candidate, 0, 0, checkOutgoing: false);
        if (rejected is not null)
        {
            return TradeActionResult.Rejected(rejected.Reason, rejected.Detail, trade);
        }

        candidate.Requests.Add(new TradeRequestMark(player, requestKey, hash));
        var previous = trade.State;
        var saved = Persist(candidate);
        if (saved is not null)
        {
            return TradeActionResult.Rejected(saved.Reason, saved.Detail, trade);
        }

        Unlock(trade);
        ApplyInto(trade, candidate);
        LockAll(trade);
        IndexRequests(trade);
        PublishTrade(trade, previous, trade.State, EconomyReject.None, Actor(player), "countered");
        RaiseTradeChanged(trade, previous);
        return new TradeActionResult(EconomyReject.None, null, trade);
    }

    /// <summary>
    /// <c>TradeAccept</c> of exactly <paramref name="version"/> (a stale one is rejected). The first acceptance is recorded; the one that
    /// completes the pair revalidates everything, escrows the credits and sends the order to the authority.
    /// </summary>
    public TradeActionResult AcceptTrade(int player, string requestKey, long tradeId, uint version, uint receiveInto)
    {
        var hash = Hex(PayloadHasher.Hash("TradeAccept", tradeId, version, receiveInto));
        var replay = ReplayTrade(player, requestKey, hash);
        if (replay is not null)
        {
            return replay;
        }

        var gate = PartyGate(player, tradeId, out var trade);
        if (gate is not null)
        {
            return gate;
        }

        if (!trade!.IsNegotiating)
        {
            return TradeActionResult.Rejected(EconomyReject.WrongState, trade.State.ToString(), trade);
        }

        if (version != trade.Version)
        {
            return TradeActionResult.Rejected(EconomyReject.StaleVersion, $"the trade is at version {trade.Version}", trade);
        }

        if (_ledger.IsFrozen)
        {
            return TradeActionResult.Rejected(EconomyReject.EconomyFrozen, _ledger.FreezeReason, trade);
        }

        var asInitiator = player == trade.Initiator;
        var otherAccepted = trade.AcceptedBy(trade.Other(player)) == trade.Version;
        var initiatorInto = asInitiator ? receiveInto : trade.InitiatorReceiveInto;
        var counterInto = asInitiator ? trade.CounterpartyReceiveInto : receiveInto;

        if (!otherAccepted)
        {
            // Only the acceptance is recorded: the trade moves when the other side accepts this version.
            var rejectedFirst = ValidateTrade(trade, initiatorInto, counterInto, checkOutgoing: false);
            if (rejectedFirst is not null)
            {
                return rejectedFirst with { Trade = trade };
            }

            var previous = trade.State;
            SetAccepted(trade, player, version, receiveInto);
            trade.Requests.Add(new TradeRequestMark(player, requestKey, hash));
            if (Persist(trade) is { } failed)
            {
                return failed with { Trade = trade };
            }

            IndexRequests(trade);
            PublishTrade(trade, previous, trade.State, EconomyReject.None, Actor(player), "accepted");
            RaiseTradeChanged(trade, previous);
            return new TradeActionResult(EconomyReject.None, null, trade);
        }

        return Execute(trade, player, requestKey, hash, version, receiveInto, initiatorInto, counterInto);
    }

    /// <summary>
    /// <c>TradeCancel</c> before anything was escrowed. The initiator withdraws (<see cref="TradeState.Cancelled"/>), the counterparty
    /// declines (<see cref="TradeState.Rejected"/>).
    /// </summary>
    public TradeActionResult CancelTrade(int player, string requestKey, long tradeId)
    {
        var hash = Hex(PayloadHasher.Hash("TradeCancel", tradeId));
        var replay = ReplayTrade(player, requestKey, hash);
        if (replay is not null)
        {
            return replay;
        }

        var gate = PartyGate(player, tradeId, out var trade);
        if (gate is not null)
        {
            return gate;
        }

        if (!trade!.IsNegotiating)
        {
            return TradeActionResult.Rejected(EconomyReject.WrongState, trade.State.ToString(), trade);
        }

        trade.Requests.Add(new TradeRequestMark(player, requestKey, hash));
        IndexRequests(trade);
        var asInitiator = player == trade.Initiator;
        Transition(trade, asInitiator ? TradeState.Cancelled : TradeState.Rejected, EconomyReject.None, Actor(player), asInitiator ? "withdrawn" : "declined");
        return new TradeActionResult(EconomyReject.None, null, trade);
    }

    // ------------------------------------------------------------------ execution

    private TradeActionResult Execute(
        TradeRecord trade, int accepter, string requestKey, string hash, uint version, uint receiveInto, uint initiatorInto, uint counterInto)
    {
        var rejected = ValidateTrade(trade, initiatorInto, counterInto, checkOutgoing: true);
        if (rejected is not null)
        {
            if (EndsTrade(rejected.Reason))
            {
                Transition(trade, TradeState.Rejected, rejected.Reason, Actor(accepter), rejected.Detail);
            }

            return rejected with { Trade = trade };
        }

        if (SendTransferOrder is null || AuthorityOnline?.Invoke() == false)
        {
            return TradeActionResult.Rejected(EconomyReject.AuthorityUnavailable, "no authority is connected", trade);
        }

        var payer = PayerOf(trade);
        var amount = CreditsOf(trade);
        var previous = trade.State;
        PostOutcome? escrow = null;
        if (amount > 0)
        {
            escrow = _ledger.Post(new PostRequest
            {
                Kind = TxKind.TradeEscrow,
                Actor = Actor(accepter),
                PlayerId = accepter,
                RequestId = requestKey,
                RequestType = "TradeAccept",
                PayloadHash = PayloadHasher.Hash("TradeAccept", trade.Id, version, receiveInto),
                Entries = [new(EffectiveWallet(payer), -amount), new(trade.EscrowWallet, amount)],
                RefType = "trade",
                RefId = trade.Id,
            });
            if (!escrow.Ok)
            {
                var reason = Finish(escrow, LedgerReason.TradeEscrow, requestKey, null);
                return TradeActionResult.Rejected(reason.Reason, reason.Detail, trade);
            }

            Notify(escrow, LedgerReason.TradeEscrow, TradeWireId(trade.Id), null);
        }

        SetAccepted(trade, accepter, version, receiveInto);
        trade.Payer = amount > 0 ? payer : 0;
        trade.EscrowAmount = amount;
        trade.State = TradeState.Transferring;
        var now = _time.GetUtcNow();
        trade.SentAt = now;
        trade.NextCheckAt = now + TimeSpan.FromSeconds(_options().TradeExecuteTimeoutSeconds);
        trade.QueryAttempts = 0;
        trade.UpdatedAt = now;
        trade.Requests.Add(new TradeRequestMark(accepter, requestKey, hash));
        Persist(trade);
        IndexRequests(trade);
        PublishTrade(trade, previous, TradeState.Transferring, EconomyReject.None, Actor(accepter), "transferring");

        var order = BuildOrder(trade);
        if (!SendTransferOrder(order))
        {
            RollBack(trade, EconomyReject.AuthorityUnavailable, "system", "the order could not be sent to the authority");
            return TradeActionResult.Rejected(EconomyReject.AuthorityUnavailable, "the authority went away", trade);
        }

        RaiseTradeChanged(trade, previous);
        return new TradeActionResult(EconomyReject.None, null, trade, escrow);
    }

    private AssetTransferOrderT BuildOrder(TradeRecord trade)
    {
        var lines = new List<AssetTransferLineT>();
        var ships = new List<AssetTransferLineT>();
        foreach (var (giver, gives) in new[] { (trade.Initiator, trade.InitiatorGives), (trade.Counterparty, trade.CounterpartyGives) })
        {
            var receiver = trade.Other(giver);
            var team = (ushort)(LiveTeamOf(receiver) ?? 0);
            foreach (var item in gives.Where(i => i.IsAsset))
            {
                if (item.Kind == TradeItemKind.Ware)
                {
                    lines.Add(new AssetTransferLineT
                    {
                        Kind = AssetTransferKind.WareMove,
                        Asset = item.Asset,
                        ToTeam = team,
                        ToPlayer = (ushort)receiver,
                        WareRef = item.WareRef,
                        Amount = item.Amount,
                        DestAsset = ResolveDestination(trade, receiver),
                    });
                }
                else
                {
                    ships.Add(new AssetTransferLineT { Kind = AssetTransferKind.OwnerChange, Asset = item.Asset, ToTeam = team, ToPlayer = (ushort)receiver });
                }
            }
        }

        // Cargo moves first, then the ownership changes (a ship that gives cargo and changes hands is emptied while it is still the giver's).
        lines.AddRange(ships);
        var timeoutMs = (long)_options().TradeExecuteTimeoutSeconds * 500;
        return new AssetTransferOrderT { TradeId = TradeWireId(trade.Id), Lines = lines, DeadlineMs = (uint)Math.Clamp(timeoutMs, 1000, 15000) };
    }

    private uint ResolveDestination(TradeRecord trade, int receiver)
    {
        var into = receiver == trade.Initiator ? trade.InitiatorReceiveInto : trade.CounterpartyReceiveInto;
        if (into != 0)
        {
            return into;
        }

        return TradeWorld is { } world && world.TryGetPlayerShip(receiver, out var ship, out _) ? ship : 0;
    }

    // ------------------------------------------------------------------ authority answers

    /// <summary>
    /// Applies an <c>AssetTransferConfirm</c>. Only a trade that is Transferring (or InDoubt, a late answer) reacts, exactly once: a
    /// duplicate does nothing. Success settles (credits to the payee, ownership into the mirror); failure refunds and unlocks.
    /// </summary>
    public TradeConfirmOutcome OnAssetTransferConfirm(AssetTransferConfirmT confirm)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        var id = confirm.TradeId is { Hi: 0 } wire ? (long)wire.Lo : -1;
        if (!_trades.TryGetValue(id, out var trade))
        {
            UnknownConfirms++;
            return TradeConfirmOutcome.UnknownTrade;
        }

        if (trade.State is not (TradeState.Transferring or TradeState.InDoubt))
        {
            DuplicateConfirms++;
            return TradeConfirmOutcome.Ignored;
        }

        if (confirm.Ok)
        {
            return Settle(trade, "authority", null) ? TradeConfirmOutcome.Settled : TradeConfirmOutcome.InDoubt;
        }

        var error = string.IsNullOrEmpty(confirm.Error) ? "the authority refused the transfer" : confirm.Error;
        if (!confirm.Compensated && confirm.FailedLine > 0)
        {
            // Earlier lines were applied and not undone: neither refund nor settle is safe.
            Park(trade, EconomyReject.AuthorityRejected, "authority", $"partially applied (failed at line {confirm.FailedLine}, not compensated): {error}");
            return TradeConfirmOutcome.InDoubt;
        }

        return RollBack(trade, EconomyReject.AuthorityRejected, "authority", error) ? TradeConfirmOutcome.RolledBack : TradeConfirmOutcome.InDoubt;
    }

    /// <summary>The authority connected (or came back): every trade that waits for it is asked about (idempotent on its side).</summary>
    public void OnAuthorityAttached()
    {
        foreach (var trade in _trades.Values.Where(t => t.State is TradeState.Transferring or TradeState.InDoubt).OrderBy(t => t.Id).ToList())
        {
            SendTradeQuery?.Invoke(trade.Id);
        }
    }

    /// <summary>The authority left for good: whatever it was asked to do is now of unknown outcome.</summary>
    public void OnAuthorityGone()
    {
        foreach (var trade in _trades.Values.Where(t => t.State == TradeState.Transferring).OrderBy(t => t.Id).ToList())
        {
            Park(trade, EconomyReject.AuthorityUnavailable, "system", "the authority left before it confirmed the transfer");
        }
    }

    // ------------------------------------------------------------------ the clock

    /// <summary>
    /// Expires negotiating trades, re-checks their scope (a relation or setting may have changed) and walks transferring trades along the
    /// timeline: <c>TradeExecuteTimeoutSeconds</c> without a confirm, then up to three <c>TradeQuery</c>s one
    /// <c>TradeQueryIntervalSeconds</c> apart, then <see cref="TradeState.InDoubt"/>. Call it from the module tick.
    /// </summary>
    public void TickTrades()
    {
        if (_trades.Count == 0)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var options = _options();
        foreach (var trade in _trades.Values.Where(t => t.IsOpen).OrderBy(t => t.Id).ToList())
        {
            if (trade.IsNegotiating)
            {
                if (now >= trade.ExpiresAt)
                {
                    Transition(trade, TradeState.Expired, EconomyReject.Timeout, "system", "the offer expired");
                }
                else if (ScopeVerdict(options, trade.Initiator, trade.Counterparty) is { } scope)
                {
                    Transition(trade, TradeState.Cancelled, scope.Reason, "system", scope.Detail ?? "the trade scope no longer allows this trade");
                }

                continue;
            }

            if (trade.State != TradeState.Transferring || trade.NextCheckAt is not { } due || now < due)
            {
                continue;
            }

            if (trade.QueryAttempts >= MaxTradeQueries)
            {
                Park(trade, EconomyReject.Timeout, "system", $"no answer from the authority after {MaxTradeQueries} queries");
                continue;
            }

            trade.QueryAttempts++;
            trade.NextCheckAt = now + TimeSpan.FromSeconds(options.TradeQueryIntervalSeconds);
            trade.UpdatedAt = now;
            Persist(trade);
            SendTradeQuery?.Invoke(trade.Id);
        }
    }

    // ------------------------------------------------------------------ admin (REST arrives with M1-E6)

    /// <summary>Resolves an InDoubt trade by hand: <paramref name="complete"/> settles it (the authority did apply the transfer), otherwise it is refunded.</summary>
    public TradeActionResult AdminResolveTrade(long tradeId, bool complete, string actor, string? reason = null)
    {
        if (!_trades.TryGetValue(tradeId, out var trade))
        {
            return TradeActionResult.Rejected(EconomyReject.UnknownTrade);
        }

        if (trade.State != TradeState.InDoubt)
        {
            return TradeActionResult.Rejected(EconomyReject.WrongState, trade.State.ToString(), trade);
        }

        trade.ResolvedBy = actor;
        var note = $"resolved by {actor}: {(complete ? "completed" : "refunded")}{(string.IsNullOrWhiteSpace(reason) ? string.Empty : " (" + reason + ")")}";
        var done = complete ? Settle(trade, actor, note) : RollBack(trade, EconomyReject.None, actor, note);
        PublishAdmin(actor, complete ? "economy.trade.resolve_complete" : "economy.trade.resolve_refund", trade, reason);
        return done
            ? new TradeActionResult(EconomyReject.None, null, trade)
            : TradeActionResult.Rejected(EconomyReject.EconomyFrozen, trade.Detail, trade);
    }

    /// <summary>Cancels a trade that is still negotiating (nothing was escrowed). A transferring or InDoubt trade has to be resolved instead.</summary>
    public TradeActionResult AdminCancelTrade(long tradeId, string actor, string? reason = null)
    {
        if (!_trades.TryGetValue(tradeId, out var trade))
        {
            return TradeActionResult.Rejected(EconomyReject.UnknownTrade);
        }

        if (!trade.IsNegotiating)
        {
            return TradeActionResult.Rejected(EconomyReject.WrongState, trade.State + (trade.IsOpen ? ": resolve it instead" : string.Empty), trade);
        }

        trade.ResolvedBy = actor;
        Transition(trade, TradeState.Cancelled, EconomyReject.None, actor, string.IsNullOrWhiteSpace(reason) ? "cancelled by an admin" : reason);
        PublishAdmin(actor, "economy.trade.cancel", trade, reason);
        return new TradeActionResult(EconomyReject.None, null, trade);
    }

    private void PublishAdmin(string actor, string action, TradeRecord trade, string? reason) =>
        _events?.Publish(new AdminActionTaken(
            _time.GetUtcNow(), _ledger.SessionId, actor, action, "trade:" + trade.Id.ToString(CultureInfo.InvariantCulture),
            new Dictionary<string, string?> { ["state"] = trade.State.ToString(), ["reason"] = reason }, null));

    // ------------------------------------------------------------------ outcomes

    /// <summary>Releases the escrow to the payee, updates ownership, completes. False when the ledger refused (the trade goes InDoubt).</summary>
    private bool Settle(TradeRecord trade, string actor, string? note)
    {
        var escrowBalance = _ledger.BalanceOf(trade.EscrowWallet);
        if (escrowBalance > 0)
        {
            var payee = trade.Other(PayerOf(trade));
            var outcome = _ledger.Post(new PostRequest
            {
                Kind = TxKind.TradeSettle,
                Actor = actor,
                PlayerId = 0,
                RequestId = "trade-settle:" + trade.Id.ToString(CultureInfo.InvariantCulture),
                RequestType = "TradeSettle",
                PayloadHash = PayloadHasher.Hash("TradeSettle", trade.Id),
                Entries = [new(trade.EscrowWallet, -escrowBalance), new(EffectiveWallet(payee), escrowBalance)],
                RefType = "trade",
                RefId = trade.Id,
                Flags = PostOptions.BypassWalletFreeze,
            });
            if (!outcome.Ok)
            {
                Park(trade, EconomyReject.EconomyFrozen, actor, "the ledger refused the settlement: " + outcome.Detail);
                return false;
            }

            if (!outcome.Replayed)
            {
                Notify(outcome, LedgerReason.TradeSettle, TradeWireId(trade.Id), null);
            }
        }

        foreach (var (giver, gives) in new[] { (trade.Initiator, trade.InitiatorGives), (trade.Counterparty, trade.CounterpartyGives) })
        {
            var receiver = trade.Other(giver);
            foreach (var ship in gives.Where(i => i.Kind == TradeItemKind.Ship))
            {
                TradeWorld?.SetOwner(ship.Asset, LiveTeamOf(receiver) ?? 0, receiver, TradeWireId(trade.Id));
            }
        }

        Transition(trade, TradeState.Completed, EconomyReject.None, actor, note ?? "completed");
        return true;
    }

    /// <summary>Refunds the escrow to the payer, unlocks, ends in RolledBack. False when the ledger refused (the trade goes InDoubt).</summary>
    private bool RollBack(TradeRecord trade, EconomyReject reason, string actor, string? detail)
    {
        var escrowBalance = _ledger.BalanceOf(trade.EscrowWallet);
        if (escrowBalance > 0)
        {
            var payer = PayerOf(trade);
            var outcome = _ledger.Post(new PostRequest
            {
                Kind = TxKind.TradeRefund,
                Actor = actor,
                PlayerId = 0,
                RequestId = "trade-refund:" + trade.Id.ToString(CultureInfo.InvariantCulture),
                RequestType = "TradeRefund",
                PayloadHash = PayloadHasher.Hash("TradeRefund", trade.Id),
                Entries = [new(trade.EscrowWallet, -escrowBalance), new(EffectiveWallet(payer), escrowBalance)],
                RefType = "trade",
                RefId = trade.Id,
                Flags = PostOptions.BypassWalletFreeze,
            });
            if (!outcome.Ok)
            {
                Park(trade, EconomyReject.EconomyFrozen, actor, "the ledger refused the refund: " + outcome.Detail);
                return false;
            }

            if (!outcome.Replayed)
            {
                Notify(outcome, LedgerReason.TradeRollback, TradeWireId(trade.Id), null);
            }
        }

        Transition(trade, TradeState.RolledBack, reason, actor, detail);
        return true;
    }

    /// <summary>Marks a trade InDoubt (needs an admin, or a late answer from the authority) and raises an alert.</summary>
    private void Park(TradeRecord trade, EconomyReject reason, string actor, string detail)
    {
        if (trade.State == TradeState.InDoubt)
        {
            trade.Detail = detail;
            return;
        }

        Transition(trade, TradeState.InDoubt, reason, actor, detail);
        _events?.Publish(new AlertRaised(
            _time.GetUtcNow(), _ledger.SessionId, AlertSeverity.Warning, TradeAlertCode(trade),
            $"Trade {trade.Id.ToString(CultureInfo.InvariantCulture)} is in doubt: {detail}"));
    }

    private static string TradeAlertCode(TradeRecord trade) => "economy_trade_in_doubt_" + trade.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>Moves the trade to <paramref name="to"/> (releasing its locks when that is final), persists, audits and notifies.</summary>
    private void Transition(TradeRecord trade, TradeState to, EconomyReject reason, string actor, string? detail)
    {
        var from = trade.State;
        trade.State = to;
        trade.Reason = reason;
        trade.Detail = detail;
        trade.UpdatedAt = _time.GetUtcNow();
        if (from == TradeState.InDoubt && to != TradeState.InDoubt)
        {
            _events?.Publish(new AlertCleared(trade.UpdatedAt, _ledger.SessionId, TradeAlertCode(trade)));
        }

        if (!trade.IsOpen)
        {
            Unlock(trade);
        }

        Persist(trade);
        PublishTrade(trade, from, to, reason, actor, detail);
        RaiseTradeChanged(trade, from);
    }

    private void PublishTrade(TradeRecord trade, TradeState from, TradeState to, EconomyReject reason, string actor, string? detail) =>
        _events?.Publish(new TradeStateChanged(_time.GetUtcNow(), _ledger.SessionId, trade.Id, from, to, reason, actor, detail));

    // ------------------------------------------------------------------ validation

    private TradeActionResult? PartyGate(int player, long tradeId, out TradeRecord? trade)
    {
        if (!_trades.TryGetValue(tradeId, out trade))
        {
            return TradeActionResult.Rejected(EconomyReject.UnknownTrade);
        }

        return player == trade.Initiator || player == trade.Counterparty ? null : TradeActionResult.Rejected(EconomyReject.NotParty, "you are not a party to this trade");
    }

    /// <summary>The stored answer of a request key already processed (same payload) or <see cref="EconomyReject.RequestIdReuse"/>; null for a new key.</summary>
    private TradeActionResult? ReplayTrade(int player, string requestKey, string hash)
    {
        if (!_tradeRequests.TryGetValue((player, requestKey), out var known))
        {
            return null;
        }

        return known.Hash == hash
            ? new TradeActionResult(EconomyReject.None, null, _trades.GetValueOrDefault(known.Trade))
            : TradeActionResult.Rejected(EconomyReject.RequestIdReuse, "request id reused with a different payload");
    }

    /// <summary>Reasons that make the trade itself impossible (the accept turns it into Rejected); the others only refuse that one request.</summary>
    private static bool EndsTrade(EconomyReject reason) =>
        reason is EconomyReject.ScopeDenied or EconomyReject.ScopeDisabled or EconomyReject.AssetNotOwned or EconomyReject.AssetUnavailable
            or EconomyReject.SameWallet or EconomyReject.UnknownPlayer or EconomyReject.AmountInvalid or EconomyReject.OverMaxAmount;

    /// <summary>The checks of design 2.14 for a trade as it would run now; null = the trade is fine.</summary>
    private TradeActionResult? ValidateTrade(TradeRecord trade, uint initiatorInto, uint counterInto, bool checkOutgoing)
    {
        var options = _options();
        if (_ledger.IsFrozen)
        {
            return TradeActionResult.Rejected(EconomyReject.EconomyFrozen, _ledger.FreezeReason);
        }

        if (!IsKnown(trade.Initiator) || !IsKnown(trade.Counterparty))
        {
            return TradeActionResult.Rejected(EconomyReject.UnknownPlayer, IsKnown(trade.Initiator) ? "unknown counterparty" : null);
        }

        if (trade.Initiator == trade.Counterparty)
        {
            return TradeActionResult.Rejected(EconomyReject.NotParty, "a trade needs two different players");
        }

        var scope = ScopeVerdict(options, trade.Initiator, trade.Counterparty);
        if (scope is not null)
        {
            return scope;
        }

        if (EffectiveWallet(trade.Initiator) == EffectiveWallet(trade.Counterparty))
        {
            return TradeActionResult.Rejected(EconomyReject.SameWallet, "both players use the same wallet");
        }

        var shape = CheckShape(trade, options);
        if (shape is not null)
        {
            return shape;
        }

        var assets = CheckAssets(trade, options, initiatorInto, counterInto);
        if (assets is not null)
        {
            return assets;
        }

        if (!checkOutgoing)
        {
            return null;
        }

        var payer = PayerOf(trade);
        var gate = CheckOutgoing(payer);
        if (gate is not null)
        {
            return TradeActionResult.Rejected(gate.Reason, gate.Detail);
        }

        var payee = trade.Other(payer);
        if (_ledger.Find(EffectiveWallet(payee)) is { Frozen: true })
        {
            return TradeActionResult.Rejected(EconomyReject.EconomyFrozen, "the other party's wallet is frozen");
        }

        return _ledger.BalanceOf(EffectiveWallet(payer)) < CreditsOf(trade)
            ? TradeActionResult.Rejected(EconomyReject.InsufficientFunds)
            : null;
    }

    private TradeActionResult? ScopeVerdict(EconomyOptions options, int a, int b)
    {
        var teamA = LiveTeamOf(a);
        var teamB = LiveTeamOf(b);
        var same = teamA is not null && teamA == teamB;
        var allied = same || (teamA is { } x && teamB is { } y && _teams?.RelationBetween(x, y) == X4MP.Core.Teams.TeamRelation.Allied);
        switch (options.TradeScope)
        {
            case EconomyScope.Off:
                return TradeActionResult.Rejected(EconomyReject.ScopeDisabled);
            case EconomyScope.Teammates:
                return same ? null : TradeActionResult.Rejected(EconomyReject.ScopeDenied, "the counterparty is not a teammate");
            case EconomyScope.Allied:
                return allied ? null : TradeActionResult.Rejected(EconomyReject.ScopeDenied, "the counterparty's team is not allied");
            default:
                return _ledger.Find(EffectiveWallet(a)) is { Frozen: true } || _ledger.Find(EffectiveWallet(b)) is { Frozen: true }
                    ? TradeActionResult.Rejected(EconomyReject.EconomyFrozen, "a wallet is frozen")
                    : null;
        }
    }

    /// <summary>M1 trades: exactly one credits item in total, at least one asset, ships and wares only, sane sizes.</summary>
    private static TradeActionResult? CheckShape(TradeRecord trade, EconomyOptions options)
    {
        var all = trade.InitiatorGives.Concat(trade.CounterpartyGives).ToList();
        if (trade.InitiatorGives.Count > MaxTradeItemsPerSide || trade.CounterpartyGives.Count > MaxTradeItemsPerSide)
        {
            return TradeActionResult.Rejected(EconomyReject.AmountInvalid, $"at most {MaxTradeItemsPerSide} items per side");
        }

        var credits = all.Where(i => i.Kind == TradeItemKind.Credits).ToList();
        if (credits.Count != 1)
        {
            return TradeActionResult.Rejected(EconomyReject.AmountInvalid, "a trade needs exactly one credits item");
        }

        if (credits[0].Amount <= 0)
        {
            return TradeActionResult.Rejected(EconomyReject.AmountInvalid, "credits must be positive");
        }

        if (credits[0].Amount > options.MaxSingleTransfer)
        {
            return TradeActionResult.Rejected(EconomyReject.OverMaxAmount, options.MaxSingleTransfer.ToString(CultureInfo.InvariantCulture));
        }

        var assets = all.Where(i => i.Kind != TradeItemKind.Credits).ToList();
        if (assets.Count == 0)
        {
            return TradeActionResult.Rejected(EconomyReject.AmountInvalid, "a trade needs at least one asset");
        }

        foreach (var item in assets)
        {
            if (item.Kind == TradeItemKind.Station)
            {
                return TradeActionResult.Rejected(EconomyReject.AssetUnavailable, "stations cannot be traded yet");
            }

            if (item.Asset == 0)
            {
                return TradeActionResult.Rejected(EconomyReject.AssetUnavailable, "an item names no asset");
            }

            if (item.Kind == TradeItemKind.Ship && !options.TradeShipsEnabled)
            {
                return TradeActionResult.Rejected(EconomyReject.ScopeDisabled, "ship trades are disabled");
            }

            if (item.Kind == TradeItemKind.Ware && (item.WareRef == 0 || item.Amount <= 0 || item.Amount > int.MaxValue))
            {
                return TradeActionResult.Rejected(EconomyReject.AmountInvalid, "a ware item needs a ware and a positive amount");
            }
        }

        // One ship appears once; a container may source several wares.
        var ships = assets.Where(i => i.Kind == TradeItemKind.Ship).Select(i => i.Asset).ToList();
        return ships.Count != ships.Distinct().Count()
            ? TradeActionResult.Rejected(EconomyReject.AssetUnavailable, "a ship is listed twice")
            : null;
    }

    private TradeActionResult? CheckAssets(TradeRecord trade, EconomyOptions options, uint initiatorInto, uint counterInto)
    {
        if (TradeWorld is not { } world)
        {
            return TradeActionResult.Rejected(EconomyReject.AuthorityUnavailable, "the world is not known to the server");
        }

        var crossTeam = LiveTeamOf(trade.Initiator) != LiveTeamOf(trade.Counterparty);
        foreach (var (giver, gives) in new[] { (trade.Initiator, trade.InitiatorGives), (trade.Counterparty, trade.CounterpartyGives) })
        {
            var receiver = trade.Other(giver);
            var into = receiver == trade.Initiator ? initiatorInto : counterInto;
            foreach (var item in gives.Where(i => i.IsAsset))
            {
                if (_tradeLocks.TryGetValue(item.Asset, out var holder) && holder != trade.Id)
                {
                    return TradeActionResult.Rejected(
                        EconomyReject.AssetUnavailable, string.Create(CultureInfo.InvariantCulture, $"asset {item.Asset} is part of open trade {holder}"));
                }

                if (!world.TryGetAsset(item.Asset, out var asset))
                {
                    return TradeActionResult.Rejected(EconomyReject.AssetUnavailable, string.Create(CultureInfo.InvariantCulture, $"unknown asset {item.Asset}"));
                }

                if (item.Kind == TradeItemKind.Ship && (!asset.IsShip || asset.IsPlayerShip))
                {
                    return TradeActionResult.Rejected(EconomyReject.AssetUnavailable, asset.IsShip ? "a ship a player is flying cannot be traded" : "the asset is not a ship");
                }

                var denied = world.DenyGive(giver, asset, crossTeam, item.Kind == TradeItemKind.Ship);
                if (denied is not null)
                {
                    return TradeActionResult.Rejected(EconomyReject.AssetNotOwned, denied);
                }

                if (item.Kind == TradeItemKind.Ware && world.CargoAmount(item.Asset, item.WareRef) is { } held && held < item.Amount)
                {
                    return TradeActionResult.Rejected(EconomyReject.AssetUnavailable, "the container holds less than the offered amount");
                }

                ushort referenceSector;
                if (item.Kind == TradeItemKind.Ware)
                {
                    var destination = into != 0 ? into : world.TryGetPlayerShip(receiver, out var ship, out _) ? ship : 0;
                    if (destination == 0 || !world.TryGetAsset(destination, out var dest))
                    {
                        return TradeActionResult.Rejected(EconomyReject.AssetUnavailable, "the receiver has no container to take the wares");
                    }

                    var receiverDenied = world.DenyReceive(receiver, dest);
                    if (receiverDenied is not null)
                    {
                        return TradeActionResult.Rejected(EconomyReject.AssetNotOwned, receiverDenied);
                    }

                    referenceSector = dest.Sector;
                }
                else
                {
                    referenceSector = into != 0 && world.TryGetAsset(into, out var chosen)
                        ? chosen.Sector
                        : world.TryGetPlayerShip(receiver, out _, out var sector) ? sector : (ushort)0;
                }

                if (options.TradeRequiresProximity && (asset.Sector == 0 || referenceSector == 0 || asset.Sector != referenceSector))
                {
                    return TradeActionResult.Rejected(
                        EconomyReject.OutOfRange, asset.Sector == 0 || referenceSector == 0 ? "a position is not known yet" : "the assets are not in the same sector");
                }
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ helpers

    private static long CreditsOf(TradeRecord trade) =>
        trade.InitiatorGives.Concat(trade.CounterpartyGives).Where(i => i.Kind == TradeItemKind.Credits).Sum(i => i.Amount);

    /// <summary>The player who gives the credits item.</summary>
    private static int PayerOf(TradeRecord trade) =>
        trade.InitiatorGives.Any(i => i.Kind == TradeItemKind.Credits) ? trade.Initiator : trade.Counterparty;

    private static void SetAccepted(TradeRecord trade, int player, uint version, uint receiveInto)
    {
        if (player == trade.Initiator)
        {
            trade.InitiatorAccepted = version;
            trade.InitiatorReceiveInto = receiveInto;
        }
        else
        {
            trade.CounterpartyAccepted = version;
            trade.CounterpartyReceiveInto = receiveInto;
        }
    }

    private static void ApplyInto(TradeRecord target, TradeRecord source)
    {
        target.Version = source.Version;
        target.State = source.State;
        target.InitiatorGives = source.InitiatorGives;
        target.CounterpartyGives = source.CounterpartyGives;
        target.InitiatorAccepted = source.InitiatorAccepted;
        target.CounterpartyAccepted = source.CounterpartyAccepted;
        target.InitiatorReceiveInto = source.InitiatorReceiveInto;
        target.CounterpartyReceiveInto = source.CounterpartyReceiveInto;
        target.Requests = source.Requests;
        target.UpdatedAt = source.UpdatedAt;
    }

    private void LockAll(TradeRecord trade)
    {
        foreach (var asset in trade.LockedAssets)
        {
            _tradeLocks[asset] = trade.Id;
        }
    }

    private void Unlock(TradeRecord trade)
    {
        foreach (var asset in _tradeLocks.Where(kv => kv.Value == trade.Id).Select(kv => kv.Key).ToList())
        {
            _tradeLocks.Remove(asset);
        }
    }

    /// <summary>Writes the trade (and its locks) through the store. Null = fine (or no store), otherwise the reason to refuse the request.</summary>
    private TradeActionResult? Persist(TradeRecord trade)
    {
        if (TradeStore is not { } store)
        {
            return null;
        }

        try
        {
            store.Save(_ledger.SessionId, trade, trade.IsOpen ? trade.LockedAssets : []);
            return null;
        }
        catch (TradeLockConflictException ex)
        {
            return TradeActionResult.Rejected(EconomyReject.AssetUnavailable, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _events?.Publish(new AlertRaised(
                _time.GetUtcNow(), _ledger.SessionId, AlertSeverity.Critical, "economy_trade_store",
                $"Could not save trade {trade.Id.ToString(CultureInfo.InvariantCulture)}: {ex.Message}"));
            return TradeActionResult.Rejected(EconomyReject.Timeout, "the trade could not be saved");
        }
    }

    private static Id128T TradeWireId(long id) => new() { Lo = (ulong)id };

    private static string Hex(byte[] hash) => Convert.ToHexString(hash);

    /// <summary>
    /// Invariants for the auditor: a final trade holds no escrow, a negotiating one none either, a transferring or InDoubt one exactly its
    /// escrowed credits; and every lock belongs to an open trade.
    /// </summary>
    public IEnumerable<string> AuditTrades(EconomyLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        foreach (var trade in _trades.Values)
        {
            var escrow = ledger.BalanceOf(trade.EscrowWallet);
            var expected = trade.State is TradeState.Transferring or TradeState.InDoubt ? trade.EscrowAmount : 0;
            if (escrow != expected)
            {
                yield return $"trade {trade.Id}: escrow holds {escrow.ToString(CultureInfo.InvariantCulture)} in state {trade.State}, expected {expected.ToString(CultureInfo.InvariantCulture)}";
            }
        }

        foreach (var (asset, holder) in _tradeLocks)
        {
            if (!_trades.TryGetValue(holder, out var trade) || !trade.IsOpen)
            {
                yield return $"asset {asset} is locked by trade {holder}, which is not open";
            }
        }
    }
}
