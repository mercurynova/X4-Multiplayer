using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;
using static X4MP.Core.Tests.World.WorldKit;

namespace X4MP.Core.Tests.World;

public sealed class StringTableTests
{
    private static byte[] AddPayload(params (uint Index, StringKind Kind, string Value)[] entries) =>
        MessageEncoder.EncodePayload(
            b => StringTableAdd.Pack(b, new StringTableAddT
            {
                Entries = [.. entries.Select(e => new StringEntryT { Index = e.Index, Kind = e.Kind, Value = e.Value })],
            }),
            256);

    [Fact]
    public void AppliesAuthorityIndicesAndTracksTheNextFreeOne()
    {
        var table = new StringTable();
        var added = table.Apply(Decode<StringTableAdd>(MsgType.StringTableAdd, AddPayload((1, StringKind.Macro, "ship_arg_s_fighter_01_a_macro"), (2, StringKind.Faction, "argon"), (5, StringKind.Ware, "energycells"))));

        Assert.Equal(3, added.Count);
        Assert.Equal("argon", table.Get(2));
        Assert.Null(table.Get(3));
        Assert.Equal(6u, table.NextIndex);
        Assert.True(table.TryFind(StringKind.Ware, "energycells", out uint index));
        Assert.Equal(5u, index);
    }

    [Fact]
    public void ResendsAreIdempotentAndConflictsKeepTheFirstValue()
    {
        var table = new StringTable();
        Assert.Equal(StringApplyResult.Added, table.Add(1, StringKind.Macro, "a"));
        Assert.Equal(StringApplyResult.Duplicate, table.Add(1, StringKind.Macro, "a"));
        Assert.Equal(StringApplyResult.Conflict, table.Add(1, StringKind.Macro, "b"));
        Assert.Equal(StringApplyResult.Invalid, table.Add(0, StringKind.Macro, "z"));
        Assert.Equal("a", table.Get(1));
        Assert.Equal(1, table.Conflicts);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void ReplayChunksRoundTripTheWholeTable()
    {
        var table = new StringTable();
        for (uint i = 1; i <= 25; i++)
        {
            table.Add(i, StringKind.Macro, "macro_" + i);
        }

        var chunks = table.EncodeChunks(10);
        Assert.Equal(3, chunks.Count);

        var copy = new StringTable();
        foreach (var chunk in chunks)
        {
            copy.Apply(Decode<StringTableAdd>(MsgType.StringTableAdd, chunk));
        }

        Assert.Equal(table.Snapshot(), copy.Snapshot());
    }

    [Fact]
    public void AddsAreForwardedToObserversAndPersisted()
    {
        var store = new InMemoryWorldStore();
        var mirror = new WorldMirror(store: store);
        var seen = new List<StringTableEntry>();
        mirror.AddObserver(new StringObserver(seen));
        var frame = new X4MP.Core.Net.InboundFrame(AsFrame(MsgType.StringTableAdd, AddPayload((1, StringKind.Macro, "m"), (2, StringKind.Text, "t"))), 0);

        Assert.True(mirror.HandleForTest(frame, authority: true));
        Assert.Equal(2, seen.Count);
        Assert.Equal(2, store.LoadStrings().Count);

        // A resend adds nothing new.
        mirror.HandleForTest(frame, authority: true);
        Assert.Equal(2, seen.Count);

        // A restarted mirror restores the table (with kinds) from the store.
        var restarted = new WorldMirror(store: store);
        restarted.RestoreFromStore();
        Assert.Equal(StringKind.Text, restarted.Strings.Snapshot()[1].Kind);
        Assert.Equal(3u, restarted.Strings.NextIndex);
    }

    private sealed class StringObserver(List<StringTableEntry> sink) : IWorldObserver
    {
        public void OnStringsAdded(IReadOnlyList<StringTableEntry> added) => sink.AddRange(added);
    }
}

public sealed class SectorGraphTests
{
    [Fact]
    public void GatesAreTwoWayAndHighwaysStayOneWay()
    {
        var graph = SectorGraph.Build(
            [1, 2, 3, 4],
            [new SectorEdge(1, 2, LinkKind.Gate), new SectorEdge(2, 3, LinkKind.Highway), new SectorEdge(3, 4, LinkKind.Accelerator)]);

        Assert.Equal([2], graph.Neighbors(1).ToArray());
        Assert.Equal([1, 3], graph.Neighbors(2).ToArray());
        Assert.Equal([4], graph.Neighbors(3).ToArray());
        Assert.Empty(graph.Neighbors(4).ToArray());
    }

    [Fact]
    public void KHopNeighbourhoodsAreOrderedByDistanceAndExcludeTheOrigin()
    {
        // 1-2-3-4-5 plus a branch 3-6
        var graph = SectorGraph.Build(
            [1, 2, 3, 4, 5, 6],
            [new SectorEdge(1, 2, LinkKind.Gate), new SectorEdge(2, 3, LinkKind.Gate), new SectorEdge(3, 4, LinkKind.Gate), new SectorEdge(4, 5, LinkKind.Gate), new SectorEdge(3, 6, LinkKind.Gate)]);

        Assert.Equal(new ushort[] { 2, 4, 6 }, graph.WithinHops(3, 1).ToArray());
        Assert.Equal([2, 4, 6, 1, 5], graph.WithinHops(3, 2).ToArray());
        Assert.Equal(2, graph.HopDistance(3, 5, 3));
        Assert.Equal(-1, graph.HopDistance(1, 5, 3));
        Assert.Equal(0, graph.HopDistance(3, 3, 1));
        Assert.Empty(graph.WithinHops(3, 0).ToArray());
        Assert.Empty(graph.WithinHops(99, 2).ToArray());
    }

    [Fact]
    public void QueriesAreServedFromTheCacheAndSelfLoopsAndUnknownSectorsAreIgnored()
    {
        var graph = SectorGraph.Build([1, 2], [new SectorEdge(1, 1, LinkKind.Gate), new SectorEdge(1, 2, LinkKind.Gate), new SectorEdge(2, 77, LinkKind.Gate)]);
        Assert.Equal([2], graph.Neighbors(1).ToArray());
        Assert.Equal([1], graph.Neighbors(2).ToArray());

        int cached = graph.CachedEntries;
        var first = graph.WithinHops(1, 3).ToArray();
        int afterFirst = graph.CachedEntries;
        var second = graph.WithinHops(1, 3).ToArray();
        Assert.Equal(first, second);
        Assert.Equal(afterFirst, graph.CachedEntries);
        Assert.True(afterFirst >= cached);
    }

    [Fact]
    public void GraphOverAFullGalaxyAnswersK1AndK2WithoutRebuilding()
    {
        // A ring of 152 sectors: every sector has exactly two gates.
        var sectors = Enumerable.Range(1, 152).Select(i => (ushort)i).ToArray();
        var links = sectors.Select(i => new SectorEdge(i, (ushort)((i % 152) + 1), LinkKind.Gate));
        var graph = SectorGraph.Build(sectors, links);

        Assert.Equal(152, graph.SectorCount);
        Assert.Equal(2, graph.WithinHops(10, 1).Length);
        Assert.Equal(4, graph.WithinHops(10, 2).Length);
        Assert.Equal(6, graph.WithinHops(10, 3).Length);
    }
}

public sealed class JournalTests
{
    private static byte[] Payload(byte b) => [b];

    [Fact]
    public void EntriesAfterTheMarkerReplayInOrder()
    {
        var store = new InMemoryWorldStore();
        var journal = new Journal(store);
        var t = DateTimeOffset.UnixEpoch;
        journal.Append(MsgType.EntitySpawn, 1, 1, Payload(1), t);
        journal.Append(MsgType.EntityChange, 1, 1, Payload(2), t);
        var marker = journal.AppendMarker(7, new CheckpointId(1, 1), 50, 100, t);
        var a = journal.Append(MsgType.EntitySpawn, 2, 1, Payload(3), t);
        var b = journal.Append(MsgType.EntityDespawn, 1, 1, Payload(4), t);
        var c = journal.Append(MsgType.EntityChange, 2, 1, Payload(5), t);

        Assert.Equal(3UL, marker.Seq);
        var replay = journal.ReadAfterMarker(new CheckpointId(1, 1))!;
        Assert.Equal([a, b, c], replay.Select(r => r.Seq));
        Assert.Equal([3, 4, 5], replay.Select(r => (int)r.Payload[0]));
        Assert.All(replay, r => Assert.False(r.IsMarker));
        Assert.Null(journal.ReadAfterMarker(new CheckpointId(9, 9)));
        Assert.Equal(6UL, journal.LastSeq);
    }

    [Fact]
    public void CompactionDropsEverythingBeforeThePreviousCheckpoint()
    {
        var store = new InMemoryWorldStore();
        var journal = new Journal(store);
        var t = DateTimeOffset.UnixEpoch;
        var c1 = new CheckpointId(1, 0);
        var c2 = new CheckpointId(2, 0);
        var c3 = new CheckpointId(3, 0);

        journal.Append(MsgType.EntitySpawn, 1, 1, Payload(1), t);       // 1
        journal.AppendMarker(1, c1, 10, 10, t);                          // 2
        journal.Append(MsgType.EntitySpawn, 2, 1, Payload(2), t);       // 3
        journal.AppendMarker(2, c2, 20, 20, t);                          // 4
        journal.Append(MsgType.EntitySpawn, 3, 1, Payload(3), t);       // 5
        journal.AppendMarker(3, c3, 30, 30, t);                          // 6
        journal.Append(MsgType.EntitySpawn, 4, 1, Payload(4), t);       // 7

        // The first checkpoint has no predecessor: nothing to drop.
        Assert.Equal(0, journal.CompactBeforePreviousCheckpoint(c1));
        Assert.Equal(7, journal.Count);

        // c2 became current: drop what precedes c1's marker (seq 1) - still keeps c1's marker and later entries.
        Assert.Equal(1, journal.CompactBeforePreviousCheckpoint(c2));
        Assert.Equal(2UL, journal.FirstSeq);
        Assert.NotNull(journal.ReadAfterMarker(c1));
        Assert.Equal([3UL, 5, 7], journal.ReadAfterMarker(c1)!.Select(r => r.Seq));

        // c3 became current: drop everything before c2's marker (seq 4), including c1's marker.
        Assert.Equal(2, journal.CompactBeforePreviousCheckpoint(c3));
        Assert.Equal(4UL, journal.FirstSeq);
        Assert.Null(journal.ReadAfterMarker(c1));
        Assert.Equal([5UL, 7], journal.ReadAfterMarker(c2)!.Select(r => r.Seq));
        Assert.Equal([7UL], journal.ReadAfterMarker(c3)!.Select(r => r.Seq));
        Assert.Equal([4UL, 6], journal.Markers.Select(m => m.Seq));
        Assert.Equal([4UL, 5, 6, 7], store.LoadJournal().Select(r => r.Seq));

        // Sequences keep growing after compaction.
        Assert.Equal(8UL, journal.Append(MsgType.EntityChange, 4, 1, Payload(9), t));
    }

    [Fact]
    public void RestoreRebuildsEntriesMarkersAndTheSequenceCounter()
    {
        var store = new InMemoryWorldStore();
        var first = new Journal(store);
        var t = DateTimeOffset.UnixEpoch;
        first.Append(MsgType.EntitySpawn, 1, 1, Payload(1), t);
        first.AppendMarker(5, new CheckpointId(8, 9), 12.5, 321, t);
        first.Append(MsgType.EntitySpawn, 2, 1, Payload(2), t);

        var second = new Journal(store);
        second.Restore();

        Assert.Equal(3UL, second.LastSeq);
        var marker = Assert.Single(second.Markers);
        Assert.Equal((2UL, new CheckpointId(8, 9), 5u, 12.5, 321u), (marker.Seq, marker.Checkpoint, marker.RequestId, marker.GameTime, marker.NextNetId));
        Assert.Equal(4UL, second.Append(MsgType.EntitySpawn, 3, 1, Payload(3), t));
    }

    [Fact]
    public void JournalRowsConvertToTheWireEntriesOfAWorldCatchUp()
    {
        var mirror = new WorldMirror();
        mirror.Spawn(Rec(10, EntityKind.Station, 2, ownerTeam: 1, name: "HQ"));
        mirror.ApplyChange(Decode<EntityChange>(MsgType.EntityChange, ChangePayload(10, ChangeField.OwnerTeam, ownerTeam: 3)));
        mirror.ApplyCargo(Decode<EntityCargo>(MsgType.EntityCargo, CargoPayload(10, (4u, 7))));
        mirror.Despawn(DespawnReason.Destroyed, 10);

        var wire = mirror.Journal.Entries.Select(JournalCodec.ToWire).ToList();

        Assert.Equal([WorldMutation.EntityRecord, WorldMutation.EntityChange, WorldMutation.EntityCargo, WorldMutation.JournalDespawn], wire.Select(w => w!.Body.Type));
        Assert.Equal("HQ", wire[0]!.Body.AsEntityRecord().Name);
        Assert.Equal((ushort)3, wire[1]!.Body.AsEntityChange().OwnerTeam);
        Assert.Equal(7, wire[2]!.Body.AsEntityCargo().Wares[0].Amount);
        Assert.Equal(DespawnReason.Destroyed, wire[3]!.Body.AsJournalDespawn().Entry.Reason);
    }

    [Fact]
    public void SaveStartedFromTheAuthorityRecordsAMarkerAtTheCurrentPosition()
    {
        var mirror = new WorldMirror();
        JournalMarker? raised = null;
        mirror.MarkerRecorded += m => raised = m;
        mirror.Spawn(Rec(10, EntityKind.Station, 2));
        Assert.True(mirror.HandleForTest(new X4MP.Core.Net.InboundFrame(AsFrame(MsgType.SaveStarted, SaveStartedPayload(3, 11, 22, 99.5, 4242)), 0), authority: true));
        mirror.Spawn(Rec(11, EntityKind.Station, 2));

        Assert.NotNull(raised);
        Assert.Equal((2UL, new CheckpointId(11, 22), 3u, 99.5, 4242u), (raised.Seq, raised.Checkpoint, raised.RequestId, raised.GameTime, raised.NextNetId));
        Assert.Equal([3UL], mirror.Journal.ReadAfterMarker(new CheckpointId(11, 22))!.Select(r => r.Seq));
    }
}

public sealed class GalaxyMetadataCacheTests
{
    private static readonly byte[] ShaA = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] ShaB = Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray();

    [Fact]
    public void IngestParsesBuildsTheGraphAndPersistsByShaHex()
    {
        var store = new InMemoryWorldStore();
        var cache = new GalaxyMetadataCache(store, TimeProvider.System);

        Assert.True(cache.TryIngest(LineGalaxy(ShaA, 5), out var error), error);

        Assert.Equal(Convert.ToHexStringLower(ShaA), cache.Current!.SaveSha256Hex);
        Assert.Equal(5, cache.Current.Sectors.Count);
        Assert.Equal([2, 4], cache.Graph.Neighbors(3).ToArray());
        Assert.Equal(1, store.GalaxyCount);
        Assert.NotNull(store.TryLoadGalaxy(Convert.ToHexStringLower(ShaA)));
        Assert.Equal("sector_003_macro", cache.Current.Find(3)!.Macro);
    }

    [Fact]
    public void ARestartedCacheReloadsTheMetadataBySha()
    {
        var store = new InMemoryWorldStore();
        new GalaxyMetadataCache(store, TimeProvider.System).TryIngest(LineGalaxy(ShaA, 4), out _);

        var restarted = new GalaxyMetadataCache(store, TimeProvider.System);
        Assert.Null(restarted.Current);
        Assert.False(restarted.TryActivate(Convert.ToHexStringLower(ShaB)));
        Assert.True(restarted.TryActivate(Convert.ToHexStringLower(ShaA).ToUpperInvariant()));
        Assert.Equal(4, restarted.Graph.SectorCount);
    }

    [Fact]
    public void SwitchingBetweenSavesKeepsEachModelAndRaisesTheChangeEvent()
    {
        var cache = new GalaxyMetadataCache(new InMemoryWorldStore(), TimeProvider.System);
        var changes = new List<string>();
        cache.CurrentChanged += m => changes.Add(m.SaveSha256Hex[..4]);

        cache.TryIngest(LineGalaxy(ShaA, 3), out _);
        cache.TryIngest(LineGalaxy(ShaB, 6), out _);
        Assert.True(cache.TryActivate(Convert.ToHexStringLower(ShaA)));
        cache.TryIngest(LineGalaxy(ShaA, 3), out _); // same save again: no change

        Assert.Equal(2, cache.CachedModels);
        Assert.Equal(3, cache.Graph.SectorCount);
        Assert.Equal(3, changes.Count);
    }

    [Fact]
    public void InvalidMetadataIsRefused()
    {
        var cache = new GalaxyMetadataCache(new InMemoryWorldStore(), TimeProvider.System);
        Assert.False(cache.TryIngest(GalaxyPayload(ShaA, [], []), out var noSectors));
        Assert.Contains("no sectors", noSectors);
        Assert.False(cache.TryIngest(GalaxyPayload([1, 2, 3], [(1, "a")], []), out var badSha));
        Assert.Contains("32", badSha);
        Assert.False(cache.TryIngest(GalaxyPayload(ShaA, [(1, "a"), (1, "b")], []), out var dup));
        Assert.Contains("repeated", dup);
        Assert.Null(cache.Current);
    }

    [Fact]
    public void MirrorHandlesGalaxyMetadataAndNotifiesObservers()
    {
        var mirror = new WorldMirror();
        GalaxyModel? seen = null;
        mirror.AddObserver(new GalaxyObserver(m => seen = m));
        Assert.True(mirror.HandleForTest(new X4MP.Core.Net.InboundFrame(AsFrame(MsgType.GalaxyMetadata, LineGalaxy(ShaA, 3)), 0), authority: true));
        Assert.NotNull(seen);
        Assert.Equal(3, mirror.Graph.SectorCount);
    }

    private sealed class GalaxyObserver(Action<GalaxyModel> sink) : IWorldObserver
    {
        public void OnGalaxyChanged(GalaxyModel galaxy) => sink(galaxy);
    }
}
