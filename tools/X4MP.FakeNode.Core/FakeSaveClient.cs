using System.Security.Cryptography;
using Google.FlatBuffers;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;

namespace X4MP.FakeNode;

/// <summary>Tunables of a fake client's join pipeline.</summary>
public sealed record FakeSaveClientOptions
{
    /// <summary>Directory for the downloaded save, its manifest and the <c>.part</c> files (a resume continues from them).</summary>
    public string Directory { get; init; } = Path.Combine(Path.GetTempPath(), "x4mp-fakenode");

    /// <summary>The simulated <c>LoadGame</c> time.</summary>
    public TimeSpan LoadDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Fraction of the manifest's entities the fake match reports as unmatched (tests the <c>ManifestMismatch</c> policy).</summary>
    public double UnmatchedFraction { get; init; }

    /// <summary>Called with (bytes of the current file, size) after every chunk (tests kill the connection from here).</summary>
    public Action<long, long>? OnDownloadProgress { get; init; }
}

/// <summary>Where a fake client is in the join pipeline.</summary>
public enum FakeJoinStage
{
    WaitingForSaveInfo,
    DownloadingSave,
    DownloadingManifest,
    Loading,
    CatchingUp,
    InGame,
}

/// <summary>
/// A fake client's join pipeline (protocol.md 6.4/6.5): on <c>SessionSaveInfo</c> it downloads the save and the manifest in-band (resuming
/// from its <c>.part</c> file), verifies the SHA-256, reports <c>SaveReady</c>, "loads", matches the manifest (<c>ManifestReport</c>), takes
/// the <c>StringTableAdd</c> replay and <c>WorldCatchUp</c> and sends <c>NodeReady</c>. Feed it every frame the node receives
/// (<see cref="HandleAsync"/>) from the single receive loop; a dropped connection is resumed by attaching the new client
/// (<see cref="Attach"/>) and handing it the next <c>SessionSaveInfo</c> (the server resends it on resume).
/// </summary>
public sealed class FakeSaveClient : IDisposable
{
    private readonly FakeSaveClientOptions _options;
    private readonly Action<string>? _log;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TcpNodeClient _client;
    private SessionSaveInfoT? _info;
    private FileStream? _part;
    private IncrementalHash? _hash;
    private UploadKind _kind;
    private long _size;
    private long _received;
    private long _lastAckSent;
    private uint _downloadId;
    private int _chunksSinceAck;
    private long _lastStatusTicks;
    private long _catchUpEntries;
    private long _stringEntries;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, string> _strings = new();

    public FakeSaveClient(TcpNodeClient client, FakeSaveClientOptions? options = null, Action<string>? log = null)
    {
        _client = client;
        Tracker = new FakePhaseTracker(client.Welcome.PlayerId);
        _options = options ?? new FakeSaveClientOptions();
        _log = log;
    }

    public FakeJoinStage Stage { get; private set; } = FakeJoinStage.WaitingForSaveInfo;

    /// <summary>The phase the server reports for this node (from <c>RosterUpdate</c>).</summary>
    public FakePhaseTracker Tracker { get; private set; }

    /// <summary>The <c>current_save_sha256</c> of the latest <c>SessionState</c> that carried one (the session's current checkpoint).</summary>
    public string? AnnouncedSaveSha { get; private set; }

    /// <summary>True when the announced save has a manifest (a client's checkpoint); false for the authority's stored start save, which is only downloaded, verified and "loaded".</summary>
    private bool HasManifest => _info!.ManifestSha256 is { Count: > 0 };

    /// <summary>The latest <c>SessionSaveInfo</c> (null before it arrived).</summary>
    public SessionSaveInfoT? SaveInfo => _info;

    /// <summary>Completes when <c>NodeReady</c> was sent.</summary>
    public Task Ready => _ready.Task;

    /// <summary>Bytes of chunks received over this object's lifetime (a resume receives only the rest).</summary>
    public long BytesReceived { get; private set; }

    /// <summary>Offset the current or last download started at (0 for a fresh one, above 0 after a resume).</summary>
    public long StartOffset { get; private set; }

    /// <summary>The hash of the save that was downloaded and verified (null before).</summary>
    public string? VerifiedSaveSha { get; private set; }

    public string? VerifiedManifestSha { get; private set; }

    public long CatchUpEntries => Interlocked.Read(ref _catchUpEntries);

    public long StringEntries => Interlocked.Read(ref _stringEntries);

    /// <summary>The distinct string-table entries received so far (index -> value); duplicates from a replay are stored once.</summary>
    public IReadOnlyDictionary<uint, string> KnownStrings => _strings;

    /// <summary>Hash mismatches seen (a mismatch restarts the file once).</summary>
    public int ChecksumFailures { get; private set; }

    /// <summary>Why the join failed (a hash that never matched, a short file), or null.</summary>
    public string? Error { get; private set; }

    /// <summary>The (possibly partial) save file in the cache directory, once <c>SessionSaveInfo</c> arrived.</summary>
    public string? SavePath => _info is null ? null : Path.Combine(_options.Directory, _info.LocalFileName);

    /// <summary>The pipeline cannot go on (the connection died under it): <see cref="Error"/> carries the reason.</summary>
    public void Abort(string reason) => Error ??= reason;

    /// <summary>Closes the part file (the file stays for a later resume).</summary>
    public void Dispose()
    {
        _part?.Dispose();
        _part = null;
        _hash?.Dispose();
        _hash = null;
    }

    /// <summary>Continues on a new connection after a drop. Pass the new client's <c>SessionSaveInfo</c> to <see cref="HandleAsync"/> to resume.</summary>
    public void Attach(TcpNodeClient client)
    {
        _client = client;
        Tracker = new FakePhaseTracker(client.Welcome.PlayerId);
        _part?.Dispose();
        _part = null;
        _hash?.Dispose();
        _hash = null;
        _chunksSinceAck = 0;
        if (Stage is FakeJoinStage.DownloadingSave or FakeJoinStage.DownloadingManifest)
        {
            Stage = FakeJoinStage.WaitingForSaveInfo;
        }
    }

    /// <summary>Handles one frame; returns true when it was part of the join pipeline.</summary>
    public async Task<bool> HandleAsync(Frame frame, CancellationToken ct)
    {
        switch (frame.Type)
        {
            case MsgType.RosterUpdate:
                Tracker.Observe(frame);
                return false; // somebody else may want it too
            case MsgType.SessionState:
                var state = MessageRegistry.Default.Decode<SessionState>(frame);
                if (state.CurrentSaveSha256Length > 0)
                    AnnouncedSaveSha = Convert.ToHexStringLower(state.GetCurrentSaveSha256Array());
                return false;
            case MsgType.SessionSaveInfo:
                if (Stage == FakeJoinStage.WaitingForSaveInfo)
                {
                    _info = MessageRegistry.Default.Decode<SessionSaveInfo>(frame).UnPack();
                    await StartAsync(UploadKind.Save, ct).ConfigureAwait(false);
                }

                // a newer checkpoint announced while this one downloads: the join carries on with the one it started with
                return true;
            case MsgType.SaveDownloadAccept:
                var accept = MessageRegistry.Default.Decode<SaveDownloadAccept>(frame);
                _downloadId = accept.DownloadId;
                _size = (long)accept.Size;
                if (_received >= _size)
                {
                    await FinishFileAsync(ct).ConfigureAwait(false); // everything was there already
                }

                return true;
            case MsgType.SaveChunk:
                await OnChunkAsync(frame, ct).ConfigureAwait(false);
                return true;
            case MsgType.StringTableAdd:
                var table = MessageRegistry.Default.Decode<StringTableAdd>(frame);
                Interlocked.Add(ref _stringEntries, table.EntriesLength);
                for (int i = 0; i < table.EntriesLength; i++)
                {
                    if (table.Entries(i) is { } entry)
                    {
                        _strings[entry.Index] = entry.Value;
                    }
                }

                return true;
            case MsgType.WorldCatchUp:
                await OnCatchUpAsync(MessageRegistry.Default.Decode<WorldCatchUp>(frame), ct).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    private string PathOf(UploadKind kind) => kind == UploadKind.Save
        ? Path.Combine(_options.Directory, _info!.LocalFileName)
        : Path.Combine(_options.Directory, _info!.LocalFileName.Replace(".xml.gz", ".x4mf", StringComparison.Ordinal));

    private byte[] ShaOf(UploadKind kind) => kind == UploadKind.Save ? [.. _info!.Sha256] : [.. _info!.ManifestSha256];

    private long SizeOf(UploadKind kind) => kind == UploadKind.Save ? (long)_info!.Size : (long)_info!.ManifestSize;

    /// <summary>Requests <paramref name="kind"/> from the offset the part file already holds.</summary>
    private async Task StartAsync(UploadKind kind, CancellationToken ct)
    {
        _kind = kind;
        Stage = kind == UploadKind.Save ? FakeJoinStage.DownloadingSave : FakeJoinStage.DownloadingManifest;
        string final = PathOf(kind);
        string part = final + ".part";
        System.IO.Directory.CreateDirectory(_options.Directory);

        if (File.Exists(final) && await VerifyFileAsync(final, ShaOf(kind), ct).ConfigureAwait(false))
        {
            // already cached and intact (a rejoin)
            _size = new FileInfo(final).Length;
            _received = _size;
            StartOffset = _size;
            await FinishCachedAsync(ct).ConfigureAwait(false);
            return;
        }

        _hash?.Dispose();
        _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _part?.Dispose();
        _part = new FileStream(part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
        long have = Math.Min(_part.Length, SizeOf(kind));
        _part.SetLength(have);
        _part.Position = 0;
        var buffer = new byte[1 << 20];
        long left = have;
        while (left > 0)
        {
            int n = _part.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (n <= 0)
            {
                break;
            }

            _hash.AppendData(buffer, 0, n);
            left -= n;
        }

        _received = have;
        StartOffset = have;
        _lastAckSent = have;
        _chunksSinceAck = 0;
        _size = SizeOf(kind);
        _log?.Invoke($"downloading {kind} {Convert.ToHexStringLower(ShaOf(kind))[..12]} from offset {have} of {_size}");
        var sha = ShaOf(kind);
        await _client.SendAsync(
            MsgType.SaveDownloadRequest,
            b => SaveDownloadRequest.Pack(b, new SaveDownloadRequestT { Sha256 = [.. sha], Kind = kind, Offset = (ulong)have }),
            ct).ConfigureAwait(false);
        await ReportStatusAsync(NodePhase.SyncingSave, _size == 0 ? 0 : (float)have / _size, have, force: true, ct).ConfigureAwait(false);
    }

    private async Task OnChunkAsync(Frame frame, CancellationToken ct)
    {
        if (_part is null || _hash is null)
        {
            return;
        }

        var chunk = SaveChunk.GetRootAsSaveChunk(new ByteBuffer(frame.Payload));
        if (chunk.TransferId != _downloadId || (long)chunk.Offset != _received || chunk.GetDataBytes() is not { } data)
        {
            return; // a stale chunk of an abandoned download
        }

        _part.Write(data.AsSpan());
        _hash.AppendData(data.AsSpan());
        _received += data.Count;
        BytesReceived += data.Count;
        _options.OnDownloadProgress?.Invoke(_received, _size);
        if (++_chunksSinceAck >= 4 || _received >= _size)
        {
            _chunksSinceAck = 0;
            _lastAckSent = _received;
            long next = _received;
            uint id = _downloadId;
            await _client.SendAsync(MsgType.SaveChunkAck, b => SaveChunkAck.CreateSaveChunkAck(b, id, (ulong)next), ct).ConfigureAwait(false);
        }

        await ReportStatusAsync(NodePhase.SyncingSave, _size == 0 ? 0 : (float)_received / _size, _received, force: false, ct).ConfigureAwait(false);
        if (_received >= _size)
        {
            await FinishFileAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task FinishFileAsync(CancellationToken ct)
    {
        string final = PathOf(_kind);
        string part = final + ".part";
        var expected = ShaOf(_kind);
        await _part!.FlushAsync(ct).ConfigureAwait(false);
        _part.Dispose();
        _part = null;
        string actual = Convert.ToHexStringLower(_hash!.GetHashAndReset());
        _hash.Dispose();
        _hash = null;
        if (!string.Equals(actual, Convert.ToHexStringLower(expected), StringComparison.Ordinal))
        {
            ChecksumFailures++;
            File.Delete(part);
            _log?.Invoke($"{_kind} checksum mismatch (got {actual[..12]}): downloading again");
            if (ChecksumFailures > 1)
            {
                Error = $"{_kind} checksum mismatch twice";
                return;
            }

            await StartAsync(_kind, ct).ConfigureAwait(false);
            return;
        }

        File.Move(part, final, overwrite: true);
        await FinishCachedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The current file is complete and verified: next file, or the load.</summary>
    private async Task FinishCachedAsync(CancellationToken ct)
    {
        if (_kind == UploadKind.Save)
        {
            VerifiedSaveSha = Convert.ToHexStringLower(ShaOf(UploadKind.Save));
            if (!HasManifest)
            {
                // The authority's stored save (an admin upload has no manifest): load it and be ready, nothing to match or catch up.
                Stage = FakeJoinStage.Loading;
                _ = Task.Run(() => LoadPipelineAsync(ct), CancellationToken.None);
                return;
            }

            await StartAsync(UploadKind.Manifest, ct).ConfigureAwait(false);
            return;
        }

        VerifiedManifestSha = Convert.ToHexStringLower(ShaOf(UploadKind.Manifest));
        Stage = FakeJoinStage.Loading;
        _ = Task.Run(() => LoadPipelineAsync(ct), CancellationToken.None);
    }

    /// <summary>
    /// Verify, ready, load, match: runs beside the receive loop (it waits for the server to confirm each phase before it sends the next
    /// frame, see <see cref="FakePhaseTracker"/>, and the roster that confirms it arrives through that loop).
    /// </summary>
    private async Task LoadPipelineAsync(CancellationToken ct)
    {
        try
        {
            var phaseWait = TimeSpan.FromSeconds(3);
            await ReportStatusAsync(NodePhase.Verifying, 1, SizeOf(UploadKind.Save), force: true, ct).ConfigureAwait(false);
            var sha = ShaOf(UploadKind.Save);
            byte[] manifestSha = HasManifest ? ShaOf(UploadKind.Manifest) : [];
            await _client.SendAsync(MsgType.SaveReady, b => SaveReady.Pack(b, new SaveReadyT { Sha256 = [.. sha], ManifestSha256 = [.. manifestSha] }), ct).ConfigureAwait(false);
            await ReportStatusAsync(NodePhase.Loading, 0, 0, force: true, ct).ConfigureAwait(false);
            await Tracker.WaitForAsync(NodePhase.Loading, phaseWait, ct).ConfigureAwait(false);
            await Task.Delay(_options.LoadDelay, ct).ConfigureAwait(false); // LoadGame
            await ReportStatusAsync(NodePhase.Matching, 0, 0, force: true, ct).ConfigureAwait(false);
            await Tracker.WaitForAsync(NodePhase.Matching, phaseWait, ct).ConfigureAwait(false);

            if (!HasManifest)
            {
                await _client.SendAsync(
                    MsgType.NodeReady,
                    b => NodeReady.Pack(b, new NodeReadyT { UniverseEpoch = 1, LoadedSaveSha256 = [.. sha] }),
                    ct).ConfigureAwait(false);
                Stage = FakeJoinStage.InGame;
                _ready.TrySetResult();
                return;
            }

            var manifest =Manifest.GetRootAsManifest(new ByteBuffer(await File.ReadAllBytesAsync(PathOf(UploadKind.Manifest), ct).ConfigureAwait(false)));
            uint total = (uint)manifest.EntriesLength;
            uint unmatched = (uint)Math.Round(total * _options.UnmatchedFraction);
            var report = new ManifestReportT
            {
                CheckpointId = _info!.CheckpointId,
                Total = total,
                Matched = total - unmatched,
                Unmatched = unmatched,
                UnmatchedSample = [],
                DurationMs = 1,
            };
            await _client.SendAsync(MsgType.ManifestReport, b => ManifestReport.Pack(b, report), ct).ConfigureAwait(false);
            Stage = FakeJoinStage.CatchingUp;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // the connection went away: a resume starts the pipeline again
        }
    }

    private async Task OnCatchUpAsync(WorldCatchUp catchUp, CancellationToken ct)
    {
        Interlocked.Add(ref _catchUpEntries, catchUp.EntriesLength);
        if (!catchUp.Final)
        {
            return;
        }

        await ReportStatusAsync(NodePhase.CatchingUp, 1, 0, force: true, ct).ConfigureAwait(false);
        await _client.SendAsync(
            MsgType.NodeReady,
            b => NodeReady.Pack(b, new NodeReadyT { UniverseEpoch = 1, LoadedSaveSha256 = [.. ShaOf(UploadKind.Save)] }),
            ct).ConfigureAwait(false);
        Stage = FakeJoinStage.InGame;
        _ready.TrySetResult();
    }

    /// <summary><c>LoadStatus</c> at most twice a second while downloading; always when forced (phase changes).</summary>
    private async Task ReportStatusAsync(NodePhase phase, float progress, long bytes, bool force, CancellationToken ct)
    {
        long now = Environment.TickCount64;
        if (!force && now - _lastStatusTicks < 500)
        {
            return;
        }

        _lastStatusTicks = now;
        await _client.SendAsync(
            MsgType.LoadStatus,
            b => LoadStatus.Pack(b, new LoadStatusT { Phase = phase, Progress = progress, BytesDone = (ulong)bytes, Detail = string.Empty }),
            ct).ConfigureAwait(false);
    }

    private static async Task<bool> VerifyFileAsync(string path, byte[] expected, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return (await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)).AsSpan().SequenceEqual(expected);
    }
}
