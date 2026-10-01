
using System.Threading.Channels;
using Google.FlatBuffers;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;

namespace X4MP.FakeNode;

/// <summary>Tunables of the fake authority's save job.</summary>
public sealed record FakeAuthoritySaveOptions
{
    /// <summary>Size of the generated save (bytes, approximately).</summary>
    public long SaveBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>Directory the fake saves and manifests are written to.</summary>
    public string Directory { get; init; } = Path.Combine(Path.GetTempPath(), "x4mp-fakenode");

    /// <summary>Reported as <c>ghosts_cleaned</c> (a real authority always strips its ghosts first; tests flip it).</summary>
    public bool GhostsCleaned { get; init; } = true;

    /// <summary>Called with (bytes sent of the current file, size) after every chunk (tests use it to kill the connection mid-upload).</summary>
    public Action<long, long>? OnUploadProgress { get; init; }

    /// <summary>How long to wait for the server to answer one step of the upload.</summary>
    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Corrupts the first byte after the announced hash was computed (tests the <c>HashMismatch</c> path).</summary>
    public bool CorruptSave { get; init; }

    /// <summary>The kind of save to upload (a broken one tests the server's sniff).</summary>
    public FakeSaveFlavor Flavor { get; init; } = FakeSaveFlavor.Valid;
}

/// <summary>What the fake authority did for one checkpoint.</summary>
public sealed record FakeCheckpointResult(
    Id128T CheckpointId, FakeSaveFile Save, FakeSaveFile Manifest, SaveStoreResult SaveResult, SaveStoreResult ManifestResult,
    long SaveResumeOffset, long ManifestResumeOffset);

/// <summary>
/// The fake authority's side of a checkpoint (protocol.md 6.3): on <c>RequestSave</c> it builds a fake save and its manifest, sends
/// <c>GalaxyMetadata</c>/<c>StringTableAdd</c> and the <c>SaveStarted</c> marker, then uploads both files in-band with the 8-chunk window,
/// honouring <c>SaveChunkAck</c> and the resume offset of <c>SaveUploadAccept</c>. Feed it every frame the node receives
/// (<see cref="Handle"/>); the upload runs as its own task so the receive loop keeps reading acks.
/// </summary>
public sealed class FakeAuthoritySaves
{
    private readonly FakeAuthority _authority;
    private readonly FakeAuthoritySaveOptions _options;
    private readonly Action<string>? _log;
    private readonly Channel<Frame> _inbox = Channel.CreateUnbounded<Frame>();
    private TcpNodeClient _client;
    private int _counter;
    private int _jobRunning;
    private FakeSaveFile? _lastSave;
    private FakeSaveFile? _lastManifest;
    private Id128T? _lastCheckpoint;
    private double _lastGameTime;
    private bool _startupSent;

    public FakeAuthoritySaves(TcpNodeClient client, FakeAuthority authority, FakeAuthoritySaveOptions? options = null, Action<string>? log = null)
    {
        _client = client;
        Tracker = new FakePhaseTracker(client.Welcome.PlayerId);
        _authority = authority;
        _options = options ?? new FakeAuthoritySaveOptions();
        _log = log;
    }

    /// <summary>Checkpoints fully uploaded (both files stored).</summary>
    public int CheckpointsStored { get; private set; }

    /// <summary>Bytes of chunks sent over the node's lifetime (a resumed upload sends only the rest).</summary>
    public long BytesSent { get; private set; }

    /// <summary>The last finished or interrupted job's outcome; null while none ran.</summary>
    public FakeCheckpointResult? LastResult { get; private set; }

    /// <summary>The exception that ended the last job (a dropped connection, a timeout), if any.</summary>
    public Exception? LastError { get; private set; }

    public bool JobRunning => Volatile.Read(ref _jobRunning) != 0;

    /// <summary>The phase the server reports for this node (from <c>RosterUpdate</c>; feed it every received frame through <see cref="Handle"/>).</summary>
    public FakePhaseTracker Tracker { get; private set; }

    /// <summary>
    /// The authority's own game is "loaded" already: reports Loading, Matching and <c>NodeReady</c> so the session asks for the first
    /// checkpoint. Waits for the server to confirm each phase (the server checks frames against the phase it has recorded), so run it
    /// beside the receive loop that calls <see cref="Handle"/>.
    /// </summary>
    public async Task ReportReadyAsync(CancellationToken ct)
    {
        var wait = TimeSpan.FromSeconds(3);
        foreach (var phase in new[] { NodePhase.Loading, NodePhase.Matching })
        {
            await _client.SendAsync(
                MsgType.LoadStatus, b => LoadStatus.Pack(b, new LoadStatusT { Phase = phase, Progress = 1, Detail = string.Empty }), ct).ConfigureAwait(false);
            bool confirmed = await Tracker.WaitForAsync(phase, wait, ct).ConfigureAwait(false);
            _log?.Invoke($"reported {phase}; server {(confirmed ? "confirmed it" : "did not confirm it in time (sending on)")}");
        }

        await _client.SendAsync(MsgType.NodeReady, b => NodeReady.Pack(b, new NodeReadyT { UniverseEpoch = 1, LoadedSaveSha256 = [] }), ct).ConfigureAwait(false);
        _log?.Invoke("sent NodeReady: waiting for the server to ask for the first checkpoint");
    }

    /// <summary>Raised on the job's thread when a checkpoint was uploaded and stored (both files).</summary>
    public event Action<FakeCheckpointResult>? CheckpointStored;

    /// <summary>The save of the last job (also the interrupted one): a resume uploads the same file.</summary>
    public FakeSaveFile? LastSave => _lastSave;

    /// <summary>The caller already sent the string table (the first checkpoint then does not repeat it).</summary>
    public void MarkStringTableSent() => _startupSent = true;

    /// <summary>Continues on a new connection (after a drop): the next <see cref="RunCheckpointAsync"/> with <c>resume</c> reuses the files.</summary>
    public void Attach(TcpNodeClient client)
    {
        _client = client;
        Tracker = new FakePhaseTracker(client.Welcome.PlayerId);
    }

    /// <summary>Routes one received frame. Returns true when it belonged to the save job (<c>RequestSave</c> starts one).</summary>
    public bool Handle(Frame frame)
    {
        switch (frame.Type)
        {
            case MsgType.RosterUpdate:
                Tracker.Observe(frame);
                return false;
            case MsgType.RequestSave:
                var request = MessageRegistry.Default.Decode<RequestSave>(frame);
                if (Interlocked.CompareExchange(ref _jobRunning, 1, 0) == 0)
                {
                    uint id = request.RequestId;
                    var reason = request.Reason;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await RunCheckpointAsync(id, reason, resume: false).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or InvalidOperationException or ObjectDisposedException)
                        {
                            LastError = ex;
                        }
                    });
                }

                return true;
            case MsgType.SaveUploadAccept or MsgType.SaveChunkAck or MsgType.SaveStored:
                _inbox.Writer.TryWrite(frame);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Builds and uploads one checkpoint. <paramref name="resume"/>: the previous attempt was cut off; upload the same files again (the
    /// server answers with the offset it already has) without repeating <c>SaveStarted</c>.
    /// </summary>
    public async Task<FakeCheckpointResult> RunCheckpointAsync(uint requestId, SaveReason reason, bool resume, CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _jobRunning, 1);
        try
        {
            if (!resume || _lastSave is null || _lastManifest is null || _lastCheckpoint is null)
            {
                int counter = ++_counter;
                _lastGameTime = counter * 60.0;
                var cp = new Id128T
                {
                    Lo = DetHash.Hash(_authority.World.Galaxy.Seed, 0xC4EC, (ulong)counter),
                    Hi = DetHash.Hash(_authority.World.Galaxy.Seed, 0x1D, (ulong)counter),
                };
                _lastCheckpoint = cp;
                _lastSave = FakeSaveGenerator.CreateSave(_options.Directory, _authority.World.Galaxy.Seed, counter, _options.SaveBytes, flavor: _options.Flavor);
                uint nextNetId = _authority.NetIds.NextNetId;
                var manifest = FakeSaveGenerator.BuildManifest(_authority, cp, _lastGameTime, nextNetId);
                _lastManifest = FakeSaveGenerator.WriteManifest(_options.Directory, $"fake-{_authority.World.Galaxy.Seed}-{counter}", manifest);
                _log?.Invoke($"checkpoint {counter}: save {_lastSave.ShaHex[..12]} ({_lastSave.Size} bytes), manifest {_lastManifest.ShaHex[..12]} ({_lastManifest.Size} bytes), reason {reason}");

                // GalaxyMetadata (keyed by the save's hash) and the string table first, then the journal marker: everything before it is in the save.
                if (!_startupSent)
                {
                    foreach (var m in _authority.StringTableMessages())
                    {
                        await _client.SendPayloadAsync(m.Type, m.Payload, ct).ConfigureAwait(false);
                    }

                    _startupSent = true;
                }

                var galaxy = _authority.BuildGalaxyMetadata(_lastSave.Sha256);
                await _client.SendPayloadAsync(galaxy.Type, galaxy.Payload, ct).ConfigureAwait(false);
                await _client.SendAsync(
                    MsgType.SaveStarted,
                    b => SaveStarted.Pack(b, new SaveStartedT { RequestId = requestId, CheckpointId = cp, GameTime = _lastGameTime, NextNetId = nextNetId }),
                    ct).ConfigureAwait(false);
            }

            var checkpoint = _lastCheckpoint!;
            var (saveResult, saveResume) = await UploadAsync(UploadKind.Save, _lastSave!, checkpoint, "X4MP fake save", ct).ConfigureAwait(false);
            var (manifestResult, manifestResume) = await UploadAsync(UploadKind.Manifest, _lastManifest!, checkpoint, "manifest", ct).ConfigureAwait(false);
            var result = new FakeCheckpointResult(checkpoint, _lastSave!, _lastManifest!, saveResult, manifestResult, saveResume, manifestResume);
            LastResult = result;
            if (saveResult is SaveStoreResult.Stored or SaveStoreResult.StoredNotCurrent && manifestResult is SaveStoreResult.Stored or SaveStoreResult.StoredNotCurrent)
            {
                CheckpointsStored++;
                CheckpointStored?.Invoke(result);
            }

            return result;
        }
        finally
        {
            Volatile.Write(ref _jobRunning, 0);
        }
    }

    private async Task<(SaveStoreResult Result, long ResumeOffset)> UploadAsync(UploadKind kind, FakeSaveFile file, Id128T checkpoint, string name, CancellationToken ct)
    {
        while (_inbox.Reader.TryRead(out _))
        {
            // stale frames of an earlier attempt
        }

        await _client.SendAsync(
            MsgType.SaveUploadBegin,
            b => SaveUploadBegin.Pack(b, new SaveUploadBeginT
            {
                CheckpointId = checkpoint,
                Kind = kind,
                Size = (ulong)file.Size,
                Sha256 = [.. file.Sha256],
                Name = name,
                GhostsCleaned = _options.GhostsCleaned,
            }),
            ct).ConfigureAwait(false);

        var first = await NextAsync(ct, MsgType.SaveUploadAccept, MsgType.SaveStored).ConfigureAwait(false);
        if (first.Type == MsgType.SaveStored)
        {
            return (MessageRegistry.Default.Decode<SaveStored>(first).Result, 0); // refused before it started (too large)
        }

        var accept = MessageRegistry.Default.Decode<SaveUploadAccept>(first);
        long offset = (long)accept.ResumeOffset;
        long resumeOffset = offset;
        long acked = offset;
        int chunk = (int)accept.ChunkSize;
        long window = (long)accept.WindowChunks * chunk;
        uint uploadId = accept.UploadId;

        await using (var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            stream.Position = offset;
            var buffer = new byte[chunk];
            var fbb = new FlatBufferBuilder(chunk + 256);
            while (offset < file.Size)
            {
                while (offset - acked >= window)
                {
                    var f = await NextAsync(ct, MsgType.SaveChunkAck, MsgType.SaveStored).ConfigureAwait(false);
                    if (f.Type == MsgType.SaveStored)
                    {
                        return (MessageRegistry.Default.Decode<SaveStored>(f).Result, resumeOffset); // the server gave up on us
                    }

                    acked = Math.Max(acked, (long)MessageRegistry.Default.Decode<SaveChunkAck>(f).NextOffset);
                }

                int n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(chunk, file.Size - offset)), ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    throw new IOException("the fake save shrank");
                }

                if (_options.CorruptSave && kind == UploadKind.Save && offset == 0)
                {
                    buffer[n / 2] ^= 0xFF;
                }

                fbb.Clear();
                var data = SaveChunk.CreateDataVectorBlock(fbb, new ArraySegment<byte>(buffer, 0, n));
                fbb.Finish(SaveChunk.CreateSaveChunk(fbb, uploadId, (ulong)offset, data).Value);
                var bb = fbb.DataBuffer;
                await _client.SendRawFrameAsync(FrameCodec.Encode(MsgType.SaveChunk, bb.ToArraySegment(bb.Position, fbb.Offset).AsSpan()), ct).ConfigureAwait(false);
                offset += n;
                BytesSent += n;
                _options.OnUploadProgress?.Invoke(offset, file.Size);
                while (_inbox.Reader.TryPeek(out var queued) && queued.Type == MsgType.SaveChunkAck && _inbox.Reader.TryRead(out queued))
                {
                    acked = Math.Max(acked, (long)MessageRegistry.Default.Decode<SaveChunkAck>(queued).NextOffset);
                }
            }
        }

        await _client.SendAsync(MsgType.SaveUploadEnd, b => SaveUploadEnd.CreateSaveUploadEnd(b, uploadId), ct).ConfigureAwait(false);
        var stored = await NextAsync(ct, MsgType.SaveStored).ConfigureAwait(false);
        var outcome = MessageRegistry.Default.Decode<SaveStored>(stored);
        _log?.Invoke($"{kind} upload {outcome.Result}{(string.IsNullOrEmpty(outcome.Detail) ? string.Empty : ": " + outcome.Detail)}");
        return (outcome.Result, resumeOffset);
    }

    /// <summary>The next frame of one of the wanted types; acks that nobody needs any more are skipped.</summary>
    private async Task<Frame> NextAsync(CancellationToken ct, params MsgType[] wanted)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.StepTimeout);
        try
        {
            while (true)
            {
                var frame = await _inbox.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                if (wanted.Contains(frame.Type))
                {
                    return frame;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no {string.Join("/", wanted)} from the server within {_options.StepTimeout.TotalSeconds:F0} s");
        }
    }
}
