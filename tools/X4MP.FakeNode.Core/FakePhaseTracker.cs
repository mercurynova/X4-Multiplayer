using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

/// <summary>
/// Follows the node phase the server reports for this node in <c>RosterUpdate</c>s. The server checks every frame against the phase it has
/// recorded (protocol.md 5, the policy table), and it processes <c>LoadStatus</c> asynchronously, so a node that sends
/// <c>LoadStatus{Matching}</c> and <c>ManifestReport</c> back to back would be too early; a real mod spends seconds loading, a fake one waits for
/// the roster to confirm the phase instead (and carries on after a timeout, for servers that do not send rosters).
/// </summary>
public sealed class FakePhaseTracker(int playerId)
{
    private int _phase = -1;

    /// <summary>The last phase the server reported for this node (null before the first roster).</summary>
    public NodePhase? Phase => Volatile.Read(ref _phase) is var p and >= 0 ? (NodePhase)p : null;

    /// <summary>Feeds one received frame; true when it was a roster that mentioned this node.</summary>
    public bool Observe(Frame frame)
    {
        if (frame.Type != MsgType.RosterUpdate)
            return false;
        var roster = MessageRegistry.Default.Decode<RosterUpdate>(frame);
        for (int i = 0; i < roster.PlayersLength; i++)
        {
            if (roster.Players(i) is { } p && p.PlayerId == playerId)
            {
                Volatile.Write(ref _phase, (int)p.Phase);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Waits until the server reports a phase at or past <paramref name="phase"/> (the pipeline order), up to <paramref name="timeout"/>.
    /// Returns false on a timeout (the caller carries on: the server may not send rosters at all).
    /// </summary>
    public async Task<bool> WaitForAsync(NodePhase phase, TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            while (Phase is not { } current || (int)current < (int)phase || current is NodePhase.Detached or NodePhase.Failed)
            {
                await Task.Delay(5, linked.Token).ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
