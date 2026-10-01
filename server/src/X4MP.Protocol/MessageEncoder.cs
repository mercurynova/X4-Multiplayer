using Google.FlatBuffers;
using X4MP.Proto;

namespace X4MP.Protocol;

/// <summary>Helpers that turn generated object-API tables (<c>XxxT</c>) into payload bytes and TCP frames.</summary>
public static class MessageEncoder
{
    /// <summary>Builds a FlatBuffers payload. <paramref name="pack"/> is typically <c>b =&gt; Xxx.Pack(b, t)</c>.</summary>
    public static byte[] EncodePayload<T>(Func<FlatBufferBuilder, Offset<T>> pack, int initialSize = 256) where T : struct
    {
        var fbb = new FlatBufferBuilder(initialSize);
        fbb.Finish(pack(fbb).Value);
        return fbb.SizedByteArray();
    }

    /// <summary>Builds a complete TCP frame (header + payload); the lane comes from the message catalog.</summary>
    public static byte[] EncodeFrame<T>(MsgType type, Func<FlatBufferBuilder, Offset<T>> pack, int initialSize = 256) where T : struct =>
        FrameCodec.Encode(type, EncodePayload(pack, initialSize));
}
