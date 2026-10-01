using Google.FlatBuffers;
using X4MP.Core.Events;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Saves;

public sealed partial class SaveService
{
    // ------------------------------------------------------------------ upload (authority to server)

    private void OnUploadBegin(SessionNode node, SaveUploadBegin begin)
    {
        if (!node.IsAuthority || node.Connection is not { } connection)
        {
            return; // the policy table lets only the authority send it
        }

        var options = Opt;
        var checkpointId = CheckpointId.From(begin.CheckpointId);
        var kind = begin.Kind;
        if (begin.Sha256Length != 32)
        {
            throw new ProtocolViolation(ViolationCode.MalformedPayload, "SaveUploadBegin: sha256 must be 32 bytes");
        }

        string shaHex = SaveFileStore.Hex(begin.GetSha256Array());
        long size = checked((long)Math.Min(begin.Size, long.MaxValue));
        string name = begin.Name ?? string.Empty;

        if (size <= 0 || size > options.MaxSaveBytes)
        {
            Reject(node, 0, checkpointId, kind, SaveStoreResult.TooLarge, $"{size} bytes (limit {options.MaxSaveBytes})");
            FailCheckpoint(checkpointId, kind, SaveStoreResult.TooLarge, "too large");
            return;
        }

        // A new begin for the same part of the same checkpoint (a retry after a reconnect) replaces the old upload.
        foreach (var old in _uploads.Values.Where(u => u.Checkpoint == checkpointId && u.Kind == kind).ToArray())
        {
            old.Abort();
            _uploads.Remove(old.Id);
            _transfers.TryRemove(old.Id, out _);
        }

        bool already = Files.SizeOf(shaHex, kind) == size;
        string part = Files.PartPathOf(shaHex, kind);
        long resume = 0;
        if (!already && File.Exists(part))
        {
            long have = new FileInfo(part).Length;
            if (have <= size)
            {
                resume = have;
            }
            else
            {
                SaveFileStore.TryDelete(part);
            }
        }

        var cp = GetCheckpoint(checkpointId);
        cp.Name = kind == UploadKind.Save && name.Length > 0 ? name : cp.Name;
        uint id = ++_nextTransferId;
        var upload = new UploadSession(
            id, node.PlayerId, node.Name, connection, checkpointId, kind, shaHex, size, name, begin.GhostsCleaned,
            part, resume, already, options.ChunkBytes, _time.GetUtcNow());
        _uploads[id] = upload;
        _transfers[id] = upload;
        _ = RunUploadAsync(upload);
        PublishStatus();

        var accept = ControlFrames.Encode(
            MsgType.SaveUploadAccept,
            fbb => SaveUploadAccept.CreateSaveUploadAccept(fbb, id, (uint)options.ChunkBytes, (ulong)(already ? size : resume), (byte)options.WindowChunks).Value,
            64);
        Send(node, accept);
    }

    private void OnUploadChunk(SessionNode node, InboundFrame frame)
    {
        uint id;
        try
        {
            id = SaveChunk.GetRootAsSaveChunk(new ByteBuffer(frame.Frame.Payload)).TransferId;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            throw new ProtocolViolation(ViolationCode.MalformedPayload, "SaveChunk: unreadable header");
        }

        if (!_uploads.TryGetValue(id, out var upload) || upload.PlayerId != node.PlayerId)
        {
            LogRefused(node.PlayerId, $"chunk for unknown upload {id}");
            node.Connection?.Stats.AddViolation();
            return;
        }

        upload.PostChunk(frame.Frame.Payload);
    }

    private void OnUploadEnd(SessionNode node, SaveUploadEnd end)
    {
        if (!_uploads.TryGetValue(end.UploadId, out var upload) || upload.PlayerId != node.PlayerId)
        {
            LogRefused(node.PlayerId, $"end of unknown upload {end.UploadId}");
            node.Connection?.Stats.AddViolation();
            return;
        }

        upload.PostEnd();
    }

    private async Task RunUploadAsync(UploadSession upload)
    {
        var outcome = await Task.Run(upload.RunAsync).ConfigureAwait(false);
        upload.Dispose();
        try
        {
            await _driver!.CallAsync(() =>
            {
                OnUploadFinished(upload, outcome);
                return true;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the actor stopped
        }
    }

    /// <summary>Actor thread: an upload ended (stored, rejected or broken).</summary>
    private void OnUploadFinished(UploadSession upload, UploadOutcome outcome)
    {
        bool tracked = _uploads.TryGetValue(upload.Id, out var current) && ReferenceEquals(current, upload);
        _transfers.TryRemove(upload.Id, out _);
        if (tracked)
        {
            _uploads.Remove(upload.Id);
        }

        PublishStatus();
        if (outcome.Aborted || !tracked)
        {
            return; // the authority is gone or a newer upload replaced this one: the part file waits for a resume
        }

        bool stored = outcome.Result == SaveStoreResult.Stored;
        var result = outcome.Result;
        if (stored)
        {
            try
            {
                if (!upload.AlreadyStored)
                {
                    Files.Promote(upload.PartPath, upload.ShaHex, upload.Kind);
                }
            }
            catch (IOException ex)
            {
                stored = false;
                result = SaveStoreResult.Aborted;
                outcome = outcome with { Result = result, Detail = "could not store the file: " + ex.Message };
            }
        }

        var cp = GetCheckpoint(upload.Checkpoint);
        if (stored)
        {
            bool cleaned = upload.GhostsCleaned;
            if (upload.Kind == UploadKind.Save)
            {
                cp.SaveSha = upload.ShaHex;
                cp.SaveSize = upload.Size;
                cp.SaveCleaned = cleaned;
                cp.Meta = outcome.Meta;
                if (upload.Name.Length > 0)
                {
                    cp.Name = upload.Name;
                }

                Catalog.AddSave(new SaveRecord(
                    upload.ShaHex, upload.Size, cp.Name.Length > 0 ? cp.Name : "x4mp " + upload.ShaHex[..12], "authority",
                    upload.PlayerName, _time.GetUtcNow(), outcome.Meta, cleaned));
                Publish(new X4MP.Core.Events.SaveStored(_time.GetUtcNow(), _sessionId > 0 ? _sessionId : null, upload.ShaHex, upload.Size, "authority"));
            }
            else
            {
                cp.ManifestSha = upload.ShaHex;
                cp.ManifestSize = upload.Size;
                cp.ManifestCleaned = cleaned;
            }

            if (!cleaned)
            {
                result = SaveStoreResult.StoredNotCurrent;
                outcome = outcome with { Result = result, Detail = "ghosts_cleaned is false: stored, but not made current" };
            }
        }
        else
        {
            LogUploadFailed(upload.Checkpoint, upload.Kind, result, outcome.Detail);
            cp.Failed = true;
        }

        SendStored(upload, result, outcome.Detail);
        EvaluateCheckpoint(cp);
    }

    private void SendStored(UploadSession upload, SaveStoreResult result, string detail)
    {
        if (_authority is null || _authority.PlayerId != upload.PlayerId)
        {
            return;
        }

        var frame = ControlFrames.Encode(
            MsgType.SaveStored,
            fbb => X4MP.Proto.SaveStored.Pack(fbb, new SaveStoredT
            {
                UploadId = upload.Id,
                CheckpointId = upload.Checkpoint.ToWire(),
                Kind = upload.Kind,
                Result = result,
                Detail = detail,
            }).Value,
            128);
        Send(_authority, frame);
    }

    /// <summary>Answers a begin that cannot start an upload (no upload id was issued).</summary>
    private static void Reject(SessionNode node, uint uploadId, CheckpointId checkpoint, UploadKind kind, SaveStoreResult result, string detail)
    {
        var frame = ControlFrames.Encode(
            MsgType.SaveStored,
            fbb => X4MP.Proto.SaveStored.Pack(fbb, new SaveStoredT { UploadId = uploadId, CheckpointId = checkpoint.ToWire(), Kind = kind, Result = result, Detail = detail }).Value,
            128);
        Send(node, frame);
    }

    // ------------------------------------------------------------------ download (server to node)

    private void OnDownloadRequest(SessionNode node, SaveDownloadRequest request)
    {
        if (node.Connection is not { } connection)
        {
            return;
        }

        if (request.Sha256Length != 32)
        {
            throw new ProtocolViolation(ViolationCode.MalformedPayload, "SaveDownloadRequest: sha256 must be 32 bytes");
        }

        string shaHex = SaveFileStore.Hex(request.GetSha256Array());
        var kind = request.Kind;
        long? size = Files.SizeOf(shaHex, kind);
        if (size is null || (long)request.Offset > size)
        {
            LogRefused(node.PlayerId, $"download of unknown {kind} {SaveFileStore.Abbrev(shaHex)} at {request.Offset}");
            connection.Stats.AddViolation();
            SendNotice(node, $"The server has no {kind} with that hash; reload your session info.");
            return;
        }

        CancelDownload(node.PlayerId);
        var options = Opt;
        uint id = ++_nextTransferId;
        var download = new DownloadSession(
            id, node.PlayerId, node.Name, connection, Files.PathOf(shaHex, kind), shaHex, kind, size.Value, (long)request.Offset,
            options.ChunkBytes, options.WindowChunks, _time.GetUtcNow(), _lifetime.Token);
        _downloads[node.PlayerId] = download;
        _transfers[id] = download;
        LogDownload(node.PlayerId, shaHex, kind, (long)request.Offset, size.Value, id);
        PublishStatus();

        var accept = ControlFrames.Encode(
            MsgType.SaveDownloadAccept,
            fbb => SaveDownloadAccept.CreateSaveDownloadAccept(fbb, id, (ulong)size.Value, (uint)options.ChunkBytes, (byte)options.WindowChunks).Value,
            64);
        Send(node, accept);
        _ = RunDownloadAsync(node.PlayerId, download);
    }

    private async Task RunDownloadAsync(int playerId, DownloadSession download)
    {
        bool ok = await Task.Run(() => download.RunAsync(Limiter, () => TimeSpan.FromSeconds(Opt.TransferStallSeconds))).ConfigureAwait(false);
        download.Dispose();
        try
        {
            await _driver!.CallAsync(() =>
            {
                if (_downloads.TryGetValue(playerId, out var current) && ReferenceEquals(current, download))
                {
                    _downloads.Remove(playerId);
                }

                _transfers.TryRemove(download.Id, out _);
                PublishStatus();
                LogDownloadEnded(playerId, download.Id, ok ? "complete" : "stopped", download.BytesTransferred);
                return true;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the actor stopped
        }
    }

    private void OnChunkAck(SessionNode node, SaveChunkAck ack)
    {
        if (_downloads.TryGetValue(node.PlayerId, out var download) && download.Id == ack.TransferId)
        {
            download.OnAck((long)ack.NextOffset);
        }
    }

    private void CancelDownload(int playerId)
    {
        if (_downloads.Remove(playerId, out var download))
        {
            download.Cancel();
        }
    }

    private static void SendNotice(SessionNode node, string text)
    {
        var frame = ControlFrames.Encode(
            MsgType.ServerNotice,
            fbb => ServerNotice.Pack(fbb, new ServerNoticeT { Severity = NoticeSeverity.Warning, Text = text, DisplayMs = 8000 }).Value,
            128);
        Send(node, frame);
    }
}
