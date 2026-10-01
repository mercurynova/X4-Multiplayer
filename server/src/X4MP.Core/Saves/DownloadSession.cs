using Google.FlatBuffers;
using X4MP.Core.Net;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Saves;

/// <summary>
/// One in-band download to a node (protocol.md 6.4): a sender task reads the stored file and queues <c>SaveChunk</c> frames on the
/// connection's Bulk lane, at most <c>window * chunk</c> bytes ahead of the last <c>SaveChunkAck</c>. Bulk is the lowest-priority lane
/// of the node's own queue, so it never delays another player's traffic; the shared <see cref="BandwidthLimiter"/> caps the server's
/// total. The actor only forwards acks into <see cref="OnAck"/>. A resume is a new download with the node's current offset.
/// </summary>
internal sealed class DownloadSession : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _cts;
    private long _acked;
    private long _sent;

    public DownloadSession(
        uint id, int playerId, string playerName, INodeConnection connection, string path, string shaHex, UploadKind kind,
        long size, long startOffset, int chunkBytes, int windowChunks, DateTimeOffset startedAt, CancellationToken linked)
    {
        Id = id;
        PlayerId = playerId;
        PlayerName = playerName;
        Connection = connection;
        Path = path;
        ShaHex = shaHex;
        Kind = kind;
        Size = size;
        StartOffset = startOffset;
        ChunkBytes = chunkBytes;
        WindowChunks = windowChunks;
        StartedAt = startedAt;
        _acked = startOffset;
        _sent = startOffset;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(linked, connection.Closed);
    }

    public uint Id { get; }

    public int PlayerId { get; }

    public string PlayerName { get; }

    public INodeConnection Connection { get; }

    public string Path { get; }

    public string ShaHex { get; }

    public UploadKind Kind { get; }

    public long Size { get; }

    public long StartOffset { get; }

    public int ChunkBytes { get; }

    public int WindowChunks { get; }

    public DateTimeOffset StartedAt { get; }

    /// <summary>Bytes handed to the connection's queue so far, counting from 0 (a resume starts at its offset).</summary>
    public long Sent => Interlocked.Read(ref _sent);

    /// <summary>Highest offset the node acknowledged.</summary>
    public long Acked => Interlocked.Read(ref _acked);

    /// <summary>Bytes this download put on the wire (without the part the node already had).</summary>
    public long BytesTransferred => Sent - StartOffset;

    public bool Completed { get; private set; }

    public void OnAck(long nextOffset)
    {
        long current;
        while (nextOffset > (current = Interlocked.Read(ref _acked)) && Interlocked.CompareExchange(ref _acked, nextOffset, current) != current)
        {
        }

        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException)
            {
                // already signalled
            }
        }
    }

    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already finished
        }
    }

    public void Dispose()
    {
        _cts.Dispose();
        _signal.Dispose();
    }

    /// <summary>Sends the file from <see cref="StartOffset"/>. Returns true when every byte was queued. Never throws.</summary>
    public async Task<bool> RunAsync(BandwidthLimiter limiter, Func<TimeSpan> stallTimeout)
    {
        var ct = _cts.Token;
        try
        {
            await using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
            file.Position = StartOffset;
            var buffer = new byte[ChunkBytes];
            var fbb = new FlatBufferBuilder(ChunkBytes + 256);
            long offset = StartOffset;
            long window = (long)WindowChunks * ChunkBytes;
            while (offset < Size)
            {
                while (offset - Acked >= window)
                {
                    if (!await _signal.WaitAsync(stallTimeout(), ct).ConfigureAwait(false))
                    {
                        return false; // no ack for too long: the node is gone or stuck
                    }
                }

                int want = (int)Math.Min(ChunkBytes, Size - offset);
                int got = await file.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (got <= 0)
                {
                    return false; // the file shrank (deleted by the janitor)
                }

                await limiter.AcquireAsync(got, ct).ConfigureAwait(false);
                var frame = EncodeChunk(fbb, buffer, got, offset);
                try
                {
                    while (true)
                    {
                        var result = Connection.TrySend(frame);
                        if (result is SendResult.Queued or SendResult.Coalesced)
                        {
                            break;
                        }

                        if (result is SendResult.Closed or SendResult.ClosedOverflow)
                        {
                            return false;
                        }

                        await Task.Delay(5, ct).ConfigureAwait(false); // Bulk lane over its safety cap: let the writer drain
                    }
                }
                finally
                {
                    frame.Release();
                }

                offset += got;
                Interlocked.Exchange(ref _sent, offset);
            }

            Completed = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private OutboundFrame EncodeChunk(FlatBufferBuilder fbb, byte[] buffer, int count, long offset)
    {
        fbb.Clear();
        var data = SaveChunk.CreateDataVectorBlock(fbb, new ArraySegment<byte>(buffer, 0, count));
        var root = SaveChunk.CreateSaveChunk(fbb, Id, (ulong)offset, data);
        fbb.Finish(root.Value);
        var bb = fbb.DataBuffer;
        return OutboundFrame.Create(MsgType.SaveChunk, Lane.Bulk, bb.ToArraySegment(bb.Position, fbb.Offset).AsSpan());
    }
}
