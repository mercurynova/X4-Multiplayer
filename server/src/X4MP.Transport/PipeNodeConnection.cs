using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using X4MP.Core.Metrics;
using X4MP.Core.Net;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Transport;

/// <summary>
/// <see cref="INodeConnection"/> over any <see cref="IDuplexPipe"/> (Kestrel's connection transport for TCP,
/// paired <see cref="Pipe"/>s for InProc). One writer loop drains the <see cref="SendQueue"/>; the reader is
/// driven by whoever calls <see cref="ReadAsync"/>. Frame headers are validated before any payload buffer
/// is allocated.
/// </summary>
public sealed class PipeNodeConnection : INodeConnection, IDisposable
{
    private const int StateOpen = 0;
    private const int StateClosing = 1;
    private const int StateClosed = 2;

    private readonly IDuplexPipe _pipe;
    private readonly NetOptions _options;
    private readonly TimeProvider _time;
    private readonly SendQueue _queue;
    private readonly CancellationTokenSource _closedCts = new();
    private readonly CancellationTokenSource _abortCts = new();
    private readonly CancellationToken _closedToken;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ITimer _slowTimer;
    private readonly Action<PipeNodeConnection>? _onClosed;
    private int _state;
    private int _reading;
    private int _finished;
    private int _inputCompleted;
    private volatile bool _readFaulted;
    private volatile IDatagramPath? _datagram;
    private int _maxInboundFrameBytes;
    private readonly byte[] _headerBuffer = new byte[FrameCodec.HeaderSize]; // single reader

    public PipeNodeConnection(
        IDuplexPipe pipe,
        EndPoint remoteEndPoint,
        NetOptions options,
        TimeProvider? time = null,
        Action<PipeNodeConnection>? onClosed = null)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ArgumentNullException.ThrowIfNull(options);

        _pipe = pipe;
        RemoteEndPoint = remoteEndPoint;
        _options = options;
        _time = time ?? TimeProvider.System;
        _onClosed = onClosed;
        _closedToken = _closedCts.Token;
        Id = ConnectionId.Next();
        Stats = new ConnectionStats();
        _maxInboundFrameBytes = options.MaxFrameBytes;
        _queue = new SendQueue(SendQueueOptions.From(options), _time, Stats, OnOverflow);
        _slowTimer = _time.CreateTimer(static s => ((SendQueue)s!).CheckSlowConsumer(), _queue, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        ServerMetrics.TrackConnection(this);
        _ = RunWriterAsync();
    }

    public ConnectionId Id { get; }

    public EndPoint RemoteEndPoint { get; }

    public ConnectionStats Stats { get; }

    public CancellationToken Closed => _closedToken;

    public Task Completion => _completion.Task;

    public int MaxInboundFrameBytes
    {
        get => Volatile.Read(ref _maxInboundFrameBytes);
        set => Volatile.Write(ref _maxInboundFrameBytes, value);
    }

    public bool CanAcceptRealtime => _queue.CanAcceptRealtime;

    public bool ControlOverSoftCap => _queue.ControlOverSoftCap;

    public long QueuedBytes(Lane lane) => _queue.PendingBytes(lane);

    public void AttachDatagramPath(IDatagramPath path) => _datagram = path;

    public SendResult TrySend(OutboundFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var datagram = _datagram;
        if (datagram is not null && frame.Lane == Lane.Realtime && datagram.TrySend(frame))
        {
            return SendResult.Queued;
        }

        return _queue.TrySend(frame);
    }

    private void OnOverflow(DisconnectCode code) => Close(code, "send queue overflow");

    public void Close(DisconnectCode reason, string? detail = null, string? expected = null, uint retryAfterMs = 0)
    {
        if (Interlocked.CompareExchange(ref _state, StateClosing, StateOpen) != StateOpen)
        {
            return;
        }

        ServerMetrics.RecordDisconnect(reason);
        try
        {
            var final = ControlFrames.Disconnect(reason, detail, expected, retryAfterMs);
            _queue.Complete(discardControl: reason == DisconnectCode.SlowConsumer, final);
            final.Release();
            _abortCts.CancelAfter(_options.CloseFlushTimeoutMs);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            Abort();
            return;
        }

        SignalClosed();
    }

    /// <summary>Immediate close: pending frames are discarded and no Disconnect is sent.</summary>
    public void Abort()
    {
        Interlocked.Exchange(ref _state, StateClosed);
        _queue.Complete(discardControl: true);
        try
        {
            _abortCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already torn down
        }

        SignalClosed();
    }

    private void SignalClosed()
    {
        try
        {
            _closedCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            _pipe.Input.CancelPendingRead();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // input already completed
        }
    }

    private async Task RunWriterAsync()
    {
        try
        {
            await ConnectionWriter.RunAsync(_queue, _pipe.Output, Stats, _options.WriterBatchBytes, null, _abortCts.Token).ConfigureAwait(false);
        }
        finally
        {
            Finish();
        }
    }

    private void Finish()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _state, StateClosed);
        SignalClosed();
        _slowTimer.Dispose();
        try
        {
            _pipe.Output.Complete();
        }
        catch (InvalidOperationException)
        {
            // already completed
        }

        TryCompleteInput();
        ServerMetrics.UntrackConnection(this);
        _completion.TrySetResult();
        _onClosed?.Invoke(this);
    }

    /// <summary>Completes the input side once no read is in progress.</summary>
    private void TryCompleteInput()
    {
        if (Volatile.Read(ref _finished) == 0 || Volatile.Read(ref _reading) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _inputCompleted, 1) == 0)
        {
            try
            {
                _pipe.Input.Complete();
            }
            catch (InvalidOperationException)
            {
                // a read is still unwinding; the pipe is dropped with the connection
            }
        }
    }

    public async ValueTask<InboundFrame?> ReadAsync(CancellationToken ct)
    {
        if (_readFaulted || Volatile.Read(ref _finished) != 0)
        {
            return null;
        }

        Interlocked.Increment(ref _reading);
        try
        {
            return await ReadCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _reading);
            TryCompleteInput();
        }
    }

    private async ValueTask<InboundFrame?> ReadCoreAsync(CancellationToken ct)
    {
        var reader = _pipe.Input;
        while (true)
        {
            if (Volatile.Read(ref _state) != StateOpen)
            {
                return null;
            }

            ReadResult result;
            try
            {
                result = await reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Abort(); // connection reset
                return null;
            }

            var buffer = result.Buffer;
            if (result.IsCanceled)
            {
                reader.AdvanceTo(buffer.Start);
                if (Volatile.Read(ref _state) != StateOpen)
                {
                    return null;
                }

                continue;
            }

            if (buffer.Length >= FrameCodec.HeaderSize)
            {
                buffer.Slice(0, FrameCodec.HeaderSize).CopyTo(_headerBuffer);
                FrameHeader parsed;
                try
                {
                    parsed = FrameCodec.ParseHeader(_headerBuffer, MaxInboundFrameBytes);
                }
                catch (ProtocolViolation)
                {
                    _readFaulted = true;
                    reader.AdvanceTo(buffer.End);
                    throw;
                }

                long total = FrameCodec.HeaderSize + (long)parsed.PayloadLength;
                if (buffer.Length >= total)
                {
                    var payload = new byte[parsed.PayloadLength];
                    buffer.Slice(FrameCodec.HeaderSize, parsed.PayloadLength).CopyTo(payload);
                    reader.AdvanceTo(buffer.GetPosition(total));
                    Stats.AddReceived((int)total, parsed.Lane);
                    return new InboundFrame(new Frame(parsed.Type, parsed.Flags, parsed.Lane, payload), _time.GetTimestamp());
                }
            }

            if (result.IsCompleted)
            {
                long left = buffer.Length;
                reader.AdvanceTo(buffer.End);
                if (left == 0)
                {
                    Abort(); // peer closed: nothing more to deliver either way
                    return null;
                }

                _readFaulted = true;
                throw new ProtocolViolation(ViolationCode.TruncatedFrame, $"stream ended with {left} bytes of a partial frame");
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Abort();
        await _completion.Task.ConfigureAwait(false);
        Dispose();
    }

    public void Dispose()
    {
        _closedCts.Dispose();
        _abortCts.Dispose();
    }
}
