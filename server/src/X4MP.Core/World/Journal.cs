using Google.FlatBuffers;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.World;

/// <summary>A checkpoint identity (<c>Id128</c>).</summary>
public readonly record struct CheckpointId(ulong Lo, ulong Hi)
{
    public override string ToString() => $"{Hi:x16}{Lo:x16}";

    public static CheckpointId From(Id128? id) => id is { } v ? new CheckpointId(v.Lo, v.Hi) : default;

    public Id128T ToWire() => new() { Lo = Lo, Hi = Hi };
}

/// <summary>
/// One journal row (<c>journal</c> table): a persistent-entity mutation in server order. <see cref="Kind"/> is the message the
/// mutation arrived in and <see cref="Payload"/> the FlatBuffers table of its <c>WorldMutation</c> body: an
/// <c>EntityRecord</c> for <see cref="MsgType.EntitySpawn"/>, a <c>JournalDespawn</c> for <see cref="MsgType.EntityDespawn"/>, an
/// <c>EntityChange</c> or an <c>EntityCargo</c>. <see cref="MsgType.SaveStarted"/> rows are checkpoint markers (payload =
/// the <c>SaveStarted</c> table).
/// </summary>
public readonly record struct JournalRecord(ulong Seq, MsgType Kind, uint NetId, ushort Sector, byte[] Payload, DateTimeOffset At)
{
    public bool IsMarker => Kind == MsgType.SaveStarted;
}

/// <summary>The journal position of a <c>SaveStarted</c> marker.</summary>
public sealed record JournalMarker(ulong Seq, CheckpointId Checkpoint, uint RequestId, double GameTime, uint NextNetId);

/// <summary>
/// The persistent-entity journal (protocol.md 8, architecture 9): every persistent mutation the authority reports, stamped
/// with a server sequence, plus the <c>SaveStarted</c> markers that tie sequence numbers to checkpoints. A node that loaded
/// the checkpoint saved at marker M replays <see cref="ReadAfterMarker"/>; after a new checkpoint becomes current,
/// <see cref="CompactBeforePreviousCheckpoint"/> drops what even the previous checkpoint no longer needs. Rows are kept in memory
/// (they are small and rare) and written behind through the <see cref="IWorldStore"/>. Actor-thread only.
/// </summary>
public sealed class Journal(IWorldStore store)
{
    private readonly List<JournalRecord> _entries = [];
    private readonly List<JournalMarker> _markers = [];
    private ulong _lastSeq;

    /// <summary>The highest sequence assigned so far (0 = none).</summary>
    public ulong LastSeq => _lastSeq;

    public int Count => _entries.Count;

    /// <summary>The oldest sequence still held (0 when empty).</summary>
    public ulong FirstSeq => _entries.Count == 0 ? 0 : _entries[0].Seq;

    public IReadOnlyList<JournalRecord> Entries => _entries;

    public IReadOnlyList<JournalMarker> Markers => _markers;

    /// <summary>Appends one mutation and returns its sequence.</summary>
    public ulong Append(MsgType kind, uint netId, ushort sector, byte[] payload, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var record = new JournalRecord(++_lastSeq, kind, netId, sector, payload, at);
        _entries.Add(record);
        store.AppendJournal(record);
        return record.Seq;
    }

    /// <summary>
    /// Records a <c>SaveStarted</c> marker at the current position: everything appended before it is inside the save,
    /// everything after is not.
    /// </summary>
    public JournalMarker AppendMarker(uint requestId, CheckpointId checkpoint, double gameTime, uint nextNetId, DateTimeOffset at)
    {
        var payload = MessageEncoder.EncodePayload(
            b => SaveStarted.Pack(b, new SaveStartedT
            {
                RequestId = requestId,
                CheckpointId = checkpoint.ToWire(),
                GameTime = gameTime,
                NextNetId = nextNetId,
            }),
            96);
        var record = new JournalRecord(++_lastSeq, MsgType.SaveStarted, 0, 0, payload, at);
        _entries.Add(record);
        store.AppendJournal(record);
        var marker = new JournalMarker(record.Seq, checkpoint, requestId, gameTime, nextNetId);
        _markers.Add(marker);
        return marker;
    }

    public JournalMarker? FindMarker(CheckpointId checkpoint)
    {
        for (int i = _markers.Count - 1; i >= 0; i--)
        {
            if (_markers[i].Checkpoint == checkpoint)
            {
                return _markers[i];
            }
        }

        return null;
    }

    /// <summary>Mutations with a sequence above <paramref name="afterSeq"/>, in order (markers excluded unless asked for).</summary>
    public IEnumerable<JournalRecord> ReadAfter(ulong afterSeq, bool includeMarkers = false)
    {
        int index = FirstIndexAfter(afterSeq);
        for (int i = index; i < _entries.Count; i++)
        {
            var record = _entries[i];
            if (includeMarkers || !record.IsMarker)
            {
                yield return record;
            }
        }
    }

    /// <summary>
    /// The catch-up for a node that loaded <paramref name="checkpoint"/>: the mutations after its marker, in order. Null when the
    /// marker is unknown (never recorded or already compacted away): the node must take a newer save.
    /// </summary>
    public IReadOnlyList<JournalRecord>? ReadAfterMarker(CheckpointId checkpoint) =>
        FindMarker(checkpoint) is { } marker ? [.. ReadAfter(marker.Seq)] : null;

    /// <summary>
    /// A new checkpoint (<paramref name="current"/>) became the session's current save: drops every entry before the marker of the
    /// checkpoint before it, and the older markers. Returns how many entries were removed (0 when there is no previous checkpoint).
    /// </summary>
    public int CompactBeforePreviousCheckpoint(CheckpointId current)
    {
        int index = _markers.FindIndex(m => m.Checkpoint == current);
        if (index <= 0)
        {
            return 0;
        }

        return CompactBefore(_markers[index - 1].Seq);
    }

    /// <summary>Drops every entry with a sequence below <paramref name="seq"/> (and the markers among them).</summary>
    public int CompactBefore(ulong seq)
    {
        int cut = FirstIndexAtOrAfter(seq);
        if (cut == 0)
        {
            return 0;
        }

        _entries.RemoveRange(0, cut);
        _markers.RemoveAll(m => m.Seq < seq);
        store.TruncateJournal(seq);
        return cut;
    }

    /// <summary>Replaces the in-memory journal with what the store holds (resuming a session).</summary>
    public void Restore()
    {
        _entries.Clear();
        _markers.Clear();
        _lastSeq = 0;
        foreach (var record in store.LoadJournal().OrderBy(r => r.Seq))
        {
            _entries.Add(record);
            _lastSeq = record.Seq;
            if (record.IsMarker && JournalCodec.TryReadMarker(record, out var marker))
            {
                _markers.Add(marker);
            }
        }
    }

    public void Clear()
    {
        _entries.Clear();
        _markers.Clear();
        _lastSeq = 0;
    }

    private int FirstIndexAfter(ulong seq) => FirstIndexAtOrAfter(seq + 1);

    private int FirstIndexAtOrAfter(ulong seq)
    {
        int lo = 0;
        int hi = _entries.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (_entries[mid].Seq < seq)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}

/// <summary>Body encoding of journal rows and their conversion to the wire <c>JournalEntry</c> (for <c>WorldCatchUp</c>).</summary>
public static class JournalCodec
{
    public static byte[] EncodeSpawn(EntityRecord record) =>
        MessageEncoder.EncodePayload(b => EntityRecord.Pack(b, record.UnPack()), 192);

    public static byte[] EncodeDespawn(uint netId, uint killerNetId, DespawnReason reason) =>
        MessageEncoder.EncodePayload(
            b => JournalDespawn.Pack(b, new JournalDespawnT { Entry = new DespawnEntryT { NetId = netId, KillerNetId = killerNetId, Reason = reason } }),
            64);

    public static byte[] EncodeChange(EntityChange change) =>
        MessageEncoder.EncodePayload(b => EntityChange.Pack(b, change.UnPack()), 128);

    public static byte[] EncodeCargo(EntityCargo cargo) =>
        MessageEncoder.EncodePayload(b => EntityCargo.Pack(b, cargo.UnPack()), 128);

    public static bool TryReadMarker(JournalRecord record, out JournalMarker marker)
    {
        marker = null!;
        if (!record.IsMarker)
        {
            return false;
        }

        var started = SaveStarted.GetRootAsSaveStarted(new ByteBuffer(record.Payload));
        marker = new JournalMarker(record.Seq, CheckpointId.From(started.CheckpointId), started.RequestId, started.GameTime, started.NextNetId);
        return true;
    }

    /// <summary>The wire form of a row for <c>WorldCatchUp.entries</c>; null for marker rows.</summary>
    public static JournalEntryT? ToWire(JournalRecord record)
    {
        var bb = new ByteBuffer(record.Payload);
        var body = record.Kind switch
        {
            MsgType.EntitySpawn => WorldMutationUnion.FromEntityRecord(EntityRecord.GetRootAsEntityRecord(bb).UnPack()),
            MsgType.EntityDespawn => WorldMutationUnion.FromJournalDespawn(JournalDespawn.GetRootAsJournalDespawn(bb).UnPack()),
            MsgType.EntityChange => WorldMutationUnion.FromEntityChange(EntityChange.GetRootAsEntityChange(bb).UnPack()),
            MsgType.EntityCargo => WorldMutationUnion.FromEntityCargo(EntityCargo.GetRootAsEntityCargo(bb).UnPack()),
            _ => null,
        };
        return body is null ? null : new JournalEntryT { Seq = record.Seq, Body = body };
    }
}
