using System.Collections.Frozen;
using Google.FlatBuffers;
using X4MP.Proto;

namespace X4MP.Protocol;

/// <summary>Catalog entry for one message type: its lane and how to verify and decode its table.</summary>
public sealed class MessageDescriptor
{
    /// <summary>
    /// Largest payload the FlatBuffers C# Verifier (Google.FlatBuffers 25.2.10) can be trusted with.
    /// Its vtable arithmetic uses Convert.ToInt16 on absolute buffer positions, so in buffers larger than
    /// 32767 bytes it throws internally (and prints to the console) and then treats the fields as absent,
    /// i.e. it silently verifies nothing. Larger payloads skip the Verifier and rely on the full-read pass.
    /// </summary>
    public const int VerifierReliableLimit = short.MaxValue;

    private readonly Func<ByteBuffer, IFlatbufferObject> _root;
    private readonly Func<Verifier, uint, bool>? _verify;
    private readonly Action<IFlatbufferObject> _fullRead;

    internal MessageDescriptor(MsgType type, Lane lane, Type clrType, Func<ByteBuffer, IFlatbufferObject> root,
        Func<Verifier, uint, bool>? verify, Action<IFlatbufferObject> fullRead)
    {
        Type = type;
        Lane = lane;
        ClrType = clrType;
        _root = root;
        _verify = verify;
        _fullRead = fullRead;
    }

    public MsgType Type { get; }

    /// <summary>Catalog lane (protocol.md section 20). The frame header lane byte must equal it.</summary>
    public Lane Lane { get; }

    /// <summary>The generated FlatBuffers table struct (e.g. <c>X4MP.Proto.Ping</c>).</summary>
    public Type ClrType { get; }

    public string Name => Type.ToString();

    internal IFlatbufferObject Decode(byte[] payload)
    {
        var bb = new ByteBuffer(payload);

        // 1. The FlatBuffers Verifier (bounds, alignment, depth/table limits) where it works: small buffers
        //    and tables without unions (its VerifyUnion reads the type byte at the wrong offset).
        if (_verify is not null && payload.Length <= VerifierReliableLimit)
        {
            if (!new Verifier(bb).VerifyBuffer(null, false, new VerifyTableAction(_verify)))
                throw new ProtocolViolation(ViolationCode.MalformedPayload, $"{Name}: FlatBuffers verification failed");
        }

        // 2. Full-read pass. ByteBuffer reads are bounds-checked, so a hostile buffer can never read out of
        //    the payload, but it can make a later field read throw. Reading every reachable field here turns
        //    that into a ProtocolViolation up front, so a decoded value is always safe for the caller to
        //    read. The schema has no recursive table types, so this cannot recurse without bound.
        var message = _root(bb);
        _fullRead(message);
        return message;
    }
}

/// <summary>
/// MsgType to decoder registry. <see cref="Decode(MsgType, byte[])"/> is the guarded entry point: every
/// failure (unknown or reserved type, lane mismatch, verifier failure, any exception while reading)
/// becomes a <see cref="ProtocolViolation"/>. The decoded value is the generated table struct, boxed.
/// </summary>
public sealed partial class MessageRegistry
{
    private readonly Dictionary<MsgType, MessageDescriptor> _building = [];
    private FrozenDictionary<MsgType, MessageDescriptor> _map = FrozenDictionary<MsgType, MessageDescriptor>.Empty;

    /// <summary>The registry for the full v0.1 catalog.</summary>
    public static MessageRegistry Default { get; } = Create();

    private static MessageRegistry Create()
    {
        var r = new MessageRegistry();
        r.RegisterAll();
        r._map = r._building.ToFrozenDictionary();
        r._building.Clear();
        return r;
    }

    private void Add<T>(MsgType type, Lane lane, Func<ByteBuffer, T> root, Func<Verifier, uint, bool>? verify, Action<T> fullRead)
        where T : struct, IFlatbufferObject
    {
        _building.Add(type, new MessageDescriptor(type, lane, typeof(T), bb => root(bb), verify, o => fullRead((T)o)));
    }

    public IEnumerable<MessageDescriptor> Descriptors => _map.Values;

    public bool TryGetDescriptor(MsgType type, out MessageDescriptor descriptor) => _map.TryGetValue(type, out descriptor!);

    /// <summary>Descriptor for a known type; throws <see cref="ProtocolViolation"/> if unknown or reserved.</summary>
    public MessageDescriptor GetDescriptor(MsgType type)
    {
        if (_map.TryGetValue(type, out var d))
            return d;
        throw type == MsgType.DamageReport
            ? new ProtocolViolation(ViolationCode.ReservedMessageType, $"message type 0x{(ushort)type:X4} is reserved")
            : new ProtocolViolation(ViolationCode.UnknownMessageType, $"unknown message type 0x{(ushort)type:X4}");
    }

    /// <summary>
    /// Verifies and decodes a payload. The returned struct (as <see cref="IFlatbufferObject"/>) is safe to
    /// read. Throws <see cref="ProtocolViolation"/> only.
    /// </summary>
    public IFlatbufferObject Decode(MsgType type, byte[] payload)
    {
        var d = GetDescriptor(type);
        if (payload.Length == 0)
            throw new ProtocolViolation(ViolationCode.ZeroLengthFrame, $"{d.Name}: empty payload");
        try
        {
            return d.Decode(payload);
        }
        catch (ProtocolViolation)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Defence in depth: a hostile buffer must never surface IndexOutOfRange and friends.
            throw new ProtocolViolation(ViolationCode.MalformedPayload, $"{d.Name}: {ex.GetType().Name}", ex);
        }
    }

    /// <summary>Verifies the lane against the catalog, then decodes the frame's payload.</summary>
    public IFlatbufferObject Decode(in Frame frame)
    {
        var d = GetDescriptor(frame.Type);
        if (frame.Lane != d.Lane)
            throw new ProtocolViolation(ViolationCode.LaneMismatch, $"{d.Name}: lane {frame.Lane}, catalog says {d.Lane}");
        return Decode(frame.Type, frame.Payload);
    }

    /// <summary>Typed decode. Throws <see cref="ProtocolViolation"/> if the type does not map to <typeparamref name="T"/>.</summary>
    public T Decode<T>(in Frame frame) where T : struct, IFlatbufferObject
    {
        var d = GetDescriptor(frame.Type);
        if (d.ClrType != typeof(T))
            throw new ProtocolViolation(ViolationCode.MalformedPayload, $"{d.Name} is {d.ClrType.Name}, not {typeof(T).Name}");
        return (T)Decode(frame);
    }
}
