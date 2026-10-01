using Microsoft.Extensions.Logging;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Economy;

/// <summary>Loan requests, <c>LoanStatus</c> frames and the loan timer (M1-E4).</summary>
public sealed partial class EconomyModule
{
    private long _nextLoanTick;

    private static bool IsLoanMessage(MsgType type) =>
        type is MsgType.LoanOffer or MsgType.LoanRespond or MsgType.LoanRepay or MsgType.LoanForgive or MsgType.LoanCancel;

    /// <summary>Hooks the loan notifications and the auditor check to the new service. Called once, when the session row exists.</summary>
    private void InitLoanHooks(EconomyService service, EconomyAuditor auditor)
    {
        service.LoanChanged = OnLoanChanged;
        auditor.AddCheck(EconomyService.AuditLoans);
    }

    /// <summary>Runs the loan timers (offer expiry, overdue) at most once a second, so a due loan is flagged well inside 10 s.</summary>
    private void TickLoans(EconomyService service, long timestamp)
    {
        if (timestamp < _nextLoanTick)
        {
            return;
        }

        _nextLoanTick = timestamp + _time.TimestampFrequency;
        service.ProcessLoanTimers();
    }

    /// <summary>A (re)connecting node learns about its open loans.</summary>
    private static void SendOpenLoans(SessionNode node, EconomyService service)
    {
        foreach (var loan in service.Loans.Where(l => l.IsOpen && l.InvolvesPlayer(node.PlayerId)).OrderBy(l => l.Id))
        {
            SendLoanStatus(node, loan);
        }
    }

    private bool OnLoanAction(SessionNode node, InboundFrame frame)
    {
        if (Service is not { } service)
        {
            return true;
        }

        Id128T? key;
        Func<string, LoanResult> run;
        try
        {
            var registry = MessageRegistry.Default;
            switch (frame.Type)
            {
                case MsgType.LoanOffer:
                    var offer = registry.Decode<LoanOffer>(frame.Frame).UnPack();
                    key = offer.RequestKey;
                    run = k => service.OfferLoan(node.PlayerId, k, offer.Borrower, offer.Principal, offer.RepayTotal, Clamp(offer.DueInS), Clamp(offer.OfferTtlS), offer.AutoRepayPct, offer.Memo);
                    break;
                case MsgType.LoanRespond:
                    var respond = registry.Decode<LoanRespond>(frame.Frame).UnPack();
                    key = respond.RequestKey;
                    run = k => service.RespondLoan(node.PlayerId, k, LoanIdOf(respond.LoanId), respond.Accept);
                    break;
                case MsgType.LoanRepay:
                    var repay = registry.Decode<LoanRepay>(frame.Frame).UnPack();
                    key = repay.RequestKey;
                    run = k => service.RepayLoan(node.PlayerId, k, LoanIdOf(repay.LoanId), repay.Amount);
                    break;
                case MsgType.LoanForgive:
                    var forgive = registry.Decode<LoanForgive>(frame.Frame).UnPack();
                    key = forgive.RequestKey;
                    run = k => service.ForgiveLoan(node.PlayerId, k, LoanIdOf(forgive.LoanId), forgive.Amount);
                    break;
                default:
                    var cancel = registry.Decode<LoanCancel>(frame.Frame).UnPack();
                    key = cancel.RequestKey;
                    run = k => service.WithdrawLoan(node.PlayerId, k, LoanIdOf(cancel.LoanId));
                    break;
            }
        }
        catch (ProtocolViolation ex)
        {
            LogMalformedAction(ex, node.PlayerId, frame.Type);
            return true;
        }

        key ??= new Id128T();
        LoanResult result;
        if (_phase != SessionPhase.Running)
        {
            result = LoanResult.Rejected(EconomyReject.SessionNotRunning, _phase.ToString());
        }
        else if (service.CheckRate(node.PlayerId) is { } limited)
        {
            result = LoanResult.Rejected(limited.Reason, limited.Detail);
        }
        else
        {
            result = run(KeyText(key));
        }

        SendLoanResult(node, key, result);
        return true;
    }

    private static int Clamp(uint value) => (int)Math.Min(value, int.MaxValue);

    /// <summary>The wire carries a loan id as an Id128 whose low half is the server's number; anything else is unknown (0).</summary>
    private static long LoanIdOf(Id128T? id) => id is null || id.Hi != 0 || id.Lo > long.MaxValue ? 0 : (long)id.Lo;

    private static Id128T WireLoanId(long id) => new() { Lo = (ulong)id, Hi = 0 };

    private void SendLoanResult(SessionNode node, Id128T key, LoanResult result)
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
            RefId = result.Loan is { } loan ? WireLoanId(loan.Id) : key,
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

    private void OnLoanChanged(LoanChange change)
    {
        foreach (var player in new[] { change.Loan.LenderId, change.Loan.BorrowerId }.Distinct())
        {
            if (_nodes.TryGetValue(player, out var node))
            {
                SendLoanStatus(node, change.Loan);
            }
        }

        foreach (var (player, text) in change.Notices)
        {
            if (_nodes.TryGetValue(player, out var node) && node.Connection is not null)
            {
                var severity = change.Loan.State == LoanState.Overdue ? NoticeSeverity.Warning : NoticeSeverity.Info;
                var notice = new ServerNoticeT { Severity = severity, Text = text, DisplayMs = 8000 };
                node.Connection.TrySend(ControlFrames.Encode(MsgType.ServerNotice, fbb => ServerNotice.Pack(fbb, notice).Value, 128));
            }
        }
    }

    private static void SendLoanStatus(SessionNode node, LoanRecord loan)
    {
        if (node.Connection is null)
        {
            return;
        }

        var status = new LoanStatusT
        {
            LoanId = WireLoanId(loan.Id),
            Lender = (ushort)Math.Clamp(loan.LenderId, 0, ushort.MaxValue),
            Borrower = (ushort)Math.Clamp(loan.BorrowerId, 0, ushort.MaxValue),
            State = ToWire(loan.State),
            Principal = loan.Principal,
            RepayTotal = loan.RepayTotal,
            Repaid = loan.Repaid,
            Forgiven = loan.Forgiven,
            CreatedTimeUs = (ulong)loan.CreatedAt.ToUnixTimeMilliseconds() * 1000,
            DueTimeUs = loan.DueAt is { } due ? (ulong)due.ToUnixTimeMilliseconds() * 1000 : 0,
            Memo = loan.Memo ?? string.Empty,
        };
        node.Connection.TrySend(ControlFrames.Encode(MsgType.LoanStatus, fbb => LoanStatus.Pack(fbb, status).Value, 160));
    }

    /// <summary>The wire has no Withdrawn: a lender who withdrew an offer shows as Cancelled.</summary>
    private static X4MP.Proto.LoanState ToWire(LoanState state) => state switch
    {
        LoanState.Offered => X4MP.Proto.LoanState.Offered,
        LoanState.Active => X4MP.Proto.LoanState.Active,
        LoanState.Overdue => X4MP.Proto.LoanState.Overdue,
        LoanState.Repaid => X4MP.Proto.LoanState.Repaid,
        LoanState.Declined => X4MP.Proto.LoanState.Declined,
        LoanState.Expired => X4MP.Proto.LoanState.Expired,
        LoanState.Forgiven => X4MP.Proto.LoanState.Forgiven,
        _ => X4MP.Proto.LoanState.Cancelled,
    };
}
