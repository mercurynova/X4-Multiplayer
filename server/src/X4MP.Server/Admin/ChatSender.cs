using System.Globalization;
using X4MP.Core.Relay;
using X4MP.Persistence;
using X4MP.Proto;
using X4MP.Server.Api;
using X4MP.Server.Auth;

namespace X4MP.Server.Admin;

/// <summary>Outcome of an admin chat send: delivered count, or validation errors, or a conflict (player offline).</summary>
internal sealed record ChatSendResult(int Delivered, Dictionary<string, string[]>? Errors, string? ConflictCode, string? ConflictMessage)
{
    public bool Ok => Errors is null && ConflictCode is null;
}

/// <summary>The admin chat send shared by <c>POST /api/v1/chat</c> and the hub's <c>SendChat</c>: validation, delivery and audit in one place.</summary>
internal static class ChatSender
{
    public static async Task<ChatSendResult> SendAsync(
        SendChatRequest? body, string actor, string? remoteIp, AdminSessions sessions, IChatControl chat, AdminStore audit)
    {
        var errors = new Dictionary<string, string[]>();
        string text = RelayModule.CleanChat(body?.Text);
        if (text.Length == 0)
        {
            errors["text"] = ["Text is required (control characters are removed)."];
        }
        else if (text.Length >= RelayModule.MaxChatLength && body!.Text!.Count(c => !char.IsControl(c)) > RelayModule.MaxChatLength)
        {
            errors["text"] = [$"The text can be at most {RelayModule.MaxChatLength} characters."];
        }

        string channel = body?.Channel?.Trim().ToLowerInvariant() ?? "all";
        if (channel is not ("all" or "player"))
        {
            errors["channel"] = ["Use all or player."];
        }
        else if (channel == "player" && body?.ToPlayerId is null)
        {
            errors["toPlayerId"] = ["toPlayerId is required for the player channel."];
        }

        if (errors.Count > 0)
        {
            return new ChatSendResult(0, errors, null, null);
        }

        int? target = null;
        if (channel == "player")
        {
            long to = body!.ToPlayerId!.Value;
            var live = await sessions.GetLiveAsync().ConfigureAwait(false);
            if (to > int.MaxValue || !live.IsConnected(to))
            {
                return new ChatSendResult(0, null, "PlayerNotOnline", "The player is not connected.");
            }

            target = (int)to;
        }

        bool broadcast = body?.AsBroadcast ?? false;
        var kind = broadcast ? ChatChannel.System : ChatChannel.Admin;
        int delivered = await chat.SendAsync(actor, text, kind, target).ConfigureAwait(false);
        audit.Audit(actor, "chat.send", target?.ToString(CultureInfo.InvariantCulture) ?? "all", remoteIp, new Dictionary<string, string?>
        {
            ["channel"] = channel,
            ["broadcast"] = broadcast ? "true" : "false",
            ["length"] = text.Length.ToString(CultureInfo.InvariantCulture),
            ["delivered"] = delivered.ToString(CultureInfo.InvariantCulture),
        });
        return new ChatSendResult(delivered, null, null, null);
    }
}
