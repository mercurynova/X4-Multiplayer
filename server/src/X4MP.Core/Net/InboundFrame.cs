using X4MP.Protocol;

namespace X4MP.Core.Net;

/// <summary>
/// One structurally valid frame read from a node (header validated against MaxFrameBytes before the payload
/// was allocated). The payload is not yet decoded: use <c>MessageRegistry.Default.Decode</c>.
/// </summary>
/// <param name="Frame">Type, flags, lane and the FlatBuffers payload.</param>
/// <param name="ReceivedTicks"><see cref="TimeProvider.GetTimestamp"/> value when the frame was fully read.</param>
public readonly record struct InboundFrame(Frame Frame, long ReceivedTicks)
{
    public X4MP.Proto.MsgType Type => Frame.Type;
}
