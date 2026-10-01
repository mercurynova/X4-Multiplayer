using X4MP.Core.Events;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Saves;

public sealed partial class SaveService
{
    // ------------------------------------------------------------------ SaveReady and ManifestReport

    private void OnSaveReady(SessionNode node, SaveReady ready)
    {
        if (ready.Sha256Length != 32)
        {
            throw new ProtocolViolation(ViolationCode.MalformedPayload, "SaveReady: sha256 must be 32 bytes");
        }

        string shaHex = SaveFileStore.Hex(ready.GetSha256Array());
        var cp = _checkpoints.Values.FirstOrDefault(c => c.SaveSha == shaHex && c.Marker is not null);
        if (cp is null)
        {
            LogRefused(node.PlayerId, $"SaveReady for a save the server does not know as a checkpoint: {SaveFileStore.Abbrev(shaHex)}");
            node.Connection?.Stats.AddViolation();
            return;
        }

        CancelDownload(node.PlayerId);
        _loaded[node.PlayerId] = cp.Id;
        Fire(node.PlayerId, NodeTrigger.ReportLoading); // the server moves the node to Loading (protocol.md 6.4)
    }

    /// <summary>
    /// The matching report (protocol.md 8.4): up to <see cref="SaveOptions.ManifestMismatchPercent"/> of unmatched entities is a warning,
    /// above it the node is disconnected with <c>ManifestMismatch</c>. A good report starts the catch-up.
    /// </summary>
    private void OnManifestReport(SessionNode node, ManifestReport report)
    {
        long total = report.Total;
        long unmatched = (long)report.Unmatched + report.Ambiguous;
        double percent = total <= 0 ? 0 : 100.0 * unmatched / total;
        double limit = Opt.ManifestMismatchPercent;
        var checkpoint = CheckpointId.From(report.CheckpointId);
        if (percent > limit)
        {
            LogManifest(node.PlayerId, unmatched, total, percent, limit);
            Publish(new AlertRaised(
                _time.GetUtcNow(), _sessionId > 0 ? _sessionId : null, AlertSeverity.Warning, "save.manifest-mismatch",
                $"{node.Name}: {unmatched} of {total} entities ({percent:F2}%) did not match the checkpoint; make a fresh checkpoint"));
            _ = _driver?.RemoveNodeAsync(
                node.PlayerId, DisconnectCode.ManifestMismatch,
                $"{percent:F2}% of the checkpoint's entities did not match (limit {limit}%)");
            return;
        }

        if (unmatched > 0)
        {
            Publish(new AlertRaised(
                _time.GetUtcNow(), _sessionId > 0 ? _sessionId : null, AlertSeverity.Warning, "save.manifest-unmatched",
                $"{node.Name}: {unmatched} of {total} entities ({percent:F2}%) did not match the checkpoint"));
        }

        if (!_loaded.TryGetValue(node.PlayerId, out var loaded))
        {
            loaded = checkpoint;
        }

        StartCatchUp(node, loaded);
    }

    // ------------------------------------------------------------------ WorldCatchUp

    /// <summary>
    /// Sends a node that matched the checkpoint everything that happened since it: the full string table, then the journal after the
    /// checkpoint's marker as <c>WorldCatchUp</c> chunks ending with <c>final</c>. The node then reports <c>NodeReady</c>.
    /// </summary>
    private void StartCatchUp(SessionNode node, CheckpointId checkpoint)
    {
        var records = _world.Journal.ReadAfterMarker(checkpoint);
        if (records is null)
        {
            // Compacted away: the checkpoint is too old to replay from. A fresh join takes the newer save.
            _ = _driver?.RemoveNodeAsync(node.PlayerId, DisconnectCode.ResumeExpired, "the checkpoint you loaded was replaced; join again");
            return;
        }

        if (node.Phase == NodePhase.Matching)
        {
            Fire(node.PlayerId, NodeTrigger.ReportCatchingUp);
        }

        SendCatchUp(node, checkpoint, records);
    }

    /// <summary>A node that reconnected inside the grace and is in game: replay what it missed (protocol.md 6.6).</summary>
    private void CatchUpAfterResume(SessionNode node)
    {
        ulong last = node.LastJournalSeq;
        var journal = _world.Journal;
        if (journal.FirstSeq != 0 && last + 1 < journal.FirstSeq)
        {
            _ = _driver?.RemoveNodeAsync(node.PlayerId, DisconnectCode.ResumeExpired, "the journal since your last update was compacted; join again");
            return;
        }

        var checkpoint = _current?.Id ?? default;
        SendCatchUp(node, checkpoint, [.. journal.ReadAfter(last)]);
    }

    private void SendCatchUp(SessionNode node, CheckpointId checkpoint, IReadOnlyList<JournalRecord> records)
    {
        if (node.Connection is not { } connection)
        {
            return;
        }

        var strings = _world.Strings.EncodeChunks();
        _ = Task.Run(() => CatchUpSenderAsync(connection, checkpoint, strings, records));
    }

    private static async Task CatchUpSenderAsync(INodeConnection connection, CheckpointId checkpoint, List<byte[]> strings, IReadOnlyList<JournalRecord> records)
    {
        var ct = connection.Closed;
        try
        {
            foreach (var payload in strings)
            {
                await SendPacedAsync(connection, MsgType.StringTableAdd, payload, ct).ConfigureAwait(false);
            }

            const int batchBytes = 96 * 1024;
            const int batchEntries = 1000;
            var batch = new List<JournalEntryT>();
            int bytes = 0;
            for (int i = 0; i < records.Count; i++)
            {
                if (JournalCodec.ToWire(records[i]) is { } entry)
                {
                    batch.Add(entry);
                    bytes += records[i].Payload.Length + 16;
                }

                if (bytes >= batchBytes || batch.Count >= batchEntries)
                {
                    await SendCatchUpFrameAsync(connection, checkpoint, batch, final: false, ct).ConfigureAwait(false);
                    batch = [];
                    bytes = 0;
                }
            }

            await SendCatchUpFrameAsync(connection, checkpoint, batch, final: true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the node went away; a resume asks again
        }
    }

    private static Task SendCatchUpFrameAsync(INodeConnection connection, CheckpointId checkpoint, List<JournalEntryT> entries, bool final, CancellationToken ct)
    {
        var payload = MessageEncoder.EncodePayload(
            b => WorldCatchUp.Pack(b, new WorldCatchUpT { CheckpointId = checkpoint.ToWire(), Entries = entries, Final = final }),
            Math.Max(256, entries.Count * 64));
        return SendPacedAsync(connection, MsgType.WorldCatchUp, payload, ct);
    }

    /// <summary>Queues a Control frame, pausing while the node's Control lane is over its soft cap (catch-up must not overflow it).</summary>
    private static async Task SendPacedAsync(INodeConnection connection, MsgType type, byte[] payload, CancellationToken ct)
    {
        while (connection.ControlOverSoftCap)
        {
            await Task.Delay(20, ct).ConfigureAwait(false);
        }

        var frame = OutboundFrame.Create(type, payload);
        try
        {
            var result = connection.TrySend(frame);
            if (result is SendResult.Closed or SendResult.ClosedOverflow)
            {
                throw new OperationCanceledException(ct);
            }
        }
        finally
        {
            frame.Release();
        }
    }
}
