using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using X4MP.Core.Events;
using X4MP.Core.Metrics;

namespace X4MP.Persistence;

/// <summary>
/// Event bus subscriber that writes domain events into <c>session_events</c> through the write-behind
/// <see cref="PersistenceWriter"/> (server-design 2.7/2.8). It never blocks: when the writer queue is full the
/// event is dropped and counted (<see cref="Dropped"/>), as the design requires. <see cref="NodeStatsReported"/> is
/// skipped (telemetry every 2 s per node belongs in metrics, not in the event log).
/// </summary>
public sealed class SessionEventSink(PersistenceWriter writer)
{
    private const string Sql =
        "INSERT INTO session_events (session_id, ts, type, player_id, sector_id, data_json) " +
        "VALUES (@session, @ts, @type, @player, @sector, @data)";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private long _dropped;

    /// <summary>Events lost because the persistence queue was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public ValueTask HandleAsync(DomainEvent domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent is NodeStatsReported)
        {
            return ValueTask.CompletedTask;
        }

        var parameters = new
        {
            session = domainEvent.Session ?? 0,
            ts = domainEvent.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            type = domainEvent.GetType().Name,
            player = (domainEvent as IPlayerScoped)?.PlayerId,
            sector = (domainEvent as ISectorScoped)?.SectorId,
            data = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), JsonOptions),
        };
        if (!writer.TryEnqueue(Sql, parameters))
        {
            Interlocked.Increment(ref _dropped);
            ServerMetrics.RecordEventDropped("persistence-writer");
        }

        return ValueTask.CompletedTask;
    }
}
