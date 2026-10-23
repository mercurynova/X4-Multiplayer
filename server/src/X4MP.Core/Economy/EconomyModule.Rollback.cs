using System.Globalization;
using X4MP.Core.Net;
using X4MP.Core.Saves;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Economy;

/// <summary>The ledger follows the checkpoint (ADR-053, F13b): marks for new checkpoints, the rollback, and telling the nodes.</summary>
public sealed partial class EconomyModule
{
    // Nodes that were told the restored balances while they were still loading: they are told again when they reach the game (a load drops what they were sent).
    private readonly HashSet<int> _resendOnInGame = [];

    /// <summary>The ledger mark a checkpoint that begins now records. Null without a session row.</summary>
    public LedgerMark? CaptureMark() => Service?.CaptureMark();

    /// <summary>
    /// The authority is about to load the checkpoint: rolls the ledger back to <paramref name="mark"/>, sends every node its restored balances and a
    /// notice. Null when there is no economy yet.
    /// </summary>
    public LedgerRollbackResult? RollBackLedger(LedgerMark? mark, string label, DateTimeOffset checkpointAt, string reason)
    {
        if (Service is not { } service)
        {
            return null;
        }

        var result = service.RollBackToMark(mark, label, reason);
        if (!result.Performed)
        {
            return result;
        }

        var when = checkpointAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        foreach (var node in _nodes.Values.ToList())
        {
            PushBalances(node, LedgerReason.AdminAdjust);
            _resendOnInGame.Add(node.PlayerId);
            if (node.Connection is null)
            {
                continue;
            }

            var balance = service.Ledger.BalanceOf(service.EffectiveWallet(node.PlayerId));
            var text = $"Session restored to the checkpoint of {when} UTC; your balance is now {LoanText.Credits(balance)}.";
            var notice = new ServerNoticeT { Severity = NoticeSeverity.Warning, Text = text, DisplayMs = 12000 };
            node.Connection.TrySend(ControlFrames.Encode(MsgType.ServerNotice, fbb => ServerNotice.Pack(fbb, notice).Value, 128));
        }

        return result;
    }

    public void OnNodePhaseChanged(SessionNode node, NodePhase previous, NodePhase current)
    {
        if (current == NodePhase.InGame && _resendOnInGame.Remove(node.PlayerId) && Service is not null)
        {
            PushBalances(node, LedgerReason.AdminAdjust);
        }
    }

    private void PushBalances(SessionNode node, LedgerReason reason)
    {
        if (Service is not { } service)
        {
            return;
        }

        var balances = node.IsAuthority
            ? service.Ledger.Wallets.Select(w => new WalletBalanceAfter(w.Id, w.Balance, w.Version)).ToList()
            : [.. service.VisibleBalances(node.PlayerId)];
        SendTo(node, balances, reason, new Id128T());
    }
}
