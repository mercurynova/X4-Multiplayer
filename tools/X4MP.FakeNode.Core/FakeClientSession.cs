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
    }

    /// <summary>An entry that arrived before its spawn (UDP Realtime overtakes the Control lane): held up to <see cref="HoldSeconds"/>.</summary>
    private readonly record struct HeldEntry(ReplicationEntry Entry, double GameTime, uint Tick, double At);

    /// <summary>The fields a baseline consists of: an entry that carries all of them can start one.</summary>
    private const ReplicationMask Complete =
        ReplicationMask.Sector | ReplicationMask.Pos | ReplicationMask.Rot | ReplicationMask.Vel | ReplicationMask.Flags | ReplicationMask.Status;

    private readonly Dictionary<uint, Ghost> _ghosts = [];
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

    public IReadOnlyCollection<uint> GhostIds => _ghosts.Keys;

    public bool IsGhost(uint netId) => _ghosts.ContainsKey(netId);

    /// <summary>
    /// A ghost to command for <paramref name="mode"/> (M1-T4), the <paramref name="n"/>-th candidate round-robin by net_id; null when there is none.
    /// Own = my team's team-common ships, Shared = a teammate's ships, Foreign = ships of another team.
    /// </summary>
    public (uint NetId, ushort Sector)? PickAsset(CommanderMode mode, int myTeam, int myPlayer, int n)
    {
        if (mode == CommanderMode.None || myTeam == 0)
            return null;
        var candidates = _ghosts
            .Where(g => mode switch
            {
                CommanderMode.Own => g.Value.OwnerTeam == myTeam && g.Value.OwnerPlayer == 0,
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
        var candidates = _ghosts.Where(g => g.Value.OwnerTeam == team).OrderBy(g => g.Key).ToList();
        if (candidates.Count == 0)
            return null;
        var pick = candidates[n % candidates.Count];
        return (pick.Key, pick.Value.Sector);
    }

    /// <summary>Applies an ownership change (a settled trade) to the ghost; the other fields do not matter to the fake client.</summary>
    private void ApplyChange(EntityChange change)
    {
        if (!_ghosts.TryGetValue(change.NetId, out var ghost))
            return;
        if ((change.Fields & ChangeField.OwnerTeam) != 0)
            ghost.OwnerTeam = change.OwnerTeam;
        if ((change.Fields & ChangeField.OwnerPlayer) != 0)
            ghost.OwnerPlayer = change.OwnerPlayer;
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
            case MsgType.EntitySpawn:
                ApplySpawn(MessageRegistry.Default.Decode<EntitySpawn>(frame));
                break;
            case MsgType.EntityDespawn:
                ApplyDespawn(MessageRegistry.Default.Decode<EntityDespawn>(frame));
                break;
            case MsgType.SectorComplete:
                SectorCompletes++;
                break;
            case MsgType.EntityChange:
                ApplyChange(MessageRegistry.Default.Decode<EntityChange>(frame));
                break;
            case MsgType.Replication:
                ApplyReplication(MessageRegistry.Default.Decode<Replication>(frame));
                break;
            case MsgType.InterestChecksum:
                return CheckChecksum(MessageRegistry.Default.Decode<InterestChecksum>(frame));
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
            if (_ghosts.TryGetValue(id, out var existing))
            {
                existing.OwnerTeam = record.OwnerTeam;
                existing.OwnerPlayer = record.OwnerPlayer;
                existing.Sector = sector; // a refresh (resync)
                existing.LastEntryAt = now;
                existing.GotFull = false;
            }
            else
            {
                _ghosts[id] = new Ghost { Sector = sector, OwnerTeam = record.OwnerTeam, OwnerPlayer = record.OwnerPlayer, LastEntryAt = now };
            }

            SpawnsApplied++;
            if (_held.Remove(id, out var held))
                ReplayHeld(id, held);
        }
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
            if (_ghosts.Remove(id))
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
        }
        else if (!ghost.GotFull)
        {
            StaleEntries++;
            return false;
        }

        ghost.LastTick = tick;
        if ((entry.Mask & ReplicationMask.Sector) != 0)
            ghost.Sector = entry.Sector;
        accepted.Add(entry);
        return true;
    }

    private IReadOnlyList<OutMessage> CheckChecksum(InterestChecksum checksum)
    {
        ulong hash = 0;
        foreach (uint id in _ghosts.Keys)
            hash ^= InterestHash.Mix(id);
        uint count = (uint)(_ghosts.Count + ChecksumCountSkew);
        ChecksumCountSkew = 0;

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
        foreach (var (id, ghost) in _ghosts)
        {
            if (now - ghost.LastEntryAt > StaleSeconds)
            {
                Fail("stale-ghost", id, string.Create(CultureInfo.InvariantCulture, $"no entry for {now - ghost.LastEntryAt:F0} s"));
                ghost.LastEntryAt = now; // report once per interval
            }
        }
    }
}
