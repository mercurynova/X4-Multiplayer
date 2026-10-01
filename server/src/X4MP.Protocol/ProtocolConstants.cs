namespace X4MP.Protocol;

/// <summary>Protocol-wide constants (protocol.md sections 3 and 4; ADR-006, ADR-036).</summary>
public static class ProtocolConstants
{
    /// <summary>Wire major version. Peers with a different major are rejected (ProtocolMismatch).</summary>
    public const ushort ProtocolMajor = 0;

    /// <summary>Wire minor version. The session uses the lower of both peers' minors.</summary>
    public const ushort ProtocolMinor = 1;

    /// <summary>Default game connection port (TCP): Control + Bulk lanes, Realtime when UDP is unavailable.</summary>
    public const int DefaultTcpPort = 47780;

    /// <summary>Default Realtime lane port (UDP).</summary>
    public const int DefaultUdpPort = 47781;

    /// <summary>Default admin GUI / REST / SignalR / save fallback port (HTTP).</summary>
    public const int DefaultHttpPort = 47790;
}
