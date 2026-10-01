namespace X4MP.Core.Net;

/// <summary>
/// The server's UDP Realtime endpoint as the gateway sees it (protocol.md 3.3): the gateway tells it which <c>conn_id</c> and
/// <c>udp_token</c> belong to which connection, the host binds the connection to a source endpoint when a valid <c>UdpHello</c> arrives.
/// </summary>
public interface IUdpRealtimeHost
{
    /// <summary>The UDP port nodes send to (goes into <c>Welcome.udp_port</c>); 0 means the lane is off.</summary>
    int Port { get; }

    /// <summary>Makes <paramref name="connection"/> bindable with this id and token. Replaces an earlier registration of the same connection.</summary>
    void Register(INodeConnection connection, uint connId, ulong token);

    /// <summary>Forgets the connection and drops its datagram path. Idempotent.</summary>
    void Unregister(INodeConnection connection);
}
