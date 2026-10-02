using System.Collections.Frozen;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Session;

/// <summary>
/// Phases a frame can arrive in, as a flags mask for the policy table. Bits 0..9 mirror
/// <see cref="NodePhase"/> (<c>1 &lt;&lt; (int)phase</c>); <see cref="Handshaking"/> is the pre-admission state.
/// </summary>
[Flags]
public enum PolicyPhase : ushort
{
    None = 0,
    Admitted = 1 << 0,
    AwaitingTeam = 1 << 1,
    SyncingSave = 1 << 2,
    Verifying = 1 << 3,
    Loading = 1 << 4,
    Matching = 1 << 5,
    CatchingUp = 1 << 6,
    InGame = 1 << 7,
    Detached = 1 << 8,
    Failed = 1 << 9,
    Handshaking = 1 << 10,

    /// <summary>Every phase of an admitted, attached node (everything after the handshake until Detached).</summary>
    Post = Admitted | AwaitingTeam | SyncingSave | Verifying | Loading | Matching | CatchingUp | InGame,

    /// <summary>The node has loaded the universe: catch-up and in-game traffic.</summary>
    Live = CatchingUp | InGame,

    Any = Post | Detached | Failed | Handshaking,
}

/// <summary>One row of the role x phase x lane table (server-design 2.4, architecture 4.1).</summary>
/// <param name="Type">Message type.</param>
/// <param name="Lane">Lane from the catalog (the frame header lane byte must equal it).</param>
/// <param name="Senders">Roles a node must hold (any one of) to send it; <see cref="Role.Admin"/> alone means the Admin flag. Empty means server-originated: a node may never send it.</param>
/// <param name="Phases">Phases in which it is accepted.</param>
public readonly record struct MessageRule(MsgType Type, Lane Lane, Role Senders, PolicyPhase Phases)
{
    /// <summary>True when only the server sends this message (a node sending it is a violation).</summary>
    public bool ServerOnly => Senders == 0;
}

/// <summary>Outcome of checking one inbound frame header against the table.</summary>
public enum PolicyVerdict
{
    Allowed,

    /// <summary>Unknown type from a peer whose minor version is higher than ours: skip and log, not a violation.</summary>
    SkipUnknown,

    /// <summary>Unknown or reserved type from a peer that should know it.</summary>
    UnknownType,

    /// <summary>The lane byte differs from the catalog.</summary>
    LaneMismatch,

    /// <summary>A server-originated message sent by a node (for example <c>WorldUpdate</c> echoed back, <c>Welcome</c>, <c>Replication</c>).</summary>
    ServerOnly,

    /// <summary>The node's roles do not allow it (a Client sending <c>WorldUpdate</c>).</summary>
    RoleDenied,

    /// <summary>Right role, wrong phase (<c>PlayerState</c> before the node is in game).</summary>
    PhaseDenied,
}

/// <summary>
/// The single source of truth for who may send what and when (server-design 2.4). The table is code, but
/// every lane comes from <see cref="MessageRegistry.Default"/> so it cannot drift from the catalog, and
/// tests assert that every <see cref="MsgType"/> has a row and that directions agree with protocol.md
/// section 20.
/// </summary>
public static class MessagePolicy
{
    private const Role A = Role.Authority;
    private const Role C = Role.Client;
    private const Role O = Role.Observer;
    private const Role AnyNode = A | C | O;
    private const Role None = 0;

    private const PolicyPhase Post = PolicyPhase.Post;
    private const PolicyPhase Live = PolicyPhase.Live;
    private const PolicyPhase InGame = PolicyPhase.InGame;

    private static readonly FrozenDictionary<MsgType, MessageRule> Table = Build();

    /// <summary>All rows, ordered by message type.</summary>
    public static IReadOnlyList<MessageRule> Rules { get; } = [.. Table.Values.OrderBy(r => (ushort)r.Type)];

    public static bool TryGetRule(MsgType type, out MessageRule rule) => Table.TryGetValue(type, out rule);

    /// <summary>Maps a node phase to the policy flag.</summary>
    public static PolicyPhase ToPolicyPhase(NodePhase phase) => (PolicyPhase)(1 << (int)phase);

    /// <summary>
    /// Checks one inbound frame header. <paramref name="peerMinor"/> is the minor version the peer announced:
    /// a higher one than ours means unknown message types are skipped instead of counted.
    /// </summary>
    public static PolicyVerdict Evaluate(MsgType type, Lane lane, Role roles, PolicyPhase phase, ushort peerMinor)
    {
        if (!Table.TryGetValue(type, out var rule) || type == MsgType.Invalid)
        {
            return peerMinor > ProtocolConstants.ProtocolMinor && !Enum.IsDefined(type)
                ? PolicyVerdict.SkipUnknown
                : PolicyVerdict.UnknownType;
        }

        if (lane != rule.Lane)
        {
            return PolicyVerdict.LaneMismatch;
        }

        if (rule.ServerOnly)
        {
            return PolicyVerdict.ServerOnly;
        }

        // Before the handshake completes a node holds no role yet; the phase check is what restricts it.
        if (phase != PolicyPhase.Handshaking)
        {
            bool allowed = rule.Senders == Role.Admin
                ? (roles & Role.Admin) != 0
                : (roles & rule.Senders & ~Role.Admin) != 0;
            if (!allowed)
            {
                return PolicyVerdict.RoleDenied;
            }
        }

        return (rule.Phases & phase) != 0 ? PolicyVerdict.Allowed : PolicyVerdict.PhaseDenied;
    }

    /// <summary>The Disconnect code that goes with a violation verdict.</summary>
    public static DisconnectCode CodeFor(PolicyVerdict verdict) => verdict switch
    {
        PolicyVerdict.UnknownType or PolicyVerdict.LaneMismatch => DisconnectCode.MalformedMessage,
        _ => DisconnectCode.UnexpectedMessage,
    };

    private static FrozenDictionary<MsgType, MessageRule> Build()
    {
        var rows = new List<(MsgType Type, Role Senders, PolicyPhase Phases)>
        {
            (MsgType.Invalid, None, PolicyPhase.None),

            // Control (0x00xx)
            (MsgType.ServerHello, None, PolicyPhase.None),
            (MsgType.ClientHello, AnyNode, PolicyPhase.Handshaking),
            (MsgType.Welcome, None, PolicyPhase.None),
            (MsgType.Disconnect, AnyNode, PolicyPhase.Any),
            (MsgType.Ping, AnyNode, PolicyPhase.Any),
            (MsgType.Pong, AnyNode, PolicyPhase.Any),
            (MsgType.UdpHello, AnyNode, Post),
            (MsgType.UdpHelloAck, None, PolicyPhase.None),
            (MsgType.ServerNotice, None, PolicyPhase.None),

            // Session and saves (0x01xx)
            (MsgType.SessionState, None, PolicyPhase.None),
            (MsgType.RosterUpdate, None, PolicyPhase.None),
            (MsgType.SessionSettings, None, PolicyPhase.None),
            (MsgType.RequestSave, None, PolicyPhase.None),
            (MsgType.SaveStarted, A, Post),
            (MsgType.SaveUploadBegin, A, Post),
            (MsgType.SaveUploadAccept, None, PolicyPhase.None),
            (MsgType.SaveChunk, A, Post),
            (MsgType.SaveChunkAck, A | C, Post),
            (MsgType.SaveUploadEnd, A, Post),
            (MsgType.SaveStored, None, PolicyPhase.None),
            (MsgType.SessionSaveInfo, None, PolicyPhase.None),
            (MsgType.SaveDownloadRequest, A | C, PolicyPhase.Admitted | PolicyPhase.SyncingSave),
            (MsgType.SaveDownloadAccept, None, PolicyPhase.None),
            (MsgType.SaveReady, A | C, PolicyPhase.SyncingSave | PolicyPhase.Verifying),
            (MsgType.LoadStatus, A | C, Post),
            (MsgType.NodeReady, A | C, PolicyPhase.Matching | PolicyPhase.CatchingUp | PolicyPhase.InGame),
            (MsgType.ManifestReport, A | C, PolicyPhase.Matching | PolicyPhase.CatchingUp),
            (MsgType.AuthorityAssign, None, PolicyPhase.None),
            (MsgType.GalaxyMetadata, A, Post),
            (MsgType.StringTableAdd, A, Post),
            (MsgType.GalaxySummary, A, Post),
            (MsgType.ServerSettingsUpdate, None, PolicyPhase.None),
            (MsgType.ModPolicyChanged, None, PolicyPhase.None),

            // World (0x02xx)
            (MsgType.EntitySpawn, A, Post),
            (MsgType.EntityDespawn, A, Post),
            (MsgType.WorldUpdate, A, Post),
            (MsgType.EntityStatusBatch, A, Post),
            (MsgType.EntityChange, A, Post),
            (MsgType.EntityCargo, A | C, Post),
            (MsgType.CaptureSet, None, PolicyPhase.None),
            (MsgType.SectorComplete, A, Post),
            (MsgType.Replication, None, PolicyPhase.None),
            (MsgType.InterestUpdate, None, PolicyPhase.None),
            (MsgType.InterestChecksum, None, PolicyPhase.None),
            (MsgType.ResyncRequest, C | O, Live),
            (MsgType.WorldCatchUp, None, PolicyPhase.None),
            (MsgType.InterestHint, C, Live),

            // Player (0x03xx)
            (MsgType.PlayerState, C, Live),
            (MsgType.PlayerShip, C, Live),
            (MsgType.OnFootState, C, Live), // M3b relays it; until then the relay accepts and drops it

            // Intents and events (0x04xx)
            (MsgType.Intent, C, InGame),
            (MsgType.IntentResult, A, Live),
            (MsgType.GameEvent, A, Post),
            (MsgType.DamageReport, None, PolicyPhase.None), // reserved (M5)

            // Chat, admin, telemetry (0x05xx, 0x06xx)
            (MsgType.ChatSend, C | O, Post),
            (MsgType.ChatMessage, None, PolicyPhase.None),
            (MsgType.AdminCommand, Role.Admin, Post),
            (MsgType.AdminResult, None, PolicyPhase.None),
            (MsgType.NodeStats, AnyNode, Post),
            (MsgType.LogForward, AnyNode, Post),

            // Teams (0x07xx)
            (MsgType.TeamTable, None, PolicyPhase.None),
            (MsgType.TeamRelations, None, PolicyPhase.None),
            (MsgType.TeamChoice, C, PolicyPhase.AwaitingTeam),
            (MsgType.TeamCreateRequest, C, PolicyPhase.AwaitingTeam),
            (MsgType.TeamChangeRequest, C, InGame),
            (MsgType.RelationChangeRequest, C, InGame),
            (MsgType.TeamRequestResult, None, PolicyPhase.None),
            (MsgType.TeamMemberChanged, None, PolicyPhase.None),
            (MsgType.RelationProposal, None, PolicyPhase.None),
            (MsgType.ReassignPlayerAssets, None, PolicyPhase.None),

            // Economy (0x08xx)
            (MsgType.WalletUpdate, None, PolicyPhase.None),
            (MsgType.CreditDelta, A | C, Live),
            (MsgType.EconomyResult, None, PolicyPhase.None),
            (MsgType.CreditTransferRequest, C, InGame),
            (MsgType.PoolDepositRequest, C, InGame),
            (MsgType.PoolWithdrawRequest, C, InGame),
            (MsgType.DonateRequest, C, InGame),
            (MsgType.LoanOffer, C, InGame),
            (MsgType.LoanRespond, C, InGame),
            (MsgType.LoanRepay, C, InGame),
            (MsgType.LoanForgive, C, InGame),
            (MsgType.LoanCancel, C, InGame),
            (MsgType.LoanStatus, None, PolicyPhase.None),
            (MsgType.TradeProposal, C, InGame),
            (MsgType.TradeCounter, C, InGame),
            (MsgType.TradeAccept, C, InGame),
            (MsgType.TradeCancel, C, InGame),
            (MsgType.TradeStatus, None, PolicyPhase.None),
            (MsgType.TradeResult, None, PolicyPhase.None),
            (MsgType.AssetTransferOrder, None, PolicyPhase.None),
            // architecture 4.1 (authoritative) lists it for the Authority only; protocol.md 5 also allows a client
            // confirming its own piloted ship. Follow architecture.md; widen here if the mod ever needs it.
            (MsgType.AssetTransferConfirm, A, InGame),
            (MsgType.TradeQuery, None, PolicyPhase.None),
        };

        var registry = MessageRegistry.Default;
        var result = new Dictionary<MsgType, MessageRule>(rows.Count);
        foreach (var (type, senders, phases) in rows)
        {
            // The lane comes from the catalog; reserved/unregistered types never carry traffic.
            var lane = registry.TryGetDescriptor(type, out var d) ? d.Lane : Lane.Control;
            result.Add(type, new MessageRule(type, lane, senders, phases));
        }

        return result.ToFrozenDictionary();
    }
}
