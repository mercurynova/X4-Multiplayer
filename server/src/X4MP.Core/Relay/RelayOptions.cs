using X4MP.Core.Settings;

namespace X4MP.Core.Relay;

/// <summary>
/// Relay and chat tunables (server-design 2.6, protocol.md 13 and 16). All Live: the module reads them through a provider on every
/// use. The 5 s intent timeout and the 20 Hz authority feed are the protocol's numbers; they are settings so a test or a lab
/// server can change them.
/// </summary>
[SettingsSection(SectionName, "Relay")]
public sealed class RelayOptions
{
    public const string SectionName = "X4MP:Relay";

    /// <summary>Upper bound of the rate at which a player's state goes to the authority (protocol.md 13: 20 Hz).</summary>
    [Setting("Player state feed to the authority (Hz)", Scope = SettingScope.Live, Min = 1, Max = 60)]
    public int PlayerStateRelayHz { get; set; } = 20;

    /// <summary>An intent the authority does not answer in this time gets <c>Rejected{Timeout}</c> (protocol.md 16.1: 5 s).</summary>
    [Setting("Intent timeout: reject an unanswered intent after (ms)", Scope = SettingScope.Live, Min = 100, Max = 120000)]
    public int IntentTimeoutMs { get; set; } = 5000;

    /// <summary>Intents one player may have waiting for the authority; more are answered with <c>Rejected{RateLimited}</c>.</summary>
    [Setting("Intents one player may have in flight", Scope = SettingScope.Live, Min = 1, Max = 10000)]
    public int MaxPendingIntentsPerPlayer { get; set; } = 64;

    /// <summary>Master switch for player chat. Admin and system messages still go out.</summary>
    [Setting("Player chat enabled", Scope = SettingScope.Live)]
    public bool ChatEnabled { get; set; } = true;

    /// <summary>Messages a player may send in a burst (token bucket size).</summary>
    [Setting("Chat burst: messages a player may send at once", Scope = SettingScope.Live, Min = 1, Max = 100)]
    public int ChatBurst { get; set; } = 5;

    /// <summary>Sustained chat rate per player (token refill).</summary>
    [Setting("Chat rate: messages per second a player may sustain", Scope = SettingScope.Live, Min = 1, Max = 100)]
    public int ChatPerSecond { get; set; } = 1;

    /// <summary>Rate-limit hits (chat and intents) per minute after which the node is closed with <c>RateLimited</c>.</summary>
    [Setting("Relay rate-limit hits tolerated per minute", Scope = SettingScope.Live, Min = 1, Max = 100000)]
    public int RateLimitHitsPerMinute { get; set; } = 120;

    /// <summary>Largest distance (m) between a player's ship and the target of a <c>KillClaim</c> or <c>HitReport</c> (protocol.md 16.2: 30 km).</summary>
    [Setting("Kill and hit claims: largest distance to the target (m)", Scope = SettingScope.Live, Min = 100, Max = 1000000)]
    public int ClaimRangeMetres { get; set; } = 30000;

    /// <summary><c>PermissionDenied</c> events recorded per player per second (the rejections themselves are always answered).</summary>
    [Setting("PermissionDenied events recorded per player per second", Scope = SettingScope.Live, Min = 1, Max = 1000)]
    public int PermissionDeniedEventsPerSecond { get; set; } = 5;
}
