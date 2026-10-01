using System.Text;
using X4MP.Core.Events;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Relay;

public sealed partial class RelayModule
{
    /// <summary>Longest chat text (protocol.md 22: 256 characters).</summary>
    public const int MaxChatLength = 256;

    private static readonly TimeSpan NoticeInterval = TimeSpan.FromSeconds(5);

    private sealed class ChatBucket
    {
        public double Tokens { get; set; }

        public long LastTick { get; set; }

        public long NextNoticeTick { get; set; }
    }

    private readonly Dictionary<int, DateTimeOffset?> _mutes = [];
    private readonly Dictionary<int, ChatBucket> _chatBuckets = [];
    private bool _mutesLoaded;

    /// <summary>
    /// Reads the stored mutes once, on first use (not in the constructor: the database is migrated after the host builds its services).
    /// A mute set before the load wins over a stored one.
    /// </summary>
    private void EnsureMutesLoaded()
    {
        if (_mutesLoaded)
        {
            return;
        }

        _mutesLoaded = true;
        try
        {
            foreach (var entry in _chat.LoadMutes())
            {
                _mutes.TryAdd(entry.PlayerId, entry.Until);
            }
        }
        catch (Exception ex)
        {
            LogMutesLoadFailed(ex);
        }
    }

    // ------------------------------------------------------------------ player chat

    private void OnChatSend(SessionNode node, InboundFrame frame)
    {
        var send = MessageRegistry.Default.Decode<ChatSend>(frame.Frame).UnPack();
        var options = Opt;
        string text = CleanChat(send.Text);
        if (text.Length == 0)
        {
            Stats.ChatRejected++;
            return;
        }

        if (!TakeChatToken(node, options))
        {
            Stats.ChatRateLimited++;
            CountRateLimitHit(node);
            return;
        }

        var channel = send.Channel;
        bool admin = (node.Roles & Role.Admin) != 0;
        if (channel == ChatChannel.System || (channel == ChatChannel.Admin && !admin))
        {
            Stats.ChatRejected++;
            Notice(node, channel == ChatChannel.Admin ? "Only admins may use the admin channel." : "That channel is not available.");
            return;
        }

        if (!options.ChatEnabled && !admin)
        {
            Stats.ChatRejected++;
            Notice(node, "Chat is disabled on this server.");
            return;
        }

        if (IsMutedNow(node.PlayerId))
        {
            Stats.ChatMuted++;
            Notice(node, "You are muted.");
            return;
        }

        switch (channel)
        {
            case ChatChannel.Team:
                if (node.TeamId == 0)
                {
                    Notice(node, "You are not on a team.");
                    return;
                }

                Deliver(node.PlayerId, node.Name, channel, text, target => target.TeamId == node.TeamId);
                break;
            case ChatChannel.Whisper:
                if (!_nodes.TryGetValue(send.ToPlayer, out var to) || !to.IsAttached)
                {
                    Notice(node, "That player is not online.");
                    return;
                }

                Deliver(node.PlayerId, node.Name, channel, text, target => target.PlayerId == to.PlayerId || target.PlayerId == node.PlayerId);
                break;
            default: // All, Admin
                Deliver(node.PlayerId, node.Name, channel, text, static _ => true);
                break;
        }

        Persist(node.PlayerId, null, channel, text);
    }

    /// <summary>Control characters out, trimmed, at most <see cref="MaxChatLength"/> characters (never splitting a surrogate pair).</summary>
    public static string CleanChat(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(text.Length, MaxChatLength + 1));
        foreach (char c in text)
        {
            if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        string clean = builder.ToString().Trim();
        if (clean.Length > MaxChatLength)
        {
            int cut = MaxChatLength;
            if (char.IsHighSurrogate(clean[cut - 1]))
            {
                cut--;
            }

            clean = clean[..cut].TrimEnd();
        }

        return clean;
    }

    private bool TakeChatToken(SessionNode node, RelayOptions options)
    {
        long now = Now;
        if (!_chatBuckets.TryGetValue(node.PlayerId, out var bucket))
        {
            bucket = new ChatBucket { Tokens = options.ChatBurst, LastTick = now };
            _chatBuckets[node.PlayerId] = bucket;
        }

        double elapsed = (now - bucket.LastTick) / (double)_time.TimestampFrequency;
        bucket.LastTick = now;
        bucket.Tokens = Math.Min(options.ChatBurst, bucket.Tokens + (elapsed * options.ChatPerSecond));
        if (bucket.Tokens >= 1)
        {
            bucket.Tokens -= 1;
            return true;
        }

        if (now >= bucket.NextNoticeTick)
        {
            bucket.NextNoticeTick = now + (long)(NoticeInterval.TotalSeconds * _time.TimestampFrequency);
            Notice(node, "You are sending messages too quickly.");
        }

        return false;
    }

    // ------------------------------------------------------------------ delivery

    private int Deliver(int fromPlayer, string fromName, ChatChannel channel, string text, Func<SessionNode, bool> wants)
    {
        var message = new ChatMessageT
        {
            FromPlayer = (ushort)Math.Clamp(fromPlayer, 0, ushort.MaxValue),
            FromName = fromName,
            Channel = channel,
            Text = text,
            ServerTimeUs = (ulong)Math.Max(0, _driver?.ServerTimeUs ?? 0),
        };
        var frame = Encode(MsgType.ChatMessage, fbb => ChatMessage.Pack(fbb, message), 128 + text.Length);
        int delivered = 0;
        foreach (var target in _nodes.Values)
        {
            if (target.IsAttached && target.Phase != NodePhase.Detached && wants(target) && TrySend(target, frame))
            {
                delivered++;
            }
        }

        frame.Release();
        Stats.ChatDelivered += delivered;
        return delivered;
    }

    /// <summary>A private <c>System</c> message to one node (mute, rate limit, refusals).</summary>
    private void Notice(SessionNode node, string text)
    {
        var message = new ChatMessageT
        {
            FromPlayer = 0,
            FromName = "server",
            Channel = ChatChannel.System,
            Text = text,
            ServerTimeUs = (ulong)Math.Max(0, _driver?.ServerTimeUs ?? 0),
        };
        var frame = Encode(MsgType.ChatMessage, fbb => ChatMessage.Pack(fbb, message), 128 + text.Length);
        TrySend(node, frame);
        frame.Release();
    }

    private void Persist(int? fromPlayer, string? fromAdmin, ChatChannel channel, string text)
    {
        var now = _time.GetUtcNow();
        long? session = SessionId;
        _chat.Append(new ChatLine(now, session ?? 0, fromPlayer, fromAdmin, channel.ToString(), text));
        Publish(new ChatPosted(now, session, fromPlayer, fromAdmin, channel.ToString(), text));
    }

    // ------------------------------------------------------------------ mute

    private bool IsMutedNow(int playerId)
    {
        EnsureMutesLoaded();
        if (!_mutes.TryGetValue(playerId, out var until))
        {
            return false;
        }

        if (until is { } end && end <= _time.GetUtcNow())
        {
            _mutes.Remove(playerId); // expired
            _chat.SetMute(playerId, null);
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------------ IChatControl (admin side)

    private Task<T> OnActor<T>(Func<T> work) =>
        (_driver ?? throw new InvalidOperationException("The relay is not attached to a session actor.")).CallAsync(work);

    public Task<bool> MuteAsync(int playerId, TimeSpan? duration = null, string actor = "system", string? reason = null) => OnActor(() =>
    {
        EnsureMutesLoaded();
        DateTimeOffset? until = duration is { } d ? _time.GetUtcNow() + d : null;
        _mutes[playerId] = until;
        var entry = new MuteEntry(playerId, until);
        _chat.SetMute(playerId, entry);
        Publish(new AdminActionTaken(
            _time.GetUtcNow(), SessionId, actor, "player.mute", playerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new Dictionary<string, string?>
            {
                ["reason"] = reason,
                ["until"] = until?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            },
            null));
        if (_nodes.TryGetValue(playerId, out var node))
        {
            Notice(node, "You have been muted.");
        }

        return true;
    });

    public Task<bool> UnmuteAsync(int playerId, string actor = "system") => OnActor(() =>
    {
        EnsureMutesLoaded();
        if (!_mutes.Remove(playerId))
        {
            return false;
        }

        _chat.SetMute(playerId, null);
        Publish(new AdminActionTaken(
            _time.GetUtcNow(), SessionId, actor, "player.unmute", playerId.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null));
        if (_nodes.TryGetValue(playerId, out var node))
        {
            Notice(node, "You have been unmuted.");
        }

        return true;
    });

    public Task<bool> IsMutedAsync(int playerId) => OnActor(() => IsMutedNow(playerId));

    public Task<IReadOnlyList<MuteEntry>> MutedAsync() => OnActor<IReadOnlyList<MuteEntry>>(() =>
    {
        EnsureMutesLoaded();
        foreach (int id in _mutes.Keys.ToArray())
        {
            IsMutedNow(id); // drops the expired ones
        }

        return [.. _mutes.Select(m => new MuteEntry(m.Key, m.Value)).OrderBy(m => m.PlayerId)];
    });

    public Task<int> SendAsync(string adminName, string text, ChatChannel channel = ChatChannel.Admin, int? toPlayer = null) => OnActor(() =>
    {
        string clean = CleanChat(text);
        if (clean.Length == 0)
        {
            return 0;
        }

        int delivered = Deliver(0, adminName, channel, clean, target => toPlayer is not { } id || target.PlayerId == id);
        Persist(null, adminName, channel, clean);
        return delivered;
    });
}
