using System.Security.Cryptography;
using System.Text;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

/// <summary>One message the authority wants to send: type + FlatBuffers payload (the lane comes from the catalog).</summary>
public sealed record OutMessage(MsgType Type, byte[] Payload)
{
    /// <summary>Encodes as a complete TCP frame.</summary>
    public byte[] ToFrame() => FrameCodec.Encode(Type, Payload);
}

/// <summary>Sequential net_id allocator (authority-assigned, never 0 or 0xFFFFFFFF).</summary>
public sealed class NetIdAllocator
{
    public NetIdAllocator(uint first = 1)
    {
        NextNetId = first;
    }

    public uint NextNetId { get; private set; }

    public uint Allocate()
    {
        if (NextNetId == ReplicationCodec.ReservedNetId)
            throw new InvalidOperationException("net_id space exhausted.");
        return NextNetId++;
    }
}

/// <summary>Session string table (protocol.md "_ref" fields): factions then macros, indices from 1.</summary>
public sealed class FakeStringTable
{
    private readonly Dictionary<string, uint> _index = new(StringComparer.Ordinal);
    private readonly List<StringEntryT> _entries = [];

    public FakeStringTable(FakeGalaxy galaxy)
    {
        foreach (var f in galaxy.Factions)
            Add(f, StringKind.Faction);
        foreach (var m in galaxy.Entities.Select(e => e.Macro).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            Add(m, StringKind.Macro);
    }

    public IReadOnlyList<StringEntryT> Entries => _entries;

    public uint Index(string value) => _index[value];

    private void Add(string value, StringKind kind)
    {
        if (_index.ContainsKey(value))
            return;
        uint i = (uint)_entries.Count + 1;
        _index[value] = i;
        _entries.Add(new StringEntryT { Index = i, Kind = kind, Value = value });
    }
}

public sealed record FakeAuthorityOptions
{
    /// <summary>Authority tick rate (server-design 6.1 default 20).</summary>
    public double TickRateHz { get; init; } = FakeWorld.TickRateHz;

    /// <summary>How many entities the simulated "index pass" covers per tick (so big sectors take longer).</summary>
    public int IndexPassEntitiesPerTick { get; init; } = 400;

    /// <summary>Average reported FPS (jittered deterministically).</summary>
    public double Fps { get; init; } = 60;

    /// <summary>EntityState is 32 bytes; 28 + table overhead stays under the 1150 B WorldUpdate budget.</summary>
    public int MaxStatesPerWorldUpdate { get; init; } = 28;

    /// <summary>Entities per EntitySpawn message.</summary>
    public int SpawnChunk { get; init; } = 150;

    /// <summary>Static entities (stations) are re-sent at this interval (seconds) as keepalives.</summary>
    public double StaticKeepaliveSeconds { get; init; } = 10;
}

/// <summary>
/// The fake authority's logic, with no I/O (server-design 6.2): it answers a <c>CaptureSet</c> by indexing the
/// requested sectors (EntitySpawn + <c>SectorComplete</c> only after the simulated pass finishes), then streams
/// <c>WorldUpdate</c>s for exactly the captured sectors at their requested rates. It also builds
/// <c>GalaxyMetadata</c>, <c>StringTableAdd</c>, <c>GalaxySummary</c> and <c>NodeStats</c>. Drive it with
/// <see cref="OnCaptureSet"/> and <see cref="Tick"/>; a runner sends the returned messages.
/// <para>
/// net_ids: the allocator hands out ids sequentially at construction in entity order, which equals
/// <see cref="FakeNetIds.ToNetId"/>, so verifiers need no map. Runtime entities allocate after the galaxy.
/// </para>
/// </summary>
public sealed class FakeAuthority
{
    private sealed class SectorCapture
    {
        public required ushort Sector { get; init; }
        public required uint Epoch { get; init; }
        public int RateHz { get; set; }
        public long CompleteAtTick { get; set; }
        public bool Indexed { get; set; }
        public HashSet<int> Known { get; } = [];
    }

    /// <summary>A focus sphere of the CaptureSet: entities inside stream at the sphere's rate instead of the sector's.</summary>
    private readonly record struct Focus(ushort Sector, Vec3 Center, double RadiusM, int RateHz);

    private readonly FakeAuthorityOptions _opt;
    private readonly Dictionary<ushort, SectorCapture> _captures = [];
    private List<Focus> _focus = [];
    private long _lastLeg = -1;

    /// <summary>The team state the authority holds (relations to apply in game, the asset re-owns it was told to do; M1-T3).</summary>
    public FakeTeamState Teams { get; } = new();

    public FakeAuthority(FakeWorld world, FakeAuthorityOptions? options = null)
    {
        World = world;
        _opt = options ?? new FakeAuthorityOptions();
        Strings = new FakeStringTable(world.Galaxy);
        NetIds = new NetIdAllocator();
        foreach (var e in world.Galaxy.Entities)
        {
            uint id = NetIds.Allocate();
            if (id != FakeNetIds.ToNetId(e.EntityId))
                throw new InvalidOperationException("net_id allocation diverged from the entity id mapping.");
        }
    }

    public FakeWorld World { get; }
    public FakeStringTable Strings { get; }
    public NetIdAllocator NetIds { get; }
    public uint CaptureEpoch { get; private set; }

    /// <summary>CaptureSet sector indices that were out of range (ignored).</summary>
    public int RejectedSectors { get; private set; }

    public IReadOnlyCollection<ushort> CapturedSectors => _captures.Keys;

    public bool IsIndexed(ushort sector) => _captures.TryGetValue(sector, out var c) && c.Indexed;

    /// <summary>Entities the authority currently streams for a sector (empty until its index pass completes).</summary>
    public IReadOnlyCollection<int> KnownEntities(ushort sector) =>
        _captures.TryGetValue(sector, out var c) ? c.Known : [];

    private double TimeOf(long tick) => tick / _opt.TickRateHz;

    private long LegOf(long tick) => FakeWorld.LegOfTime(TimeOf(tick));

    // ---------------- startup messages ----------------

    /// <summary>StringTableAdd chunks + GalaxyMetadata (sent once after the handshake / upload of the save).</summary>
    public IReadOnlyList<OutMessage> StartupMessages(byte[]? saveSha256 = null)
    {
        var list = new List<OutMessage>(StringTableMessages());
        list.Add(BuildGalaxyMetadata(saveSha256));
        return list;
    }

    /// <summary>The full string table as <c>StringTableAdd</c> chunks (what the authority sends once, and what the server replays on join).</summary>
    public IReadOnlyList<OutMessage> StringTableMessages()
    {
        var list = new List<OutMessage>();
        const int chunk = 500;
        for (int i = 0; i < Strings.Entries.Count; i += chunk)
        {
            var add = new StringTableAddT { Entries = [.. Strings.Entries.Skip(i).Take(chunk)] };
            list.Add(new OutMessage(MsgType.StringTableAdd, MessageEncoder.EncodePayload(b => StringTableAdd.Pack(b, add), 16384)));
        }

        return list;
    }

    /// <summary>
    /// Placeholder save hash per seed, used where no fake save file exists (offline tests). A real run passes the SHA-256 of the
    /// generated save file (<see cref="FakeSaveGenerator"/>) to <see cref="BuildGalaxyMetadata"/>.
    /// </summary>
    public byte[] SaveSha256 => SHA256.HashData(Encoding.UTF8.GetBytes($"x4mp-fake-save-{World.Galaxy.Seed}"));

    /// <summary>The sector table in wire form (also embedded in the manifest).</summary>
    public List<SectorInfoT> BuildSectorInfos()
    {
        var g = World.Galaxy;
        return
        [
            .. g.Sectors.Select(s => new SectorInfoT
            {
                Index = s.Index,
                Macro = s.Macro,
                ClusterMacro = s.ClusterMacro,
                Name = s.Name,
                OwnerRef = Strings.Index(g.Factions[s.OwnerFaction]),
                GalaxyPos = new Vec3fT { X = (float)s.GalaxyPos.X, Y = (float)s.GalaxyPos.Y, Z = (float)s.GalaxyPos.Z },
            }),
        ];
    }

    public OutMessage BuildGalaxyMetadata(byte[]? saveSha256 = null)
    {
        var g = World.Galaxy;
        var meta = new GalaxyMetadataT
        {
            SaveSha256 = [.. saveSha256 ?? SaveSha256],
            Sectors = BuildSectorInfos(),
            Links = [],
        };
        foreach (var l in g.Links)
        {
            meta.Links.Add(new SectorLinkT { From = l.A, To = l.B, Kind = l.Kind, FromPos = ToVec3f(l.PosInA), ToPos = ToVec3f(l.PosInB) });
            meta.Links.Add(new SectorLinkT { From = l.B, To = l.A, Kind = l.Kind, FromPos = ToVec3f(l.PosInB), ToPos = ToVec3f(l.PosInA) });
        }
        return new OutMessage(MsgType.GalaxyMetadata, MessageEncoder.EncodePayload(b => GalaxyMetadata.Pack(b, meta), 65536));
    }

    private static Vec3fT ToVec3f(Vec3 v) => new() { X = (float)v.X, Y = (float)v.Y, Z = (float)v.Z };

    // ---------------- capture ----------------

    /// <summary>
    /// Applies a CaptureSet: removed sectors stop immediately; new ones start an index pass that completes
    /// after <c>ceil(entities / IndexPassEntitiesPerTick)</c> ticks (at least 1); rates of kept ones update.
    /// Focus spheres make entities inside them stream at the sphere's rate (the server asks 20 Hz around each player).
    /// </summary>
    public void OnCaptureSet(CaptureSetT set, long tick)
    {
        CaptureEpoch = set.Epoch;
        var wanted = new Dictionary<ushort, int>();
        foreach (var cs in set.Sectors ?? [])
        {
            if (cs.Sector == 0 || cs.Sector > World.Galaxy.Sectors.Count)
            {
                RejectedSectors++;
                continue;
            }
            wanted[cs.Sector] = Math.Max(1, (int)cs.RateHz);
        }

        foreach (var s in _captures.Keys.Where(s => !wanted.ContainsKey(s)).ToList())
            _captures.Remove(s);

        _focus = [.. (set.Focus ?? []).Where(f => f.Sector != 0 && f.Sector <= World.Galaxy.Sectors.Count && f.RateHz > 0)
            .Select(f => new Focus(f.Sector, f.Center is null ? default : new Vec3(f.Center.X, f.Center.Y, f.Center.Z), f.RadiusM, f.RateHz))];

        foreach (var (sector, rate) in wanted)
        {
            if (_captures.TryGetValue(sector, out var existing))
            {
                existing.RateHz = rate;
                continue;
            }
            int count = World.EntitiesInSector(sector, LegOf(tick)).Count;
            long passTicks = Math.Max(1, (count + _opt.IndexPassEntitiesPerTick - 1) / _opt.IndexPassEntitiesPerTick);
            _captures[sector] = new SectorCapture { Sector = sector, Epoch = set.Epoch, RateHz = rate, CompleteAtTick = tick + passTicks };
        }
    }

    // ---------------- per tick ----------------

    /// <summary>Everything the authority sends at <paramref name="tick"/>, in order.</summary>
    public IReadOnlyList<OutMessage> Tick(long tick)
    {
        var output = new List<OutMessage>();
        long leg = LegOf(tick);
        double now = TimeOf(tick);

        // 1. membership changes at leg boundaries (ships that jumped in or out of captured sectors)
        if (leg != _lastLeg)
        {
            foreach (var cap in _captures.Values.Where(c => c.Indexed))
            {
                var members = new HashSet<int>(World.EntitiesInSector(cap.Sector, leg));
                var leaving = cap.Known.Where(id => !members.Contains(id)).Order().ToList();
                var entering = members.Where(id => !cap.Known.Contains(id)).Order().ToList();
                if (leaving.Count > 0)
                {
                    var despawn = new EntityDespawnT
                    {
                        // The entity still exists, it just left the capture set; OutOfInterest is the closest reason.
                        Entries = [.. leaving.Select(id => new DespawnEntryT { NetId = FakeNetIds.ToNetId(id), Reason = DespawnReason.OutOfInterest })],
                    };
                    output.Add(new OutMessage(MsgType.EntityDespawn, MessageEncoder.EncodePayload(b => EntityDespawn.Pack(b, despawn), 4096)));
                    foreach (var id in leaving)
                        cap.Known.Remove(id);
                }
                AddSpawns(output, entering, now, EntityOrigin.AuthorityRuntime);
                foreach (var id in entering)
                    cap.Known.Add(id);
            }
            _lastLeg = leg;
        }

        // 2. finished index passes: EntitySpawn for everything, then the SectorComplete marker
        foreach (var cap in _captures.Values.Where(c => !c.Indexed && tick >= c.CompleteAtTick).OrderBy(c => c.Sector))
        {
            var members = World.EntitiesInSector(cap.Sector, leg);
            AddSpawns(output, members, now, EntityOrigin.Manifest);
            foreach (var id in members)
                cap.Known.Add(id);
            cap.Indexed = true;
            var done = new SectorCompleteT { Sector = cap.Sector, Epoch = cap.Epoch, EntityCount = (uint)members.Count };
            output.Add(new OutMessage(MsgType.SectorComplete, MessageEncoder.EncodePayload(b => SectorComplete.Pack(b, done))));
        }

        // 3. WorldUpdate for indexed sectors at their rates (entities inside a focus sphere at the sphere's rate)
        var states = new List<EntityStateT>();
        foreach (var cap in _captures.Values.Where(c => c.Indexed).OrderBy(c => c.Sector))
        {
            long period = Math.Max(1, (long)Math.Round(_opt.TickRateHz / cap.RateHz));
            long staticPeriod = Math.Max(1, (long)Math.Round(_opt.StaticKeepaliveSeconds * _opt.TickRateHz));
            var spheres = _focus.Where(f => f.Sector == cap.Sector).ToList();
            foreach (int id in cap.Known.Order())
            {
                bool isStation = World.Galaxy.Entities[id - 1].IsStation;
                long entityPeriod = isStation ? staticPeriod : period;
                if (!isStation && spheres.Count > 0)
                {
                    var pos = World.GetKinematics(id, now).Pos;
                    foreach (var f in spheres)
                    {
                        if ((pos - f.Center).Length <= f.RadiusM)
                            entityPeriod = Math.Min(entityPeriod, Math.Max(1, (long)Math.Round(_opt.TickRateHz / f.RateHz)));
                    }
                }
                if ((tick + id) % entityPeriod == 0)
                    states.Add(World.GetState(id, now));
            }
        }
        for (int i = 0; i < states.Count; i += _opt.MaxStatesPerWorldUpdate)
        {
            var update = new WorldUpdateT
            {
                AuthorityTick = (uint)tick,
                CaptureTimeUs = CaptureTimeUs(tick),
                GameTime = now,
                States = [.. states.Skip(i).Take(_opt.MaxStatesPerWorldUpdate)],
            };
            output.Add(new OutMessage(MsgType.WorldUpdate, MessageEncoder.EncodePayload(b => WorldUpdate.Pack(b, update), 1280)));
        }

        // An EntitySpawn state carries no sample time, so the server assumes it is as fresh as the latest world update. An empty
        // WorldUpdate in front of the spawns of this tick says exactly which game time that is.
        if (output.Any(m => m.Type == MsgType.EntitySpawn))
        {
            var clock = new WorldUpdateT { AuthorityTick = (uint)tick, CaptureTimeUs = CaptureTimeUs(tick), GameTime = now, States = [] };
            output.Insert(0, new OutMessage(MsgType.WorldUpdate, MessageEncoder.EncodePayload(b => WorldUpdate.Pack(b, clock), 64)));
        }
        return output;
    }

    /// <summary>Fake "server clock" at the sample time of a tick.</summary>
    public ulong CaptureTimeUs(long tick) => (ulong)Math.Round(tick * 1_000_000.0 / _opt.TickRateHz);

    private void AddSpawns(List<OutMessage> output, IEnumerable<int> ids, double now, EntityOrigin origin)
    {
        var batch = new List<EntityRecordT>();
        void Flush()
        {
            if (batch.Count == 0)
                return;
            var spawn = new EntitySpawnT { Entities = [.. batch] };
            output.Add(new OutMessage(MsgType.EntitySpawn, MessageEncoder.EncodePayload(b => EntitySpawn.Pack(b, spawn), 16384)));
            batch.Clear();
        }
        foreach (int id in ids)
        {
            batch.Add(MakeRecord(id, now, origin));
            if (batch.Count >= _opt.SpawnChunk)
                Flush();
        }
        Flush();
    }

    private EntityRecordT MakeRecord(int id, double now, EntityOrigin origin)
    {
        var e = World.Galaxy.Entities[id - 1];
        return new EntityRecordT
        {
            NetId = FakeNetIds.ToNetId(id),
            Kind = e.Kind,
            Origin = origin,
            MacroRef = Strings.Index(e.Macro),
            OwnerRef = Strings.Index(World.Galaxy.Factions[e.Faction]),
            Name = e.Name,
            Idcode = e.IdCode,
            Hull = 255,
            Shield = 255,
            State = World.GetState(id, now),
        };
    }

    // ---------------- periodic reports ----------------

    /// <summary>Whole-galaxy ship counts by class + station counts, independent of the CaptureSet (~0.2 Hz).</summary>
    public OutMessage BuildGalaxySummary(long tick)
    {
        var sum = BuildGalaxySummaryData(tick);
        return new OutMessage(MsgType.GalaxySummary, MessageEncoder.EncodePayload(b => GalaxySummary.Pack(b, sum), 4096));
    }

    public GalaxySummaryT BuildGalaxySummaryData(long tick)
    {
        long leg = LegOf(tick);
        var g = World.Galaxy;
        var sectors = new List<SectorSummaryT>();
        foreach (var s in g.Sectors)
        {
            var row = new SectorSummaryT { Sector = s.Index };
            foreach (int id in World.EntitiesInSector(s.Index, leg))
            {
                switch (g.Entities[id - 1].Kind)
                {
                    case EntityKind.ShipXS: row.ShipsXs++; break;
                    case EntityKind.ShipS: row.ShipsS++; break;
                    case EntityKind.ShipM: row.ShipsM++; break;
                    case EntityKind.ShipL: row.ShipsL++; break;
                    case EntityKind.ShipXL: row.ShipsXl++; break;
                    case EntityKind.Station: row.Stations++; break;
                    default: break;
                }
            }
            sectors.Add(row);
        }
        return new GalaxySummaryT { GameTime = TimeOf(tick), Sectors = sectors };
    }

    /// <summary>Per-node stats (every 2 s): FPS jitters deterministically around <see cref="FakeAuthorityOptions.Fps"/>.</summary>
    public OutMessage BuildNodeStats(long tick)
    {
        var stats = BuildNodeStatsData(tick);
        return new OutMessage(MsgType.NodeStats, MessageEncoder.EncodePayload(b => NodeStats.Pack(b, stats)));
    }

    public NodeStatsT BuildNodeStatsData(long tick)
    {
        double u = DetHash.Unit(DetHash.Hash(World.Galaxy.Seed, 0x57A75, (ulong)tick));
        double fps = _opt.Fps * (0.95 + 0.10 * u);
        return new NodeStatsT
        {
            Fps = (float)fps,
            FrameMsP95 = (float)(1000.0 / fps * 1.3),
            GameTime = TimeOf(tick),
            Ghosts = 0,
            SuppressedLocal = 0,
            UdpActive = true,
            MemoryMb = (uint)(3000 + (int)(u * 400)),
            MdHookState = FeatureState.Ok,
            TeamSetupState = FeatureState.Ok,
        };
    }
}
