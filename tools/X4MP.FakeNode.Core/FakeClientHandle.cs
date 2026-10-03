using System.Collections.Concurrent;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

/// <summary>The server's answer to an order sent through <see cref="FakeClientHandle"/>.</summary>
public sealed record OrderOutcome(IntentStatus Status, RejectReason Reason);

/// <summary>
/// A running fake client as seen by a test or tool (M1-F3): who it is, what it knows, and a way to send one targeted <c>AssetOrder</c> and wait for the
/// server's answer (the <c>--commander</c> loop picks its targets by itself; this is for checking one asset against one team).
/// </summary>
public sealed class FakeClientHandle
{
    private const ulong ProbeKeyBase = 1UL << 40;

    private readonly NodeLink _link;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<OrderOutcome>> _pending = new();
    private long _probeSeq;

    internal FakeClientHandle(NodeLink link, FakeClientSession session, LiveNodeStats stats, string name)
    {
        _link = link;
        Session = session;
        Stats = stats;
        Name = name;
    }

    public string Name { get; }

    public int PlayerId => _link.PlayerId;

    /// <summary>The team the server last reported for this node (<c>RosterUpdate</c>).</summary>
    public int TeamId => _link.Team;

    public FakeClientSession Session { get; }

    public LiveNodeStats Stats { get; }

    /// <summary>Sends an <c>AssetOrder</c> (move) for one asset and returns the server's answer, or null when none came within <paramref name="timeout"/>.</summary>
    public async Task<OrderOutcome?> SendOrderAsync(uint netId, ushort sector, TimeSpan timeout, CancellationToken ct = default)
    {
        ulong key = ProbeKeyBase + (ulong)Interlocked.Increment(ref _probeSeq);
        var tcs = new TaskCompletionSource<OrderOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;
        try
        {
            var intent = BuildOrder(_link.PlayerId, key, 0, netId, sector);
            await _link.Client.SendAsync(MsgType.Intent, b => Intent.Pack(b, intent), ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    /// <summary>Sends a chat message (<c>ChatSend</c>) as this client: all, team, or a whisper to <paramref name="toPlayer"/> (m3-plan 4.11).</summary>
    public async Task SendChatAsync(ChatChannel channel, string text, ushort toPlayer = 0, CancellationToken ct = default)
    {
        var chat = new ChatSendT { Channel = channel, ToPlayer = toPlayer, Text = text };
        await _link.Client.SendPayloadAsync(MsgType.ChatSend, MessageEncoder.EncodePayload(b => ChatSend.Pack(b, chat), 320), ct).ConfigureAwait(false);
    }

    /// <summary>Called by the client's frame handler for every <c>IntentResult</c>.</summary>
    internal void OnIntentResult(ulong key, IntentStatus status, RejectReason reason)
    {
        if (_pending.TryGetValue(key, out var tcs))
            tcs.TrySetResult(new OrderOutcome(status, reason));
    }

    internal static IntentT BuildOrder(int playerId, ulong key, long gameTime, uint netId, ushort sector) => new()
    {
        RequestKey = new Id128T { Lo = key, Hi = (ulong)playerId },
        RequestId = (uint)key,
        GameTime = gameTime,
        Body = IntentBodyUnion.FromAssetOrder(new AssetOrderT { Asset = netId, Order = OrderKind.MoveTo, Sector = sector }),
    };
}
