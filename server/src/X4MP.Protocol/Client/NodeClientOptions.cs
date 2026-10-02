using X4MP.Proto;

namespace X4MP.Protocol.Client;

/// <summary>Everything a node puts into its <c>ClientHello</c>.</summary>
public sealed record NodeClientOptions
{
    public string PlayerName { get; init; } = "FakeNode";

    /// <summary>32 random bytes, stable per node identity. Generated if left null.</summary>
    public byte[]? PlayerKey { get; init; }

    /// <summary>Session password; null when the server has no auth.</summary>
    public string? Password { get; init; }

    /// <summary>Admin password to request the Admin role; null = not requesting it.</summary>
    public string? AdminPassword { get; init; }

    public Role RequestedRoles { get; init; } = Role.Client;
    public ulong ClientCaps { get; init; }
    public string ModVersion { get; init; } = "0.1.0";
    public string ModBuild { get; init; } = "fakenode";
    public string GameVersion { get; init; } = "9.00";
    public string GameBuild { get; init; } = "900-611726";
    public string X4NativeVersion { get; init; } = "fakenode";
    public string Platform { get; init; } = "win64";

    /// <summary>Resume token from a previous <c>Welcome</c> (null = fresh join).</summary>
    public Id128T? ResumeToken { get; init; }

    /// <summary>Last journal sequence applied locally (resume only).</summary>
    public ulong LastJournalSeq { get; init; }

    /// <summary>How long the whole handshake may take (protocol.md 4.1: 10 s server side).</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxFrameBytes { get; init; } = FrameCodec.DefaultMaxFrameBytes;

    /// <summary>
    /// Failure injection (FakeNode): wraps the socket stream after connecting (a slow or delayed reader). Only used by
    /// <see cref="TcpNodeClient.ConnectAsync(string,int,NodeClientOptions,System.Threading.CancellationToken)"/>.
    /// </summary>
    public Func<Stream, Stream>? StreamWrapper { get; init; }

    /// <summary>Local address to bind the TCP socket to (for example <c>127.0.0.2</c>, so a misbehaving client has an address of its own); null = any.</summary>
    public string? LocalAddress { get; init; }

    /// <summary>Failure injection: size of the socket's receive buffer (set before connecting; 0 = the system default). A small one makes TCP flow control stop the sender early.</summary>
    public int ReceiveBufferBytes { get; init; }
}
