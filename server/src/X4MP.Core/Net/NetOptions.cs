using X4MP.Core.Settings;
using X4MP.Protocol;

namespace X4MP.Core.Net;

/// <summary>
/// Boot-time networking settings (server-design 2.9 NetOptions plus the gateway and send-queue knobs).
/// Bound from <c>X4MP:Net</c>. Defaults follow ADR-026 and protocol.md section 4.
/// Every tunable is a <see cref="SettingScope.Boot"/> setting: the gateway and send queues copy them at startup,
/// so the GUI shows them read-only with a "restart required" note.
/// </summary>
[SettingsSection(SectionName, "Network")]
public sealed record NetOptions
{
    public const string SectionName = "X4MP:Net";

    /// <summary>Set false to run without the node listener (admin-only server, some tests).</summary>
    [Setting("Accept game connections from nodes")]
    public bool Enabled { get; set; } = true;

    /// <summary>Node TCP bind endpoint, <c>address:port</c>.</summary>
    [Setting("Node TCP bind endpoint (address:port)", MaxLength = 64)]
    public string NodeTcpEndpoint { get; set; } = "0.0.0.0:" + ProtocolConstants.DefaultTcpPort;

    /// <summary>Largest accepted frame payload (ADR-026: 1 MiB).</summary>
    [Setting("Largest accepted frame payload (bytes)", Min = 1024, Max = 64 * 1024 * 1024)]
    public int MaxFrameBytes { get; set; } = FrameCodec.DefaultMaxFrameBytes;

    /// <summary>Payload cap before the handshake completes (a ClientHello is small).</summary>
    [Setting("Largest frame accepted before the handshake completes (bytes)", Min = 1024, Max = 1024 * 1024)]
    public int HandshakeMaxFrameBytes { get; set; } = 64 * 1024;

    // --- Send queue (ADR-026) ---

    /// <summary>Control lane soft cap: producers should pause spawn and catch-up traffic.</summary>
    [Setting("Control lane soft cap (bytes)", Min = 65536)]
    public long ControlSoftCapBytes { get; set; } = 8L * 1024 * 1024;

    /// <summary>Control lane hard cap: <c>Close(SlowConsumer)</c>.</summary>
    [Setting("Control lane hard cap, closes a slow consumer (bytes)", Min = 65536)]
    public long ControlHardCapBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>Oldest queued Control frame older than this closes the connection with SlowConsumer.</summary>
    [Setting("Slow-consumer timeout (s)", Min = 1, Max = 600)]
    public int SlowConsumerTimeoutSeconds { get; set; } = 15;

    /// <summary>Realtime lane low watermark: <see cref="INodeConnection.CanAcceptRealtime"/> is true below it.</summary>
    [Setting("Realtime lane low watermark (bytes)", Min = 1024)]
    public long RealtimeLowWatermarkBytes { get; set; } = 64 * 1024;

    /// <summary>Realtime lane high watermark: further frames get <see cref="SendResult.DroppedLane"/>.</summary>
    [Setting("Realtime lane high watermark (bytes)", Min = 1024)]
    public long RealtimeHighWatermarkBytes { get; set; } = 256 * 1024;

    /// <summary>Bulk lane safety cap. Bulk producers are window-flow-controlled, so this is never reached by design.</summary>
    [Setting("Bulk lane safety cap (bytes)", Min = 65536)]
    public long BulkCapBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>Bytes the writer copies into the pipe before each flush.</summary>
    [Setting("Writer batch size (bytes)", Min = 1024, Max = 16 * 1024 * 1024)]
    public int WriterBatchBytes { get; set; } = 64 * 1024;

    /// <summary>How long Close waits for the disconnect frame to flush before aborting the socket.</summary>
    [Setting("Close flush timeout (ms)", Min = 0, Max = 60000)]
    public int CloseFlushTimeoutMs { get; set; } = 2000;

    // --- Gateway (protocol.md 4, server-design 7.3) ---

    [Setting("Handshake timeout (s)", Min = 1, Max = 120)]
    public int HandshakeTimeoutSeconds { get; set; } = 10;

    [Setting("Maximum connections per IP address", Min = 1, Max = 1000)]
    public int MaxConnectionsPerIp { get; set; } = 4;

    /// <summary>Global handshake rate limit (token bucket refill per second; burst equals this value).</summary>
    [Setting("Global handshake rate limit (per second)", Min = 1, Max = 10000)]
    public int HandshakesPerSecond { get; set; } = 20;

    /// <summary>Failed auth attempts allowed per minute per IP before further attempts get RateLimited.</summary>
    [Setting("Failed authentications allowed per minute per IP", Min = 1, Max = 1000)]
    public int AuthFailuresPerMinute { get; set; } = 5;

    /// <summary>Delay before a failed auth is answered (slows guessing).</summary>
    [Setting("Delay before a failed authentication is answered (ms)", Min = 0, Max = 30000)]
    public int AuthFailureDelayMs { get; set; } = 1000;

    /// <summary>Policy violations tolerated per minute; one more closes the connection and temp-bans the IP.</summary>
    [Setting("Policy violations tolerated per minute", Min = 1, Max = 10000)]
    public int ViolationLimitPerMinute { get; set; } = 20;

    [Setting("Temporary IP ban after too many violations (minutes)", Min = 1, Max = 10080)]
    public int TempBanMinutes { get; set; } = 5;

    // --- Identity / compatibility (ADR-004) ---

    /// <summary>Game builds the server accepts. Always enforced; equality with the authority is additionally enforced.</summary>
    [Setting("Accepted game builds")]
    public List<string> SupportedGameBuilds { get; set; } = ["900-611726"];

    /// <summary>If set, every node's <c>mod_version</c> must equal this; otherwise it must equal the authority's.</summary>
    [Setting("Required mod version (empty = same as the authority)", MaxLength = 64)]
    public string? RequiredModVersion { get; set; }

    /// <summary>Compare <c>mod_build</c> with the authority's too (strict mode, protocol.md 4.2).</summary>
    [Setting("Compare the mod build with the authority's too")]
    public bool ModBuildStrict { get; set; } = true;

    /// <summary>Admin downgrade: log an extensions mismatch instead of rejecting.</summary>
    [Setting("Log an extensions mismatch instead of rejecting it")]
    public bool ExtensionsMismatchIsWarning { get; set; }

    /// <summary>Join password (plain text in boot config; only SHA-256 of it is kept in memory). Null or empty = open.</summary>
    [Setting("Join password (empty = open server)", Secret = true, MaxLength = 128)]
    public string? JoinPassword { get; set; }

    /// <summary>Password for in-game admin (<c>admin_proof</c>). Null or empty = no node can become admin.</summary>
    [Setting("In-game admin password (empty = disabled)", Secret = true, MaxLength = 128)]
    public string? AdminPassword { get; set; }

    [Setting("Server name", MaxLength = 64)]
    public string ServerName { get; set; } = "X4MP Server";

    [Setting("Maximum players", Min = 1, Max = 64)]
    public int MaxPlayers { get; set; } = 8;

    [Setting("Reconnect grace (s)", Min = 5, Max = 600)]
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
