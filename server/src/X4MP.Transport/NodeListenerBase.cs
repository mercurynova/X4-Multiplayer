using System.IO.Pipelines;
using System.Net;
using System.Threading.Channels;
using X4MP.Core.Net;

namespace X4MP.Transport;

/// <summary>
/// Shared plumbing of the listeners: transports hand over connections, <see cref="AcceptAsync"/> yields
/// them. Counts live connections so tests (and diagnostics) can prove nothing leaks.
/// </summary>
public abstract class NodeListenerBase : INodeListener
{
    private readonly Channel<INodeConnection> _accepted = Channel.CreateBounded<INodeConnection>(
        new BoundedChannelOptions(1024) { SingleReader = false, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly NetOptions _options;
    private readonly TimeProvider _time;
    private int _active;
    private long _total;

    protected NodeListenerBase(NetOptions options, TimeProvider? time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    public abstract string Name { get; }

    /// <summary>Connections created and not yet fully closed.</summary>
    public int ActiveConnections => Volatile.Read(ref _active);

    /// <summary>Connections created since start.</summary>
    public long TotalConnections => Interlocked.Read(ref _total);

    public IAsyncEnumerable<INodeConnection> AcceptAsync(CancellationToken ct) => _accepted.Reader.ReadAllAsync(ct);

    /// <summary>Wraps a transport pipe in a <see cref="PipeNodeConnection"/> that is counted until it closes.</summary>
    protected PipeNodeConnection CreateConnection(IDuplexPipe pipe, EndPoint remote)
    {
        Interlocked.Increment(ref _active);
        Interlocked.Increment(ref _total);
        return new PipeNodeConnection(pipe, remote, _options, _time, _ => Interlocked.Decrement(ref _active));
    }

    /// <summary>Queues a connection for <see cref="AcceptAsync"/>. False if the backlog is full or the listener is disposed.</summary>
    protected bool Offer(INodeConnection connection) => _accepted.Writer.TryWrite(connection);

    public virtual async ValueTask DisposeAsync()
    {
        _accepted.Writer.TryComplete();
        while (_accepted.Reader.TryRead(out var pending))
        {
            await pending.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }
}
