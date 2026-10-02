using System.Globalization;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Economy;

/// <summary>
/// Trades on the wire (M1-E5): the four player requests, the authority's <c>AssetTransferConfirm</c>, <c>TradeStatus</c> /
/// <c>TradeResult</c> to the parties, <c>AssetTransferOrder</c> and <c>TradeQuery</c> to the authority, and the tick that runs the
/// timeline. The rules live in <see cref="EconomyService"/> (<c>EconomyService.Trades.cs</c>).
/// </summary>
public sealed partial class EconomyModule
{
    /// <summary>Persistence of trades and their locks; null keeps them in memory. Set before the session begins.</summary>
    public ITradeStore? TradeStore { get; set; }

    /// <summary>The mirror view the trade rules read; without it a trade that names an asset is refused. Set before the session begins.</summary>
    public ITradeWorld? TradeWorld { get; set; }

    private void BeginTrades(EconomyService service, EconomyAuditor auditor)
    {
        service.TradeStore = TradeStore ?? new InMemoryTradeStore();
        service.TradeWorld = TradeWorld;
        service.SendTransferOrder = SendOrderToAuthority;
        service.SendTradeQuery = SendQueryToAuthority;
        service.AuthorityOnline = () => Authority() is not null;
        service.TradeChanged = OnTradeChanged;
        service.LoadTrades();
        auditor.AddCheck(ledger => service.AuditTrades(ledger));
    }

    private SessionNode? Authority() => _nodes.Values.FirstOrDefault(n => n.IsAuthority && n.Connection is not null);

    private void TradesNodeAttached(SessionNode node, bool resumed)
    {
        if (Service is not { } service)
        {
            return;
        }

        if (node.IsAuthority)
        {
            service.OnAuthorityAttached();
            return;
        }

        // A player who (re)connects learns the state of the trades it is part of.
        if (resumed)
        {
            foreach (var trade in service.Trades.Where(t => t.IsOpen && (t.Initiator == node.PlayerId || t.Counterparty == node.PlayerId)).OrderBy(t => t.Id))
            {
                SendStatus(node, trade, service);
            }
        }
    }

    private void TradesNodeLeft(SessionNode node)
    {
        if (node.IsAuthority)
        {
            Service?.OnAuthorityGone();
        }
    }

    private void TradesTick() => Service?.TickTrades();

    private bool TradeOnMessage(SessionNode node, InboundFrame frame)
    {
        if (frame.Type == MsgType.AssetTransferConfirm)
        {
            if (Service is not { } confirmService)
            {
                return true;
            }

            if (!node.IsAuthority)
            {
                return true; // MessagePolicy only lets the authority send it; belt and braces
            }

            try
            {
                confirmService.OnAssetTransferConfirm(MessageRegistry.Default.Decode<AssetTransferConfirm>(frame.Frame).UnPack());
            }
            catch (ProtocolViolation ex)
            {
                LogMalformedAction(ex, node.PlayerId, frame.Type);
            }

            return true;
        }

        if (frame.Type is not (MsgType.TradeProposal or MsgType.TradeCounter or MsgType.TradeAccept or MsgType.TradeCancel))
        {
            return false;
        }

        if (Service is not { } service)
        {
            return true;
        }

        Id128T? key;
        Func<string, TradeActionResult> run;
        try
        {
            var registry = MessageRegistry.Default;
            switch (frame.Type)
            {
                case MsgType.TradeProposal:
                    var proposal = registry.Decode<TradeProposal>(frame.Frame).UnPack();
                    key = proposal.RequestKey;
                    run = k => service.ProposeTrade(
                        node.PlayerId, k, proposal.Counterparty, Items(proposal.Give), Items(proposal.Want), proposal.TtlS, proposal.Memo);
                    break;
                case MsgType.TradeCounter:
                    var counter = registry.Decode<TradeCounter>(frame.Frame).UnPack();
                    key = counter.RequestKey;
                    run = k => service.CounterTrade(node.PlayerId, k, TradeIdOf(counter.TradeId), counter.BaseVersion, Items(counter.Give), Items(counter.Want));
                    break;
                case MsgType.TradeAccept:
                    var accept = registry.Decode<TradeAccept>(frame.Frame).UnPack();
                    key = accept.RequestKey;
                    run = k => service.AcceptTrade(node.PlayerId, k, TradeIdOf(accept.TradeId), accept.Version, accept.ReceiveIntoAsset);
                    break;
                default:
                    var cancel = registry.Decode<TradeCancel>(frame.Frame).UnPack();
                    key = cancel.RequestKey;
                    run = k => service.CancelTrade(node.PlayerId, k, TradeIdOf(cancel.TradeId));
                    break;
            }
        }
        catch (ProtocolViolation ex)
        {
            LogMalformedAction(ex, node.PlayerId, frame.Type);
            return true;
        }

        key ??= new Id128T();
        TradeActionResult result;
        if (_phase != SessionPhase.Running)
        {
            result = TradeActionResult.Rejected(EconomyReject.SessionNotRunning, _phase.ToString());
        }
        else
        {
            var limited = service.CheckRate(node.PlayerId);
            result = limited is not null ? TradeActionResult.Rejected(limited.Reason, limited.Detail) : run(KeyText(key));
        }

        SendTradeResult(node, key, result, service);
        return true;
    }

    private static List<TradeItemModel> Items(List<TradeItemT>? items) => [.. (items ?? []).Select(TradeItemModel.FromWire)];

    /// <summary>Trade ids are server-made counters carried in the low half of the wire id; anything else names no trade.</summary>
    private static long TradeIdOf(Id128T? id) => id is { Hi: 0 } wire ? (long)wire.Lo : -1;

    private static Id128T TradeWire(long id) => new() { Lo = (ulong)id };

    private static void SendTradeResult(SessionNode node, Id128T key, TradeActionResult result, EconomyService service)
    {
        if (node.Connection is null)
        {
            return;
        }

        var message = new EconomyResultT
        {
            RequestKey = key,
            Status = result.Ok ? EconomyStatus.Ok : EconomyStatus.Rejected,
            Reason = result.Reason,
            Detail = result.Detail ?? string.Empty,
            RefId = result.Trade is { } trade ? TradeWire(trade.Id) : key,
            Balances = [.. service.VisibleBalances(node.PlayerId).Select(ToWire)],
        };
        node.Connection.TrySend(ControlFrames.Encode(MsgType.EconomyResult, fbb => EconomyResult.Pack(fbb, message).Value, 192));
    }

    // ------------------------------------------------------------------ authority traffic

    private bool SendOrderToAuthority(AssetTransferOrderT order) =>
        Authority() is { Connection: { } connection }
        && connection.TrySend(ControlFrames.Encode(MsgType.AssetTransferOrder, fbb => AssetTransferOrder.Pack(fbb, order).Value, 256)) is X4MP.Core.Net.SendResult.Queued or X4MP.Core.Net.SendResult.Coalesced;

    private bool SendQueryToAuthority(long tradeId)
    {
        var query = new TradeQueryT { TradeId = TradeWire(tradeId) };
        return Authority() is { Connection: { } connection }
            && connection.TrySend(ControlFrames.Encode(MsgType.TradeQuery, fbb => TradeQuery.Pack(fbb, query).Value, 48)) is X4MP.Core.Net.SendResult.Queued or X4MP.Core.Net.SendResult.Coalesced;
    }

    // ------------------------------------------------------------------ status to the parties

    private void OnTradeChanged(TradeRecord trade, TradeState previous)
    {
        if (Service is not { } service)
        {
            return;
        }

        foreach (var player in new[] { trade.Initiator, trade.Counterparty })
        {
            if (!_nodes.TryGetValue(player, out var node) || node.Connection is null)
            {
                continue;
            }

            SendStatus(node, trade, service);
            if (!trade.IsOpen)
            {
                SendFinal(node, trade);
            }
        }
    }

    private static void SendStatus(SessionNode node, TradeRecord trade, EconomyService service)
    {
        if (node.Connection is null)
        {
            return;
        }

        var status = new TradeStatusT
        {
            TradeId = TradeWire(trade.Id),
            Version = trade.Version,
            State = trade.State,
            Initiator = Side(trade.Initiator, trade.InitiatorGives, trade.InitiatorAccepted, service),
            Counterparty = Side(trade.Counterparty, trade.CounterpartyGives, trade.CounterpartyAccepted, service),
            ExpiresTimeUs = (ulong)Math.Max(0, trade.ExpiresAt.ToUnixTimeMilliseconds()) * 1000,
            Memo = trade.Memo ?? string.Empty,
        };
        node.Connection.TrySend(ControlFrames.Encode(MsgType.TradeStatus, fbb => TradeStatus.Pack(fbb, status).Value, 256));
    }

    private static TradeSideT Side(int player, List<TradeItemModel> gives, uint accepted, EconomyService service) => new()
    {
        PlayerId = (ushort)player,
        TeamId = (ushort)(service.AppliedTeamOf(player) ?? 0),
        Give = [.. gives.Select(i => i.ToWire())],
        AcceptedVersion = accepted,
    };

    private static void SendFinal(SessionNode node, TradeRecord trade)
    {
        if (node.Connection is null)
        {
            return;
        }

        var result = new TradeResultT
        {
            TradeId = TradeWire(trade.Id),
            Version = trade.Version,
            State = trade.State,
            Reason = trade.Reason,
            Detail = trade.Detail ?? string.Empty,
        };
        node.Connection.TrySend(ControlFrames.Encode(MsgType.TradeResult, fbb => TradeResult.Pack(fbb, result).Value, 128));
    }

    /// <summary>The trade the id names (for the admin API of M1-E6 and tests).</summary>
    public TradeRecord? FindTrade(string id) => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? Service?.FindTrade(n) : null;
}
