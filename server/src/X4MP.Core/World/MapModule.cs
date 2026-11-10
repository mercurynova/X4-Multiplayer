using Google.FlatBuffers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.World;

/// <summary>
/// The live strategic map as a session module (T1.6): the authority polls its universe and sends <c>KnownSectors</c>;
/// this module validates it, keeps the latest map, forwards the cached <c>GalaxyMetadata</c> to every node and fans out
/// <c>MapSnapshot</c>/<c>MapDelta</c>. The map is deliberately separate from <see cref="WorldMirror"/>'s tactical state:
/// a client learns every sector's owner without visiting it.
/// </summary>
public sealed partial class MapModule : ISessionModule
{
    private readonly WorldMirror _world;
    private readonly ILogger _logger;
    private readonly Dictionary<int, SessionNode> _nodes = [];
    private readonly Dictionary<ushort, MapSectorT> _sectors = [];
    private readonly Dictionary<(ushort From, ushort To), MapLinkT> _links = [];
    private ulong _revision;
    private ulong _version;
    private bool _hasMap;

    public MapModule(WorldMirror world, ILogger<MapModule>? logger = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _world.Galaxy.CurrentChanged += OnGalaxyChanged;
    }

    /// <summary>The number of sectors in the latest accepted map (diagnostics and tests).</summary>
    public int SectorCount => _sectors.Count;

    /// <summary>The latest version sent to nodes (diagnostics and tests).</summary>
    public ulong Version => _version;

    public void OnNodeAttached(SessionNode node, bool resumed)
    {
        ArgumentNullException.ThrowIfNull(node);
        _nodes[node.PlayerId] = node;
        LogAttach(node.PlayerId, node.IsAuthority, resumed, _hasMap, _sectors.Count);

        // A fresh authority connection restarts its local revision at 1; the server must not mistake the first map for stale.
        if (node.IsAuthority)
        {
            _revision = 0;
        }

        if (_world.Galaxy.Current is { } galaxy)
        {
            SendGalaxy(node, galaxy.Payload);
        }

        if (_hasMap)
        {
            SendSnapshot(node);
        }
    }

    public void OnNodeLeft(SessionNode node, string reason)
    {
        ArgumentNullException.ThrowIfNull(node);
        _nodes.Remove(node.PlayerId);
    }

    public void OnSessionPhaseChanged(SessionPhase previous, SessionPhase current)
    {
        if (current == SessionPhase.Ended)
        {
            Clear();
        }
    }

    public bool OnMessage(SessionNode node, InboundFrame frame)
    {
        if (frame.Type != MsgType.KnownSectors)
        {
            return false;
        }

        // MessagePolicy already restricts this to the authority; the guard keeps a future policy change from letting a
        // client rewrite the shared map.
        if (!node.IsAuthority)
        {
            return true;
        }

        KnownSectorsT message;
        try
        {
            message = MessageRegistry.Default.Decode<KnownSectors>(frame.Frame).UnPack();
        }
        catch (ProtocolViolation ex)
        {
            LogInvalidFrame(ex.Code.ToString());
            return true;
        }

        Apply(node, message);
        return true;
    }

    /// <summary>Forgets the map (the session ended); the next authority map is sent as a snapshot.</summary>
    public void Clear()
    {
        _sectors.Clear();
        _links.Clear();
        _hasMap = false;
        _revision = 0;
        _version = 0;
    }

    private void Apply(SessionNode node, KnownSectorsT message)
    {
        if (_hasMap && message.Revision <= _revision)
        {
            return; // a delayed map from the previous authority connection
        }

        LogKnown(node.PlayerId, message.Revision, message.Sectors.Count, message.Links.Count);

        var sectors = new Dictionary<ushort, MapSectorT>();
        foreach (var sector in message.Sectors)
        {
            if (sector is null || sector.Index == 0 || !sectors.TryAdd(sector.Index, sector))
            {
                LogInvalidMap("zero or repeated sector index");
                return;
            }
        }

        if (sectors.Count == 0)
        {
            LogEmptyMap();
            return;
        }

        var links = new Dictionary<(ushort From, ushort To), MapLinkT>();
        foreach (var link in message.Links)
        {
            if (link is null)
            {
                continue;
            }

            (ushort from, ushort to) = link.From < link.To ? (link.From, link.To) : (link.To, link.From);
            if (from == to || !sectors.ContainsKey(from) || !sectors.ContainsKey(to))
            {
                LogInvalidMap("link to an unknown sector");
                return;
            }

            links[(from, to)] = new MapLinkT { From = from, To = to, Kind = link.Kind };
        }

        bool changed = !_hasMap || sectors.Count != _sectors.Count || links.Count != _links.Count;
        if (!changed)
        {
            foreach (var (index, sector) in sectors)
            {
                if (!_sectors.TryGetValue(index, out var previous) || !Same(previous, sector))
                {
                    changed = true;
                    break;
                }
            }
        }

        if (!changed)
        {
            foreach (var (key, link) in links)
            {
                if (!_links.TryGetValue(key, out var previous) || !Same(previous, link))
                {
                    changed = true;
                    break;
                }
            }
        }

        _revision = message.Revision;
        if (!changed)
        {
            return;
        }

        var removed = _sectors.Keys.Where(index => !sectors.ContainsKey(index)).Order().ToArray();
        var upserts = sectors.Values
            .Where(sector => !_sectors.TryGetValue(sector.Index, out var previous) || !Same(previous, sector))
            .OrderBy(sector => sector.Index)
            .ToList();
        var linkUpserts = links
            .Where(pair => !_links.TryGetValue(pair.Key, out var previous) || !Same(previous, pair.Value))
            .Select(pair => pair.Value)
            .OrderBy(link => link.From)
            .ThenBy(link => link.To)
            .ToList();

        // A delta can remove sectors (and their links) but cannot remove a single link between two live sectors.
        bool linkRemovedBetweenLive = _links.Keys.Any(key =>
            !links.ContainsKey(key) && sectors.ContainsKey(key.From) && sectors.ContainsKey(key.To));
        bool snapshot = !_hasMap || linkRemovedBetweenLive;

        _sectors.Clear();
        foreach (var (index, sector) in sectors)
        {
            _sectors[index] = sector;
        }

        _links.Clear();
        foreach (var (key, link) in links)
        {
            _links[key] = link;
        }

        _hasMap = true;
        _version++;

        if (snapshot)
        {
            BroadcastSnapshot();
            LogSnapshot(_version, _sectors.Count, _links.Count);
        }
        else
        {
            BroadcastDelta(upserts, linkUpserts, removed);
            LogDelta(_version, upserts.Count, linkUpserts.Count, removed.Length);
        }
    }

    private void OnGalaxyChanged(GalaxyModel galaxy)
    {
        foreach (var node in _nodes.Values)
        {
            SendGalaxy(node, galaxy.Payload);
        }
    }

    private void SendGalaxy(SessionNode node, byte[] payload)
    {
        if (node.Connection is null || payload.Length == 0)
        {
            return;
        }

        try
        {
            node.Connection.TrySend(OutboundFrame.Create(MsgType.GalaxyMetadata, payload));
        }
        catch (ArgumentException ex)
        {
            LogGalaxyTooLarge(ex);
        }
    }

    private void SendSnapshot(SessionNode node)
    {
        var snapshot = new MapSnapshotT
        {
            Version = _version,
            Sectors = [.. _sectors.Values.OrderBy(sector => sector.Index)],
            Links = [.. _links.Values.OrderBy(link => link.From).ThenBy(link => link.To)],
        };
        Send(node, MsgType.MapSnapshot, fbb => MapSnapshot.Pack(fbb, snapshot).Value, EstimateSize(snapshot.Sectors.Count, snapshot.Links.Count));
    }

    private void BroadcastSnapshot()
    {
        foreach (var node in _nodes.Values)
        {
            SendSnapshot(node);
        }
    }

    private void BroadcastDelta(List<MapSectorT> upserts, List<MapLinkT> links, ushort[] removed)
    {
        var delta = new MapDeltaT
        {
            Version = _version,
            Sectors = upserts,
            Links = links,
            RemovedSectors = [.. removed],
        };
        foreach (var node in _nodes.Values)
        {
            Send(node, MsgType.MapDelta, fbb => MapDelta.Pack(fbb, delta).Value, EstimateSize(upserts.Count, links.Count));
        }
    }

    private static void Send(SessionNode node, MsgType type, Func<FlatBufferBuilder, int> pack, int initialSize)
    {
        node.Connection?.TrySend(ControlFrames.Encode(type, pack, initialSize));
    }

    private static int EstimateSize(int sectors, int links) => Math.Max(256, 64 + (sectors * 128) + (links * 24));

    private static bool Same(MapSectorT a, MapSectorT b) =>
        a.Index == b.Index
        && string.Equals(a.Macro, b.Macro, StringComparison.Ordinal)
        && string.Equals(a.ClusterMacro, b.ClusterMacro, StringComparison.Ordinal)
        && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
        && string.Equals(a.Owner, b.Owner, StringComparison.Ordinal)
        && a.GalaxyPos?.X == b.GalaxyPos?.X
        && a.GalaxyPos?.Y == b.GalaxyPos?.Y
        && a.GalaxyPos?.Z == b.GalaxyPos?.Z;

    private static bool Same(MapLinkT a, MapLinkT b) => a.From == b.From && a.To == b.To && a.Kind == b.Kind;

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "map: node {PlayerId} attached (authority {Authority}, resumed {Resumed}); map {HasMap} with {Sectors} sectors")]
    private partial void LogAttach(int playerId, bool authority, bool resumed, bool hasMap, int sectors);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "map: KnownSectors from node {PlayerId}: revision {Revision}, {Sectors} sectors, {Links} links")]
    private partial void LogKnown(int playerId, ulong revision, int sectors, int links);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "map: authority sent an invalid KnownSectors frame ({Code})")]
    private partial void LogInvalidFrame(string code);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "map: authority map ignored: {Reason}")]
    private partial void LogInvalidMap(string reason);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "map: authority sent an empty map; keeping the previous one")]
    private partial void LogEmptyMap();

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "map: snapshot version {Version} ({Sectors} sectors, {Links} links)")]
    private partial void LogSnapshot(ulong version, int sectors, int links);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "map: delta version {Version} ({Upserts} sector upserts, {Links} link upserts, {Removed} removed)")]
    private partial void LogDelta(ulong version, int upserts, int links, int removed);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "map: GalaxyMetadata does not fit in a frame")]
    private partial void LogGalaxyTooLarge(Exception ex);
}
