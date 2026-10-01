namespace X4MP.Protocol;

/// <summary>Why inbound bytes were rejected. Every decode failure maps to one of these.</summary>
public enum ViolationCode
{
    /// <summary>payload_len exceeds MaxFrameBytes (checked before any allocation).</summary>
    FrameTooLarge,
    /// <summary>payload_len is 0: a FlatBuffers message is never empty.</summary>
    ZeroLengthFrame,
    /// <summary>The stream or buffer ended inside a header or payload.</summary>
    TruncatedFrame,
    /// <summary>Reserved flag bits (bit1..7) are set.</summary>
    ReservedFlags,
    /// <summary>The Compressed flag is set but Lz4Frames is not negotiated (off in v1).</summary>
    CompressedNotSupported,
    /// <summary>Lane byte is not 0, 1 or 2.</summary>
    InvalidLane,
    /// <summary>Lane byte does not match the catalog lane for this message type.</summary>
    LaneMismatch,
    /// <summary>msg_type is not in the catalog (callers may skip it when the peer's minor is higher).</summary>
    UnknownMessageType,
    /// <summary>msg_type is reserved (e.g. DamageReport) and has no decoder yet.</summary>
    ReservedMessageType,
    /// <summary>The FlatBuffers Verifier rejected the payload, or reading it threw.</summary>
    MalformedPayload,
    /// <summary>A float field is NaN or infinite, or a value is outside its encodable range.</summary>
    InvalidValue,
    /// <summary>UDP datagram header problem (magic, major version, reserved bits, size).</summary>
    MalformedDatagram,
    /// <summary>Replication entry stream is malformed (truncated, unknown net_id 0, bad EXT length...).</summary>
    MalformedReplication,
}

/// <summary>
/// Thrown by every decoder in this library for any malformed or hostile input. Decoders never let
/// IndexOutOfRange, ArgumentOutOfRange or similar escape. Maps to Disconnect(MalformedMessage) and
/// counts toward the violation limit (protocol.md section 2, server-design.md 7.3).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix", Justification = "Name fixed by the protocol brief; it is the protocol-level violation signal.")]
public sealed class ProtocolViolation : Exception
{
    public ViolationCode Code { get; }

    public ProtocolViolation(ViolationCode code, string message)
        : base($"{code}: {message}")
    {
        Code = code;
    }

    public ProtocolViolation(ViolationCode code, string message, Exception inner)
        : base($"{code}: {message}", inner)
    {
        Code = code;
    }
}
