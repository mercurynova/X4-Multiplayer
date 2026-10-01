using X4MP.Core.Net;
using X4MP.Core.Permissions;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Relay;

public sealed partial class RelayModule
{
    private readonly record struct IntentKey(int PlayerId, ulong Lo, ulong Hi);

    private sealed class PendingIntent(IntentKey key, uint requestId, long deadline)
    {
        public IntentKey Key { get; } = key;

        public uint RequestId { get; } = requestId;

        public long Deadline { get; } = deadline;
    }

    private readonly Dictionary<IntentKey, PendingIntent> _pendingIntents = [];
    private readonly Dictionary<int, int> _pendingPerPlayer = [];
    private readonly Dictionary<int, ViolationTracker> _rateHits = [];
    private readonly List<PendingIntent> _scratch = [];

    /// <summary>Intents waiting for the authority right now (diagnostics, tests).</summary>
    public int PendingIntentCount => _pendingIntents.Count;

    // ------------------------------------------------------------------ client -> authority

    private void OnIntent(SessionNode node, InboundFrame frame)
    {
        var intent = MessageRegistry.Default.Decode<Intent>(frame.Frame).UnPack();
        intent.RequestKey ??= new Id128T();
        var key = new IntentKey(node.PlayerId, intent.RequestKey.Lo, intent.RequestKey.Hi);
        if (_pendingIntents.ContainsKey(key))
        {
            LogDropped("Intent", "duplicate request key while pending"); // the one result is still coming
            return;
        }

        if (intent.Body is null || intent.Body.Type == IntentBody.NONE || intent.Body.Value is null)
        {
            Reject(node, intent, RejectReason.InvalidParameters, "intent without a body");
            return;
        }

        var options = Opt;
        _pendingPerPlayer.TryGetValue(node.PlayerId, out int inFlight);
        if (inFlight >= options.MaxPendingIntentsPerPlayer)
        {
            Reject(node, intent, RejectReason.RateLimited, "too many intents in flight");
            CountRateLimitHit(node);
            return;
        }

        if (Authority is not { } authority)
        {
            Reject(node, intent, RejectReason.AuthorityUnavailable, "no authority in game");
            return;
        }

        if (Validate(node, intent) is { } refused)
        {
            Reject(node, intent, refused, null);
            return;
        }

        if (CheckPermissions(node, intent) is { } denied)
        {
            Reject(node, intent, denied.Reason, denied.Detail);
            return;
        }

        intent.PlayerId = (ushort)Math.Clamp(node.PlayerId, 0, ushort.MaxValue); // stamped by the server; ignored from clients
        var forward = Encode(MsgType.Intent, fbb => Intent.Pack(fbb, intent), 256);
        bool queued = TrySend(authority, forward);
        forward.Release();
        if (!queued)
        {
            Reject(node, intent, RejectReason.AuthorityUnavailable, "the authority cannot take it now");
            return;
        }

        var pending = new PendingIntent(key, intent.RequestId, Now + (long)(options.IntentTimeoutMs / 1000.0 * _time.TimestampFrequency));
        _pendingIntents[key] = pending;
        _pendingPerPlayer[node.PlayerId] = inFlight + 1;
        Stats.IntentsForwarded++;
    }

    private RejectReason? Validate(SessionNode node, IntentT intent)
    {
        if (_interest is { } interest)
        {
            // protocol.md 16.2: a kill or hit claim needs the target in the sender's interest.
            switch (intent.Body.Type)
            {
                case IntentBody.KillClaim when intent.Body.AsKillClaim() is { } claim && !interest.IsHeld(node.PlayerId, claim.Target):
                    return RejectReason.NotInInterest;
                case IntentBody.HitReport when intent.Body.AsHitReport()?.Hits is { } hits && hits.Any(h => !interest.IsHeld(node.PlayerId, h.Target)):
                    return RejectReason.NotInInterest;
            }
        }

        foreach (var validator in _validators)
        {
            if (validator.Validate(node, intent) is { } reason)
            {
                return reason;
            }
        }

        return null;
    }

    /// <summary>
    /// The asset permission gate (server-design 2.13). A refusal is answered <c>Rejected</c>, recorded as a rate-limited
    /// <see cref="PermissionDenied"/> event, and never forwarded; it is not a protocol violation.
    /// </summary>
    private PermissionVerdict? CheckPermissions(SessionNode node, IntentT intent)
    {
        if (AssetPermissions is not { } gate)
        {
            return null;
        }

        var result = gate.Check(node, intent);
        if (result.Verdict.Allowed)
        {
            return null;
        }

        Stats.IntentsPermissionDenied++;
        if (AllowDeniedEvent(node.PlayerId))
        {
            Publish(new PermissionDenied(
                _time.GetUtcNow(), SessionId, node.PlayerId, result.EntityId, result.Action.ToString(), result.Verdict.Reason.ToString(), result.Verdict.Detail));
        }
        else
        {
            Stats.PermissionDeniedEventsSuppressed++;
        }

        return result.Verdict;
    }

    private readonly Dictionary<int, (long WindowStart, int Count)> _deniedWindows = [];

    private bool AllowDeniedEvent(int playerId)
    {
        long now = Now;
        _deniedWindows.TryGetValue(playerId, out var window);
        if (window.WindowStart == 0 || now - window.WindowStart >= _time.TimestampFrequency)
        {
            window = (now, 0);
        }

        window.Count++;
        _deniedWindows[playerId] = window;
        return window.Count <= Opt.PermissionDeniedEventsPerSecond;
    }

    // ------------------------------------------------------------------ authority -> client

    private void OnIntentResult(SessionNode node, InboundFrame frame)
    {
        if (!node.IsAuthority)
        {
            return;
        }

        var result = MessageRegistry.Default.Decode<IntentResult>(frame.Frame);
        var requestKey = result.RequestKey;
        var key = new IntentKey(result.PlayerId, requestKey?.Lo ?? 0, requestKey?.Hi ?? 0);
        if (!_pendingIntents.TryGetValue(key, out var pending))
        {
            Stats.IntentResultsStale++; // timed out already, or never asked: the sender got its one result
            LogDropped("IntentResult", "no matching pending intent");
            return;
        }

        Complete(pending);
        if (_nodes.TryGetValue(key.PlayerId, out var origin))
        {
            var forward = OutboundFrame.Create(MsgType.IntentResult, frame.Frame.Payload);
            TrySend(origin, forward);
            forward.Release();
            Stats.IntentResultsRelayed++;
        }
    }

    // ------------------------------------------------------------------ results made by the server

    private void Reject(SessionNode node, IntentT intent, RejectReason reason, string? detail) =>
        SendRejected(node, intent.RequestKey, intent.RequestId, reason, detail);

    private void SendRejected(SessionNode node, Id128T? requestKey, uint requestId, RejectReason reason, string? detail)
    {
        var result = new IntentResultT
        {
            RequestKey = requestKey ?? new Id128T(),
            RequestId = requestId,
            PlayerId = (ushort)Math.Clamp(node.PlayerId, 0, ushort.MaxValue),
            Status = IntentStatus.Rejected,
            Reason = reason,
            Detail = detail ?? string.Empty,
        };
        var frame = Encode(MsgType.IntentResult, fbb => IntentResult.Pack(fbb, result), 128);
        TrySend(node, frame);
        frame.Release();
        Stats.IntentsRejected++;
    }

    private void ExpireIntents(long timestamp)
    {
        if (_pendingIntents.Count == 0)
        {
            return;
        }

        _scratch.Clear();
        foreach (var pending in _pendingIntents.Values)
        {
            if (pending.Deadline <= timestamp)
            {
                _scratch.Add(pending);
            }
        }

        foreach (var pending in _scratch)
        {
            Complete(pending);
            Stats.IntentTimeouts++;
            if (_nodes.TryGetValue(pending.Key.PlayerId, out var origin))
            {
                SendRejected(origin, new Id128T { Lo = pending.Key.Lo, Hi = pending.Key.Hi }, pending.RequestId, RejectReason.Timeout, "the authority did not answer in time");
            }
        }

        _scratch.Clear();
    }

    private void FailAllIntents(RejectReason reason, string detail)
    {
        if (_pendingIntents.Count == 0)
        {
            return;
        }

        _scratch.Clear();
        _scratch.AddRange(_pendingIntents.Values);
        foreach (var pending in _scratch)
        {
            Complete(pending);
            if (_nodes.TryGetValue(pending.Key.PlayerId, out var origin))
            {
                SendRejected(origin, new Id128T { Lo = pending.Key.Lo, Hi = pending.Key.Hi }, pending.RequestId, reason, detail);
            }
        }

        _scratch.Clear();
    }

    private void Complete(PendingIntent pending)
    {
        _pendingIntents.Remove(pending.Key);
        if (_pendingPerPlayer.TryGetValue(pending.Key.PlayerId, out int count))
        {
            if (count <= 1)
            {
                _pendingPerPlayer.Remove(pending.Key.PlayerId);
            }
            else
            {
                _pendingPerPlayer[pending.Key.PlayerId] = count - 1;
            }
        }
    }

    /// <summary>A node went away: its intents can no longer be answered (a resume starts clean), and nothing of it is fed upstream.</summary>
    private void DropPlayerWork(int playerId)
    {
        if (_pendingPerPlayer.Remove(playerId))
        {
            _scratch.Clear();
            foreach (var pending in _pendingIntents.Values)
            {
                if (pending.Key.PlayerId == playerId)
                {
                    _scratch.Add(pending);
                }
            }

            foreach (var pending in _scratch)
            {
                _pendingIntents.Remove(pending.Key);
            }

            _scratch.Clear();
        }

        if (_flows.TryGetValue(playerId, out var flow))
        {
            flow.Dirty = false;
            flow.Latest = null;
        }
    }

    // ------------------------------------------------------------------ rate-limit strikes (intents and chat)

    /// <summary>Counts one rate-limit hit against the node; sustained hitting closes it with <c>RateLimited</c>.</summary>
    private void CountRateLimitHit(SessionNode node)
    {
        if (!_rateHits.TryGetValue(node.PlayerId, out var tracker))
        {
            tracker = new ViolationTracker(int.MaxValue, _time);
            _rateHits[node.PlayerId] = tracker;
        }

        if (tracker.Record() > Opt.RateLimitHitsPerMinute)
        {
            node.Connection?.Close(DisconnectCode.RateLimited, "relay rate limit");
        }
    }
}
