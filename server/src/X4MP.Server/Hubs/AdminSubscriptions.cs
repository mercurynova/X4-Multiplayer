using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Serilog.Events;

namespace X4MP.Server.Hubs;

/// <summary>The topics (SignalR-group equivalents) of the admin hub. Each has a subscriber count, so an empty topic costs nothing.</summary>
public enum HubTopic
{
    Dashboard,
    Galaxy,
    Logs,
    Diagnostics,
    Chat,
    Economy,
}

/// <summary>The server-side filter of one client's log tail.</summary>
internal sealed record LogTailFilter(LogEventLevel? Minimum, string? Source, string? Query)
{
    public static LogTailFilter None { get; } = new(null, null, null);
}

/// <summary>One connected admin browser: its outbound pump and what it subscribed to.</summary>
internal sealed class HubClient(string id, HubCallerContext context, ClientPump<IAdminClient> pump, bool isAdmin)
{
    private readonly Lock _gate = new();
    private readonly HashSet<HubTopic> _topics = [];
    private readonly HashSet<ushort> _sectors = [];
    private LogTailFilter _logs = LogTailFilter.None;

    public string Id { get; } = id;

    public HubCallerContext Context { get; } = context;

    public ClientPump<IAdminClient> Pump { get; } = pump;

    public bool IsAdmin { get; } = isAdmin;

    /// <summary>The highest log sequence number already sent to (or covered by the backfill of) this client.</summary>
    public long LogCursor { get; set; }

    public bool Join(HubTopic topic)
    {
        lock (_gate)
        {
            return _topics.Add(topic);
        }
    }

    public bool Leave(HubTopic topic)
    {
        lock (_gate)
        {
            return _topics.Remove(topic);
        }
    }

    public bool IsIn(HubTopic topic)
    {
        lock (_gate)
        {
            return _topics.Contains(topic);
        }
    }

    public IReadOnlyCollection<HubTopic> Topics()
    {
        lock (_gate)
        {
            return [.. _topics];
        }
    }

    public LogTailFilter LogFilter
    {
        get
        {
            lock (_gate)
            {
                return _logs;
            }
        }
        set
        {
            lock (_gate)
            {
                _logs = value;
            }
        }
    }

    public IReadOnlyCollection<ushort> Sectors()
    {
        lock (_gate)
        {
            return [.. _sectors];
        }
    }

    public int SectorCount
    {
        get
        {
            lock (_gate)
            {
                return _sectors.Count;
            }
        }
    }

    /// <summary>Adds a sector view; false when already present or the limit is reached (<paramref name="limitHit"/>).</summary>
    public bool AddSector(ushort sector, int max, out bool limitHit)
    {
        lock (_gate)
        {
            limitHit = false;
            if (_sectors.Contains(sector))
            {
                return false;
            }

            if (_sectors.Count >= max)
            {
                limitHit = true;
                return false;
            }

            _sectors.Add(sector);
            return true;
        }
    }

    public bool RemoveSector(ushort sector)
    {
        lock (_gate)
        {
            return _sectors.Remove(sector);
        }
    }
}

/// <summary>
/// Who is connected and what each connection subscribed to. The broadcaster asks <see cref="Count"/> before it builds anything, so a topic
/// without subscribers costs nothing. Counts are kept with interlocked updates; the connection table is a concurrent dictionary.
/// </summary>
public sealed class AdminSubscriptions
{
    private readonly ConcurrentDictionary<string, HubClient> _clients = new(StringComparer.Ordinal);
    private readonly int[] _counts = new int[Enum.GetValues<HubTopic>().Length];
    private int _sectorViews;

    /// <summary>Connected hub clients.</summary>
    public int ConnectionCount => _clients.Count;

    /// <summary>Subscribers of <paramref name="topic"/>.</summary>
    public int Count(HubTopic topic) => Volatile.Read(ref _counts[(int)topic]);

    /// <summary>Sector views held by all clients together (a sector viewed twice counts twice).</summary>
    public int SectorViewCount => Volatile.Read(ref _sectorViews);

    /// <summary>The distinct sectors some client views.</summary>
    public IReadOnlySet<ushort> ViewedSectors() => _clients.Values.SelectMany(c => c.Sectors()).ToHashSet();

    internal IEnumerable<HubClient> Clients => _clients.Values;

    internal HubClient? Find(string connectionId) => _clients.GetValueOrDefault(connectionId);

    internal void Add(HubClient client) => _clients[client.Id] = client;

    /// <summary>Removes a connection and its subscriptions; returns it (null when unknown).</summary>
    internal HubClient? Remove(string connectionId)
    {
        if (!_clients.TryRemove(connectionId, out var client))
        {
            return null;
        }

        foreach (var topic in client.Topics())
        {
            Leave(client, topic);
        }

        foreach (var sector in client.Sectors())
        {
            if (client.RemoveSector(sector))
            {
                Interlocked.Decrement(ref _sectorViews);
            }
        }

        return client;
    }

    internal IEnumerable<HubClient> In(HubTopic topic) => Count(topic) == 0 ? [] : _clients.Values.Where(c => c.IsIn(topic));

    internal bool Join(HubClient client, HubTopic topic)
    {
        if (!client.Join(topic))
        {
            return false;
        }

        Interlocked.Increment(ref _counts[(int)topic]);
        return true;
    }

    internal bool Leave(HubClient client, HubTopic topic)
    {
        if (!client.Leave(topic))
        {
            return false;
        }

        Interlocked.Decrement(ref _counts[(int)topic]);
        return true;
    }

    internal void SectorAdded() => Interlocked.Increment(ref _sectorViews);

    internal void SectorRemoved() => Interlocked.Decrement(ref _sectorViews);
}
