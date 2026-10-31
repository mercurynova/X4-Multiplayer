using System.Globalization;
using Microsoft.Extensions.Logging;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Economy;

/// <summary>
/// M5-29: the ledger list the economy screen shows. The requester sees its own wallet, its team's shared and pool
/// wallets, and the transactions of a trade or loan it is a party to; the escrow line of somebody else's trade stays
/// invisible, so a page never reveals a balance the player could not see in a <c>WalletUpdate</c>.
/// </summary>
public sealed partial class EconomyModule
{
    private const int LedgerDefaultLimit = 50;
    private const int LedgerMaxLimit = 200;

    /// <summary>How many rows one page asks the store for, per row returned: the store filters by wallet/kind/actor,
    /// never by "what this player may see", so a selective visibility rule needs the slack. A page that comes back short
    /// with <c>more</c> false simply had nothing else.</summary>
    private const int LedgerOverfetch = 4;

    private bool LedgerOnMessage(SessionNode node, InboundFrame frame)
    {
        if (frame.Type != MsgType.LedgerRequest)
        {
            return false;
        }

        if (Service is not { } service)
        {
            return true; // no session row yet: nothing to list, and nobody else handles it
        }

        LedgerRequestT request;
        try
        {
            request = MessageRegistry.Default.Decode<LedgerRequest>(frame.Frame).UnPack();
        }
        catch (ProtocolViolation ex)
        {
            LogMalformedLedger(ex, node.PlayerId);
            return true;
        }

        var key = request.RequestKey ?? new Id128T();
        if (_phase != SessionPhase.Running)
        {
            SendLedgerPage(node, key, EconomyReject.SessionNotRunning, _phase.ToString(), [], more: false);
            return true;
        }

        var limit = Math.Clamp(request.Limit == 0 ? LedgerDefaultLimit : request.Limit, 1, LedgerMaxLimit);
        var before = string.IsNullOrEmpty(request.Before) ? null : request.Before;
        var fetched = service.Ledger.QueryTransactions(new LedgerQuery(Before: before, Limit: (limit * LedgerOverfetch) + 1));

        var rows = new List<LedgerRowT>(limit);
        var more = false;
        foreach (var tx in fetched)
        {
            if (RowOf(tx, node.PlayerId, service) is not { } row)
            {
                continue;
            }

            if (rows.Count == limit)
            {
                more = true; // one visible row past the page: there is another page
                break;
            }

            rows.Add(row);
        }

        SendLedgerPage(node, key, EconomyReject.None, string.Empty, rows, more);
        return true;
    }

    /// <summary>The row a player may see for one transaction, or null when every line of it is invisible to them.</summary>
    private static LedgerRowT? RowOf(LedgerTransaction tx, int playerId, EconomyService service)
    {
        long delta = 0;
        WalletId? wallet = null;
        foreach (var entry in tx.Entries)
        {
            if (!service.IsVisibleTo(playerId, entry.Wallet))
            {
                continue;
            }

            delta += entry.Amount;
            wallet ??= entry.Wallet;
        }

        if (wallet is not { } seen)
        {
            return null;
        }

        var kind = (byte)tx.Kind <= (byte)LedgerKind.Other ? (LedgerKind)(byte)tx.Kind : LedgerKind.Other;
        return new LedgerRowT
        {
            Id = tx.Id,
            AtUs = (ulong)Math.Max(0, tx.At.ToUnixTimeMilliseconds()) * 1000,
            Kind = kind,
            Wallet = ToWalletRef(seen),
            Delta = delta,
            Actor = ActorOf(tx.Actor),
            RefType = tx.RefType ?? string.Empty,
            RefId = tx.RefId ?? 0,
            Note = tx.Note ?? string.Empty,
            Reversed = tx.ReversedBy is not null,
        };
    }

    /// <summary>The player behind a transaction's actor string ("player:7"), or 0 for the server and the authority.</summary>
    private static ushort ActorOf(string? actor)
    {
        if (actor is null || !actor.StartsWith("player:", StringComparison.Ordinal))
        {
            return 0;
        }

        return int.TryParse(actor.AsSpan(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
            ? (ushort)Math.Min(id, ushort.MaxValue)
            : (ushort)0;
    }

    private static void SendLedgerPage(SessionNode node, Id128T key, EconomyReject reason, string detail,
                                       List<LedgerRowT> rows, bool more)
    {
        if (node.Connection is null)
        {
            return;
        }

        var page = new LedgerPageT
        {
            RequestKey = key,
            Status = reason == EconomyReject.None ? EconomyStatus.Ok : EconomyStatus.Rejected,
            Reason = reason,
            Detail = detail,
            Rows = rows,
            More = more,
        };
        node.Connection.TrySend(ControlFrames.Encode(MsgType.LedgerPage, fbb => LedgerPage.Pack(fbb, page).Value, 512));
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "malformed LedgerRequest from player {PlayerId}")]
    private partial void LogMalformedLedger(Exception ex, int playerId);
}
