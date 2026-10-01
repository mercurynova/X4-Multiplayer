using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using X4MP.Core.Events;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Core.Teams;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Economy;

/// <summary>
/// The economy as a session module (server-design 2.14): owns the ledger, the auditor and the <see cref="EconomyService"/>
/// of the session, books <c>CreditDelta</c> frames and sends <c>WalletUpdate</c>s. Later economy tasks (donations, loans,
/// trades) hook into <see cref="Service"/>, <see cref="Auditor"/> and <see cref="OnMessage"/>.
/// </summary>
public sealed partial class EconomyModule : ISessionModule
{
    private readonly Func<EconomyOptions> _options;
    private readonly IEconomyStore _store;
    private readonly ITeamDirectory? _teams;
    private readonly TimeProvider _time;
    private readonly IEventPublisher? _events;
    private readonly ILogger _logger;
    private readonly Dictionary<int, SessionNode> _nodes = [];
    private SessionPhase _phase = SessionPhase.Idle;
    private CreditMode _lastMode;
    private bool _subscribed;

    /// <param name="teams">Null while no team module is registered: everybody is then in one implicit team (Auto resolves to Shared).</param>
    public EconomyModule(
        Func<EconomyOptions> options,
        IEconomyStore store,
        ITeamDirectory? teams = null,
        TimeProvider? time = null,
        IEventPublisher? events = null,
        ILogger<EconomyModule>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        _options = options;
        _store = store;
        _teams = teams;
        _time = time ?? TimeProvider.System;
        _events = events;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _lastMode = options().CreditMode;
    }

    /// <summary>Null until the session row exists (<see cref="OnSessionBegun"/>).</summary>
    public EconomyService? Service { get; private set; }

    public EconomyAuditor? Auditor { get; private set; }

    public void OnSessionBegun(long sessionId)
    {
        if (Service is not null)
        {
            return;
        }

        var ledger = new EconomyLedger(sessionId, _store, _time, _events);
        Service = new EconomyService(ledger, _store, _teams, _options, _time, _events, () => _phase) { Changed = Send };
        Auditor = new EconomyAuditor(ledger, _store, _time, () => TimeSpan.FromSeconds(Math.Max(1, _options().AuditIntervalSeconds)));
        Service.Start();
        InitLoanHooks(Service, Auditor);
        if (_teams is not null && !_subscribed)
        {
            _subscribed = true;
            _teams.Changed += _ => Service?.Reconcile(confirm: false, actor: "system");
        }
    }

    public void OnNodeAttached(SessionNode node, bool resumed)
    {
        ArgumentNullException.ThrowIfNull(node);
        _nodes[node.PlayerId] = node;
        if (Service is not { } service)
        {
            return;
        }

        if (!resumed)
        {
            service.EnsurePlayer(node.PlayerId);
        }

        // The node learns its balances and, through acked_delta_seq, which CreditDeltas it may stop resending.
        var balances = node.IsAuthority
            ? service.Ledger.Wallets.Select(w => new WalletBalanceAfter(w.Id, w.Balance, w.Version)).ToList()
            : [.. service.VisibleBalances(node.PlayerId)];
        SendTo(node, balances, LedgerReason.GameIncome, new Id128T());
        SendOpenLoans(node, service);
    }

    public void OnNodeLeft(SessionNode node, string reason)
    {
        ArgumentNullException.ThrowIfNull(node);
        _nodes.Remove(node.PlayerId);
        Service?.ForgetRate(node.PlayerId);
    }

    public void OnSessionPhaseChanged(SessionPhase previous, SessionPhase current)
    {
        _phase = current;
        if (Service is { MigrationPending: true } service)
        {
            service.Reconcile(confirm: false, actor: "system");
        }
    }

    public void OnTick(long timestamp)
    {
        if (Service is not { } service)
        {
            return;
        }

        Auditor?.Tick(timestamp);
        TickLoans(service, timestamp);
        var mode = _options().CreditMode;
        if (mode != _lastMode || service.MigrationPending)
        {
            _lastMode = mode;
            service.Reconcile(confirm: false, actor: "system");
        }
    }

    public bool OnMessage(SessionNode node, InboundFrame frame)
    {
        if (frame.Type is MsgType.CreditTransferRequest or MsgType.DonateRequest or MsgType.PoolDepositRequest or MsgType.PoolWithdrawRequest)
        {
            return OnPlayerAction(node, frame);
        }

        if (IsLoanMessage(frame.Type))
        {
            return OnLoanAction(node, frame);
        }

        if (frame.Type != MsgType.CreditDelta)
        {
            return false;
        }

        if (Service is not { } service)
        {
            return true; // no session row yet: nothing can be booked, and nobody else handles it
        }

        CreditDeltaT delta;
        try
        {
            delta = MessageRegistry.Default.Decode<CreditDelta>(frame.Frame).UnPack();
        }
        catch (ProtocolViolation ex)
        {
            LogMalformedDelta(ex, node.PlayerId);
            return true;
        }

        service.BookCreditDelta(node.PlayerId, node.IsAuthority, delta);
        return true;
    }

    // ------------------------------------------------------------------ player actions (M1-E3)

    private bool OnPlayerAction(SessionNode node, InboundFrame frame)
    {
        if (Service is not { } service)
        {
            return true;
        }

        Id128T? key;
        Func<string, EconomyActionResult> run;
        try
        {
            var registry = MessageRegistry.Default;
            switch (frame.Type)
            {
                case MsgType.CreditTransferRequest:
                    var transfer = registry.Decode<CreditTransferRequest>(frame.Frame).UnPack();
                    key = transfer.RequestKey;
                    run = k => service.Transfer(node.PlayerId, k, transfer.ToPlayer, transfer.Amount, transfer.Memo);
                    break;
                case MsgType.DonateRequest:
                    var donate = registry.Decode<DonateRequest>(frame.Frame).UnPack();
                    key = donate.RequestKey;
                    run = k => service.Donate(node.PlayerId, k, donate.ToPlayer, donate.Amount, donate.Memo);
                    break;
                case MsgType.PoolDepositRequest:
                    var deposit = registry.Decode<PoolDepositRequest>(frame.Frame).UnPack();
                    key = deposit.RequestKey;
                    run = k => service.PoolDeposit(node.PlayerId, k, deposit.Amount);
                    break;
                default:
                    var withdraw = registry.Decode<PoolWithdrawRequest>(frame.Frame).UnPack();
                    key = withdraw.RequestKey;
                    run = k => service.PoolWithdraw(node.PlayerId, k, withdraw.Amount);
                    break;
            }
        }
        catch (ProtocolViolation ex)
        {
            LogMalformedAction(ex, node.PlayerId, frame.Type);
            return true;
        }

        key ??= new Id128T();
        var keyText = KeyText(key);
        EconomyActionResult result;
        if (_phase != SessionPhase.Running)
        {
            result = EconomyActionResult.Rejected(EconomyReject.SessionNotRunning, _phase.ToString());
        }
        else
        {
            result = service.CheckRate(node.PlayerId) ?? run(keyText);
        }

        SendResult(node, key, result);
        return true;
    }

    private static string KeyText(Id128T key) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{key.Hi:x16}{key.Lo:x16}");

    private void SendResult(SessionNode node, Id128T key, EconomyActionResult result)
    {
        if (node.Connection is null || Service is not { } service)
        {
            return;
        }

        var message = new EconomyResultT
        {
            RequestKey = key,
            Status = result.Ok ? EconomyStatus.Ok : EconomyStatus.Rejected,
            Reason = result.Reason,
            Detail = result.Detail ?? string.Empty,
            RefId = key,
            Balances = [],
        };
        foreach (var balance in result.Outcome?.Balances ?? [])
        {
            if (balance.Wallet.Kind != WalletKind.World && (node.IsAuthority || service.IsVisibleTo(node.PlayerId, balance.Wallet)))
            {
                message.Balances.Add(ToWire(balance));
            }
        }

        node.Connection.TrySend(ControlFrames.Encode(MsgType.EconomyResult, fbb => EconomyResult.Pack(fbb, message).Value, 128));
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "malformed {Type} from player {PlayerId}")]
    private partial void LogMalformedAction(Exception ex, int playerId, MsgType type);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "malformed CreditDelta from player {PlayerId}")]
    private partial void LogMalformedDelta(Exception ex, int playerId);

    // ------------------------------------------------------------------ WalletUpdate

    private void Send(WalletChange change)
    {
        if (Service is not { } service)
        {
            return;
        }

        foreach (var node in _nodes.Values)
        {
            var balances = node.IsAuthority
                ? change.Changed
                : [.. change.Changed.Where(b => service.IsVisibleTo(node.PlayerId, b.Wallet))];
            if (balances.Count == 0 && change.AckTo != node.PlayerId)
            {
                continue;
            }

            SendTo(node, balances, change.Reason, change.Ref);
        }
    }

    private void SendTo(SessionNode node, IReadOnlyList<WalletBalanceAfter> balances, LedgerReason reason, Id128T key)
    {
        if (node.Connection is null || Service is not { } service)
        {
            return;
        }

        var update = new WalletUpdateT
        {
            Balances = [],
            Reason = reason,
            RefId = key,
            EffectiveMode = service.AppliedMode,
            AckedDeltaSeq = service.Ledger.LastDeltaSeq(node.PlayerId),
        };
        foreach (var balance in balances)
        {
            if (balance.Wallet.Kind == WalletKind.World)
            {
                continue; // internal counter-party, not on the wire
            }

            update.Balances.Add(ToWire(balance));
        }

        node.Connection.TrySend(ControlFrames.Encode(MsgType.WalletUpdate, fbb => WalletUpdate.Pack(fbb, update).Value, 128));
    }

    private static WalletBalanceT ToWire(WalletBalanceAfter balance) => new()
    {
        Wallet = new WalletRefT { Kind = ToWire(balance.Wallet.Kind), OwnerId = (ushort)Math.Clamp(balance.Wallet.OwnerId, 0, ushort.MaxValue) },
        Balance = balance.Balance,
        Version = (ulong)balance.Version,
    };

    private static X4MP.Proto.WalletKind ToWire(WalletKind kind) => kind switch
    {
        WalletKind.TeamShared => X4MP.Proto.WalletKind.TeamShared,
        WalletKind.TeamPool => X4MP.Proto.WalletKind.TeamPool,
        WalletKind.Escrow => X4MP.Proto.WalletKind.Escrow,
        _ => X4MP.Proto.WalletKind.Player,
    };
}
