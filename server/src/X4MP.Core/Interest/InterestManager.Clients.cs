using System.Runtime.InteropServices;
using X4MP.Core.Net;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Interest;

public sealed partial class InterestManager
{
    private readonly Dictionary<ushort, (InterestTier Tier, bool Predicted)> _desired = [];
    private readonly Dictionary<ushort, (InterestTier Tier, SectorAdmission Admission)> _final = [];
    private readonly List<SectorDemand> _demands = [];
    private readonly List<ushort> _sectorScratch = [];
    private readonly List<ushort> _removedScratch = [];
    private readonly List<ushort> _changedScratch = [];
    private readonly List<MirrorEntity> _batch = [];
    private readonly List<DespawnEntryT> _despawnScratch = [];

    // ------------------------------------------------------------------ recompute

    /// <summary>
    /// Recomputes one client's interest from its sector, hints, linger and the ghost budget, and turns the difference into
    /// messages: <c>InterestUpdate</c> first, then despawns for sectors that left and spawn jobs for sectors that entered.
    /// </summary>
    private void Recompute(ClientInterest c, long now)
    {
        var opt = Opt;
        ExpireTimers(c, now);

        // 1. What the client wants.
        _desired.Clear();
        if (c.Sector != 0)
        {
            _desired[c.Sector] = (InterestTier.Sector, false);
            if (!c.IsAuthority)
            {
                foreach (ushort n in _mirror.Graph.WithinHops(c.Sector, opt.PrefetchDepth))
                {
                    Want(n, InterestTier.Adjacent, false);
                }

                foreach (var (sector, _) in c.Hints)
                {
                    if (sector != c.Sector)
                    {
                        Want(sector, InterestTier.Adjacent, true);
                    }
                }

                foreach (var (sector, _) in c.Linger)
                {
                    if (sector != c.Sector)
                    {
                        Want(sector, InterestTier.Linger, false);
                    }
                }
            }
        }

        // 2. What the ghost budget allows.
        _demands.Clear();
        foreach (var (sector, want) in _desired)
        {
            _demands.Add(new SectorDemand(sector, want.Tier, _mirror.CountsIn(sector), want.Predicted));
        }

        var wasCapped = c.HardCap;
        var level = c.IsAuthority ? BudgetLevel.Full : GhostBudgetPolicy.Choose(CollectionsMarshal.AsSpan(_demands), opt.MaxGhosts, c.Level);
        c.Level = level;
        _final.Clear();
        foreach (var d in _demands)
        {
            var admission = c.IsAuthority ? SectorAdmission.All : GhostBudgetPolicy.Admission(level, d);
            if (admission != SectorAdmission.None)
            {
                _final[d.Sector] = (d.Tier, admission);
            }
        }

        // 3. Subscription diff.
        _removedScratch.Clear();
        foreach (ushort sector in c.Subs.Keys)
        {
            if (!_final.ContainsKey(sector))
            {
                _removedScratch.Add(sector);
            }
        }

        foreach (ushort sector in _removedScratch)
        {
            c.Subs.Remove(sector);
        }

        _changedScratch.Clear();
        foreach (var (sector, want) in _final)
        {
            if (!c.Subs.TryGetValue(sector, out var sub))
            {
                c.Subs[sector] = new SectorSubscription(sector) { Tier = want.Tier, Admission = want.Admission, Delivery = SectorDelivery.Pending };
            }
            else
            {
                sub.Tier = want.Tier;
                if (sub.Admission != want.Admission)
                {
                    sub.Admission = want.Admission;
                    _changedScratch.Add(sector);
                }
            }
        }

        if (!c.ReceivesSpawns)
        {
            return;
        }

        // 4. Tell the client, then move entities.
        SendInterestUpdate(c);
        foreach (ushort sector in _removedScratch)
        {
            DespawnSector(c, sector);
        }

        foreach (ushort sector in _changedScratch)
        {
            if (c.Subs.TryGetValue(sector, out var sub) && sub.Delivery != SectorDelivery.Pending)
            {
                SyncSector(c, sub);
            }
        }

        foreach (var sub in c.Subs.Values)
        {
            if (sub.Delivery == SectorDelivery.Pending && IsCaptureComplete(sub.Sector))
            {
                BeginDelivery(c, sub);
            }
            else if (c.HardCap && sub.Tier == InterestTier.Sector && sub.Delivery == SectorDelivery.Delivered && c.Ghosts < opt.MaxGhosts)
            {
                SyncSector(c, sub); // room again below the cap: bring back what was cut
            }
            else if (wasCapped && !c.HardCap && sub.Delivery == SectorDelivery.Delivered)
            {
                SyncSector(c, sub);
            }
        }

        if (c.HardCap && c.Ghosts > opt.MaxGhosts)
        {
            EnforceHardCap(c, opt.MaxGhosts);
        }
    }

    private void Want(ushort sector, InterestTier tier, bool predicted)
    {
        if (_desired.TryGetValue(sector, out var existing))
        {
            _desired[sector] = (existing.Tier >= tier ? existing.Tier : tier, existing.Predicted || predicted);
        }
        else
        {
            _desired[sector] = (tier, predicted);
        }
    }

    private void ExpireTimers(ClientInterest c, long now)
    {
        if (c.Linger.Count > 0)
        {
            _sectorScratch.Clear();
            foreach (var (sector, until) in c.Linger)
            {
                if (until <= now)
                {
                    _sectorScratch.Add(sector);
                }
            }

            foreach (ushort sector in _sectorScratch)
            {
                c.Linger.Remove(sector);
            }
        }

        if (c.Hints.Count > 0)
        {
            _sectorScratch.Clear();
            foreach (var (sector, until) in c.Hints)
            {
                if (until <= now)
                {
                    _sectorScratch.Add(sector);
                }
            }

            foreach (ushort sector in _sectorScratch)
            {
                c.Hints.Remove(sector);
            }
        }
    }

    /// <summary>The client's player ship moved to another sector: a gate crossing, a jump or a teleport.</summary>
    private void OnClientSectorChanged(ClientInterest c, ushort newSector, long now)
    {
        ushort old = c.Sector;
        int lingerSeconds = Opt.LingerSeconds;
        if (old != 0 && old != newSector && lingerSeconds > 0 && c.ReceivesSpawns)
        {
            c.Linger[old] = now + Seconds(lingerSeconds);
        }

        c.Linger.Remove(newSector);
        c.Hints.Remove(newSector);
        c.Sector = newSector;
        SetGridSector(c, c.ReceivesSpawns ? newSector : (ushort)0);
        Recompute(c, now);
        PumpJobs(c);
        MaybeSendCaptureSet(now);
    }

    private void SetGridSector(ClientInterest c, ushort sector)
    {
        ushort old = c.GridSector;
        if (old == sector)
        {
            return;
        }

        c.GridSector = sector;
        if (old != 0 && _gridRefs.TryGetValue(old, out int n))
        {
            if (n <= 1)
            {
                _gridRefs.Remove(old);
                _grid.Deactivate(old);
            }
            else
            {
                _gridRefs[old] = n - 1;
            }
        }

        if (sector != 0)
        {
            int refs = _gridRefs.GetValueOrDefault(sector);
            _gridRefs[sector] = refs + 1;
            if (refs == 0)
            {
                _grid.Activate(sector, _mirror.TransientIn(sector));
            }
        }
    }

    // ------------------------------------------------------------------ admission and delivery

    /// <summary>Whether <paramref name="e"/> belongs in the client's ghost set right now.</summary>
    private bool Admit(ClientInterest c, MirrorEntity e)
    {
        if (!c.ReceivesSpawns || e.IsPersistent || e.NetId == 0)
        {
            return false;
        }

        if (e.IsPlayerShip)
        {
            return _visibility.IsVisible(c.PlayerId, e); // galaxy-wide, outside the budget
        }

        if (!c.Subs.TryGetValue(e.Sector, out var sub) || sub.Delivery == SectorDelivery.Pending)
        {
            return false;
        }

        if (sub.Admission == SectorAdmission.None || (sub.Admission == SectorAdmission.LargeOnly && e.Size != EntitySize.Large))
        {
            return false;
        }

        if (c.HardCap && c.Ghosts >= Opt.MaxGhosts && !c.Held.ContainsKey(e.NetId))
        {
            return false;
        }

        return _visibility.IsVisible(c.PlayerId, e);
    }

    /// <summary>Starts delivering a sector the authority has completed: spawns near-first, then <c>SectorComplete</c>.</summary>
    private void BeginDelivery(ClientInterest c, SectorSubscription sub)
    {
        sub.Delivery = SectorDelivery.Delivering;
        var ids = new List<uint>();
        CollectMissing(c, sub.Sector, ids, despawn: null);
        c.Jobs.Add(new SpawnJob(sub.Sector, ids, sendComplete: true));
        PumpJobs(c);
    }

    /// <summary>Brings the client's held set of an already delivered sector in line with what is admitted now.</summary>
    private void SyncSector(ClientInterest c, SectorSubscription sub)
    {
        var ids = new List<uint>();
        _despawnScratch.Clear();
        CollectMissing(c, sub.Sector, ids, _despawnScratch);
        SendDespawns(c, DespawnReason.OutOfInterest);
        if (ids.Count > 0)
        {
            var existing = c.Jobs.Find(j => j.Sector == sub.Sector);
            if (existing is not null)
            {
                existing.Ids.AddRange(ids);
            }
            else
            {
                c.Jobs.Add(new SpawnJob(sub.Sector, ids, sendComplete: false));
            }

            PumpJobs(c);
        }
    }

    /// <summary>
    /// Entities of a sector the client should hold but does not (<paramref name="ids"/>, nearest to the player first when it is the
    /// player's sector) and, when <paramref name="despawn"/> is given, those it holds but should not.
    /// </summary>
    private void CollectMissing(ClientInterest c, ushort sector, List<uint> ids, List<DespawnEntryT>? despawn)
    {
        var bucket = _mirror.TransientIn(sector);
        List<(long Distance, uint NetId)>? ordered = sector == c.Sector ? [] : null;
        foreach (var e in bucket)
        {
            if (e.IsPlayerShip)
            {
                continue; // galaxy-wide, handled separately
            }

            bool held = c.Held.ContainsKey(e.NetId);
            bool admit = Admit(c, e) || (held && AdmitIgnoringCap(c, e));
            if (admit && !held)
            {
                if (ordered is not null)
                {
                    ordered.Add((NearGrid.DistanceSquared(e, c.Px, c.Py, c.Pz), e.NetId));
                }
                else
                {
                    ids.Add(e.NetId);
                }
            }
            else if (!admit && held && despawn is not null)
            {
                despawn.Add(new DespawnEntryT { NetId = e.NetId, Reason = DespawnReason.OutOfInterest });
                RemoveHeld(c, e.NetId);
            }
        }

        if (ordered is not null)
        {
            ordered.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
            foreach (var (_, id) in ordered)
            {
                ids.Add(id);
            }
        }
    }

    /// <summary>Admission without the hard-cap test (an entity already held keeps its place unless the cap enforcement drops it).</summary>
    private bool AdmitIgnoringCap(ClientInterest c, MirrorEntity e)
    {
        if (!c.Subs.TryGetValue(e.Sector, out var sub) || sub.Delivery == SectorDelivery.Pending || sub.Admission == SectorAdmission.None)
        {
            return false;
        }

        return (sub.Admission != SectorAdmission.LargeOnly || e.Size == EntitySize.Large) && _visibility.IsVisible(c.PlayerId, e);
    }

    /// <summary>Despawns every held ghost of a sector the client no longer follows (<c>OutOfInterest</c>) and drops its pending jobs.</summary>
    private void DespawnSector(ClientInterest c, ushort sector)
    {
        c.Jobs.RemoveAll(j => j.Sector == sector);
        _despawnScratch.Clear();
        foreach (var e in _mirror.TransientIn(sector))
        {
            if (!e.IsPlayerShip && c.Held.ContainsKey(e.NetId))
            {
                _despawnScratch.Add(new DespawnEntryT { NetId = e.NetId, Reason = DespawnReason.OutOfInterest });
                RemoveHeld(c, e.NetId);
            }
        }

        SendDespawns(c, DespawnReason.OutOfInterest);
    }

    /// <summary>Last budget level: drop the farthest ghosts until the client is at <paramref name="budget"/>.</summary>
    private void EnforceHardCap(ClientInterest c, int budget)
    {
        var candidates = new List<(long Distance, uint NetId)>();
        foreach (var (id, isPlayer) in c.Held)
        {
            if (!isPlayer && _mirror.TryGet(id, out var e))
            {
                candidates.Add((NearGrid.DistanceSquared(e, c.Px, c.Py, c.Pz), id));
            }
        }

        candidates.Sort(static (a, b) => b.Distance.CompareTo(a.Distance));
        _despawnScratch.Clear();
        foreach (var (_, id) in candidates)
        {
            if (c.Ghosts <= budget)
            {
                break;
            }

            _despawnScratch.Add(new DespawnEntryT { NetId = id, Reason = DespawnReason.OutOfInterest });
            RemoveHeld(c, id);
        }

        SendDespawns(c, DespawnReason.OutOfInterest);
    }

    private static void RemoveHeld(ClientInterest c, uint netId)
    {
        if (c.Held.Remove(netId, out bool isPlayer) && !isPlayer)
        {
            c.Ghosts--;
        }
    }

    private static void AddHeld(ClientInterest c, MirrorEntity e)
    {
        bool isPlayer = e.IsPlayerShip;
        if (c.Held.TryAdd(e.NetId, isPlayer) && !isPlayer)
        {
            c.Ghosts++;
        }
    }

    /// <summary>Sends queued spawn batches within the per-tick frame budget and the control lane's soft cap, then the sector markers.</summary>
    private void PumpJobs(ClientInterest c)
    {
        if (c.Jobs.Count == 0 || !c.ReceivesSpawns)
        {
            return;
        }

        var opt = Opt;
        int batchMax = opt.SpawnBatchEntities;
        int index = 0;
        while (index < c.Jobs.Count)
        {
            var job = c.Jobs[index];
            while (job.Next < job.Ids.Count)
            {
                if (c.FramesLeft <= 0 || _transport.IsOverSoftCap(c.PlayerId))
                {
                    return; // resume next tick
                }

                _batch.Clear();
                while (job.Next < job.Ids.Count && _batch.Count < batchMax)
                {
                    uint id = job.Ids[job.Next++];
                    if (!c.Held.ContainsKey(id) && _mirror.TryGet(id, out var e) && Admit(c, e))
                    {
                        _batch.Add(e);
                    }
                }

                if (_batch.Count == 0)
                {
                    continue;
                }

                if (!SendSpawn(c, _batch, 0))
                {
                    return;
                }

                job.Sent += _batch.Count;
                c.FramesLeft--;
            }

            if (job.SendComplete)
            {
                if (!c.Subs.TryGetValue(job.Sector, out var sub))
                {
                    c.Jobs.RemoveAt(index);
                    continue;
                }

                if (!SendSectorComplete(c, job))
                {
                    return;
                }

                sub.Delivery = SectorDelivery.Delivered;
            }

            c.Jobs.RemoveAt(index);
        }
    }

    // ------------------------------------------------------------------ wire helpers

    private static EntityRecordT ToRecord(MirrorEntity e) => new()
    {
        NetId = e.NetId,
        Kind = e.Kind,
        Origin = e.Origin,
        MacroRef = e.MacroRef,
        OwnerRef = e.OwnerRef,
        OwnerTeam = e.OwnerTeam,
        OwnerPlayer = e.OwnerPlayer,
        ParentNetId = e.ParentNetId,
        ControllerPlayer = e.ControllerPlayer,
        Name = e.Name,
        Idcode = e.IdCode,
        Hull = e.Hull,
        Shield = e.Shield,
        State = new EntityStateT
        {
            NetId = e.NetId, Sector = e.Sector, Flags = e.Flags, Px = e.Px, Py = e.Py, Pz = e.Pz,
            Yaw = e.Yaw, Pitch = e.Pitch, Roll = e.Roll, Vx = e.Vx, Vy = e.Vy, Vz = e.Vz,
        },
    };

    private static bool Accepted(SendResult result) => result is SendResult.Queued or SendResult.Coalesced;

    private SendResult Send(int playerId, MsgType type, byte[] payload)
    {
        var frame = OutboundFrame.Create(type, payload);
        try
        {
            return _transport.Send(playerId, frame);
        }
        finally
        {
            frame.Release();
        }
    }

    /// <summary>One encode to many receivers (server-design 2.6: encode once, send to N, release once).</summary>
    private void SendToClients(MsgType type, byte[] payload, Func<ClientInterest, bool> receiver)
    {
        OutboundFrame? frame = null;
        try
        {
            foreach (var c in _clients.Values)
            {
                if (c.ReceivesSpawns && receiver(c))
                {
                    frame ??= OutboundFrame.Create(type, payload);
                    _transport.Send(c.PlayerId, frame);
                }
            }
        }
        finally
        {
            frame?.Release();
        }
    }

    private bool SendSpawn(ClientInterest c, List<MirrorEntity> entities, ulong journalSeq)
    {
        var message = new EntitySpawnT { JournalSeq = journalSeq, Entities = [.. entities.Select(ToRecord)] };
        var payload = MessageEncoder.EncodePayload(b => EntitySpawn.Pack(b, message), 128 + (entities.Count * 160));
        if (!Accepted(Send(c.PlayerId, MsgType.EntitySpawn, payload)))
        {
            return false;
        }

        foreach (var e in entities)
        {
            AddHeld(c, e);
        }

        Stats.SpawnsSent += entities.Count;
        return true;
    }

    /// <summary>Sends the entries collected in <see cref="_despawnScratch"/> (already removed from the held set) in frames of 500.</summary>
    private void SendDespawns(ClientInterest c, DespawnReason reason)
    {
        const int Chunk = 500;
        for (int offset = 0; offset < _despawnScratch.Count; offset += Chunk)
        {
            var entries = _despawnScratch.GetRange(offset, Math.Min(Chunk, _despawnScratch.Count - offset));
            foreach (var entry in entries)
            {
                entry.Reason = entry.Reason == DespawnReason.Unknown ? reason : entry.Reason;
            }

            var message = new EntityDespawnT { Entries = entries };
            var payload = MessageEncoder.EncodePayload(b => EntityDespawn.Pack(b, message), 64 + (entries.Count * 16));
            Send(c.PlayerId, MsgType.EntityDespawn, payload);
            Stats.DespawnsSent += entries.Count;
        }

        _despawnScratch.Clear();
    }

    private bool SendSectorComplete(ClientInterest c, SpawnJob job)
    {
        uint epoch = _capture.TryGetValue(job.Sector, out var cs) ? cs.CompleteEpoch : 0;
        var message = new SectorCompleteT { Sector = job.Sector, Epoch = epoch, EntityCount = (uint)job.Sent };
        var payload = MessageEncoder.EncodePayload(b => SectorComplete.Pack(b, message), 48);
        if (!Accepted(Send(c.PlayerId, MsgType.SectorComplete, payload)))
        {
            return false;
        }

        Stats.SectorCompletesSent++;
        return true;
    }

    private static bool TiersDiffer(ClientInterest c)
    {
        if (c.Subs.Count != c.LastSent.Count)
        {
            return true;
        }

        foreach (var sub in c.Subs.Values)
        {
            if (!c.LastSent.TryGetValue(sub.Sector, out var old) || old != sub.Tier)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Sends the tiers that changed since the last <c>InterestUpdate</c> (a full list the first time).</summary>
    private void SendInterestUpdate(ClientInterest c)
    {
        if (c.Sector == 0)
        {
            return;
        }

        bool full = !c.SentFull;
        if (!full && !TiersDiffer(c))
        {
            return; // the common case on a tick: nothing to say, nothing allocated
        }

        var entries = new List<SectorTierT>();
        foreach (var sub in c.Subs.Values)
        {
            if (full || !c.LastSent.TryGetValue(sub.Sector, out var old) || old != sub.Tier)
            {
                entries.Add(new SectorTierT { Sector = sub.Sector, Tier = sub.Tier });
            }
        }

        if (!full)
        {
            foreach (var sector in c.LastSent.Keys)
            {
                if (!c.Subs.ContainsKey(sector))
                {
                    entries.Add(new SectorTierT { Sector = sector, Tier = InterestTier.None });
                }
            }
        }

        var message = new InterestUpdateT { Epoch = c.Epoch + 1, Full = full, Sectors = entries };
        var payload = MessageEncoder.EncodePayload(b => InterestUpdate.Pack(b, message), 64 + (entries.Count * 4));
        if (!Accepted(Send(c.PlayerId, MsgType.InterestUpdate, payload)))
        {
            return;
        }

        c.Epoch++;
        c.SentFull = true;
        c.LastSent.Clear();
        foreach (var sub in c.Subs.Values)
        {
            c.LastSent[sub.Sector] = sub.Tier;
        }

        Stats.InterestUpdatesSent++;
    }

    // ------------------------------------------------------------------ authority messages and client hints

    /// <summary>The authority says every entity of a sector was sent for capture epoch <c>epoch</c>: the sector can now be forwarded.</summary>
    public void HandleSectorComplete(SectorComplete message)
    {
        if (!_capture.TryGetValue(message.Sector, out var state) || message.Epoch < state.AddedEpoch)
        {
            Stats.StaleSectorCompletes++;
            return;
        }

        state.Complete = true;
        state.CompleteEpoch = message.Epoch;
        long now = Now;
        foreach (var c in _clients.Values.ToArray())
        {
            if (c.ReceivesSpawns && c.Subs.TryGetValue(message.Sector, out var sub) && sub.Delivery == SectorDelivery.Pending)
            {
                Recompute(c, now); // starts the delivery (and re-checks the budget now that the sector's size is known)
                PumpJobs(c);
            }
        }
    }

    /// <summary>
    /// A client's <c>InterestHint</c> (only with the negotiated <c>InterestHint</c> capability; the server may ignore it): prefetch a
    /// non-adjacent jump target for a while. The client never chooses its tiers; this only widens the prefetch.
    /// </summary>
    public void HandleHint(int playerId, InterestHint hint)
    {
        var opt = Opt;
        ushort target = hint.TargetSector;
        if (!opt.HonorInterestHints
            || !_clients.TryGetValue(playerId, out var c)
            || (c.Caps & (ulong)Capability.InterestHint) == 0
            || !c.ReceivesSpawns
            || target == 0
            || target == c.Sector
            || !_mirror.Graph.Contains(target)
            || (c.Hints.Count >= MaxHintsPerClient && !c.Hints.ContainsKey(target)))
        {
            Stats.HintsIgnored++;
            return;
        }

        double seconds = Math.Clamp((hint.EtaMs / 1000.0) + 10, 5, Math.Max(5, opt.HintMaxSeconds));
        long now = Now;
        c.Hints[target] = now + Seconds(seconds);
        Stats.HintsAccepted++;
        Recompute(c, now);
        PumpJobs(c);
        MaybeSendCaptureSet(now);
    }

    private const int MaxHintsPerClient = 4;

    // ------------------------------------------------------------------ world mirror observer

    public void OnEntitySpawned(MirrorEntity entity, bool isNew, ushort previousSector, ulong journalSeq)
    {
        if (entity.IsPersistent)
        {
            ForwardSpawn(entity, journalSeq);
            return;
        }

        if (_grid.ActiveSectorCount > 0)
        {
            _grid.Move(entity);
        }

        Reevaluate(entity);
    }

    public void OnEntityStateChanged(MirrorEntity entity, ushort previousSector, StateChange change)
    {
        if (entity.IsPersistent)
        {
            return;
        }

        if ((change & (StateChange.Position | StateChange.Sector)) != 0 && _grid.ActiveSectorCount > 0)
        {
            _grid.Move(entity);
        }

        if ((change & StateChange.Sector) != 0)
        {
            Reevaluate(entity);
        }
    }

    public void OnEntityChanged(MirrorEntity entity, EntityChange change, ulong journalSeq)
    {
        var t = change.UnPack();
        t.JournalSeq = journalSeq;
        var payload = MessageEncoder.EncodePayload(b => EntityChange.Pack(b, t), 128);
        uint netId = entity.NetId;
        bool persistent = entity.IsPersistent;
        SendToClients(MsgType.EntityChange, payload, c => persistent || c.Held.ContainsKey(netId));
        if (!persistent && (change.Fields & (ChangeField.Controller | ChangeField.Kind)) != 0)
        {
            Reevaluate(entity);
        }
    }

    public void OnEntityCargo(MirrorEntity entity, EntityCargo cargo, ulong journalSeq)
    {
        var t = cargo.UnPack();
        t.JournalSeq = journalSeq;
        var payload = MessageEncoder.EncodePayload(b => EntityCargo.Pack(b, t), 128);
        uint netId = entity.NetId;
        bool persistent = entity.IsPersistent;
        SendToClients(MsgType.EntityCargo, payload, c => persistent || c.Held.ContainsKey(netId));
    }

    public void OnEntityDespawned(MirrorEntity entity, DespawnReason reason, uint killerNetId, ulong journalSeq)
    {
        _grid.Remove(entity);
        var message = new EntityDespawnT
        {
            JournalSeq = journalSeq,
            Entries = [new DespawnEntryT { NetId = entity.NetId, KillerNetId = killerNetId, Reason = reason }],
        };
        var payload = MessageEncoder.EncodePayload(b => EntityDespawn.Pack(b, message), 64);
        uint netId = entity.NetId;
        if (entity.IsPersistent)
        {
            SendToClients(MsgType.EntityDespawn, payload, _ => true);
            return;
        }

        SendToClients(MsgType.EntityDespawn, payload, c => c.Held.ContainsKey(netId));
        foreach (var c in _clients.Values)
        {
            RemoveHeld(c, netId);
        }

        Stats.DespawnsSent++;
    }

    public void OnPlayerShipUpdated(PlayerShipState ship)
    {
        if (!_clients.TryGetValue(ship.PlayerId, out var c) || ship.Sector == 0)
        {
            return;
        }

        c.Px = ship.Px;
        c.Py = ship.Py;
        c.Pz = ship.Pz;
        if (ship.Sector != c.Sector)
        {
            OnClientSectorChanged(c, ship.Sector, Now);
        }
    }

    /// <summary>
    /// New strings go to every client node that is past the join replay (CatchingUp or InGame) before any spawn that may reference
    /// them: the Control lane keeps order, and the authority sends the strings first.
    /// </summary>
    public void OnStringsAdded(IReadOnlyList<StringTableEntry> added)
    {
        var message = new StringTableAddT
        {
            Entries = [.. added.Select(s => new StringEntryT { Index = s.Index, Kind = s.Kind, Value = s.Value })],
        };
        var payload = MessageEncoder.EncodePayload(b => StringTableAdd.Pack(b, message), 64 + (added.Count * 48));
        OutboundFrame? frame = null;
        try
        {
            foreach (var node in _nodes.Values)
            {
                if (!node.IsAuthority && node.IsAttached && (node.Roles & Role.Client) != 0
                    && node.Phase is NodePhase.CatchingUp or NodePhase.InGame)
                {
                    frame ??= OutboundFrame.Create(MsgType.StringTableAdd, payload);
                    _transport.Send(node.PlayerId, frame);
                }
            }
        }
        finally
        {
            frame?.Release();
        }
    }

    public void OnGalaxyChanged(GalaxyModel galaxy)
    {
        // A new graph changes every neighbourhood: recompute on the next tick (and now, so a first galaxy takes effect immediately).
        long now = Now;
        foreach (var c in _clients.Values.ToArray())
        {
            Recompute(c, now);
            PumpJobs(c);
        }
    }

    public void OnWorldCleared()
    {
        foreach (var c in _clients.Values)
        {
            c.Held.Clear();
            c.Ghosts = 0;
            c.Jobs.Clear();
            c.Subs.Clear();
            c.LastSent.Clear();
            c.SentFull = false;
        }

        _capture.Clear();
        _grid.Clear();
        _gridRefs.Clear();
        foreach (var c in _clients.Values)
        {
            c.GridSector = 0;
        }
    }

    /// <summary>Persistent entities go to everybody (matched to their local copy by the manifest), stamped with the journal sequence.</summary>
    private void ForwardSpawn(MirrorEntity entity, ulong journalSeq)
    {
        var message = new EntitySpawnT { JournalSeq = journalSeq, Entities = [ToRecord(entity)] };
        var payload = MessageEncoder.EncodePayload(b => EntitySpawn.Pack(b, message), 256);
        SendToClients(MsgType.EntitySpawn, payload, _ => true);
    }

    /// <summary>Re-checks one transient entity against every client: spawn it where it became admitted, despawn it where it is not any more.</summary>
    private void Reevaluate(MirrorEntity entity)
    {
        foreach (var c in _clients.Values)
        {
            if (!c.ReceivesSpawns)
            {
                continue;
            }

            bool held = c.Held.TryGetValue(entity.NetId, out bool wasPlayer);
            bool admit = held ? AdmitIgnoringCapOrPlayer(c, entity) : Admit(c, entity);
            if (held && !admit)
            {
                RemoveHeld(c, entity.NetId);
                _despawnScratch.Clear();
                _despawnScratch.Add(new DespawnEntryT { NetId = entity.NetId, Reason = DespawnReason.OutOfInterest });
                SendDespawns(c, DespawnReason.OutOfInterest);
            }
            else if (!held && admit)
            {
                _batch.Clear();
                _batch.Add(entity);
                SendSpawn(c, _batch, 0);
            }
            else if (held && wasPlayer != entity.IsPlayerShip)
            {
                // The controller changed: move the ghost between the budgeted and the exempt count.
                c.Held[entity.NetId] = entity.IsPlayerShip;
                c.Ghosts += entity.IsPlayerShip ? -1 : 1;
            }
        }
    }

    private bool AdmitIgnoringCapOrPlayer(ClientInterest c, MirrorEntity e) =>
        e.IsPlayerShip ? _visibility.IsVisible(c.PlayerId, e) : AdmitIgnoringCap(c, e);
}
