using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using X4MP.Core.Events;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Saves;

/// <summary>What the save service says about the session's checkpoints (read from any thread).</summary>
public sealed record SaveStatus(
    string? CurrentSha256,
    string? CurrentManifestSha256,
    long CurrentSizeBytes,
    double CurrentGameTime,
    DateTimeOffset? CurrentAt,
    string? CurrentName,
    int PendingUploads,
    int ActiveDownloads);

/// <summary>
/// The save service (M1-12, server-design 3, protocol.md 6.3 to 6.5), a session module. It asks the authority for checkpoints
/// (<c>RequestSave</c>: at session start, every <see cref="SaveOptions.AutosaveMinutes"/>, on join under the <c>FreshSave</c> policy, on
/// stop and on admin request), accepts the in-band upload of the save and its manifest, stores them content-addressed, makes a checkpoint
/// the session's current save once both are stored with <c>ghosts_cleaned</c> (and compacts the journal), distributes it to the nodes
/// (in-band windowed download with resume, or the HTTP fallback), applies the manifest-report policy and replays the string table and the
/// journal since the checkpoint to a node that finished matching (<c>WorldCatchUp</c>).
/// <para>
/// The module logic runs on the actor thread. The disk and the sockets are not: uploads are written by a worker per upload, downloads are
/// read and queued by a sender task per download, the actor only starts, routes acks to and stops them.
/// </para>
/// </summary>
public sealed partial class SaveService : ISessionModule, ISessionActorBound, IDisposable
{
    private sealed class Checkpoint(CheckpointId id)
    {
        public CheckpointId Id { get; } = id;

        public JournalMarker? Marker { get; set; }

        public uint RequestId { get; set; }

        public SaveReason Reason { get; set; } = SaveReason.Admin;

        public DateTimeOffset At { get; set; }

        public double GameTime { get; set; }

        public uint NextNetId { get; set; }

        public string? SaveSha { get; set; }

        public long SaveSize { get; set; }

        public string Name { get; set; } = string.Empty;

        public SaveMeta Meta { get; set; } = SaveMeta.Empty;

        public bool SaveCleaned { get; set; }

        public string? ManifestSha { get; set; }

        public long ManifestSize { get; set; }

        public bool ManifestCleaned { get; set; }

        public bool Failed { get; set; }

        public bool Finished { get; set; }

        public bool IsCurrent { get; set; }
    }

    private sealed record Pending(uint RequestId, SaveReason Reason, long SentAt);

    private readonly Func<SaveOptions> _options;
    private readonly WorldMirror _world;
    private readonly TimeProvider _time;
    private readonly IEventPublisher? _events;
    private readonly ISaveSeedHook[] _seedHooks;
    private readonly ILogger _logger;
    private readonly Dictionary<CheckpointId, Checkpoint> _checkpoints = [];
    private readonly Dictionary<uint, UploadSession> _uploads = [];
    private readonly Dictionary<int, DownloadSession> _downloads = [];
    private readonly ConcurrentDictionary<uint, object> _transfers = new();
    private readonly Dictionary<int, CheckpointId> _loaded = [];
    private readonly HashSet<int> _waitingForFresh = [];
    private readonly CancellationTokenSource _lifetime = new();
    private ISessionNodeDriver? _driver;
    private SessionPhase _phase = SessionPhase.Idle;
    private SessionNode? _authority;
    private Checkpoint? _current;
    private Checkpoint? _previous;
    private Pending? _inFlight;
    private uint _nextTransferId;
    private uint _nextRequestId = 1;
    private int _slotCounter;
    private long _sessionId;
    private long _nextAutosave = long.MaxValue;
    private long _freshWaitSince;
    private bool _seeded;
    private bool _stopPending;
    private int _disposed;
    private long _lastRequestFailedAt = long.MinValue;
    private readonly Dictionary<int, SessionNode> _knownNodes = [];
    private readonly HashSet<int> _freshBypass = [];
    private volatile SaveStatus _status = new(null, null, 0, 0, null, null, 0, 0);
    private volatile IReadOnlySet<string> _protected = new HashSet<string>();

    /// <param name="options">Supplies the options on every use (Live settings).</param>
    /// <param name="files">The content-addressed store.</param>
    /// <param name="catalog">The saves/checkpoints rows.</param>
    /// <param name="world">The world mirror: the journal, the string table and the galaxy cache.</param>
    public SaveService(
        Func<SaveOptions> options,
        SaveFileStore files,
        ISaveCatalog catalog,
        WorldMirror world,
        TimeProvider? time = null,
        IEventPublisher? events = null,
        IEnumerable<ISaveSeedHook>? seedHooks = null,
        ILogger<SaveService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(world);
        _options = options;
        Files = files;
        Catalog = catalog;
        _world = world;
        _time = time ?? TimeProvider.System;
        _events = events;
        _seedHooks = seedHooks?.ToArray() ?? [];
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        Tokens = new DownloadTokens(_time);
        Limiter = new BandwidthLimiter(() => _options().SaveBandwidthCapMBps, _time);
        _world.MarkerRecorded += OnMarker;
    }

    private SaveOptions Opt => _options();

    public SaveFileStore Files { get; }

    public ISaveCatalog Catalog { get; }

    /// <summary>The HTTP fallback's download tokens.</summary>
    public DownloadTokens Tokens { get; }

    /// <summary>The shared outbound bandwidth cap.</summary>
    public BandwidthLimiter Limiter { get; }

    /// <summary>The latest state (refreshed on every change; safe to read from any thread).</summary>
    public SaveStatus Status => _status;

    /// <summary>Uploads and downloads in progress (any thread).</summary>
    public IReadOnlyList<TransferSnapshot> Transfers
    {
        get
        {
            var list = new List<TransferSnapshot>();
            foreach (var t in _transfers.Values)
            {
                switch (t)
                {
                    case UploadSession u:
                        list.Add(new TransferSnapshot(u.Id, true, u.PlayerId, u.PlayerName, u.ShaHex, u.Kind, u.Size, u.Received, u.ResumeOffset, u.StartedAt));
                        break;
                    case DownloadSession d:
                        list.Add(new TransferSnapshot(d.Id, false, d.PlayerId, d.PlayerName, d.ShaHex, d.Kind, d.Size, d.Sent, d.StartOffset, d.StartedAt));
                        break;
                }
            }

            return [.. list.OrderBy(t => t.Id)];
        }
    }

    /// <summary>The SHA-256s the janitor must keep: the current and the previous checkpoint of the running session.</summary>
    public IReadOnlySet<string> ProtectedSha256() => _protected;

    /// <summary>True while a part file belongs to a running upload.</summary>
    public bool IsPartInUse(string path) => _transfers.Values.OfType<UploadSession>().Any(u => string.Equals(u.PartPath, path, StringComparison.OrdinalIgnoreCase));

    public void Bind(ISessionNodeDriver driver) => _driver = driver;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // the container disposes a service registered twice (singleton and module) once per registration
        }

        _lifetime.Cancel();
        AbortTransfers();
        _lifetime.Dispose();
    }

    // ------------------------------------------------------------------ admin commands

    /// <summary>Asks the authority for a checkpoint now (admin request, <c>POST /sessions/{id}/request-save</c>). False when it cannot be sent.</summary>
    public Task<bool> RequestSaveAsync(SaveReason reason = SaveReason.Admin) =>
        _driver is null ? Task.FromResult(false) : _driver.CallAsync(() => SendRequestSave(reason));

    // ------------------------------------------------------------------ module callbacks

    public void OnSessionBegun(long sessionId)
    {
        ResetSession();
        _sessionId = sessionId;
    }

    public void OnSessionPhaseChanged(SessionPhase previous, SessionPhase current)
    {
        _phase = current;
        switch (current)
        {
            case SessionPhase.Stopping:
                BeginFinalSave();
                break;
            case SessionPhase.Ended:
                AbortTransfers();
                _stopPending = false;
                break;
            case SessionPhase.Idle:
                ResetSession();
                break;
        }
    }

    public void OnNodeAttached(SessionNode node, bool resumed)
    {
        _knownNodes[node.PlayerId] = node;
        if (node.IsAuthority)
        {
            _authority = node;
            if (node.Phase == NodePhase.SyncingSave)
            {
                SendStartSaveInfo(node); // also after a resume that was cut off while it downloaded
            }

            return;
        }

        if (resumed && node.Phase is NodePhase.InGame or NodePhase.CatchingUp && node.LastJournalSeq > 0)
        {
            CatchUpAfterResume(node);
        }
        else if (resumed && node.Phase == NodePhase.CatchingUp && _loaded.TryGetValue(node.PlayerId, out var loadedCheckpoint))
        {
            // Dropped inside the catch-up before it applied a single journal entry: replay everything after its checkpoint (strings included).
            StartCatchUp(node, loadedCheckpoint);
        }
        else if (resumed && node.Phase == NodePhase.InGame)
        {
            // In game with no journal position (nothing applied since the checkpoint): the strings the authority added while the socket was
            // down are missed by the incremental send, and the node's DLL may have restarted, so a resume always gets the full table (M3-14).
            SendStringTable(node);
        }
        else if (node.Phase == NodePhase.SyncingSave)
        {
            SendSaveInfo(node);
        }
    }

    public void OnNodePhaseChanged(SessionNode node, NodePhase previous, NodePhase current)
    {
        if (node.IsAuthority)
        {
            if (current == NodePhase.InGame)
            {
                TryRequestInitialSave();
            }
            else if (current == NodePhase.SyncingSave && node.Announced)
            {
                SendStartSaveInfo(node);
            }

            return;
        }

        if (current == NodePhase.SyncingSave && node.Announced)
        {
            SendSaveInfo(node);
        }
    }

    public void OnNodeDetached(SessionNode node, DetachReason reason)
    {
        CancelDownload(node.PlayerId);
        _waitingForFresh.Remove(node.PlayerId);
        if (node.IsAuthority)
        {
            AuthorityGone(keepRequest: true);
        }
    }

    public void OnNodeLeft(SessionNode node, string reason)
    {
        CancelDownload(node.PlayerId);
        Tokens.RevokePlayer(node.PlayerId);
        _knownNodes.Remove(node.PlayerId);
        _loaded.Remove(node.PlayerId);
        _waitingForFresh.Remove(node.PlayerId);
        if (node.IsAuthority)
        {
            AuthorityGone(keepRequest: false);
            _authority = null;
        }
    }

    public bool OnMessage(SessionNode node, InboundFrame frame)
    {
        try
        {
            switch (frame.Type)
            {
                case MsgType.SaveUploadBegin:
                    OnUploadBegin(node, MessageRegistry.Default.Decode<SaveUploadBegin>(frame.Frame));
                    return true;
                case MsgType.SaveChunk:
                    OnUploadChunk(node, frame);
                    return true;
                case MsgType.SaveUploadEnd:
                    OnUploadEnd(node, MessageRegistry.Default.Decode<SaveUploadEnd>(frame.Frame));
                    return true;
                case MsgType.SaveChunkAck:
                    OnChunkAck(node, MessageRegistry.Default.Decode<SaveChunkAck>(frame.Frame));
                    return true;
                case MsgType.SaveDownloadRequest:
                    OnDownloadRequest(node, MessageRegistry.Default.Decode<SaveDownloadRequest>(frame.Frame));
                    return true;
                case MsgType.SaveReady:
                    OnSaveReady(node, MessageRegistry.Default.Decode<SaveReady>(frame.Frame));
                    return true;
                case MsgType.ManifestReport:
                    OnManifestReport(node, MessageRegistry.Default.Decode<ManifestReport>(frame.Frame));
                    return true;
                default:
                    return false;
            }
        }
        catch (ProtocolViolation violation)
        {
            node.Connection?.Stats.AddViolation();
            node.Connection?.Close(DisconnectCode.MalformedMessage, violation.Code.ToString());
            return true;
        }
    }

    public void OnTick(long timestamp)
    {
        if (_inFlight is { } pending && _time.GetElapsedTime(pending.SentAt, timestamp) > TimeSpan.FromSeconds(Opt.SaveRequestTimeoutSeconds))
        {
            LogRequestTimedOut(pending.RequestId, pending.Reason);
            _inFlight = null;
            _lastRequestFailedAt = timestamp;
            if (_stopPending)
            {
                CompleteStop("final save request timed out");
            }
        }

        if (_inFlight is null && _authority?.Connection is not null && _authority.Phase == NodePhase.InGame)
        {
            if (_current is null || _phase == SessionPhase.AuthorityLoading)
            {
                TryRequestInitialSave();
            }
            else if (_phase == SessionPhase.Running && Opt.AutosaveMinutes > 0 && timestamp >= _nextAutosave)
            {
                SendRequestSave(SaveReason.Autosave);
            }
        }

        if (_waitingForFresh.Count > 0 && (_authority?.Connection is null || _time.GetElapsedTime(_freshWaitSince, timestamp) > TimeSpan.FromSeconds(Opt.SaveRequestTimeoutSeconds)))
        {
            ReleaseFreshWaiters(); // nothing to wait for: hand out the checkpoint we have
        }
    }

    // ------------------------------------------------------------------ small helpers

    private bool AuthorityLive => _authority?.Connection is not null;

    private void Publish(DomainEvent domainEvent)
    {
        try
        {
            _events?.Publish(domainEvent);
        }
        catch (Exception ex)
        {
            LogPublishFailed(ex);
        }
    }

    private void Fire(int playerId, NodeTrigger trigger)
    {
        if (_driver is null)
        {
            return;
        }

        _ = _driver.ApplyNodeTriggerAsync(playerId, trigger).ContinueWith(
            static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private static void Send(SessionNode node, OutboundFrame frame)
    {
        node.Connection?.TrySend(frame);
        frame.Release();
    }

    private void ResetSession()
    {
        AbortTransfers();
        _checkpoints.Clear();
        _loaded.Clear();
        _waitingForFresh.Clear();
        _current = null;
        _previous = null;
        _inFlight = null;
        _startSave = null;
        _startInfoConnection = null;
        _seeded = false;
        _stopPending = false;
        _nextAutosave = long.MaxValue;
        _sessionId = 0;
        Tokens.Clear();
        PublishStatus();
    }

    private void AbortTransfers()
    {
        foreach (var upload in _uploads.Values)
        {
            upload.Abort();
        }

        _uploads.Clear();
        foreach (var download in _downloads.Values)
        {
            download.Cancel();
        }

        _downloads.Clear();
    }

    private void PublishStatus()
    {
        var cp = _current;
        _status = new SaveStatus(
            cp?.SaveSha, cp?.ManifestSha, cp?.SaveSize ?? 0, cp?.GameTime ?? 0, cp?.At, cp?.Name,
            _uploads.Count, _downloads.Count);

        // What the janitor (another thread) must not delete: the current and the previous checkpoint and whatever is being uploaded.
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var checkpoint in new[] { _current, _previous })
        {
            if (checkpoint?.SaveSha is { } save)
            {
                keep.Add(save);
            }

            if (checkpoint?.ManifestSha is { } manifest)
            {
                keep.Add(manifest);
            }
        }

        foreach (var upload in _uploads.Values)
        {
            keep.Add(upload.ShaHex);
        }

        if (_startSave is { } start && _current is null)
        {
            keep.Add(start.Sha);
        }

        _protected = keep;
    }

    // ------------------------------------------------------------------ logging

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "save request {RequestId} ({Reason}) was not answered in time")]
    private partial void LogRequestTimedOut(uint requestId, SaveReason reason);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "could not publish a save event")]
    private partial void LogPublishFailed(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "save request {RequestId} ({Reason}) sent as slot {Slot}")]
    private partial void LogRequested(uint requestId, SaveReason reason, string slot);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "checkpoint {Checkpoint} is now the current save: {Sha} ({Bytes} bytes), journal compacted by {Compacted}")]
    private partial void LogCurrent(CheckpointId checkpoint, string sha, long bytes, int compacted);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "checkpoint {Checkpoint}: {Kind} upload {Result}: {Detail}")]
    private partial void LogUploadFailed(CheckpointId checkpoint, UploadKind kind, SaveStoreResult result, string detail);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "player {PlayerId}: refused a save message: {Why}")]
    private partial void LogRefused(int playerId, string why);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "player {PlayerId}: download {Sha} {Kind} from offset {Offset} of {Size} (id {Id})")]
    private partial void LogDownload(int playerId, string sha, UploadKind kind, long offset, long size, uint id);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "player {PlayerId}: download {Id} {Outcome} ({Bytes} bytes sent)")]
    private partial void LogDownloadEnded(int playerId, uint id, string outcome, long bytes);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "player {PlayerId}: manifest report: {Unmatched} of {Total} unmatched ({Percent:F2}%), limit {Limit}%")]
    private partial void LogManifest(int playerId, long unmatched, long total, double percent, double limit);
}
