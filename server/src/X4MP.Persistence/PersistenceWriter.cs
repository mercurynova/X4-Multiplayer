using System.Data;
using System.Diagnostics;
using System.Threading.Channels;
using Dapper;
using Microsoft.Data.Sqlite;

namespace X4MP.Persistence;

/// <summary>Result of one committed write-behind batch.</summary>
public readonly record struct BatchCommitted(int ItemCount, TimeSpan Duration);

/// <summary>
/// Write-behind queue (server-design 2.8). Producers enqueue SQL (or a callback) without blocking on
/// the database; one background loop owns the write connection and commits batches of up to
/// <see cref="PersistenceOptions.MaxBatchItems"/> items, or whatever arrived within
/// <see cref="PersistenceOptions.FlushInterval"/>, in a single transaction. Disposing completes the
/// queue and flushes everything still pending.
/// </summary>
public sealed class PersistenceWriter : IAsyncDisposable
{
    private abstract class Item;

    private sealed class WriteItem(Action<SqliteConnection, SqliteTransaction> action) : Item
    {
        public Action<SqliteConnection, SqliteTransaction> Action { get; } = action;
    }

    private sealed class FlushMarker : Item
    {
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Channel<Item> _channel;
    private readonly SqliteConnection _connection;
    private readonly int _maxBatchItems;
    private readonly TimeSpan _flushInterval;
    private readonly TimeSpan _disposeTimeout;
    private readonly Task _loop;
    private int _disposed;

    public PersistenceWriter(SqliteConnectionFactory factory, PersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxBatchItems, 1);

        _maxBatchItems = options.MaxBatchItems;
        _flushInterval = options.FlushInterval;
        _disposeTimeout = options.DisposeTimeout;
        _channel = Channel.CreateBounded<Item>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _connection = factory.Open();
        _loop = Task.Factory.StartNew(
            RunAsync, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Receives a one-line warning when something goes wrong outside a caller's reach (the shutdown flush timing out). The server wires it to its log.</summary>
    public Action<string>? OnWarning { get; set; }

    /// <summary>Raised after each batch commits (also for batches retried item-by-item).</summary>
    public event Action<BatchCommitted>? BatchCommitted;

    /// <summary>Raised when an individual write fails and is dropped; the rest of its batch still commits.</summary>
    public event Action<Exception>? WriteFailed;

    /// <summary>Queues a SQL statement. Returns false if the queue is full or the writer is disposed.</summary>
    public bool TryEnqueue(string sql, object? parameters = null) =>
        _channel.Writer.TryWrite(CreateSqlItem(sql, parameters));

    /// <summary>Queues arbitrary work (several statements, one transaction share). Returns false if the queue is full or the writer is disposed.</summary>
    public bool TryEnqueue(Action<SqliteConnection, SqliteTransaction> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _channel.Writer.TryWrite(new WriteItem(action));
    }

    /// <summary>Queues a SQL statement, waiting while the queue is full.</summary>
    public ValueTask EnqueueAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(CreateSqlItem(sql, parameters), cancellationToken);

    /// <summary>Queues arbitrary work to run on the write connection inside the batch transaction.</summary>
    public ValueTask EnqueueAsync(
        Action<SqliteConnection, SqliteTransaction> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _channel.Writer.WriteAsync(new WriteItem(action), cancellationToken);
    }

    /// <summary>Completes when everything queued before this call has been committed.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var marker = new FlushMarker();
        await _channel.Writer.WriteAsync(marker, cancellationToken).ConfigureAwait(false);
        await marker.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        try
        {
            await _loop.WaitAsync(_disposeTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The loop is stuck (a locked database, a hung disk): give up so shutdown ends. The connection is left open because the loop may still use it.
            OnWarning?.Invoke($"persistence writer did not finish within {_disposeTimeout.TotalSeconds:F0} s at shutdown; {_channel.Reader.Count} queued writes may be lost");
            return;
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private static WriteItem CreateSqlItem(string sql, object? parameters)
    {
        ArgumentException.ThrowIfNullOrEmpty(sql);
        return new WriteItem((connection, transaction) => connection.Execute(sql, parameters, transaction));
    }

    private async Task RunAsync()
    {
        var reader = _channel.Reader;
        var batch = new List<Item>(_maxBatchItems);

        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            var deadline = Stopwatch.GetTimestamp() + (long)(_flushInterval.TotalSeconds * Stopwatch.Frequency);
            var flushNow = false;

            while (batch.Count < _maxBatchItems && !flushNow)
            {
                if (reader.TryRead(out var item))
                {
                    batch.Add(item);
                    flushNow = item is FlushMarker;
                    continue;
                }

                var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                using var timeout = new CancellationTokenSource(remaining);
                try
                {
                    if (!await reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false))
                    {
                        break; // completed: flush what we have, then the outer loop ends
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            Commit(batch);
        }
    }

    private void Commit(List<Item> batch)
    {
        var writes = batch.OfType<WriteItem>().ToList();
        if (writes.Count > 0)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                RunInTransaction(writes);
            }
            catch (Exception)
            {
                // Isolate the poison item: retry each write on its own, report the ones that fail.
                foreach (var write in writes)
                {
                    try
                    {
                        RunInTransaction([write]);
                    }
                    catch (Exception ex)
                    {
                        WriteFailed?.Invoke(ex);
                    }
                }
            }

            BatchCommitted?.Invoke(new BatchCommitted(writes.Count, Stopwatch.GetElapsedTime(started)));
        }

        foreach (var marker in batch.OfType<FlushMarker>())
        {
            marker.Completion.TrySetResult();
        }
    }

    private void RunInTransaction(List<WriteItem> writes)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            foreach (var write in writes)
            {
                write.Action(_connection, transaction);
            }
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }
}
