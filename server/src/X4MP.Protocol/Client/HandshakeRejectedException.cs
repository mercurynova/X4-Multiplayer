using X4MP.Proto;

namespace X4MP.Protocol.Client;

/// <summary>The server answered the handshake with <c>Disconnect</c> (or the stream ended / timed out).</summary>
public sealed class HandshakeRejectedException : Exception
{
    public HandshakeRejectedException(DisconnectCode code, string message, string expected = "", uint retryAfterMs = 0)
        : base($"handshake rejected: {code}" + (message.Length > 0 ? $" ({message})" : ""))
    {
        Code = code;
        ServerMessage = message;
        Expected = expected;
        RetryAfterMs = retryAfterMs;
    }

    public DisconnectCode Code { get; }
    public string ServerMessage { get; }
    public string Expected { get; }
    public uint RetryAfterMs { get; }
}
