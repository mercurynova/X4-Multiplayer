using Microsoft.Extensions.Logging;
using Google.FlatBuffers;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;

namespace X4MP.Core.Saves;

// F24 (ADR-054): a checkpoint before a PLANNED authority leave. The authority mod sends LeaveSaveRequest when its player disconnects (or exits to
// the main menu / quits) with the universe still loaded; the server takes a fresh checkpoint with SaveReason.Leave and answers LeaveSaveResult
// once it is current (or could not be made). The mod then sends its Disconnect(ClientQuit). A crash, a lost socket or a leave from the main menu
// never arrives here: the universe is gone, there is nothing to save.
public sealed partial class SaveService
{
    private SessionNode? _leaveNode;
    private bool _leaveStarted;

    /// <summary>True while a planned authority leave waits for its checkpoint (tests, GUI).</summary>
    public bool LeaveSavePending => _leaveNode is not null;

    private void OnLeaveSaveRequest(SessionNode node, LeaveSaveRequest request)
    {
        if (!node.IsAuthority || node != _authority || node.Connection is null)
        {
            LogRefused(node.PlayerId, "LeaveSaveRequest from a node that is not the authority");
            node.Connection?.Stats.AddViolation();
            return;
        }

        if (_leaveNode is not null)
        {
            return; // a repeat while the first is waiting: the one answer will do
        }

        if (_phase is not (SessionPhase.Running or SessionPhase.Paused) || node.Phase != NodePhase.InGame)
        {
            ReplyLeave(node, false, $"the session is {_phase}, no checkpoint is taken");
            return;
        }

        _leaveNode = node;
        _leaveStarted = false;
        LogLeaveRequested(node.PlayerId, request.Kind);
        TryStartLeaveSave();
    }

    /// <summary>Sends the leave checkpoint request as soon as no other request is in flight (an earlier one is finished first: its state is older than the leave).</summary>
    private void TryStartLeaveSave()
    {
        if (_leaveNode is not { } node || _leaveStarted || _inFlight is not null)
        {
            return;
        }

        if (SendRequestSave(SaveReason.Leave))
        {
            _leaveStarted = true;
        }
        else
        {
            ReplyLeave(node, false, "the authority could not be asked for a checkpoint");
        }
    }

    /// <summary>The request that was in flight is over (current, stored but not current, failed or timed out): answers a waiting leave.</summary>
    private void OnLeaveRequestFinished(bool current, string detail)
    {
        if (_leaveNode is { } node && _leaveStarted)
        {
            ReplyLeave(node, current, current ? "checkpoint is current" : detail);
        }
        else
        {
            TryStartLeaveSave(); // an earlier request just finished: the leave checkpoint can go now
        }
    }

    private void ReplyLeave(SessionNode node, bool saved, string detail)
    {
        if (_leaveNode == node)
        {
            _leaveNode = null;
            _leaveStarted = false;
        }

        var result = new LeaveSaveResultT { Saved = saved, Detail = detail };
        Send(node, ControlFrames.Encode(MsgType.LeaveSaveResult, fbb => LeaveSaveResult.Pack(fbb, result).Value, 64));
        LogLeaveAnswered(node.PlayerId, saved, detail);
    }

    private void ClearLeave(SessionNode node)
    {
        if (_leaveNode == node)
        {
            _leaveNode = null;
            _leaveStarted = false;
        }
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "player {PlayerId}: the authority leaves on purpose (kind {Kind}): taking a checkpoint first")]
    private partial void LogLeaveRequested(int playerId, byte kind);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "player {PlayerId}: leave checkpoint answered saved={Saved}: {Detail}")]
    private partial void LogLeaveAnswered(int playerId, bool saved, string detail);
}
