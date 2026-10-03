using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using X4MP.Core.Diagnostics;
using X4MP.Server.Admin;

namespace X4MP.Server.Hubs;

/// <summary>
/// Pushes <c>NodeDiagnosticsChanged</c> to the dashboard topic when a node forwarded lines or a self-test table (task M2-13). The first change
/// goes out at once; further changes of the same player are coalesced into one push per <see cref="Interval"/> (the pump keeps only the latest
/// push per player anyway). Nothing is built while the topic has no subscribers.
/// </summary>
internal sealed partial class NodeDiagnosticsBroadcaster(AdminSubscriptions subs, NodeDiagnosticsStore store, TimeProvider time, ILogger<NodeDiagnosticsBroadcaster> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<int, bool> _dirty = new();
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.Changed += OnChanged;
        try
        {
            while (await _signal.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                _signal.Reader.TryRead(out _);
                Flush();
                await Task.Delay(Interval, time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        finally
        {
            store.Changed -= OnChanged;
        }
    }

    private void OnChanged(int playerId)
    {
        if (subs.Count(HubTopic.Dashboard) == 0)
        {
            return;
        }

        _dirty[playerId] = true;
        _signal.Writer.TryWrite(true);
    }

    private void Flush()
    {
        try
        {
            foreach (int id in _dirty.Keys.ToList())
            {
                _dirty.TryRemove(id, out _);
                var clients = subs.In(HubTopic.Dashboard).ToList();
                if (clients.Count == 0 || store.Get(id) is not { } snapshot)
                {
                    continue;
                }

                var dto = DiagnosticsMapping.ToDto(snapshot);
                foreach (var client in clients)
                {
                    client.Pump.Post(c => c.NodeDiagnosticsChanged(dto), "node-diag:" + DiagnosticsMapping.Invariant(id));
                }
            }
        }
        catch (Exception ex)
        {
            LogPushFailed(ex);
        }
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "pushing node diagnostics failed")]
    private partial void LogPushFailed(Exception ex);
}
