using X4MP.Protocol;

namespace X4MP.Core.Net;

/// <summary>
/// Boot-time networking settings (server-design 2.9 NetOptions plus the gateway and send-queue knobs).
/// Bound from <c>X4MP:Net</c>. Defaults follow ADR-026 and protocol.md section 4.
/// </summary>
public sealed class NetOptions
{
    public const string SectionName = "X4MP:Net";

    /// <summary>Set false to run without the node listener (admin-only server, some tests).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Node TCP bind endpoint, <c>address:port</c>.</summary>
    public string NodeTcpEndpoint { get; set; } = "0.0.0.0:" + ProtocolConstants.DefaultTcpPort;

    /// <summary>Largest accepted frame payload (ADR-026: 1 MiB).</summary>
    public int MaxFrameBytes { get; set; } = FrameCodec.DefaultMaxFrameBytes;

    /// <summary>Payload cap before the handshake completes (a ClientHello is small).</summary>
    public int HandshakeMaxFrameBytes { get; set; } = 64 * 1024;

    // --- Send queue (ADR-026) ---

    /// <summary>Control lane soft cap: producers should pause spawn and catch-up traffic.</summary>
    public long ControlSoftCapBytes { get; set; } = 8L * 1024 * 1024;

    /// <summary>Control lane hard cap: <c>Close(SlowConsumer)</c>.</summary>
    public long ControlHardCapBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>Oldest queued Control frame older than this closes the connection with SlowConsumer.</summary>
    public int SlowConsumerTimeoutSeconds { get; set; } = 15;

    /// <summary>Realtime lane low watermark: <see cref="INodeConnection.CanAcceptRealtime"/> is true below it.</summary>
    public long RealtimeLowWatermarkBytes { get; set; } = 64 * 1024;

    /// <summary>Realtime lane high watermark: further frames get <see cref="SendResult.DroppedLane"/>.</summary>
    public long RealtimeHighWatermarkBytes { get; set; } = 256 * 1024;

    /// <summary>Bulk lane safety cap. Bulk producers are window-flow-controlled, so this is never reached by design.</summary>
    public long BulkCapBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>Bytes the writer copies into the pipe before each flush.</summary>
    public int WriterBatchBytes { get; set; } = 64 * 1024;

    /// <summary>How long Close waits for the disconnect frame to flush before aborting the socket.</summary>
    public int CloseFlushTimeoutMs { get; set; } = 2000;

    // --- Gateway (protocol.md 4, server-design 7.3) ---

    public int HandshakeTimeoutSeconds { get; set; } = 10;

    public int MaxConnectionsPerIp { get; set; } = 4;

    /// <summary>Global handshake rate limit (token bucket refill per second; burst equals this value).</summary>
    public int HandshakesPerSecond { get; set; } = 20;

    /// <summary>Failed auth attempts allowed per minute per IP before further attempts get RateLimited.</summary>
    public int AuthFailuresPerMinute { get; set; } = 5;

    /// <summary>Delay before a failed auth is answered (slows guessing).</summary>
    public int AuthFailureDelayMs { get; set; } = 1000;

    /// <summary>Policy violations tolerated per minute; one more closes the connection and temp-bans the IP.</summary>
    public int ViolationLimitPerMinute { get; set; } = 20;

    public int TempBanMinutes { get; set; } = 5;

    // --- Identity / compatibility (ADR-004) ---

    /// <summary>Game builds the server accepts. Always enforced; equality with the authority is additionally enforced.</summary>
    public List<string> SupportedGameBuilds { get; set; } = ["900-611726"];

    /// <summary>If set, every node's <c>mod_version</c> must equal this; otherwise it must equal the authority's.</summary>
    public string? RequiredModVersion { get; set; }

    /// <summary>Compare <c>mod_build</c> with the authority's too (strict mode, protocol.md 4.2).</summary>
    public bool ModBuildStrict { get; set; } = true;

    /// <summary>Admin downgrade: log an extensions mismatch instead of rejecting.</summary>
    public bool ExtensionsMismatchIsWarning { get; set; }

    /// <summary>Join password (plain text in boot config; only SHA-256 of it is kept in memory). Null or empty = open.</summary>
    public string? JoinPassword { get; set; }

    /// <summary>Password for in-game admin (<c>admin_proof</c>). Null or empty = no node can become admin.</summary>
    public string? AdminPassword { get; set; }

    public string ServerName { get; set; } = "X4MP Server";

    public int MaxPlayers { get; set; } = 8;

    public int ResumeGraceSeconds { get; set; } = 60;

    /// <summary>Capability bits the server advertises (<see cref="X4MP.Proto.Capability"/>).</summary>
    public ulong ServerCaps { get; set; } =
        (ulong)(X4MP.Proto.Capability.GhostRender
            | X4MP.Proto.Capability.DamageSync
            | X4MP.Proto.Capability.StationBuild
            | X4MP.Proto.Capability.TradeSync
            | X4MP.Proto.Capability.CaptureSync
            | X4MP.Proto.Capability.LogForward
            | X4MP.Proto.Capability.InterestHint
            | X4MP.Proto.Capability.Economy);
}
