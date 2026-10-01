using System.Globalization;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

/// <summary>
/// A minimal stand-in for the server's per-client delta replicator (used by offline tests and <c>inspect</c>):
/// turns authoritative <see cref="EntityStateT"/>s into <c>Replication</c> messages through the real
/// <see cref="ReplicationCodec"/>. A new entity gets a full entry; later entries carry only the fields that
/// changed against the baseline (absolute values), plus TIME.
/// </summary>
public sealed class ReferenceReplicator
{
    private readonly Dictionary<uint, EntityStateT> _baseline = [];

    /// <summary>Entry size is at most 35 bytes; 30 entries stay under the 1150 B datagram budget.</summary>
    public int MaxEntriesPerMessage { get; init; } = 30;

    public void Reset() => _baseline.Clear();

    public IReadOnlyList<ReplicationT> Build(uint serverTick, ulong serverTimeUs, double gameTime, IEnumerable<EntityStateT> states)
    {
        var messages = new List<ReplicationT>();
        var entries = new List<ReplicationEntry>();

        void Flush()
        {
            if (entries.Count == 0)
                return;
            messages.Add(new ReplicationT
            {
                ServerTick = serverTick,
                ServerTimeUs = serverTimeUs,
                AuthorityGameTime = gameTime,
                EntryCount = (ushort)entries.Count,
                Entries = [.. ReplicationCodec.Encode(entries.ToArray())],
            });
            entries.Clear();
        }

        foreach (var s in states)
        {
            entries.Add(MakeEntry(s));
            _baseline[s.NetId] = s;
            if (entries.Count >= MaxEntriesPerMessage)
                Flush();
        }
        Flush();
        return messages;
    }

    private ReplicationEntry MakeEntry(EntityStateT s)
    {
        var e = new ReplicationEntry { NetId = s.NetId, Mask = ReplicationMask.Time, TimeMs = 0 };
        _baseline.TryGetValue(s.NetId, out var b);

        if (b is null || b.Sector != s.Sector)
        {
            e.Mask |= ReplicationMask.Sector;
            e.Sector = s.Sector;
        }
        if (b is null || b.Px != s.Px || b.Py != s.Py || b.Pz != s.Pz)
        {
            e.Mask |= ReplicationMask.Pos;
            e.PosX = s.Px;
            e.PosY = s.Py;
            e.PosZ = s.Pz;
        }
        if (b is null || b.Yaw != s.Yaw || b.Pitch != s.Pitch || b.Roll != s.Roll)
        {
            e.Mask |= ReplicationMask.Rot;
            e.Yaw = s.Yaw;
            e.Pitch = s.Pitch;
            e.Roll = s.Roll;
        }
        if (b is null || b.Vx != s.Vx || b.Vy != s.Vy || b.Vz != s.Vz)
        {
            e.Mask |= ReplicationMask.Vel;
            e.VelX = s.Vx;
            e.VelY = s.Vy;
            e.VelZ = s.Vz;
        }
        if (b is null || b.Flags != s.Flags)
        {
            e.Mask |= ReplicationMask.Flags;
            e.StateFlags = s.Flags;
        }
        return e;
    }
}

/// <summary>Allowed deviation between a received (dequantised) value and ground truth.</summary>
public sealed record VerifyTolerance
{
    /// <summary>Half a quantisation step (1/64 m) plus float slack.</summary>
    public double PositionMetres { get; init; } = 0.5 / Quantize.PositionStepsPerMetre + 1e-6;

    /// <summary>Half a rotation count (rad * 32768/pi), as counts.</summary>
    public double RotationCounts { get; init; } = 0.5 + 1e-6;

    /// <summary>Half a velocity step (0.25 m/s).</summary>
    public double VelocityMps { get; init; } = 0.125 + 1e-6;
}

public sealed record Violation(string Kind, uint NetId, double GameTime, string Detail);

/// <summary>
/// End-to-end position verifier (server-design 6.3): decodes <c>Replication</c> with the real codec and compares every
/// received field to the fake world's ground truth at the entry's sample time
/// (<c>authority_game_time + TIME/1000</c>) within quantisation tolerance. It also keeps its own baseline, so entries
/// that omit fields are merged like a real client would, and flags first entries that are not complete.
/// </summary>
public sealed class ReplicationVerifier
{
    private const int MaxStoredViolations = 200;

    private sealed class Known
    {
        public ushort Sector;
        public int Px, Py, Pz;
        public short Yaw, Pitch, Roll;
    }

    private readonly FakeWorld _world;
    private readonly VerifyTolerance _tol;
    private readonly Dictionary<uint, Known> _baseline = [];
    private readonly List<Violation> _violations = [];

    public ReplicationVerifier(FakeWorld world, VerifyTolerance? tolerance = null)
    {
        _world = world;
        _tol = tolerance ?? new VerifyTolerance();
    }

    public long MessagesChecked { get; private set; }
    public long EntriesChecked { get; private set; }

    /// <summary>Total violations (the stored list is capped).</summary>
    public long Errors { get; private set; }

    public IReadOnlyList<Violation> Violations => _violations;

    public int KnownEntities => _baseline.Count;

    public string Summary() =>
        string.Create(CultureInfo.InvariantCulture, $"messages={MessagesChecked} entries={EntriesChecked} errors={Errors}");

    public void Reset() => _baseline.Clear();

    private void Fail(string kind, uint netId, double time, string detail)
    {
        Errors++;
        if (_violations.Count < MaxStoredViolations)
            _violations.Add(new Violation(kind, netId, time, detail));
    }

    /// <summary>Verifies one decoded Replication table.</summary>
    public void Verify(ReplicationT message)
    {
        MessagesChecked++;
        List<ReplicationEntry> entries;
        try
        {
            entries = ReplicationCodec.Decode(message.Entries?.ToArray() ?? [], message.EntryCount);
        }
        catch (ProtocolViolation ex)
        {
            Fail("malformed", 0, message.AuthorityGameTime, ex.Message);
            return;
        }

        foreach (var e in entries)
            VerifyEntry(message.AuthorityGameTime, e);
    }

    /// <summary>Verifies a Replication frame (decodes it first).</summary>
    public void Verify(Frame frame)
    {
        if (frame.Type != MsgType.Replication)
            throw new ArgumentException("Not a Replication frame.", nameof(frame));
        Verify(MessageRegistry.Default.Decode<Replication>(frame).UnPack());
    }

    private void VerifyEntry(double referenceGameTime, in ReplicationEntry e)
    {
        EntriesChecked++;
        double time = referenceGameTime + ((e.Mask & ReplicationMask.Time) != 0 ? e.TimeMs / 1000.0 : 0);
        int entityId = FakeNetIds.ToEntityId(e.NetId);
        if (entityId < 1 || entityId > _world.Galaxy.Entities.Count)
        {
            Fail("unknown-entity", e.NetId, time, "net_id is not part of the fake galaxy");
            return;
        }

        var truth = _world.GetKinematics(entityId, time);
        bool first = !_baseline.TryGetValue(e.NetId, out var k);
        k ??= new Known();
        const ReplicationMask needed = ReplicationMask.Sector | ReplicationMask.Pos | ReplicationMask.Rot;
        if (first && (e.Mask & needed) != needed)
            Fail("incomplete-first-entry", e.NetId, time, $"mask {e.Mask} lacks sector/pos/rot for a new entity");

        double slack = (e.Mask & ReplicationMask.Time) != 0 && e.TimeMs != 0 ? truth.Vel.Length * 0.0005 : 0;

        if ((e.Mask & ReplicationMask.Sector) != 0)
        {
            k.Sector = e.Sector;
            if (e.Sector != truth.Sector)
                Fail("sector", e.NetId, time, $"got {e.Sector}, truth {truth.Sector}");
        }
        if ((e.Mask & ReplicationMask.Pos) != 0)
        {
            k.Px = e.PosX;
            k.Py = e.PosY;
            k.Pz = e.PosZ;
            double dx = Quantize.PositionToMetres(e.PosX) - truth.Pos.X;
            double dy = Quantize.PositionToMetres(e.PosY) - truth.Pos.Y;
            double dz = Quantize.PositionToMetres(e.PosZ) - truth.Pos.Z;
            double worst = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz)));
            if (worst > _tol.PositionMetres + slack)
                Fail("position", e.NetId, time, string.Create(CultureInfo.InvariantCulture, $"error {worst:F4} m (dx {dx:F4}, dy {dy:F4}, dz {dz:F4})"));
        }
        if ((e.Mask & ReplicationMask.Rot) != 0)
        {
            k.Yaw = e.Yaw;
            k.Pitch = e.Pitch;
            k.Roll = e.Roll;
            double worst = Math.Max(AngleErrorCounts(e.Yaw, truth.Yaw), Math.Max(AngleErrorCounts(e.Pitch, truth.Pitch), AngleErrorCounts(e.Roll, truth.Roll)));
            if (worst > _tol.RotationCounts + slack * Quantize.RotationCountsPerRadian)
                Fail("rotation", e.NetId, time, string.Create(CultureInfo.InvariantCulture, $"error {worst:F3} counts"));
        }
        if ((e.Mask & ReplicationMask.Vel) != 0)
        {
            bool coarse = (e.StateFlags & (ushort)StateFlags.VelCoarse) != 0 && (e.Mask & ReplicationMask.Flags) != 0;
            double dx = Quantize.VelocityToMps(e.VelX, coarse) - truth.Vel.X;
            double dy = Quantize.VelocityToMps(e.VelY, coarse) - truth.Vel.Y;
            double dz = Quantize.VelocityToMps(e.VelZ, coarse) - truth.Vel.Z;
            double worst = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz)));
            if (worst > _tol.VelocityMps)
                Fail("velocity", e.NetId, time, string.Create(CultureInfo.InvariantCulture, $"error {worst:F3} m/s"));
        }
        _baseline[e.NetId] = k;
    }

    private static double AngleErrorCounts(short received, double truthRadians)
    {
        double d = received - truthRadians * Quantize.RotationCountsPerRadian;
        d -= 65536.0 * Math.Round(d / 65536.0);
        return Math.Abs(d);
    }
}
