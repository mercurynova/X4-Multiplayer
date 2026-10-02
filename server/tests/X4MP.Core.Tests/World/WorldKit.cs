using Google.FlatBuffers;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Tests.World;

/// <summary>Builders for the authority's world messages and a decoded-message shortcut.</summary>
public static class WorldKit
{
    public static Frame AsFrame(MsgType type, byte[] payload) =>
        new(type, FrameOptions.None, MessageRegistry.Default.GetDescriptor(type).Lane, payload);

    public static EntityRecordT Rec(
        uint netId, EntityKind kind, ushort sector, int px = 0, int py = 0, int pz = 0,
        ushort ownerTeam = 0, ushort ownerPlayer = 0, ushort controller = 0, EntityOrigin origin = EntityOrigin.AuthorityRuntime,
        string name = "", byte hull = 255) => new()
        {
            NetId = netId,
            Kind = kind,
            Origin = origin,
            OwnerTeam = ownerTeam,
            OwnerPlayer = ownerPlayer,
            ControllerPlayer = controller,
            Name = name,
            Idcode = "ABC-" + netId,
            Hull = hull,
            Shield = 255,
            State = new EntityStateT { NetId = netId, Sector = sector, Px = px, Py = py, Pz = pz },
        };

    public static byte[] SpawnPayload(params EntityRecordT[] records) => SpawnPayloadAt(0, records);

    public static byte[] SpawnPayloadAt(double gameTime, params EntityRecordT[] records) =>
        MessageEncoder.EncodePayload(b => EntitySpawn.Pack(b, new EntitySpawnT { Entities = [.. records], GameTime = gameTime }), 512);

    public static byte[] DespawnPayload(DespawnReason reason, uint killer, params uint[] netIds) =>
        MessageEncoder.EncodePayload(
            b => EntityDespawn.Pack(b, new EntityDespawnT
            {
                Entries = [.. netIds.Select(n => new DespawnEntryT { NetId = n, KillerNetId = killer, Reason = reason })],
            }),
            256);

    public static byte[] ChangePayload(uint netId, ChangeField fields, ushort ownerTeam = 0, ushort ownerPlayer = 0, string? name = null) =>
        MessageEncoder.EncodePayload(
            b => EntityChange.Pack(b, new EntityChangeT { NetId = netId, Fields = fields, OwnerTeam = ownerTeam, OwnerPlayer = ownerPlayer, Name = name }),
            128);

    public static byte[] CargoPayload(uint netId, params (uint Ware, int Amount)[] wares) =>
        MessageEncoder.EncodePayload(
            b => EntityCargo.Pack(b, new EntityCargoT
            {
                NetId = netId,
                Wares = [.. wares.Select(w => new WareAmountT { WareRef = w.Ware, Amount = w.Amount })],
            }),
            128);

    public static EntityStateT State(uint netId, ushort sector, int px, int py = 0, int pz = 0, ushort flags = 0, short vx = 0) =>
        new() { NetId = netId, Sector = sector, Px = px, Py = py, Pz = pz, Flags = flags, Vx = vx };

    public static byte[] UpdatePayload(uint tick, double gameTime, IEnumerable<EntityStateT> states) =>
        MessageEncoder.EncodePayload(
            b => WorldUpdate.Pack(b, new WorldUpdateT { AuthorityTick = tick, GameTime = gameTime, States = [.. states] }),
            4096);

    public static byte[] StatusPayload(params (uint NetId, byte Hull, byte Shield)[] statuses) =>
        MessageEncoder.EncodePayload(
            b => EntityStatusBatch.Pack(b, new EntityStatusBatchT
            {
                Statuses = [.. statuses.Select(s => new EntityStatusT { NetId = s.NetId, Hull = s.Hull, Shield = s.Shield })],
            }),
            256);

    public static byte[] PlayerStatePayload(uint seq, uint netId, ushort sector, int px, int py = 0, int pz = 0) =>
        MessageEncoder.EncodePayload(
            b => PlayerState.Pack(b, new PlayerStateT { Seq = seq, NetId = netId, Sector = sector, Px = px, Py = py, Pz = pz, Hull = 200, Shield = 100 }),
            128);

    public static byte[] SaveStartedPayload(uint requestId, ulong lo, ulong hi, double gameTime = 100, uint nextNetId = 1000) =>
        MessageEncoder.EncodePayload(
            b => SaveStarted.Pack(b, new SaveStartedT
            {
                RequestId = requestId,
                CheckpointId = new Id128T { Lo = lo, Hi = hi },
                GameTime = gameTime,
                NextNetId = nextNetId,
            }),
            96);

    public static T Decode<T>(MsgType type, byte[] payload) where T : struct, IFlatbufferObject =>
        MessageRegistry.Default.Decode<T>(AsFrame(type, payload));

    public static void Spawn(this WorldMirror mirror, params EntityRecordT[] records) =>
        mirror.ApplySpawn(Decode<EntitySpawn>(MsgType.EntitySpawn, SpawnPayload(records)));

    public static void Despawn(this WorldMirror mirror, DespawnReason reason, params uint[] netIds) =>
        mirror.ApplyDespawn(Decode<EntityDespawn>(MsgType.EntityDespawn, DespawnPayload(reason, 0, netIds)));

    public static IngestResult Update(this WorldMirror mirror, params EntityStateT[] states) =>
        mirror.IngestWorldUpdate(UpdatePayload(1, 1.0, states));

    /// <summary>Feeds a frame as player 1 (the authority or an ordinary client).</summary>
    public static bool HandleForTest(this WorldMirror mirror, X4MP.Core.Net.InboundFrame frame, bool authority, int playerId = 1) =>
        mirror.HandleFrame(frame, playerId, authority);

    public static byte[] GalaxyPayload(byte[] sha, (ushort Index, string Macro)[] sectors, (ushort From, ushort To, LinkKind Kind)[] links) =>
        MessageEncoder.EncodePayload(
            b => GalaxyMetadata.Pack(b, new GalaxyMetadataT
            {
                SaveSha256 = [.. sha],
                Sectors = [.. sectors.Select(s => new SectorInfoT { Index = s.Index, Macro = s.Macro, ClusterMacro = "cluster", Name = s.Macro, GalaxyPos = new Vec3fT() })],
                Links = [.. links.Select(l => new SectorLinkT { From = l.From, To = l.To, Kind = l.Kind, FromPos = new Vec3fT(), ToPos = new Vec3fT() })],
            }),
            2048);

    /// <summary>A line of sectors 1-2-...-n joined by gates.</summary>
    public static byte[] LineGalaxy(byte[] sha, int count) =>
        GalaxyPayload(
            sha,
            [.. Enumerable.Range(1, count).Select(i => ((ushort)i, $"sector_{i:D3}_macro"))],
            [.. Enumerable.Range(1, count - 1).Select(i => ((ushort)i, (ushort)(i + 1), LinkKind.Gate))]);
}
