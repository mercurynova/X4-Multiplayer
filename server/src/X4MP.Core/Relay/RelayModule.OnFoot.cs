using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Relay;

/// <summary>
/// On-foot presence relay (ADR-046, on-foot-presence.md 3.2, M3b).
/// <list type="bullet">
/// <item><b><c>OnFootState</c> (Realtime, C-&gt;S-&gt;C).</b> The server stamps <c>player_id</c>, validates the sample (a known innermost
/// container, a container-local position within <see cref="RelayOptions.OnFootRangeMetres"/>, a token bucket) and forwards it to every node
/// whose own last state shares the sender's innermost container — and to the peers of the <em>previous</em> container, so a player who walks
/// into another room sends them the leave state. The authority is treated like any other node: it gets these messages only when its own
/// human shares the container.</item>
/// <item><b><c>PresenceRoster</c> (Control, S-&gt;N).</b> Galaxy-wide (Q2), rebuilt on any container/mode/roomtype change, sent at most
/// <see cref="RelayOptions.PresenceRosterHz"/>.</item>
/// <item><b><c>PlayerAppearance</c> (Control, C-&gt;S-&gt;C).</b> Stored per player with a server-stamped revision and relayed on join and on
/// change (ADR-051). The catalogue check is the M3b appearance task; until then the strings are only cleaned and length-capped.</item>
/// <item><b><c>PlayerInteraction</c> (Control, C-&gt;S-&gt;C).</b> The in-world social trigger (wave, emote, poke): rate limited and forwarded
/// to the target only. Credits and team invites reuse the economy/team messages.</item>
/// </list>
/// Everything runs on the session actor thread.
/// </summary>
public sealed partial class RelayModule
{
    /// <summary>Longest appearance string the server keeps (macro and preset ids are far shorter; this stops junk).</summary>
    private const int AppearanceStringLimit = 96;

    /// <summary>Top bit of <c>container_net_id</c>: set means the id is a client-matched key (hash of the sector macro, the station
    /// macro and the position rounded to 50 m) instead of a server-allocated net id. Both clients derive it from the game state, so it
    /// needs no manifest entry; the server only checks its shape. See <c>docs/protocol.md</c> 8.2 and <c>docs/mod-design.md</c> 4.12.</summary>
    public const uint MatchedContainerBit = 0x8000_0000u;

    /// <summary>Per-player on-foot relay state: the newest accepted sample and the token bucket.</summary>
    private sealed class OnFootFlow(int playerId)
    {
        public int PlayerId { get; } = playerId;

        /// <summary>The newest accepted sample (its container is the player's presence key).</summary>
        public OnFootStateT? Last { get; set; }

        public uint LastSeq { get; set; }

        public double Tokens { get; set; } = -1;

        public long LastRefill { get; set; }

        public bool OnFoot => Last is { Mode: not OnFootMode.None };

        public uint Container => Last?.ContainerNetId ?? 0;
    }

    private readonly Dictionary<int, OnFootFlow> _onFoot = [];
    private readonly Dictionary<int, PlayerAppearanceT> _appearances = [];
    private readonly Dictionary<int, uint> _appearanceRevisions = [];
    private readonly Dictionary<int, (double Tokens, long At)> _interactionBuckets = [];
    private bool _rosterDirty;
    private long _nextRosterAt;

    /// <summary>The newest on-foot sample the server accepted for a player (tests, GUI, the presence line of the admin API).</summary>
    public bool TryGetOnFoot(int playerId, out OnFootStateT state)
    {
        if (_onFoot.TryGetValue(playerId, out var flow) && flow.Last is { } last)
        {
            state = last;
            return true;
        }

        state = null!;
        return false;
    }

    /// <summary>The appearance the server holds for a player (null before the first <c>PlayerAppearance</c>).</summary>
    public PlayerAppearanceT? GetAppearance(int playerId) => _appearances.GetValueOrDefault(playerId);

    // ------------------------------------------------------------------ OnFootState

    private void OnOnFootState(SessionNode node, InboundFrame frame)
    {
        Stats.OnFootStatesReceived++;
        var state = MessageRegistry.Default.Decode<OnFootState>(frame.Frame).UnPack();
        if (!_onFoot.TryGetValue(node.PlayerId, out var flow))
        {
            flow = new OnFootFlow(node.PlayerId);
            _onFoot[node.PlayerId] = flow;
        }

        if (!IsValidOnFoot(state, out string why))
        {
            Stats.OnFootStatesDropped++;
            LogOnFootDropped(node.PlayerId, why);
            return;
        }

        if (flow.LastSeq != 0 && state.Seq != 0 && state.Seq < flow.LastSeq && flow.LastSeq - state.Seq < 0x1000_0000)
        {
            Stats.OnFootStatesStale++;
            return;
        }

        if (!TakeOnFootToken(flow, Now))
        {
            Stats.OnFootStatesRateLimited++;
            return;
        }

        uint previousContainer = flow.OnFoot ? flow.Container : 0;
        bool changed = flow.Last is not { } before
            || before.Mode != state.Mode
            || before.ContainerNetId != state.ContainerNetId
            || before.OuterContainerNetId != state.OuterContainerNetId
            || (before.Room?.Roomtype ?? 0) != (state.Room?.Roomtype ?? 0);

        state.PlayerId = (ushort)node.PlayerId; // never trust the client's value
        flow.Last = state;
        flow.LastSeq = state.Seq;

        RelayOnFoot(node, state, previousContainer);
        if (changed)
        {
            _rosterDirty = true;
        }
    }

    /// <summary>Known container, a plausible container-local position, a defined mode. The enum range is ours to enforce: flatc's
    /// verifier checks the wire, not the meaning.</summary>
    private bool IsValidOnFoot(OnFootStateT state, out string why)
    {
        if (!Enum.IsDefined(state.Mode))
        {
            why = "mode";
            return false;
        }

        if (state.Mode == OnFootMode.None)
        {
            why = "";
            return true; // the leave state: no container to check
        }

        if (state.ContainerNetId == 0)
        {
            why = "container";
            return false;
        }

        // M3b: a container id with the top bit set is a CLIENT-MATCHED key (sector macro + station macro + position rounded to
        // 50 m, hashed) rather than a server-allocated net id. Station interiors have no manifest net id until M4 (ADR-009 gives
        // them one in the checkpoint manifest), so both nodes derive the same id from the game state and the server only checks
        // its shape. When M4 lands, the client switches to the manifest id and this branch stops being taken.
        bool matched = (state.ContainerNetId & MatchedContainerBit) != 0;
        bool matchedOuter = (state.OuterContainerNetId & MatchedContainerBit) != 0;

        if (!matched && _mirror is not null && !_mirror.TryGet(state.ContainerNetId, out _))
        {
            why = "container-not-in-mirror";
            return false;
        }

        if (state.OuterContainerNetId != 0 && !matchedOuter && _mirror is not null && !_mirror.TryGet(state.OuterContainerNetId, out _))
        {
            why = "outer-container-not-in-mirror";
            return false;
        }

        // Container-local, 1/64 m (world.fbs): a player cannot be kilometres away from the station they are walking in.
        long limit = Math.Max(1, Opt.OnFootRangeMetres) * 64L;
        if (Math.Abs((long)state.Cx) > limit || Math.Abs((long)state.Cy) > limit || Math.Abs((long)state.Cz) > limit)
        {
            why = "range";
            return false;
        }

        why = "";
        return true;
    }

    /// <summary>Token bucket: <see cref="RelayOptions.OnFootRelayHz"/> sustained, <see cref="RelayOptions.OnFootBurst"/> at once. Dropped
    /// samples are not queued — the next one is 100 ms away and presence is latest-wins.</summary>
    private bool TakeOnFootToken(OnFootFlow flow, long now)
    {
        double rate = Math.Max(1, Opt.OnFootRelayHz);
        double burst = Math.Max(1, Opt.OnFootBurst);
        if (flow.Tokens < 0)
        {
            flow.Tokens = burst;
        }
        else
        {
            flow.Tokens = Math.Min(burst, flow.Tokens + ((now - flow.LastRefill) / (double)_time.TimestampFrequency * rate));
        }

        flow.LastRefill = now;
        if (flow.Tokens < 1)
        {
            return false;
        }

        flow.Tokens -= 1;
        return true;
    }

    /// <summary>Forwards a sample to the peers of the new and of the previous container (the latter get the leave state).</summary>
    private void RelayOnFoot(SessionNode sender, OnFootStateT state, uint previousContainer)
    {
        uint container = state.Mode == OnFootMode.None ? 0 : state.ContainerNetId;
        foreach (var peer in _nodes.Values)
        {
            if (peer.PlayerId == sender.PlayerId
                || !IsLive(peer)
                || !HasOnFoot(peer)
                || (!SharesContainer(peer.PlayerId, container) && !SharesContainer(peer.PlayerId, previousContainer)))
            {
                continue;
            }

            SendOnFoot(peer, state);
        }
    }

    private void SendOnFoot(SessionNode peer, OnFootStateT state)
    {
        var frame = Encode(
            MsgType.OnFootState,
            fbb => OnFootState.Pack(fbb, state),
            96,
            coalesceKey: ((ulong)MsgType.OnFootState << 32) | state.PlayerId);
        TrySend(peer, frame);
        frame.Release();
        Stats.OnFootStatesRelayed++;
    }

    /// <summary>True when the peer's own accepted sample says it stands in <paramref name="container"/>.</summary>
    private bool SharesContainer(int peerPlayerId, uint container) =>
        container != 0 && _onFoot.TryGetValue(peerPlayerId, out var flow) && flow.OnFoot && flow.Container == container;

    /// <summary>A synthetic leave state for a player whose connection is gone: the peers in their container despawn the actor.</summary>
    private void SendLeaveStates(int playerId)
    {
        if (!_onFoot.TryGetValue(playerId, out var flow) || !flow.OnFoot || flow.Last is not { } last)
        {
            return;
        }

        var leave = new OnFootStateT
        {
            PlayerId = (ushort)playerId,
            Seq = last.Seq + 1,
            SampleTimeUs = last.SampleTimeUs,
            Mode = OnFootMode.None,
            ContainerNetId = 0,
            OuterContainerNetId = 0,
        };

        foreach (var peer in _nodes.Values)
        {
            if (peer.PlayerId == playerId || !IsLive(peer) || !HasOnFoot(peer) || !SharesContainer(peer.PlayerId, last.ContainerNetId))
            {
                continue;
            }

            SendOnFoot(peer, leave);
        }

        flow.Last = leave;
        _rosterDirty = true;
    }

    private static bool HasOnFoot(SessionNode node) =>
        ((node.Attached?.NegotiatedCaps ?? 0) & (ulong)Capability.OnFootPresence) != 0;

    // ------------------------------------------------------------------ PresenceRoster

    /// <summary>Sends the roster when it changed and its 1 Hz slot is due (called from <c>OnTick</c>).</summary>
    private void FlushPresenceRoster(long now)
    {
        if (!_rosterDirty || now < _nextRosterAt)
        {
            return;
        }

        _rosterDirty = false;
        _nextRosterAt = now + (_time.TimestampFrequency / Math.Max(1, Opt.PresenceRosterHz));
        var roster = BuildPresenceRoster();
        foreach (var peer in _nodes.Values)
        {
            if (IsLive(peer) && HasOnFoot(peer))
            {
                SendPresenceRoster(peer, roster);
            }
        }
    }

    private PresenceRosterT BuildPresenceRoster()
    {
        var entries = new List<PresenceEntryT>();
        foreach (var flow in _onFoot.Values)
        {
            if (!flow.OnFoot || flow.Last is not { } state || !_nodes.ContainsKey(flow.PlayerId))
            {
                continue;
            }

            entries.Add(new PresenceEntryT
            {
                PlayerId = (ushort)flow.PlayerId,
                Mode = state.Mode,
                ContainerNetId = state.ContainerNetId,
                OuterContainerNetId = state.OuterContainerNetId,
                Roomtype = state.Room?.Roomtype ?? 0,
                RoomName = "",
            });
        }

        return new PresenceRosterT { Full = true, Entries = [.. entries], Removed = [] };
    }

    private void SendPresenceRoster(SessionNode peer, PresenceRosterT roster)
    {
        var frame = Encode(MsgType.PresenceRoster, fbb => PresenceRoster.Pack(fbb, roster), 256);
        TrySend(peer, frame);
        frame.Release();
        Stats.PresenceRostersSent++;
    }

    // ------------------------------------------------------------------ PlayerAppearance

    private void OnPlayerAppearance(SessionNode node, InboundFrame frame)
    {
        var appearance = MessageRegistry.Default.Decode<PlayerAppearance>(frame.Frame).UnPack();
        appearance.PlayerId = (ushort)node.PlayerId;
        appearance.Revision = _appearanceRevisions.TryGetValue(node.PlayerId, out var revision) ? revision + 1 : 1;
        appearance.Race = Clean(appearance.Race);
        appearance.Preset = Clean(appearance.Preset);
        appearance.CharacterMacro = Clean(appearance.CharacterMacro);
        appearance.Clothing = Clean(appearance.Clothing);
        _appearanceRevisions[node.PlayerId] = appearance.Revision;
        _appearances[node.PlayerId] = appearance;

        // ADR-051: the catalogue check (allowed race/preset/macro for the build) is the appearance task; here the values are only cleaned.
        foreach (var peer in _nodes.Values)
        {
            if (peer.PlayerId == node.PlayerId || !IsLive(peer) || !HasOnFoot(peer))
            {
                continue;
            }

            SendAppearance(peer, appearance);
        }
    }

    private void SendAppearance(SessionNode peer, PlayerAppearanceT appearance)
    {
        var frame = Encode(MsgType.PlayerAppearance, fbb => PlayerAppearance.Pack(fbb, appearance), 192);
        TrySend(peer, frame);
        frame.Release();
        Stats.AppearancesRelayed++;
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var text = value.Trim();
        if (text.Length > AppearanceStringLimit)
        {
            text = text[..AppearanceStringLimit];
        }

        return text.Any(char.IsControl) ? new string([.. text.Where(c => !char.IsControl(c))]) : text;
    }

    // ------------------------------------------------------------------ PlayerInteraction

    private void OnPlayerInteraction(SessionNode node, InboundFrame frame)
    {
        var interaction = MessageRegistry.Default.Decode<PlayerInteraction>(frame.Frame).UnPack();
        interaction.FromPlayer = (ushort)node.PlayerId;
        if (!Enum.IsDefined(interaction.Kind)
            || interaction.ToPlayer == 0
            || interaction.ToPlayer == node.PlayerId
            || !_nodes.TryGetValue(interaction.ToPlayer, out var target)
            || !IsLive(target)
            || !HasOnFoot(target))
        {
            Stats.InteractionsRejected++;
            return;
        }

        if (!TakeInteractionToken(node.PlayerId, Now))
        {
            Stats.InteractionsRateLimited++;
            return;
        }

        // The in-world trigger only: team policy for InviteTeam/OpenTrade is the M5b Talk task (ADR-046 3.1).
        var outbound = Encode(MsgType.PlayerInteraction, fbb => PlayerInteraction.Pack(fbb, interaction), 64);
        TrySend(target, outbound);
        outbound.Release();
        Stats.InteractionsRelayed++;
    }

    private bool TakeInteractionToken(int playerId, long now)
    {
        double rate = Math.Max(1, Opt.InteractionPerSecond);
        double burst = Math.Max(1, Opt.InteractionBurst);
        (double tokens, long at) = _interactionBuckets.TryGetValue(playerId, out var bucket) ? bucket : (burst, now);
        tokens = Math.Min(burst, tokens + ((now - at) / (double)_time.TimestampFrequency * rate));
        if (tokens < 1)
        {
            _interactionBuckets[playerId] = (tokens, now);
            return false;
        }

        _interactionBuckets[playerId] = (tokens - 1, now);
        return true;
    }

    // ------------------------------------------------------------------ join / leave hooks

    /// <summary>A node that negotiates the capability gets the presence snapshot and everybody's appearance when it attaches.</summary>
    private void SendOnFootSnapshot(SessionNode node)
    {
        if (!HasOnFoot(node))
        {
            return;
        }

        foreach (var (player, appearance) in _appearances)
        {
            if (player != node.PlayerId)
            {
                SendAppearance(node, appearance);
            }
        }

        if (_appearances.TryGetValue(node.PlayerId, out var own))
        {
            SendAppearance(node, own); // the canonical revision the server holds for this player
        }

        SendPresenceRoster(node, BuildPresenceRoster());
    }

    private void ForgetOnFoot(int playerId)
    {
        SendLeaveStates(playerId);
        _onFoot.Remove(playerId);
        _interactionBuckets.Remove(playerId);
        _rosterDirty = true;
    }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "relay: dropped an on-foot state of player {PlayerId} ({Why})")]
    private partial void LogOnFootDropped(int playerId, string why);
}
