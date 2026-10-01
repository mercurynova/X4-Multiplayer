using System.Security.Cryptography;
using System.Threading.Channels;
using X4MP.Core.Net;
using X4MP.Core.World;
using X4MP.Proto;

namespace X4MP.Core.Saves;

/// <summary>How an upload ended.</summary>
internal sealed record UploadOutcome(SaveStoreResult Result, string Detail, SaveMeta Meta, long Size, bool Aborted);

/// <summary>
/// One in-band upload from the authority (protocol.md 6.3 steps 3 to 5). The actor only forwards chunk frames into <see cref="Post"/>;
/// a worker task writes them to the <c>.part</c> file, hashes incrementally, acks every <see cref="AckEvery"/> chunks (and at the end),
/// and on <c>SaveUploadEnd</c> verifies hash, size and content before the file enters the store. The actor thread never touches the disk.
/// A part file left by a broken upload is picked up again by the next <c>SaveUploadBegin</c> with the same hash (resume).
/// </summary>
internal sealed class UploadSession : IDisposable
{
    public const int AckEvery = 4;

    private readonly record struct Item(bool End, byte[]? Payload);

    private readonly Channel<Item> _queue = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _cts = new();
    private long _received;

    public UploadSession(
        uint id, int playerId, string playerName, INodeConnection connection, CheckpointId checkpoint, UploadKind kind,
        string shaHex, long size, string name, bool ghostsCleaned, string partPath, long resumeOffset, bool alreadyStored,
        int chunkBytes, DateTimeOffset startedAt)
    {
        Id = id;
        PlayerId = playerId;
        PlayerName = playerName;
        Connection = connection;
        Checkpoint = checkpoint;
        Kind = kind;
        ShaHex = shaHex;
        Size = size;
        Name = name;
        GhostsCleaned = ghostsCleaned;
        PartPath = partPath;
        ResumeOffset = resumeOffset;
        AlreadyStored = alreadyStored;
        ChunkBytes = chunkBytes;
        StartedAt = startedAt;
        Volatile.Write(ref _received, alreadyStored ? size : resumeOffset);
    }

    public uint Id { get; }

    public int PlayerId { get; }

    public string PlayerName { get; }

    public INodeConnection Connection { get; }

    public CheckpointId Checkpoint { get; }

    public UploadKind Kind { get; }

    public string ShaHex { get; }

    public long Size { get; }

    public string Name { get; }

    public bool GhostsCleaned { get; }

    public string PartPath { get; }

    public long ResumeOffset { get; }

    /// <summary>The file is in the store already: no bytes are needed, the end marker just confirms.</summary>
    public bool AlreadyStored { get; }

    public int ChunkBytes { get; }

    public DateTimeOffset StartedAt { get; }

    public long Received => Interlocked.Read(ref _received);

    /// <summary>Queues a chunk frame payload (not parsed here).</summary>
    public bool PostChunk(byte[] payload) => _queue.Writer.TryWrite(new Item(false, payload));

    public bool PostEnd() => _queue.Writer.TryWrite(new Item(true, null));

    /// <summary>Stops the worker (the connection is gone or the session ended). The part file stays for a resume.</summary>
    public void Dispose() => _cts.Dispose();

    public void Abort()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already finished
        }

        _queue.Writer.TryComplete();
    }

    /// <summary>Runs until the end marker, an error or <see cref="Abort"/>. Never throws.</summary>
    public async Task<UploadOutcome> RunAsync()
    {
        var ct = _cts.Token;
        try
        {
            return await RunCoreAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new UploadOutcome(SaveStoreResult.Aborted, "aborted", SaveMeta.Empty, Size, Aborted: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UploadOutcome(SaveStoreResult.Aborted, "storage error: " + ex.Message, SaveMeta.Empty, Size, Aborted: false);
        }
    }

    private async Task<UploadOutcome> RunCoreAsync(CancellationToken ct)
    {
        FileStream? file = null;
        IncrementalHash? hash = null;
        try
        {
            long next = Received;
            if (!AlreadyStored)
            {
                file = new FileStream(PartPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
                hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                if (next > 0)
                {
                    if (!await HashPrefixAsync(file, hash, next, ct).ConfigureAwait(false))
                    {
                        // the part file is shorter than the resume offset we promised: start over
                        return Fail(SaveStoreResult.Aborted, "the partial upload vanished; send it again from 0");
                    }
                }

                file.SetLength(next);
                file.Position = next;
            }

            int sinceAck = 0;
            bool ended = false;
            while (!ended && await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var item))
                {
                    if (item.End)
                    {
                        ended = true;
                        break;
                    }

                    if (AlreadyStored)
                    {
                        continue; // nothing to write; the sender ignores the resume offset it was given
                    }

                    var error = WriteChunk(file!, hash!, item.Payload!, ref next);
                    if (error is not null)
                    {
                        return Fail(SaveStoreResult.Aborted, error);
                    }

                    Volatile.Write(ref _received, next);
                    if (++sinceAck >= AckEvery || next == Size)
                    {
                        sinceAck = 0;
                        SendAck(next);
                    }
                }
            }

            if (!ended)
            {
                ct.ThrowIfCancellationRequested();
                return new UploadOutcome(SaveStoreResult.Aborted, "aborted", SaveMeta.Empty, Size, Aborted: true);
            }

            if (AlreadyStored)
            {
                return new UploadOutcome(SaveStoreResult.Stored, "already stored", SaveMeta.Empty, Size, Aborted: false);
            }

            if (next != Size)
            {
                return Fail(SaveStoreResult.Aborted, $"incomplete: {next} of {Size} bytes");
            }

            await file!.FlushAsync(ct).ConfigureAwait(false);
            await file.DisposeAsync().ConfigureAwait(false);
            file = null;
            var actual = Convert.ToHexStringLower(hash!.GetHashAndReset());
            if (!string.Equals(actual, ShaHex, StringComparison.Ordinal))
            {
                SaveFileStore.TryDelete(PartPath);
                return new UploadOutcome(SaveStoreResult.HashMismatch, $"sha256 is {actual}, announced {ShaHex}", SaveMeta.Empty, Size, Aborted: false);
            }

            var sniff = SaveSniffer.Check(PartPath, Kind, out var meta);
            if (sniff != SniffResult.Ok)
            {
                SaveFileStore.TryDelete(PartPath);
                string what = Kind == UploadKind.Save ? "a gzip save with a <savegame root" : "a manifest (file identifier X4MF)";
                return new UploadOutcome(SaveStoreResult.NotASave, $"not {what}: {sniff}", SaveMeta.Empty, Size, Aborted: false);
            }

            return new UploadOutcome(SaveStoreResult.Stored, string.Empty, meta, Size, Aborted: false);
        }
        finally
        {
            hash?.Dispose();
            if (file is not null)
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private UploadOutcome Fail(SaveStoreResult result, string detail) => new(result, detail, SaveMeta.Empty, Size, Aborted: false);

    private static async Task<bool> HashPrefixAsync(FileStream file, IncrementalHash hash, long count, CancellationToken ct)
    {
        if (file.Length < count)
        {
            return false;
        }

        var buffer = new byte[1 << 20];
        long left = count;
        file.Position = 0;
        while (left > 0)
        {
            int n = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            hash.AppendData(buffer, 0, n);
            left -= n;
        }

        return true;
    }

    /// <summary>Validates and writes one chunk (synchronously: the span of the payload must not cross an await). Null on success.</summary>
    private string? WriteChunk(FileStream file, IncrementalHash hash, byte[] payload, ref long next)
    {
        try
        {
            var chunk = SaveChunk.GetRootAsSaveChunk(new Google.FlatBuffers.ByteBuffer(payload));
            if (chunk.TransferId != Id)
            {
                return "chunk for another transfer";
            }

            if (chunk.Offset != (ulong)next)
            {
                return $"chunk at offset {chunk.Offset}, expected {next}";
            }

            int length = chunk.DataLength;
            if (length <= 0 || length > ChunkBytes)
            {
                return $"chunk of {length} bytes (limit {ChunkBytes})";
            }

            if (next + length > Size)
            {
                return "more data than announced";
            }

            if (chunk.GetDataBytes() is not { } segment)
            {
                return "chunk without data";
            }

            file.Write(segment.AsSpan());
            hash.AppendData(segment.AsSpan());
            next += length;
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            return "malformed chunk";
        }
    }

    private void SendAck(long next)
    {
        var frame = ControlFrames.Encode(
            MsgType.SaveChunkAck,
            fbb => SaveChunkAck.CreateSaveChunkAck(fbb, Id, (ulong)next).Value,
            32);
        Connection.TrySend(frame);
        frame.Release();
    }
}
