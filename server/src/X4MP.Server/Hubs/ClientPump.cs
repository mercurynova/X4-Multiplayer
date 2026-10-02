using System.Threading.Channels;

namespace X4MP.Server.Hubs;

/// <summary>
/// The outbound queue of one hub connection: a bounded list of pending sends and one task that runs them one at a time. A browser that
/// stops reading blocks only its own pump; <see cref="Post"/> never waits. A send tagged with a coalesce key replaces the pending send with
/// the same key (latest wins: frames, snapshots); an untagged send is an event and the oldest event is dropped when the queue is full.
/// When one send does not finish within <c>stallTimeout</c> the connection counts as stalled: <c>onStalled</c> runs (the hub aborts the
/// connection) and the pump stops.
/// </summary>
/// <typeparam name="TClient">The client proxy a send runs against (<c>IAdminClient</c> in the hub).</typeparam>
public sealed class ClientPump<TClient> : IAsyncDisposable
    where TClient : class
{
    private sealed class Entry(Func<TClient, Task> send, string? key)
    {
        public Func<TClient, Task> Send { get; set; } = send;

        public string? Key { get; } = key;
    }

    private readonly TClient _client;
    private readonly int _capacity;
    private readonly TimeSpan _stallTimeout;
    private readonly Action? _onStalled;
    private readonly Lock _gate = new();
    private readonly LinkedList<Entry> _pending = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byKey = new(StringComparer.Ordinal);
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private long _dropped;
    private long _coalesced;
    private long _sent;
    private bool _stalled;

    public ClientPump(TClient client, int capacity = 256, TimeSpan? stallTimeout = null, Action? onStalled = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _client = client;
        _capacity = capacity;
        _stallTimeout = stallTimeout ?? TimeSpan.FromSeconds(15);
        _onStalled = onStalled;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Sends waiting to run (events plus coalesced slots).</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Events dropped because the queue was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Sends replaced by a newer send with the same key.</summary>
    public long Coalesced => Interlocked.Read(ref _coalesced);

    /// <summary>Sends that completed (successfully or not).</summary>
    public long Sent => Interlocked.Read(ref _sent);

    /// <summary>True once a send exceeded the stall timeout.</summary>
    public bool Stalled => Volatile.Read(ref _stalled);

    /// <summary>Queues a send. Never blocks. Returns false when the pump stopped (stalled or disposed).</summary>
    public bool Post(Func<TClient, Task> send, string? coalesceKey = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        if (Stalled || _cts.IsCancellationRequested)
        {
            return false;
        }

        lock (_gate)
        {
            if (coalesceKey is not null && _byKey.TryGetValue(coalesceKey, out var existing))
            {
                existing.Value.Send = send;
                Interlocked.Increment(ref _coalesced);
            }
            else
            {
                if (_pending.Count >= _capacity)
                {
                    DropOldestEvent();
                }

                var node = _pending.AddLast(new Entry(send, coalesceKey));
                if (coalesceKey is not null)
                {
                    _byKey[coalesceKey] = node;
                }
            }
        }

        _signal.Writer.TryWrite(true);
        return true;
    }

    private void DropOldestEvent()
    {
        // Prefer dropping the oldest event; if every slot is a coalesced frame, drop the oldest of those.
        var victim = _pending.First;
        for (var node = _pending.First; node is not null; node = node.Next)
        {
            if (node.Value.Key is null)
            {
                victim = node;
                break;
            }
        }

        if (victim is null)
        {
            return;
        }

        if (victim.Value.Key is { } key)
        {
            _byKey.Remove(key);
        }

        _pending.Remove(victim);
        Interlocked.Increment(ref _dropped);
    }

    private bool TryTake(out Func<TClient, Task> send)
    {
        lock (_gate)
        {
            var first = _pending.First;
            if (first is null)
            {
                send = null!;
                return false;
            }

            _pending.RemoveFirst();
            if (first.Value.Key is { } key)
            {
                _byKey.Remove(key);
            }

            send = first.Value.Send;
            return true;
        }
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (await _signal.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                _signal.Reader.TryRead(out _);
                while (TryTake(out var send))
                {
                    try
                    {
                        await send(_client).WaitAsync(_stallTimeout, ct).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        Volatile.Write(ref _stalled, true);
                        lock (_gate)
                        {
                            _pending.Clear();
                            _byKey.Clear();
                        }

                        _onStalled?.Invoke();
                        return;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        // The connection is closing or the proxy failed: the hub's disconnect handler removes this client.
                    }
                    finally
                    {
                        Interlocked.Increment(ref _sent);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // disposed
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _signal.Writer.TryComplete();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        _cts.Dispose();
    }
}
