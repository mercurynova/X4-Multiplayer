using System.Collections.Concurrent;
using System.Globalization;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

/// <summary>
/// The receiving half of a fake client (server-design 6.3 <c>--verify</c>), with no I/O: feed it every frame the server sends
/// (<see cref="Handle"/>) and it behaves like the mod should.
/// <list type="bullet">
/// <item><b>Ghost set.</b> <c>EntitySpawn</c> adds a ghost (persistent kinds are matched to local objects, not ghosted), <c>EntityDespawn</c> removes it
/// and tombstones the id for 5 s. A spawn of a known id refreshes it (resync).</item>
/// <item><b>Replication.</b> Every entry is decoded with the real codec and checked against the fake world's ground truth by
/// <see cref="ReplicationVerifier"/> (position, rotation, velocity, sector within quantisation tolerance at the entry's sample time). An entry for an
/// id that is neither a ghost nor tombstoned (a state before its spawn) is a violation, as is a ghost that gets no entry at all for far
/// longer than the keyframe interval.</item>
/// <item><b>Desync guard.</b> <c>InterestChecksum</c> is compared with the ghost set; a mismatch sends <c>ResyncRequest</c>
/// (<see cref="Handle"/> returns it) and counts. A mismatch that does not heal after a few checksums is a violation.</item>
/// </list>
/// </summary>
public sealed class FakeClientSession
{
    /// <summary>A ghost that received nothing for this long (3 x the longest keyframe interval) is stale.</summary>
    public const double StaleSeconds = 45;

    private const double TombstoneSeconds = 5;

    /// <summary>protocol.md 10.2: an entry whose spawn has not arrived is held this long, then dropped.</summary>
    public const double HoldSeconds = 2;

    private const int MaxHeld = 4096;
    private const int PersistentMismatchLimit = 3;

    private sealed class Ghost
    {
        public ushort Sector;
        public ushort OwnerTeam;
        public ushort OwnerPlayer;
        public double LastEntryAt;

        /// <summary>A full state entry arrived since the (re)spawn: the ghost's baseline is complete.</summary>
        public bool GotFull;

        /// <summary>Server tick of the newest frame that carried an entry of this ghost (an entry of an older frame arrived late and is ignored).</summary>
        public uint LastTick;

        // --- player ships only (M3-05): what a wingman needs to follow the ship, and the arrival statistics
        public bool IsPlayerShip;
        public string Name = string.Empty;
        public ushort ControllerPlayer;
        public Vec3 Pos;
        public Vec3 Vel;
        public double Yaw;
        public double Pitch;
        public ushort StateFlags;
        public double PoseAt;
        public bool HasPose;
        public FakeSyncStats? Sync;
    }

    /// <summary>The newest replicated pose of a player ship, as a wingman or a test reads it.</summary>
    public sealed record PlayerPose(uint NetId, string Name, ushort ControllerPlayer, ushort Sector, Vec3 Pos, Vec3 Vel, double Yaw, double Pitch, double AgeSeconds);

    /// <summary>The distance within which a player ship counts as Near for <see cref="FakeSyncStats"/> (m3-plan 4.5).</summary>
    public const double NearMetres = 15_000;

    private volatile OwnPose _own = new(0, default);

    private sealed record OwnPose(ushort Sector, Vec3 Pos);

    /// <summary>Where this client's own ship is (set by the runner every tick): decides whether an entry of another ship counts as Near.</summary>
    public void SetOwnPose(ushort sector, Vec3 position) => _own = new OwnPose(sector, position);

    /// <summary>The player id the server gave this node (needed to recognise its own avatar and its own chat messages); 0 = unknown.</summary>
    public int OwnPlayerId { get; set; }

    /// <summary>The avatar the authority spawned for this node (<c>EntitySpawn</c> with this node as controller), once it arrived.</summary>
    public (uint NetId, ushort Sector, Vec3 Position)? OwnAvatar { get; private set; }

    private bool _ownGhostExcluded;
    private uint _ownAvatarId;
    private readonly TaskCompletionSource _ownAvatarArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the spawn of this node's own avatar arrived (<see cref="OwnAvatar"/> is set then).</summary>
    public Task OwnAvatarReady => _ownAvatarArrived.Task;

    /// <summary>
    /// True once the server was found to leave this node's own ship out of its ghost set (<c>InterestChecksum</c> only matches without it): the avatar
    /// answer is then no ghost. m3-plan M3-01 makes the server do this; before that the own avatar is a ghost like any other player ship.
    /// </summary>
    public bool OwnShipExcludedByServer => _ownGhostExcluded;

    /// <summary>Messages received on the chat channels (newest last, capped at 200).</summary>
    public IReadOnlyList<ChatLine> ChatLog
    {
        get
        {
            lock (_chatLog)
                return [.. _chatLog];
        }
    }

    private readonly List<ChatLine> _chatLog = [];

    /// <summary>Answer every chat message of another player with <c>echo: &lt;text&gt;</c> on the same channel (<c>--chat-echo</c>).</summary>
    public bool ChatEcho { get; set; }

    public long ChatReceived { get; private set; }

    public long ChatEchoed { get; private set; }

    /// <summary>The prefix an echo starts with; a message that has it is never echoed again (two echo bots would answer each other for ever).</summary>
    public const string EchoPrefix = "echo: ";

    /// <summary>An entry that arrived before its spawn (UDP Realtime overtakes the Control lane): held up to <see cref="HoldSeconds"/>.</summary>
    private readonly record struct HeldEntry(ReplicationEntry Entry, double GameTime, uint Tick, double At);

    /// <summary>The fields a baseline consists of: an entry that carries all of them can start one.</summary>
    private const ReplicationMask Complete =
        ReplicationMask.Sector | ReplicationMask.Pos | ReplicationMask.Rot | ReplicationMask.Vel | ReplicationMask.Flags | ReplicationMask.Status;

    // Concurrent: the reader thread adds/removes ghosts while the node loop (CheckStale, PickAsset...) and tests read the table. A plain
    // Dictionary threw ArgumentException from CopyTo (not just InvalidOperationException) when a snapshot raced a spawn.
    private readonly ConcurrentDictionary<uint, Ghost> _ghosts = [];
    private readonly Dictionary<uint, double> _tombstones = [];
    private readonly Dictionary<uint, List<HeldEntry>> _held = [];
    private int _heldCount;
    private readonly Func<double> _clock;
    private readonly bool _verify;
    private int _mismatchStreak;
    private long _extraErrors;
    private readonly List<Violation> _violations = [];

    /// <param name="world">Ground truth (a private instance per session: <see cref="FakeWorld"/> caches are not shared across threads).</param>
    /// <param name="verify">Check entries against ground truth (otherwise they are only counted).</param>
    /// <param name="clock">Seconds, monotonic (tombstones and staleness); defaults to a stopwatch.</param>
    public FakeClientSession(FakeWorld world, bool verify = true, Func<double>? clock = null, VerifyTolerance? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        Verifier = new ReplicationVerifier(world, tolerance) { AllowRuntimeEntities = true };
        _verify = verify;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _clock = clock ?? (() => watch.Elapsed.TotalSeconds);
    }

    public ReplicationVerifier Verifier { get; }

    /// <summary>Team table, relation matrix and policy as the server last said (M1-T3); the fake NPC hostility reads it.</summary>
    public FakeTeamState Teams { get; } = new();

    /// <summary>Ghosts held now (persistent entities are not ghosts).</summary>
    public int Ghosts => _ghosts.Count;

    public IReadOnlyCollection<uint> GhostIds => (IReadOnlyCollection<uint>)_ghosts.Keys.ToArray();

    public bool IsGhost(uint netId) => _ghosts.ContainsKey(netId);

    /// <summary>
    /// A ghost to command for <paramref name="mode"/> (M1-T4), the <paramref name="n"/>-th candidate round-robin by net_id; null when there is none.
    /// Own = my team's team-common ships and the ones I own, Shared = a teammate's ships, Foreign = ships of another team.
    /// </summary>
    public (uint NetId, ushort Sector)? PickAsset(CommanderMode mode, int myTeam, int myPlayer, int n)
    {
        if (mode == CommanderMode.None || myTeam == 0)
            return null;
        var candidates = _ghosts
            .Where(g => mode switch
            {
                CommanderMode.Own => g.Value.OwnerTeam == myTeam && (g.Value.OwnerPlayer == 0 || g.Value.OwnerPlayer == myPlayer),
                CommanderMode.Shared => g.Value.OwnerTeam == myTeam && g.Value.OwnerPlayer != 0 && g.Value.OwnerPlayer != myPlayer,
                _ => g.Value.OwnerTeam != 0 && g.Value.OwnerTeam != myTeam,
            })
            .OrderBy(g => g.Key)
            .ToList();
        if (candidates.Count == 0)
            return null;
        var pick = candidates[n % candidates.Count];
        return (pick.Key, pick.Value.Sector);
    }

    /// <summary>
    /// A ghost owned by <paramref name="team"/> (any player), the <paramref name="n"/>-th round-robin by net_id: what a trading client offers (M1-E5).
    /// </summary>
    public (uint NetId, ushort Sector)? PickTeamAsset(int team, int n)
    {
        if (team == 0)
            return null;
        List<KeyValuePair<uint, Ghost>> candidates = [];
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                // The reader thread adds and removes ghosts while the node loop looks: a collection that changed under the enumeration is read again.
                candidates = [.. _ghosts.Where(g => g.Value.OwnerTeam == team).OrderBy(g => g.Key)];
                break;
            }
            catch (InvalidOperationException)
            {
                candidates = [];
            }
        }

        if (candidates.Count == 0)
            return null;
        var pick = candidates[n % candidates.Count];
        return (pick.Key, pick.Value.Sector);
    }

    /// <summary>Times the connection was resumed (<see cref="ResetForResume"/>): <c>--disconnect-every</c>, <c>--reload-every</c>.</summary>
    public int Resumes { get; private set; }

    /// <summary>Full-state entries (keyframes) received since the last <see cref="ResetForResume"/>: the server restarts the baselines of a resumed node.</summary>
    public long KeyframesSinceResume { get; private set; }

    /// <summary>Ghosts held when the last resume happened.</summary>
    public int GhostsBeforeResume { get; private set; }

    /// <summary>
    /// The connection was replaced (protocol.md 6.6): the server clears this client's baselines and re-sends spawns and keyframes for its whole interest
    /// set, so the ghost table starts empty (a ghost the server despawned during the gap would otherwise live on). Counters keep adding up.
    /// </summary>
    public void ResetForResume()
    {
        GhostsBeforeResume = _ghosts.Count;
        _ghosts.Clear();
        _tombstones.Clear();
        _held.Clear();
        _heldCount = 0;
        _mismatchStreak = 0;
        Verifier.Reset();
        KeyframesSinceResume = 0;
        Resumes++;
    }

    public long SpawnsApplied { get; private set; }

    public long DespawnsApplied { get; private set; }

    public long ReplicationFrames { get; private set; }

    public long ReplicationEntries { get; private set; }

    public long ChecksumsOk { get; private set; }

    public long ChecksumMismatches { get; private set; }

    public long ResyncsRequested { get; private set; }

    /// <summary>Entries for ids that were despawned less than 5 s ago (frames that were in flight): ignored, as the mod does.</summary>
    public long TombstonedEntries { get; private set; }

    /// <summary>
    /// Partial entries that arrived for a ghost before its first full entry: frames that were already queued when the entity despawned and
    /// spawned again (the Control lane overtakes the Realtime lane) describe the old life. The client ignores them; the full entry the server
    /// always sends first after a spawn follows.
    /// </summary>
    public long StaleEntries { get; private set; }

    public long SectorCompletes { get; private set; }

    /// <summary>
    /// True for a node whose Realtime lane is UDP: a Replication entry for an id without a spawn is held until its spawn arrives (at most
    /// <see cref="HoldSeconds"/>) instead of counting as a violation, because the spawn travels on TCP and may be overtaken.
    /// </summary>
    public bool HoldUnknownEntries { get; set; }

    /// <summary>Entries held for a spawn that never came within <see cref="HoldSeconds"/> (dropped, as protocol.md 10.2 says).</summary>
    public long HeldDropped { get; private set; }

    /// <summary>Entries that were held and applied when their spawn arrived.</summary>
    public long HeldApplied { get; private set; }

    /// <summary>Entries of a frame older than one already applied to the same ghost (a datagram that arrived out of order): ignored.</summary>
    public long ReorderedEntries { get; private set; }

    /// <summary>Test hook: the next checksum is compared against a deliberately wrong ghost count (exercises the resync path).</summary>
    public int ChecksumCountSkew { get; set; }

    /// <summary>All violations: position errors from the verifier plus protocol-level ones found here.</summary>
    public long Errors => Verifier.Errors + _extraErrors;

    public IReadOnlyList<Violation> Violations => [.. Verifier.Violations, .. _violations];

    public string Summary() => string.Create(CultureInfo.InvariantCulture,
        $"ghosts={Ghosts} spawns={SpawnsApplied} despawns={DespawnsApplied} frames={ReplicationFrames} entries={ReplicationEntries} " +
        $"checksums={ChecksumsOk}/{ChecksumsOk + ChecksumMismatches} resyncs={ResyncsRequested} errors={Errors}");

    /// <summary>Records a protocol-level failure found by the runner (for example a resume that never got its keyframes).</summary>
    public void Report(string kind, string detail) => Fail(kind, 0, detail);

    private void Fail(string kind, uint netId, string detail)
    {
        _extraErrors++;
        if (_violations.Count < 100)
            _violations.Add(new Violation(kind, netId, 0, detail));
    }

    /// <summary>Processes one frame from the server; returns what the client sends back (<c>ResyncRequest</c>), usually nothing.</summary>
    public IReadOnlyList<OutMessage> Handle(Frame frame)
    {
        switch (frame.Type)
        {
            case MsgType.EntityChange:
                ApplyChange(MessageRegistry.Default.Decode<EntityChange>(frame));
                break;
            case MsgType.EntitySpawn:
                ApplySpawn(MessageRegistry.Default.Decode<EntitySpawn>(frame));
                break;
            case MsgType.EntityDespawn:
                ApplyDespawn(MessageRegistry.Default.Decode<EntityDespawn>(frame));
                break;
            case MsgType.SectorComplete:
                SectorCompletes++;
                break;
            case MsgType.Replication:
                ApplyReplication(MessageRegistry.Default.Decode<Replication>(frame));
                break;
            case MsgType.InterestChecksum:
                return CheckChecksum(MessageRegistry.Default.Decode<InterestChecksum>(frame));
            case MsgType.ChatMessage:
                return OnChat(MessageRegistry.Default.Decode<ChatMessage>(frame));
            case MsgType.TeamTable or MsgType.TeamRelations or MsgType.SessionSettings or MsgType.TeamMemberChanged
                or MsgType.RelationProposal or MsgType.TeamRequestResult or MsgType.ReassignPlayerAssets:
                return Teams.Handle(frame);
        }

        return [];
    }

    /// <summary>The same persistent kinds as the server's mirror: stations and other fixtures are matched to the local copy, never ghosted.</summary>
    public static bool IsPersistent(EntityKind kind) => kind is
        EntityKind.Station or EntityKind.Gate or EntityKind.Accelerator or EntityKind.HighwayEntry
        or EntityKind.Satellite or EntityKind.NavBeacon or EntityKind.ResourceProbe or EntityKind.Mine or EntityKind.LaserTower;

    /// <summary><c>EntityChange{Controller}</c> applied to a ghost (an avatar whose player left is parked: controller 0).</summary>
    public long ControllerChanges { get; private set; }

    /// <summary>The controller player (0 = none) a tracked player ship has now; null when it is no player ship ghost.</summary>
    public int? ControllerOf(uint netId) => _ghosts.TryGetValue(netId, out var g) && g.IsPlayerShip ? g.ControllerPlayer : null;

    /// <summary>Ownership changes (<c>EntityChange</c> with an owner bit) applied to ghosts (M1-F3: a moved player's assets change team).</summary>
    public long OwnerChanges { get; private set; }

    private void ApplyChange(EntityChange change)
    {
        if (!_ghosts.TryGetValue(change.NetId, out var ghost))
            return;
        if ((change.Fields & ChangeField.OwnerTeam) != 0)
        {
            ghost.OwnerTeam = change.OwnerTeam;
            OwnerChanges++;
        }

        if ((change.Fields & ChangeField.OwnerPlayer) != 0)
            ghost.OwnerPlayer = change.OwnerPlayer;
        if ((change.Fields & ChangeField.Controller) != 0)
        {
            ghost.ControllerPlayer = change.ControllerPlayer;
            ControllerChanges++;
        }

        if ((change.Fields & ChangeField.Name) != 0)
            ghost.Name = change.Name ?? string.Empty;
    }

    /// <summary>The net_ids of the ghosts this client believes are owned by <paramref name="player"/> in <paramref name="team"/>, in net_id order.</summary>
    public IReadOnlyList<(uint NetId, ushort Sector)> GhostsOwnedBy(int team, int player)
    {
        // The reader thread changes the ghost table while a test or tool looks at it from another thread: take the snapshot again if it moved.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return [.. _ghosts.Where(g => g.Value.OwnerTeam == team && g.Value.OwnerPlayer == player).OrderBy(g => g.Key).Select(g => (g.Key, g.Value.Sector))];
            }
            catch (InvalidOperationException) when (attempt < 8)
            {
                // retry
            }
        }
    }

    /// <summary>The owner this client currently believes a ghost has (null = no such ghost).</summary>
    public (ushort Team, ushort Player)? OwnerOf(uint netId) =>
        _ghosts.TryGetValue(netId, out var g) ? (g.OwnerTeam, g.OwnerPlayer) : null;

    private void ApplySpawn(EntitySpawn spawn)
    {
        double now = _clock();
        for (int i = 0; i < spawn.EntitiesLength; i++)
        {
            if (spawn.Entities(i) is not { } record || IsPersistent(record.Kind))
                continue;
            uint id = record.NetId;
            _tombstones.Remove(id);
            ushort sector = record.State?.Sector ?? 0;
            bool playerShip = record.Origin == EntityOrigin.PlayerShip || record.ControllerPlayer != 0;
            bool own = playerShip && OwnPlayerId != 0 && (record.ControllerPlayer == OwnPlayerId || (record.Origin == EntityOrigin.PlayerShip && record.OwnerPlayer == OwnPlayerId));
            if (own)
            {
                _ownAvatarId = id;
                var s = record.State;
                OwnAvatar = (id, sector, s is null ? default : new Vec3(Quantize.PositionToMetres(s.Value.Px), Quantize.PositionToMetres(s.Value.Py), Quantize.PositionToMetres(s.Value.Pz)));
                _ownAvatarArrived.TrySetResult();
                if (_ownGhostExcluded)
                {
                    SpawnsApplied++;
                    continue; // the server does not count this ship in the ghost set
                }
            }

            if (_ghosts.TryGetValue(id, out var existing))
            {
                existing.OwnerTeam = record.OwnerTeam;
                existing.OwnerPlayer = record.OwnerPlayer;
                existing.Sector = sector; // a refresh (resync)
                existing.LastEntryAt = now;
                existing.GotFull = false;
                if (playerShip)
                    NotePlayerRecord(existing, record);
            }
            else
            {
                var ghost = new Ghost { Sector = sector, OwnerTeam = record.OwnerTeam, OwnerPlayer = record.OwnerPlayer, LastEntryAt = now };
                if (playerShip)
                    NotePlayerRecord(ghost, record);
                _ghosts[id] = ghost;
            }

            SpawnsApplied++;
            if (_held.Remove(id, out var held))
                ReplayHeld(id, held);
        }
    }

    /// <summary>A player ship (avatar, host ship) is tracked: name, controller, the pose of its spawn record and a place for the arrival statistics.</summary>
    private static void NotePlayerRecord(Ghost ghost, EntityRecord record)
    {
        ghost.IsPlayerShip = true;
        ghost.Name = record.Name ?? string.Empty;
        ghost.ControllerPlayer = record.ControllerPlayer;
        ghost.Sync ??= new FakeSyncStats();
        if (record.State is { } s)
        {
            ghost.Pos = new Vec3(Quantize.PositionToMetres(s.Px), Quantize.PositionToMetres(s.Py), Quantize.PositionToMetres(s.Pz));
            ghost.Yaw = Quantize.RotationToRadians(s.Yaw);
            ghost.Pitch = Quantize.RotationToRadians(s.Pitch);
            ghost.Vel = default;
            ghost.HasPose = true;
        }
    }

    /// <summary>The newest replicated pose of the player ship whose name is <paramref name="playerName"/> (<c>[MP] Alice</c>, <c>[MP] Alice (offline)</c> and <c>Alice</c> all match); null when it is not known.</summary>
    public PlayerPose? FindPlayerPose(string playerName)
    {
        string wanted = StripName(playerName);
        double now = _clock();
        foreach (var (id, g) in _ghosts.ToArray())
        {
            if (g.IsPlayerShip && g.HasPose && string.Equals(StripName(g.Name), wanted, StringComparison.OrdinalIgnoreCase))
                return new PlayerPose(id, g.Name, g.ControllerPlayer, g.Sector, g.Pos, g.Vel, g.Yaw, g.Pitch, Math.Max(0, now - g.PoseAt));
        }

        return null;
    }

    /// <summary>Every player ship this client tracks, with what arrived for it so far (net_id order).</summary>
    public IReadOnlyList<SyncSummary> SyncSummaries() =>
        [.. _ghosts.ToArray().Where(g => g.Value.IsPlayerShip && g.Value.Sync is not null).OrderBy(g => g.Key).Select(g => g.Value.Sync!.Summarize(g.Key, g.Value.Name))];

    /// <summary>Net_ids of the player ships this client currently sees as ghosts.</summary>
    public IReadOnlyList<uint> PlayerShipIds => [.. _ghosts.ToArray().Where(g => g.Value.IsPlayerShip).Select(g => g.Key).Order()];

    private static string StripName(string name)
    {
        string n = name.Trim();
        if (n.StartsWith("[MP]", StringComparison.Ordinal))
            n = n[4..].TrimStart();
        const string offline = "(offline)";
        if (n.EndsWith(offline, StringComparison.OrdinalIgnoreCase))
            n = n[..^offline.Length].TrimEnd();
        return n;
    }

    private IReadOnlyList<OutMessage> OnChat(ChatMessage message)
    {
        string text = message.Text ?? string.Empty;
        var line = new ChatLine(message.FromPlayer, message.FromName ?? string.Empty, message.Channel, text);
        lock (_chatLog)
        {
            _chatLog.Add(line);
            if (_chatLog.Count > 200)
                _chatLog.RemoveAt(0);
        }

        ChatReceived++;
        if (!ChatEcho || message.FromPlayer == 0 || message.FromPlayer == OwnPlayerId || text.StartsWith(EchoPrefix, StringComparison.Ordinal)
            || message.Channel is not (ChatChannel.All or ChatChannel.Team or ChatChannel.Whisper))
            return [];

        string reply = EchoPrefix + text;
        if (reply.Length > 250)
            reply = reply[..250];
        var send = new ChatSendT
        {
            Channel = message.Channel,
            ToPlayer = message.Channel == ChatChannel.Whisper ? message.FromPlayer : (ushort)0,
            Text = reply,
        };
        ChatEchoed++;
        return [new OutMessage(MsgType.ChatSend, MessageEncoder.EncodePayload(b => ChatSend.Pack(b, send), 320))];
    }

    private void ReplayHeld(uint id, List<HeldEntry> held)
    {
        _heldCount -= held.Count;
        double now = _clock();
        foreach (var h in held)
        {
            if (now - h.At > HoldSeconds)
            {
                HeldDropped++;
                continue;
            }

            var accepted = new List<ReplicationEntry>(1);
            if (AcceptEntry(h.Entry, h.Tick, now, accepted))
                HeldApplied++;
            if (_verify && accepted.Count > 0)
                Verifier.VerifyEntries(h.GameTime, accepted);
        }
    }

    private void DropExpiredHeld(double now)
    {
        if (_held.Count == 0)
            return;
        List<uint>? gone = null;
        foreach (var (id, list) in _held)
        {
            int expired = list.RemoveAll(h => now - h.At > HoldSeconds);
            if (expired > 0)
            {
                HeldDropped += expired;
                _heldCount -= expired;
            }

            if (list.Count == 0)
                (gone ??= []).Add(id);
        }

        if (gone is not null)
            foreach (uint id in gone)
                _held.Remove(id);
    }

    private void ApplyDespawn(EntityDespawn despawn)
    {
        double now = _clock();
        for (int i = 0; i < despawn.EntriesLength; i++)
        {
            uint id = despawn.Entries(i)!.Value.NetId;
            if (_ghosts.TryRemove(id, out _))
            {
                DespawnsApplied++;
                _tombstones[id] = now + TombstoneSeconds;
                Verifier.Forget(id);
            }
        }
    }

    private void ApplyReplication(Replication message)
    {
        double now = _clock();
        ReplicationFrames++;
        List<ReplicationEntry> entries;
        try
        {
            entries = ReplicationCodec.Decode(message.GetEntriesArray(), message.EntryCount);
        }
        catch (ProtocolViolation ex)
        {
            Fail("malformed-replication", 0, ex.Message);
            return;
        }

        DropExpiredHeld(now);
        var accepted = new List<ReplicationEntry>(entries.Count);
        foreach (var entry in entries)
        {
            ReplicationEntries++;
            if (_ghosts.ContainsKey(entry.NetId))
            {
                AcceptEntry(entry, message.ServerTick, now, accepted);
            }
            else if (_tombstones.TryGetValue(entry.NetId, out double until) && until > now)
            {
                TombstonedEntries++;
            }
            else if (HoldUnknownEntries)
            {
                if (_heldCount >= MaxHeld)
                {
                    HeldDropped++;
                    continue;
                }

                if (!_held.TryGetValue(entry.NetId, out var list))
                    _held[entry.NetId] = list = [];
                list.Add(new HeldEntry(entry, message.AuthorityGameTime, message.ServerTick, now));
                _heldCount++;
            }
            else
            {
                Fail("state-before-spawn", entry.NetId, $"entry for an id that is not a ghost (mask {entry.Mask})");
            }
        }

        if (_verify)
            Verifier.VerifyEntries(message.AuthorityGameTime, accepted);
    }

    /// <summary>Applies one entry to its ghost. False when it is ignored (older than what the ghost already got, or a partial entry before the first full one).</summary>
    private bool AcceptEntry(ReplicationEntry entry, uint tick, double now, List<ReplicationEntry> accepted)
    {
        if (!_ghosts.TryGetValue(entry.NetId, out var ghost))
            return false;
        ghost.LastEntryAt = now;
        if (tick < ghost.LastTick)
        {
            ReorderedEntries++; // arrived after a newer frame: applying it would move the ghost back (the server only folds the newest into its baseline)
            return false;
        }

        if ((entry.Mask & Complete) == Complete)
        {
            ghost.GotFull = true;
            KeyframesSinceResume++;
        }
        else if (!ghost.GotFull)
        {
            StaleEntries++;
            return false;
        }

        ghost.LastTick = tick;
        if ((entry.Mask & ReplicationMask.Sector) != 0)
            ghost.Sector = entry.Sector;
        if (ghost.IsPlayerShip)
            TrackPose(ghost, entry, now);
        accepted.Add(entry);
        return true;
    }

    /// <summary>Merges one entry into the pose of a tracked player ship and records its arrival for the statistics.</summary>
    private void TrackPose(Ghost ghost, in ReplicationEntry entry, double now)
    {
        if ((entry.Mask & ReplicationMask.Pos) != 0)
            ghost.Pos = new Vec3(Quantize.PositionToMetres(entry.PosX), Quantize.PositionToMetres(entry.PosY), Quantize.PositionToMetres(entry.PosZ));
        if ((entry.Mask & ReplicationMask.Rot) != 0)
        {
            ghost.Yaw = Quantize.RotationToRadians(entry.Yaw);
            ghost.Pitch = Quantize.RotationToRadians(entry.Pitch);
        }

        if ((entry.Mask & ReplicationMask.Flags) != 0)
            ghost.StateFlags = entry.StateFlags;
        if ((entry.Mask & ReplicationMask.Vel) != 0)
        {
            bool coarse = (ghost.StateFlags & (ushort)StateFlags.VelCoarse) != 0;
            ghost.Vel = new Vec3(Quantize.VelocityToMps(entry.VelX, coarse), Quantize.VelocityToMps(entry.VelY, coarse), Quantize.VelocityToMps(entry.VelZ, coarse));
        }

        ghost.HasPose = true;
        ghost.PoseAt = now;
        var own = _own;
        bool near = own.Sector != 0 && ghost.Sector == own.Sector && (ghost.Pos - own.Pos).Length <= NearMetres;
        ghost.Sync?.Record(now, near, ghost.Vel.Length);
    }

    private IReadOnlyList<OutMessage> CheckChecksum(InterestChecksum checksum)
    {
        ulong hash = 0;
        foreach (uint id in _ghosts.Keys)
            hash ^= InterestHash.Mix(id);
        uint count = (uint)(_ghosts.Count + ChecksumCountSkew);
        ChecksumCountSkew = 0;

        // The server may keep this node's own ship out of its ghost set (M3-01). When the checksum only matches without it, stop counting it.
        if (_ownAvatarId != 0 && !_ownGhostExcluded && (checksum.Count != count || checksum.XorHash != hash)
            && _ghosts.ContainsKey(_ownAvatarId) && checksum.Count == count - 1 && checksum.XorHash == (hash ^ InterestHash.Mix(_ownAvatarId)))
        {
            _ghosts.TryRemove(_ownAvatarId, out _);
            _ownGhostExcluded = true;
            hash ^= InterestHash.Mix(_ownAvatarId);
            count--;
        }

        if (checksum.Count == count && checksum.XorHash == hash)
        {
            ChecksumsOk++;
            _mismatchStreak = 0;
            return [];
        }

        ChecksumMismatches++;
        _mismatchStreak++;
        ResyncsRequested++;
        if (_mismatchStreak >= PersistentMismatchLimit)
            Fail("checksum", 0, $"{_mismatchStreak} checksums in a row differ: server {checksum.Count} ghosts, client {count}");

        var request = new ResyncRequestT { Sectors = [], Reason = $"checksum mismatch: server {checksum.Count}/{checksum.XorHash:x16}, client {count}/{hash:x16}" };
        return [new OutMessage(MsgType.ResyncRequest, MessageEncoder.EncodePayload(b => ResyncRequest.Pack(b, request), 128))];
    }

    /// <summary>Flags ghosts that received nothing for <see cref="StaleSeconds"/> (keyframes guarantee one entry every 5 or 15 s). Call now and then, and at the end.</summary>
    public void CheckStale()
    {
        double now = _clock();
        // The reader thread adds and removes ghosts while the node loop looks (the first check runs right after joining, in the middle of the
        // catch-up): enumerate a snapshot, taken again if the table moved under it. Without this a bot died with "Collection was modified".
        KeyValuePair<uint, Ghost>[] snapshot = [];
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                snapshot = _ghosts.ToArray(); // atomic on a ConcurrentDictionary
                break;
            }
            catch (InvalidOperationException) when (attempt < 19)
            {
                // retry
            }
        }

        foreach (var (id, ghost) in snapshot)
        {
            if (id == _ownAvatarId && _ownAvatarId != 0)
                continue; // the server never replicates a node's own ship to it (it stays in the interest set, so it is a ghost without entries)
            if (now - ghost.LastEntryAt > StaleSeconds)
            {
                Fail("stale-ghost", id, string.Create(CultureInfo.InvariantCulture, $"no entry for {now - ghost.LastEntryAt:F0} s"));
                ghost.LastEntryAt = now; // report once per interval
            }
        }
    }
}

/// <summary>One chat message a fake client received.</summary>
public sealed record ChatLine(ushort FromPlayer, string FromName, ChatChannel Channel, string Text);
