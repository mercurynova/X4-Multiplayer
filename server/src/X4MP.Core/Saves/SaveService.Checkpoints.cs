using X4MP.Core.Events;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Core.World;
using X4MP.Proto;

namespace X4MP.Core.Saves;

public sealed partial class SaveService
{
    private Checkpoint GetCheckpoint(CheckpointId id)
    {
        if (!_checkpoints.TryGetValue(id, out var cp))
        {
            cp = new Checkpoint(id) { At = _time.GetUtcNow() };
            _checkpoints[id] = cp;
        }

        return cp;
    }

    // ------------------------------------------------------------------ requesting checkpoints

    /// <summary>Sends <c>RequestSave</c> to the authority. False when there is no authority, a request is already in flight, or it is not in game yet.</summary>
    private bool SendRequestSave(SaveReason reason)
    {
        if (_authority is not { Connection: not null } authority || _inFlight is not null)
        {
            return false;
        }

        if (reason != SaveReason.Shutdown && authority.Phase != NodePhase.InGame)
        {
            return false;
        }

        uint requestId = _nextRequestId++;
        string slot = $"x4mp_{Math.Max(0, _sessionId)}_{++_slotCounter}";
        _inFlight = new Pending(requestId, reason, _time.GetTimestamp());
        var frame = ControlFrames.Encode(
            MsgType.RequestSave,
            fbb => RequestSave.CreateRequestSave(fbb, requestId, reason, fbb.CreateString(slot)).Value,
            64);
        Send(authority, frame);
        LogRequested(requestId, reason, slot);
        return true;
    }

    /// <summary>The authority is in game and the session has no checkpoint yet (it started on a save the server has never seen).</summary>
    private void TryRequestInitialSave()
    {
        if (_current is null && _inFlight is null && _authority is { Phase: NodePhase.InGame } && (_lastRequestFailedAt == long.MinValue || _time.GetElapsedTime(_lastRequestFailedAt, _time.GetTimestamp()) > TimeSpan.FromSeconds(10)))
        {
            SendRequestSave(SaveReason.SessionStart);
        }
    }

    /// <summary>
    /// <c>SaveStarted</c> reached the world mirror, which journaled the marker (so everything the authority sent before it is inside
    /// the save and the journal position is known). Links it to the request in flight.
    /// </summary>
    private void OnMarker(JournalMarker marker)
    {
        var cp = GetCheckpoint(marker.Checkpoint);
        cp.Marker = marker;
        cp.RequestId = marker.RequestId;
        cp.GameTime = marker.GameTime;
        cp.NextNetId = marker.NextNetId;
        cp.At = _time.GetUtcNow();
        if (_inFlight is { } pending && marker.RequestId == pending.RequestId)
        {
            cp.Reason = pending.Reason;
        }
        else if (marker.RequestId == 0)
        {
            cp.Reason = SaveReason.Admin; // a manual save by the authority player
        }
    }

    private void FailCheckpoint(CheckpointId id, UploadKind kind, SaveStoreResult result, string why)
    {
        var cp = GetCheckpoint(id);
        cp.Failed = true;
        LogUploadFailed(id, kind, result, why);
        EvaluateCheckpoint(cp);
    }

    // ------------------------------------------------------------------ making a checkpoint current

    /// <summary>Called after every upload result: when both parts are stored (or one failed) the checkpoint is finished.</summary>
    private void EvaluateCheckpoint(Checkpoint cp)
    {
        if (cp.Finished)
        {
            return;
        }

        bool bothStored = cp.SaveSha is not null && cp.ManifestSha is not null;
        if (!bothStored && !cp.Failed)
        {
            return;
        }

        cp.Finished = true;
        if (bothStored && !cp.Failed)
        {
            if (cp.SaveCleaned && cp.ManifestCleaned)
            {
                MakeCurrent(cp);
            }
            else
            {
                Publish(new AlertRaised(
                    _time.GetUtcNow(), _sessionId > 0 ? _sessionId : null, AlertSeverity.Warning, "save.ghosts-not-cleaned",
                    $"checkpoint {SaveFileStore.Abbrev(cp.SaveSha!)} was uploaded with ghosts_cleaned=false: stored, not made current"));
            }
        }

        FinishRequest(cp);
    }

    private void MakeCurrent(Checkpoint cp)
    {
        var previous = _current;
        _previous = previous;
        _current = cp;
        cp.IsCurrent = true;
        int compacted = _world.Journal.CompactBeforePreviousCheckpoint(cp.Id);
        _world.Galaxy.TryActivate(cp.SaveSha!);
        if (_sessionId > 0)
        {
            Catalog.AddCheckpoint(_sessionId, new CheckpointRecord(
                cp.Id, cp.SaveSha!, cp.ManifestSha!, cp.ManifestSize, cp.Marker?.Seq ?? 0, cp.GameTime, cp.NextNetId, true, cp.At));
            Catalog.SetSessionSave(_sessionId, cp.SaveSha!, initial: previous is null);
        }

        PublishStatus();
        LogCurrent(cp.Id, cp.SaveSha!, cp.SaveSize, compacted);
        _driver?.SetCurrentSave(Convert.FromHexString(cp.SaveSha!), cp.GameTime);
        SetNextAutosave();
        BroadcastSaveInfo();
        ReleaseFreshWaiters();
        if (!_seeded)
        {
            _seeded = true;
            SeedFromSave(cp);
        }

        if (_phase == SessionPhase.AuthorityLoading)
        {
            _driver?.RaiseSessionTrigger(SessionTrigger.CheckpointStored, "first checkpoint stored");
        }
    }

    private void SeedFromSave(Checkpoint cp)
    {
        var info = new InitialSaveInfo(_sessionId, _authority?.PlayerId ?? 0, cp.Id, cp.SaveSha!, cp.Meta, cp.GameTime, cp.NextNetId);
        foreach (var hook in _seedHooks)
        {
            try
            {
                hook.OnInitialSaveStored(info);
            }
            catch (Exception ex)
            {
                LogPublishFailed(ex);
            }
        }
    }

    private void SetNextAutosave()
    {
        double minutes = Opt.AutosaveMinutes;
        _nextAutosave = minutes <= 0
            ? long.MaxValue
            : _time.GetTimestamp() + (long)(minutes * 60.0 * _time.TimestampFrequency);
    }

    /// <summary>The request that produced <paramref name="cp"/> is over, successfully or not.</summary>
    private void FinishRequest(Checkpoint cp)
    {
        if (_inFlight is { } pending && (cp.RequestId == pending.RequestId || cp.RequestId == 0))
        {
            _inFlight = null;
        }
        else if (_inFlight is not null && cp.Marker is null)
        {
            _inFlight = null;
        }

        if (!cp.IsCurrent)
        {
            _lastRequestFailedAt = _time.GetTimestamp();
            SetNextAutosave();
            ReleaseFreshWaiters(); // the fresh save they waited for did not become current: give them the one we have
        }

        if (_stopPending)
        {
            CompleteStop("final save finished");
        }
    }

    // ------------------------------------------------------------------ stopping

    private void BeginFinalSave()
    {
        if (!AuthorityLive)
        {
            _stopPending = true;
            CompleteStop("no authority to save");
            return;
        }

        _stopPending = true;
        if (_inFlight is null)
        {
            SendRequestSave(SaveReason.Shutdown);
        }

        // else: a request is already running; its result counts as the final save
    }

    private void CompleteStop(string reason)
    {
        if (!_stopPending)
        {
            return;
        }

        _stopPending = false;
        var driver = _driver;
        if (driver is null)
        {
            return;
        }

        // Queued, not nested: this may be running inside the actor's own phase change.
        _ = driver.CallAsync(() =>
        {
            if (_phase == SessionPhase.Stopping)
            {
                driver.RaiseSessionTrigger(SessionTrigger.StopCompleted, reason);
            }

            return true;
        });
    }

    private void AuthorityGone()
    {
        foreach (var upload in _uploads.Values.ToArray())
        {
            upload.Abort();
        }

        _uploads.Clear();
        _inFlight = null;
        PublishStatus();
        if (_stopPending)
        {
            CompleteStop("the authority went away");
        }
    }

    // ------------------------------------------------------------------ distribution (SessionSaveInfo)

    private void BroadcastSaveInfo()
    {
        foreach (var node in NodesNeedingInfo())
        {
            SendSaveInfo(node);
        }
    }

    private IEnumerable<SessionNode> NodesNeedingInfo()
    {
        // Every attached non-authority node that is still syncing; in-game nodes keep what they run on.
        foreach (var node in _knownNodes.Values.ToArray())
        {
            if (!node.IsAuthority && node.Connection is not null && node.Announced && node.Phase == NodePhase.SyncingSave)
            {
                yield return node;
            }
        }
    }

    private void SendSaveInfo(SessionNode node)
    {
        if (_current is not { SaveSha: not null, ManifestSha: not null } cp || node.Connection is null)
        {
            return;
        }

        var options = Opt;
        _knownNodes[node.PlayerId] = node;
        if (options.JoinCheckpointPolicy == JoinCheckpointPolicy.FreshSave && !_freshBypass.Contains(node.PlayerId) && NeedsFreshSave(cp))
        {
            if (AuthorityLive)
            {
                if (_waitingForFresh.Add(node.PlayerId) && _waitingForFresh.Count == 1)
                {
                    _freshWaitSince = _time.GetTimestamp();
                }

                SendRequestSave(SaveReason.JoinRequested); // false when one is already running: that one will do
                return;
            }
        }

        _waitingForFresh.Remove(node.PlayerId);
        bool http = options.HttpFallback && node.Attached is { } attached && (attached.NegotiatedCaps & (ulong)Capability.SaveHttp) != 0;
        string token = string.Empty;
        string httpUrl = string.Empty;
        string manifestUrl = string.Empty;
        if (http)
        {
            token = Tokens.Issue(node.PlayerId, TimeSpan.FromMinutes(options.DownloadTokenMinutes));
            string baseUrl = options.PublicHttpBaseUrl.TrimEnd('/');
            httpUrl = $"{baseUrl}/files/saves/{cp.SaveSha}";
            manifestUrl = $"{baseUrl}/files/saves/{cp.ManifestSha}";
        }

        var info = new SessionSaveInfoT
        {
            CheckpointId = cp.Id.ToWire(),
            Sha256 = [.. Convert.FromHexString(cp.SaveSha!)],
            Size = (ulong)cp.SaveSize,
            DisplayName = cp.Name,
            LocalFileName = SaveFileStore.LocalFileName(cp.SaveSha!),
            ManifestSha256 = [.. Convert.FromHexString(cp.ManifestSha!)],
            ManifestSize = (ulong)cp.ManifestSize,
            HttpUrl = httpUrl,
            ManifestHttpUrl = manifestUrl,
            DownloadToken = token,
            GameTime = cp.GameTime,
        };
        Send(node, ControlFrames.Encode(MsgType.SessionSaveInfo, fbb => SessionSaveInfo.Pack(fbb, info).Value, 512));
    }

    /// <summary><c>join_checkpoint_policy = FreshSave</c>: the journal is long or the checkpoint old (protocol.md 6.5).</summary>
    private bool NeedsFreshSave(Checkpoint cp)
    {
        var options = Opt;
        ulong since = cp.Marker is { } marker ? _world.Journal.LastSeq - marker.Seq : 0;
        bool journalLong = since > (ulong)options.FreshSaveJournalEntries;
        bool old = _time.GetUtcNow() - cp.At >= TimeSpan.FromMinutes(options.FreshSaveMaxAgeMinutes);
        return journalLong || old;
    }

    private void ReleaseFreshWaiters()
    {
        if (_waitingForFresh.Count == 0)
        {
            return;
        }

        var waiting = _waitingForFresh.ToArray();
        _waitingForFresh.Clear();
        foreach (var playerId in waiting)
        {
            if (_knownNodes.TryGetValue(playerId, out var node) && node.Phase == NodePhase.SyncingSave)
            {
                SendSaveInfoNoWait(node);
            }
        }
    }

    /// <summary>Sends the current checkpoint without applying the fresh-save wait again (the wait is over).</summary>
    private void SendSaveInfoNoWait(SessionNode node)
    {
        _freshBypass.Add(node.PlayerId);
        try
        {
            SendSaveInfo(node);
        }
        finally
        {
            _freshBypass.Remove(node.PlayerId);
        }
    }
}
