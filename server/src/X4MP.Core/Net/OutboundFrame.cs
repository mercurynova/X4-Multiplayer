using System.Buffers;
using Google.FlatBuffers;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Net;

/// <summary>
/// A fully framed (header + payload) outbound message, ref-counted so one encode can fan out to N
/// connections. The buffer comes from <see cref="ArrayPool{T}"/> and goes back when the last reference is
/// released. Create with refcount 1; each queue that keeps the frame takes its own reference.
/// </summary>
public sealed class OutboundFrame
{
    private byte[]? _buffer;
    private readonly int _length;
    private int _refs = 1;

    private OutboundFrame(byte[] buffer, int length, MsgType type, Lane lane, ulong coalesceKey)
    {
        _buffer = buffer;
        _length = length;
        MessageType = type;
        Lane = lane;
        CoalesceKey = coalesceKey;
    }

    public Lane Lane { get; }

    public MsgType MessageType { get; }

    /// <summary>0 = never coalesce. Only the Realtime lane coalesces (latest wins per key).</summary>
    public ulong CoalesceKey { get; }

    /// <summary>Total framed length in bytes (8-byte header + payload).</summary>
    public int Length => _length;

    /// <summary>Fully framed bytes. Valid only while the caller holds a reference.</summary>
    public ReadOnlyMemory<byte> Bytes => (_buffer ?? throw new ObjectDisposedException(nameof(OutboundFrame))).AsMemory(0, _length);

    /// <summary>Current reference count (diagnostics and tests).</summary>
    public int RefCount => Volatile.Read(ref _refs);

    /// <summary>Frames <paramref name="payload"/> with the catalog lane for <paramref name="type"/>.</summary>
    public static OutboundFrame Create(MsgType type, ReadOnlySpan<byte> payload, ulong coalesceKey = 0, int maxFrameBytes = FrameCodec.DefaultMaxFrameBytes) =>
        Create(type, MessageRegistry.Default.GetDescriptor(type).Lane, payload, coalesceKey, maxFrameBytes);

    /// <summary>Frames <paramref name="payload"/> with an explicit lane.</summary>
    public static OutboundFrame Create(MsgType type, Lane lane, ReadOnlySpan<byte> payload, ulong coalesceKey = 0, int maxFrameBytes = FrameCodec.DefaultMaxFrameBytes)
    {
        if (payload.Length == 0)
        {
            throw new ArgumentException("A frame payload cannot be empty.", nameof(payload));
        }

        if (payload.Length > maxFrameBytes)
        {
            throw new ArgumentException($"Payload {payload.Length} exceeds MaxFrameBytes {maxFrameBytes}.", nameof(payload));
        }

        int total = FrameCodec.HeaderSize + payload.Length;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(total);
        FrameCodec.WriteHeader(buffer, (uint)payload.Length, type, FrameOptions.None, lane);
        payload.CopyTo(buffer.AsSpan(FrameCodec.HeaderSize));
        return new OutboundFrame(buffer, total, type, lane, coalesceKey);
    }

    /// <summary>Frames a finished <see cref="FlatBufferBuilder"/> (after <c>Finish</c>) with the catalog lane.</summary>
    public static OutboundFrame Create(MsgType type, FlatBufferBuilder finished, ulong coalesceKey = 0, int maxFrameBytes = FrameCodec.DefaultMaxFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(finished);
        var bb = finished.DataBuffer;
        return Create(type, new ReadOnlySpan<byte>(bb.ToSizedArray()), coalesceKey, maxFrameBytes);
    }

    /// <summary>Takes an additional reference (Interlocked). Throws if the frame was already fully released.</summary>
    public OutboundFrame AddRef()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _refs);
            ObjectDisposedException.ThrowIf(current <= 0, this);
        }
        while (Interlocked.CompareExchange(ref _refs, current + 1, current) != current);

        return this;
    }

    /// <summary>Drops a reference; the pooled buffer is returned at zero.</summary>
    public void Release()
    {
        int now = Interlocked.Decrement(ref _refs);
        if (now == 0)
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        else if (now < 0)
        {
            throw new InvalidOperationException("OutboundFrame released more often than referenced.");
        }
    }
}
