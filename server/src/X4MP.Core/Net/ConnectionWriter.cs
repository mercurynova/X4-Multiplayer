using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;

namespace X4MP.Core.Net;

/// <summary>
/// The per-connection writer loop (server-design 2.3). Drains a <see cref="SendQueue"/> in strict priority
/// order into a <see cref="PipeWriter"/> and flushes. It is the only code that awaits a flush, so a slow
/// or non-reading peer stalls this loop and nothing else: producers keep calling
/// <see cref="SendQueue.TrySend"/>, which never blocks.
/// </summary>
public static class ConnectionWriter
{
    /// <summary>
    /// Runs until the queue is sealed and drained, the pipe reports completion, or
    /// <paramref name="ct"/> fires. Frames still held when it stops are released. Never throws for I/O
    /// errors (a reset connection simply ends the loop).
    /// </summary>
    /// <param name="queue">Source queue.</param>
    /// <param name="output">Destination pipe writer.</param>
    /// <param name="stats">Counters to update.</param>
    /// <param name="batchBytes">Bytes copied into the pipe before each flush.</param>
    /// <param name="flushed">Called for each frame after the flush that carried it completed (baseline advance for TCP deltas). May be null.</param>
    /// <param name="ct">Aborts the loop, including a pending flush.</param>
    public static async Task RunAsync(
        SendQueue queue,
        PipeWriter output,
        ConnectionStats stats,
        int batchBytes,
        Action<OutboundFrame>? flushed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(stats);

        var inFlight = new List<OutboundFrame>(64);
        try
        {
            while (await queue.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                int batch = 0;
                while (batch < batchBytes && queue.TryDequeue(out var frame))
                {
                    inFlight.Add(frame);
                    output.Write(frame.Bytes.Span);
                    batch += frame.Length;
                }

                long started = Stopwatch.GetTimestamp();
                FlushResult result = await output.FlushAsync(ct).ConfigureAwait(false);
                stats.AddFlush(Stopwatch.GetTimestamp() - started);

                foreach (var frame in inFlight)
                {
                    stats.AddSent(frame.Length, frame.Lane);
                    flushed?.Invoke(frame);
                    frame.Release();
                }

                inFlight.Clear();
                if (result.IsCompleted || result.IsCanceled)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // aborted
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // peer reset or pipe already completed
        }
        finally
        {
            foreach (var frame in inFlight)
            {
                frame.Release();
            }

            queue.Complete(discardControl: true);
            while (queue.TryDequeue(out var left))
            {
                left.Release();
            }
        }
    }
}
