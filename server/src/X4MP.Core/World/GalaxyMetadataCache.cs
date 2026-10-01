using System.Numerics;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.World;

/// <summary>One sector of the galaxy (<c>SectorInfo</c>).</summary>
public sealed record GalaxySector(ushort Index, string Macro, string ClusterMacro, string Name, uint OwnerRef, Vector3 GalaxyPos);

/// <summary>
/// The parsed <c>GalaxyMetadata</c> of one save (protocol.md 5): the sector table, the link list and the
/// <see cref="SectorGraph"/>. <see cref="Payload"/> is the original message body, kept so the server can forward it to joining
/// nodes (and cache it) without re-encoding.
/// </summary>
public sealed class GalaxyModel
{
    internal GalaxyModel(string saveShaHex, byte[] saveSha256, byte[] payload, GalaxySector[] sectors, SectorEdge[] links, SectorGraph graph)
    {
        SaveSha256Hex = saveShaHex;
        SaveSha256 = saveSha256;
        Payload = payload;
        Sectors = sectors;
        Links = links;
        Graph = graph;
    }

    public string SaveSha256Hex { get; }

    public byte[] SaveSha256 { get; }

    /// <summary>The <c>GalaxyMetadata</c> FlatBuffers payload as received.</summary>
    public byte[] Payload { get; }

    /// <summary>Sectors ordered by index.</summary>
    public IReadOnlyList<GalaxySector> Sectors { get; }

    public IReadOnlyList<SectorEdge> Links { get; }

    public SectorGraph Graph { get; }

    public GalaxySector? Find(ushort index)
    {
        int lo = 0;
        int hi = Sectors.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            ushort value = Sectors[mid].Index;
            if (value == index)
            {
                return Sectors[mid];
            }

            if (value < index)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return null;
    }

    /// <summary>Parses and validates a <c>GalaxyMetadata</c> payload; null with a reason when it is unusable.</summary>
    public static GalaxyModel? TryParse(byte[] payload, out string? error)
    {
        ArgumentNullException.ThrowIfNull(payload);
        GalaxyMetadata message;
        try
        {
            message = MessageRegistry.Default.Decode<GalaxyMetadata>(new Frame(MsgType.GalaxyMetadata, FrameOptions.None, Lane.Control, payload));
        }
        catch (ProtocolViolation ex)
        {
            error = ex.Code.ToString();
            return null;
        }

        var sha = message.GetSaveSha256Array();
        if (sha is not { Length: 32 })
        {
            error = "save_sha256 must be 32 bytes";
            return null;
        }

        var sectors = new List<GalaxySector>(message.SectorsLength);
        var seen = new HashSet<ushort>();
        for (int i = 0; i < message.SectorsLength; i++)
        {
            if (message.Sectors(i) is not { } info)
            {
                continue;
            }

            if (info.Index == 0 || !seen.Add(info.Index))
            {
                error = $"sector index {info.Index} is zero or repeated";
                return null;
            }

            var pos = info.GalaxyPos;
            sectors.Add(new GalaxySector(
                info.Index, info.Macro ?? string.Empty, info.ClusterMacro ?? string.Empty, info.Name ?? string.Empty, info.OwnerRef,
                pos is { } p ? new Vector3(p.X, p.Y, p.Z) : Vector3.Zero));
        }

        if (sectors.Count == 0)
        {
            error = "no sectors";
            return null;
        }

        sectors.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        var links = new List<SectorEdge>(message.LinksLength);
        for (int i = 0; i < message.LinksLength; i++)
        {
            if (message.Links(i) is { } link && seen.Contains(link.From) && seen.Contains(link.To))
            {
                links.Add(new SectorEdge(link.From, link.To, link.Kind));
            }
        }

        error = null;
        var graph = SectorGraph.Build(seen, links);
        return new GalaxyModel(Convert.ToHexStringLower(sha), sha, payload, [.. sectors], [.. links], graph);
    }
}

/// <summary>
/// <c>GalaxyMetadata</c> cache keyed by the save's SHA-256 (server-design 2.5, 2.8): the authority sends the metadata once
/// per save; the cache keeps it in memory, persists it (<c>galaxy_cache</c>) and reloads it after a restart, so a session
/// that resumes a known save does not wait for the authority. Actor-thread only.
/// </summary>
public sealed class GalaxyMetadataCache(IWorldStore store, TimeProvider time)
{
    private readonly Dictionary<string, GalaxyModel> _models = new(StringComparer.Ordinal);

    /// <summary>The galaxy of the save the session currently runs on, or null before one is known.</summary>
    public GalaxyModel? Current { get; private set; }

    /// <summary>The graph of <see cref="Current"/>, or an empty graph.</summary>
    public SectorGraph Graph => Current?.Graph ?? SectorGraph.Empty;

    public int CachedModels => _models.Count;

    /// <summary>Raised on the actor thread after <see cref="Current"/> changed.</summary>
    public event Action<GalaxyModel>? CurrentChanged;

    /// <summary>
    /// Makes the galaxy of <paramref name="saveSha256Hex"/> current from memory or from the persisted cache. False when the
    /// save's metadata was never seen (wait for the authority's <c>GalaxyMetadata</c>).
    /// </summary>
    public bool TryActivate(string saveSha256Hex)
    {
        ArgumentException.ThrowIfNullOrEmpty(saveSha256Hex);
        string key = saveSha256Hex.ToLowerInvariant();
        if (!_models.TryGetValue(key, out var model))
        {
            var payload = store.TryLoadGalaxy(key);
            model = payload is null ? null : GalaxyModel.TryParse(payload, out _);
            if (model is null || !string.Equals(model.SaveSha256Hex, key, StringComparison.Ordinal))
            {
                return false;
            }

            _models[key] = model;
        }

        SetCurrent(model);
        return true;
    }

    /// <summary>Stores and activates the metadata the authority sent; false (with the reason) when it is invalid.</summary>
    public bool TryIngest(byte[] payload, out string? error)
    {
        var model = GalaxyModel.TryParse(payload, out error);
        if (model is null)
        {
            return false;
        }

        if (!_models.TryGetValue(model.SaveSha256Hex, out var known))
        {
            known = model;
            _models[model.SaveSha256Hex] = model;
            store.SaveGalaxy(model.SaveSha256Hex, payload, time.GetUtcNow());
        }

        SetCurrent(known);
        return true;
    }

    public GalaxyModel? Find(string saveSha256Hex) => _models.GetValueOrDefault(saveSha256Hex.ToLowerInvariant());

    public void Reset() => Current = null;

    private void SetCurrent(GalaxyModel model)
    {
        bool changed = !ReferenceEquals(Current, model);
        Current = model;
        if (changed)
        {
            CurrentChanged?.Invoke(model);
        }
    }
}
