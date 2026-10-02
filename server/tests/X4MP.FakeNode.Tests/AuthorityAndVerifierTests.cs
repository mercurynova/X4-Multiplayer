using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode.Tests;

public sealed class AuthorityAndVerifierTests
{
    private static FakeWorld NewWorld(ulong seed = 42) => new(FakeGalaxy.Generate(seed));

    private static ushort BusySector(FakeWorld w, long leg = 0) =>
        w.Galaxy.Sectors.Select(s => s.Index).OrderByDescending(s => w.EntitiesInSector(s, leg).Count).First();

    private static CaptureSetT Capture(uint epoch, params (ushort Sector, byte Rate)[] sectors) =>
        new() { Epoch = epoch, Sectors = [.. sectors.Select(s => new CaptureSectorT { Sector = s.Sector, RateHz = s.Rate })] };

    private static T Decode<T>(OutMessage m) where T : struct, Google.FlatBuffers.IFlatbufferObject =>
        MessageRegistry.Default.Decode<T>(new Frame(m.Type, FrameOptions.None, MessageRegistry.Default.GetDescriptor(m.Type).Lane, m.Payload));

    [Fact]
    public void NetIdsAreAllocatedSequentiallyAndMatchEntityIds()
    {
        var w = NewWorld();
        var a = new FakeAuthority(w);
        Assert.Equal((uint)w.Galaxy.Entities.Count + 1, a.NetIds.NextNetId);
        Assert.Equal((uint)w.Galaxy.Entities.Count + 1, a.NetIds.Allocate());
    }

    [Fact]
    public void StartupMessagesCarryStringTableAndGalaxyMetadata()
    {
        var w = NewWorld();
        var a = new FakeAuthority(w);
        var msgs = a.StartupMessages();
        Assert.Contains(msgs, m => m.Type == MsgType.StringTableAdd);
        var meta = Decode<GalaxyMetadata>(msgs.Last(m => m.Type == MsgType.GalaxyMetadata)).UnPack();
        Assert.Equal(152, meta.Sectors.Count);
        Assert.Equal(w.Galaxy.Links.Count * 2, meta.Links.Count);
        Assert.Equal(32, meta.SaveSha256.Count);
        Assert.All(msgs, m => Assert.True(m.ToFrame().Length <= FrameCodec.DefaultMaxFrameBytes));
    }

    [Fact]
    public void SectorCompleteOnlyAfterIndexPassAndAfterEverySpawn()
    {
        var w = NewWorld();
        var a = new FakeAuthority(w, new FakeAuthorityOptions { IndexPassEntitiesPerTick = 20 });
        ushort sector = BusySector(w);
        int count = w.EntitiesInSector(sector, 0).Count;
        Assert.True(count > 60);
        a.OnCaptureSet(Capture(7, (sector, 10)), tick: 0);

        long expectedTicks = (count + 19) / 20;
        var seen = new List<OutMessage>();
        long completedAt = -1;
        for (long t = 0; t < expectedTicks + 3; t++)
        {
            var o = a.Tick(t);
            Assert.DoesNotContain(o, m => m.Type == MsgType.WorldUpdate && t < expectedTicks);
            seen.AddRange(o);
            if (completedAt < 0 && o.Any(m => m.Type == MsgType.SectorComplete))
                completedAt = t;
        }
        Assert.Equal(expectedTicks, completedAt);

        int spawned = 0;
        foreach (var m in seen)
        {
            if (m.Type == MsgType.EntitySpawn)
                spawned += Decode<EntitySpawn>(m).EntitiesLength;
            if (m.Type == MsgType.SectorComplete)
            {
                var sc = Decode<SectorComplete>(m);
                Assert.Equal(sector, sc.Sector);
                Assert.Equal(7u, sc.Epoch);
                Assert.Equal((uint)count, sc.EntityCount);
                Assert.Equal(count, spawned); // marker follows all spawns
            }
        }
        Assert.True(a.IsIndexed(sector));
    }

    [Fact]
    public void OnlyCapturedSectorsAreStreamedAtTheirRate()
    {
        var w = NewWorld();
        var a = new FakeAuthority(w);
        ushort busy = BusySector(w);
        ushort other = w.Galaxy.Sectors.Select(s => s.Index).First(s => s != busy);
        a.OnCaptureSet(Capture(1, (busy, 10)), 0);

        var counts = new Dictionary<uint, int>();
        for (long t = 0; t < 400; t++)
            foreach (var m in a.Tick(t))
            {
                Assert.True(m.Payload.Length + FrameCodec.HeaderSize <= 1150 || m.Type != MsgType.WorldUpdate, "WorldUpdate over the datagram budget");
                if (m.Type != MsgType.WorldUpdate)
                    continue;
                var upd = Decode<WorldUpdate>(m).UnPack();
                foreach (var s in upd.States)
                {
                    Assert.Equal(busy, w.SectorAt(FakeNetIds.ToEntityId(s.NetId), upd.GameTime));
                    counts[s.NetId] = counts.GetValueOrDefault(s.NetId) + 1;
                }
            }

        Assert.NotEmpty(counts);
        Assert.DoesNotContain(counts.Keys, id => w.EntitiesInSector(other, 0).Contains(FakeNetIds.ToEntityId(id)));
        // a ship at 10 Hz sends ~ 200 updates in 20 s
        int ship = w.EntitiesInSector(busy, 0).First(id => !w.Galaxy.Entities[id - 1].IsStation);
        Assert.InRange(counts[(uint)ship], 190, 210);

        // dropping the sector stops everything
        a.OnCaptureSet(Capture(2), 400);
        Assert.Empty(a.Tick(401));
        Assert.Empty(a.CapturedSectors);
    }

    [Fact]
    public void ATickThatSpawnsEntitiesPutsAClockUpdateFirstAndGoesOutOnTheOrderedLane()
    {
        var w = NewWorld();
        var a = new FakeAuthority(w);
        a.OnCaptureSet(Capture(1, (BusySector(w), 10)), tick: 0);

        bool sawSpawnTick = false;
        for (long t = 0; t < 40; t++)
        {
            var o = a.Tick(t);
            bool spawns = o.Any(m => m.Type == MsgType.EntitySpawn);
            Assert.Equal(spawns, FakeAuthority.NeedsOrderedLane(o));
            if (!spawns)
                continue;
            sawSpawnTick = true;
            // the spawns are stamped with the game time of the update in front of them
            Assert.Equal(MsgType.WorldUpdate, o[0].Type);
            var clock = Decode<WorldUpdate>(o[0]).UnPack();
            Assert.Empty(clock.States);
            Assert.Equal((uint)t, clock.AuthorityTick);
        }
        Assert.True(sawSpawnTick);
        Assert.False(FakeAuthority.NeedsOrderedLane([]));
    }

    [Fact]
    public void OutOfRangeSectorsAreRejected()
    {
        var a = new FakeAuthority(NewWorld());
        a.OnCaptureSet(Capture(1, (0, 5), (9999, 5)), 0);
        Assert.Equal(2, a.RejectedSectors);
        Assert.Empty(a.CapturedSectors);
    }

    [Fact]
    public void ShipsEnteringAndLeavingACapturedSectorAreSpawnedAndDespawned()
    {
        var w = NewWorld();
        var a = new FakeAuthority(w);
        ushort sector = BusySector(w);
        a.OnCaptureSet(Capture(1, (sector, 5)), 0);
        bool spawn = false, despawn = false;
        for (long t = 0; t < 6 * FakeWorld.TicksPerLeg; t += 1)
        {
            foreach (var m in a.Tick(t))
            {
                if (t == 0 || t == 1)
                    continue;
                spawn |= m.Type == MsgType.EntitySpawn;
                despawn |= m.Type == MsgType.EntityDespawn;
            }
            // the streamed set always equals the world's occupancy
            if (t % 600 == 0 && t > 5)
                Assert.True(a.KnownEntities(sector).Order().SequenceEqual(w.EntitiesInSector(sector, FakeWorld.LegOfTick(t)).Order()));
        }
        Assert.True(spawn && despawn);
    }

    [Fact]
    public void GalaxySummaryTotalsMatchTheGalaxy()
    {
        var w = NewWorld();
        var a = new FakeAuthority(w);
        var sum = Decode<GalaxySummary>(a.BuildGalaxySummary(0)).UnPack();
        Assert.Equal(152, sum.Sectors.Count);
        int ships = sum.Sectors.Sum(s => s.ShipsXs + s.ShipsS + s.ShipsM + s.ShipsL + s.ShipsXl);
        Assert.Equal(w.Galaxy.ShipCount, ships);
        Assert.Equal(w.Galaxy.StationCount, sum.Sectors.Sum(s => s.Stations));
        // summary tracks jumps over time, totals stay
        var later = a.BuildGalaxySummaryData(10 * FakeWorld.TicksPerLeg);
        Assert.Equal(ships, later.Sectors.Sum(s => s.ShipsXs + s.ShipsS + s.ShipsM + s.ShipsL + s.ShipsXl));
        Assert.NotEqual(sum.Sectors.Select(s => s.ShipsS).ToArray(), later.Sectors.Select(s => s.ShipsS).ToArray());
    }

    [Fact]
    public void NodeStatsFpsJittersAroundTheConfiguredValue()
    {
        var a = new FakeAuthority(NewWorld(), new FakeAuthorityOptions { Fps = 50 });
        var fps = Enumerable.Range(0, 50).Select(i => Decode<NodeStats>(a.BuildNodeStats(i * 40)).Fps).ToList();
        Assert.All(fps, f => Assert.InRange(f, 47.4, 52.6));
        Assert.True(fps.Distinct().Count() > 10);
        Assert.Equal(FeatureState.Ok, Decode<NodeStats>(a.BuildNodeStats(0)).MdHookState);
    }

    // ---------------- verifier round trip ----------------

    /// <summary>Runs authority -> reference replicator -> real codec -> verifier for <paramref name="ticks"/> ticks.</summary>
    private static ReplicationVerifier RoundTrip(FakeWorld w, ushort sector, int ticks, Func<ReplicationT, ReplicationT>? tamper = null)
    {
        var authority = new FakeAuthority(w);
        authority.OnCaptureSet(Capture(1, (sector, 20)), 0);
        var replicator = new ReferenceReplicator();
        var verifier = new ReplicationVerifier(w);
        for (long t = 0; t < ticks; t++)
        {
            var states = new List<EntityStateT>();
            foreach (var m in authority.Tick(t))
            {
                if (m.Type == MsgType.WorldUpdate)
                    states.AddRange(Decode<WorldUpdate>(m).UnPack().States);
            }
            if (states.Count == 0)
                continue;
            foreach (var rep in replicator.Build((uint)t, authority.CaptureTimeUs(t), t / FakeWorld.TickRateHz, states))
            {
                // through the real frame + registry decode path, like a client would
                var payload = MessageEncoder.EncodePayload(b => Replication.Pack(b, tamper?.Invoke(rep) ?? rep));
                var frame = FrameCodec.Encode(MsgType.Replication, payload);
                Assert.True(FrameCodec.TryDecode(frame, out var decoded, out _));
                verifier.Verify(decoded);
            }
        }
        return verifier;
    }

    [Fact]
    public void VerifierSeesZeroErrorsOver10kTicksForACapturedSector()
    {
        var w = NewWorld();
        ushort sector = BusySector(w);
        var v = RoundTrip(w, sector, 10_000);
        Assert.Equal(0, v.Errors);
        Assert.True(v.EntriesChecked > 100_000, $"only {v.EntriesChecked} entries checked");
        Assert.True(v.KnownEntities >= w.EntitiesInSector(sector, 0).Count);
    }

    [Fact]
    public void VerifierDetectsAnInjectedPositionError()
    {
        var w = NewWorld();
        ushort sector = BusySector(w);
        int n = 0;
        var v = RoundTrip(w, sector, 400, rep =>
        {
            if (++n != 25)
                return rep;
            // corrupt the first entry's X position by 1 m (64 steps)
            var entries = ReplicationCodec.Decode(rep.Entries.ToArray(), rep.EntryCount);
            var e = entries[0];
            Assert.True((e.Mask & ReplicationMask.Pos) != 0);
            e.PosX += 64;
            entries[0] = e;
            return new ReplicationT
            {
                ServerTick = rep.ServerTick,
                ServerTimeUs = rep.ServerTimeUs,
                AuthorityGameTime = rep.AuthorityGameTime,
                EntryCount = rep.EntryCount,
                Entries = [.. ReplicationCodec.Encode(entries.ToArray())],
            };
        });
        Assert.Equal(1, v.Errors);
        Assert.Equal("position", v.Violations[0].Kind);
    }

    [Fact]
    public void VerifierFlagsWrongSectorUnknownEntityAndIncompleteFirstEntry()
    {
        var w = NewWorld();
        var v = new ReplicationVerifier(w);
        var good = w.GetState(w.Galaxy.StationCount + 1, 3.0);

        ReplicationT Msg(params ReplicationEntry[] entries) => new()
        {
            AuthorityGameTime = 3.0,
            EntryCount = (ushort)entries.Length,
            Entries = [.. ReplicationCodec.Encode(entries)],
        };

        const ReplicationMask full = ReplicationMask.Sector | ReplicationMask.Pos | ReplicationMask.Rot | ReplicationMask.Vel | ReplicationMask.Time;
        ReplicationEntry Entry(EntityStateT s, ReplicationMask mask, ushort sectorOverride = 0) => new()
        {
            NetId = s.NetId, Mask = mask, Sector = sectorOverride != 0 ? sectorOverride : s.Sector,
            PosX = s.Px, PosY = s.Py, PosZ = s.Pz, Yaw = s.Yaw, Pitch = s.Pitch, Roll = s.Roll, VelX = s.Vx, VelY = s.Vy, VelZ = s.Vz,
        };

        v.Verify(Msg(Entry(good, full)));
        Assert.Equal(0, v.Errors);

        v.Verify(Msg(Entry(good, full, (ushort)(good.Sector % 152 + 1))));
        Assert.Contains(v.Violations, x => x.Kind == "sector");

        v.Verify(Msg(new ReplicationEntry { NetId = 99_999_999, Mask = ReplicationMask.Time }));
        Assert.Contains(v.Violations, x => x.Kind == "unknown-entity");

        var other = w.GetState(w.Galaxy.StationCount + 2, 3.0);
        v.Verify(Msg(Entry(other, ReplicationMask.Pos | ReplicationMask.Time)));
        Assert.Contains(v.Violations, x => x.Kind == "incomplete-first-entry");
    }

    [Fact]
    public void VerifierToleratesQuantisationButNotTwoSteps()
    {
        var w = NewWorld();
        var v = new ReplicationVerifier(w);
        int id = w.Galaxy.StationCount + 5;
        var s = w.GetState(id, 12.0);
        ReplicationT Msg(int dx) => new()
        {
            AuthorityGameTime = 12.0,
            EntryCount = 1,
            Entries = [.. ReplicationCodec.Encode([new ReplicationEntry
            {
                NetId = s.NetId, Mask = ReplicationMask.Sector | ReplicationMask.Pos | ReplicationMask.Rot,
                Sector = s.Sector, PosX = s.Px + dx, PosY = s.Py, PosZ = s.Pz, Yaw = s.Yaw, Pitch = s.Pitch, Roll = s.Roll,
            }])],
        };
        v.Verify(Msg(0));
        Assert.Equal(0, v.Errors);
        v.Verify(Msg(2));
        Assert.Equal(1, v.Errors);
    }
}
