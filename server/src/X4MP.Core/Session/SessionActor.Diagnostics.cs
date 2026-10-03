using X4MP.Core.Diagnostics;
using X4MP.Core.Net;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Session;

public sealed partial class SessionActor
{
    private const double DropNoticeIntervalSeconds = 10;
    private long _lastDropNoticeTicks;

    /// <summary>What nodes forwarded with <c>LogForward</c> (set by the host). Without it the lines are discarded.</summary>
    public NodeDiagnosticsStore? Diagnostics { get; set; }

    /// <summary>
    /// A <c>LogForward</c> batch: rate-limited per node, kept as the node's last lines, written to the server log as <c>node:&lt;player&gt;</c>, and
    /// scanned for the self-test table (docs/mod-design.md 8.5.1). Runs on the actor thread.
    /// </summary>
    private void OnLogForward(SessionNode slot, InboundFrame frame)
    {
        if (Diagnostics is not { } store)
        {
            return;
        }

        var forward = MessageRegistry.Default.Decode<LogForward>(frame.Frame).UnPack();
        var lines = new List<(LogLevel Level, string Text)>(forward.Lines?.Count ?? 0);
        foreach (var l in forward.Lines ?? [])
        {
            lines.Add((l.Level, l.Text ?? string.Empty));
        }

        var result = store.Ingest(slot.PlayerId, slot.Name, lines, _time.GetUtcNow());
        foreach (var line in result.Accepted)
        {
            LogNodeLine(line.Level switch
            {
                LogLevel.Debug => Microsoft.Extensions.Logging.LogLevel.Debug,
                LogLevel.Warn => Microsoft.Extensions.Logging.LogLevel.Warning,
                LogLevel.Error => Microsoft.Extensions.Logging.LogLevel.Error,
                _ => Microsoft.Extensions.Logging.LogLevel.Information,
            }, slot.Name, line.Text);
        }

        long now = Now;
        if (result.Dropped > 0 && now - _lastDropNoticeTicks >= (long)(DropNoticeIntervalSeconds * _time.TimestampFrequency))
        {
            _lastDropNoticeTicks = now;
            LogNodeLogsDropped(slot.PlayerId, slot.Name, result.Dropped);
        }
    }

    [Microsoft.Extensions.Logging.LoggerMessage(Message = "node:{Player:l} {Text:l}")]
    private partial void LogNodeLine(Microsoft.Extensions.Logging.LogLevel level, string player, string text);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "player {PlayerId} ({Name}): forwarded log rate-limited, {Dropped} lines dropped in the last batch")]
    private partial void LogNodeLogsDropped(int playerId, string name, int dropped);
}
