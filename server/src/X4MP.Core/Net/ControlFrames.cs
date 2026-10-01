using Google.FlatBuffers;
using X4MP.Proto;

namespace X4MP.Core.Net;

/// <summary>Builders for the small connection-control frames the networking layer itself sends.</summary>
public static class ControlFrames
{
    /// <summary>A <c>Disconnect</c> frame (Control lane). Strings are truncated to keep the frame tiny.</summary>
    public static OutboundFrame Disconnect(DisconnectCode code, string? message = null, string? expected = null, uint retryAfterMs = 0)
    {
        var fbb = new FlatBufferBuilder(128);
        var msg = message is null ? default : fbb.CreateString(Truncate(message));
        var exp = expected is null ? default : fbb.CreateString(Truncate(expected));
        var offset = X4MP.Proto.Disconnect.CreateDisconnect(fbb, code, msg, exp, retryAfterMs);
        fbb.Finish(offset.Value);
        return OutboundFrame.Create(MsgType.Disconnect, fbb);
    }

    /// <summary>A <c>Pong</c> answering a <c>Ping</c> (Control lane).</summary>
    public static OutboundFrame Pong(uint seq, ulong echoSendTimeUs, ulong recvTimeUs, ulong replyTimeUs)
    {
        var fbb = new FlatBufferBuilder(64);
        var offset = X4MP.Proto.Pong.CreatePong(fbb, seq, echoSendTimeUs, recvTimeUs, replyTimeUs);
        fbb.Finish(offset.Value);
        return OutboundFrame.Create(MsgType.Pong, fbb);
    }

    /// <summary>Frames any object-API table. <paramref name="pack"/> must return the root offset value.</summary>
    public static OutboundFrame Encode(MsgType type, Func<FlatBufferBuilder, int> pack, int initialSize = 256, ulong coalesceKey = 0)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var fbb = new FlatBufferBuilder(initialSize);
        fbb.Finish(pack(fbb));
        return OutboundFrame.Create(type, fbb, coalesceKey);
    }

    private static string Truncate(string s) => s.Length <= 512 ? s : s[..512];
}
