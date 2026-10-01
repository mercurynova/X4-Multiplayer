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
    private const int PersistentMismatchLimit = 3;

    private sealed class Ghost
    {
        public ushort Sector;
        public double LastEntryAt;

        /// <summary>A full state entry arrived since the (re)spawn: the ghost's baseline is complete.</summary>
        public bool GotFull;
    }

    /// <summary>The fields a baseline consists of: an entry that carries all of them can start one.</summary>
    private const ReplicationMask Complete =
        ReplicationMask.Sector | ReplicationMask.Pos | ReplicationMask.Rot | ReplicationMask.Vel | ReplicationMask.Flags | ReplicationMask.Status;

    private readonly Dictionary<uint, Ghost> _ghosts = [];
    private readonly Dictionary<uint, double> _tombstones = [];
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
                existing.Sector = sector; // a refresh (resync)
                existing.LastEntryAt = now;
                existing.GotFull = false;
            }
            else
            {
                _ghosts[id] = new Ghost { Sector = sector, LastEntryAt = now };
            }

            SpawnsApplied++;
        }
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

        var accepted = new List<ReplicationEntry>(entries.Count);
        foreach (var entry in entries)
        {
            ReplicationEntries++;
            if (_ghosts.TryGetValue(entry.NetId, out var ghost))
            {
                ghost.LastEntryAt = now;
                if ((entry.Mask & Complete) == Complete)
                {
                    ghost.GotFull = true;
                }
                else if (!ghost.GotFull)
                {
                    StaleEntries++;
                    continue;
                }

                if ((entry.Mask & ReplicationMask.Sector) != 0)
                    ghost.Sector = entry.Sector;
                accepted.Add(entry);
            }
            else if (_tombstones.TryGetValue(entry.NetId, out double until) && until > now)
            {
                TombstonedEntries++;
            }
            else
            {
                Fail("state-before-spawn", entry.NetId, $"entry for an id that is not a ghost (mask {entry.Mask})");
            }
        }

        if (_verify)
            Verifier.VerifyEntries(message.AuthorityGameTime, accepted);
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
