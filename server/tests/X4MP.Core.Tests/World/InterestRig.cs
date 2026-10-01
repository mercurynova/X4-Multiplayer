using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Interest;
using X4MP.Core.Net;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;
using static X4MP.Core.Tests.World.WorldKit;

namespace X4MP.Core.Tests.World;

/// <summary>Records what the interest manager sends, per client and to the authority.</summary>
public sealed class FakeTransport : IInterestTransport
{
    public List<(int Player, MsgType Type, byte[] Payload)> ToClients { get; } = [];

    public List<(MsgType Type, byte[] Payload)> ToAuthority { get; } = [];

    public bool AuthorityReady { get; set; } = true;

    public HashSet<int> OverSoftCap { get; } = [];

    public bool IsOverSoftCap(int playerId) => OverSoftCap.Contains(playerId);

    public SendResult Send(int playerId, OutboundFrame frame)
    {
        ToClients.Add((playerId, frame.MessageType, frame.Bytes[FrameCodec.HeaderSize..].ToArray()));
        return SendResult.Queued;
    }

    public SendResult SendToAuthority(OutboundFrame frame)
    {
        ToAuthority.Add((frame.MessageType, frame.Bytes[FrameCodec.HeaderSize..].ToArray()));
        return SendResult.Queued;
    }

    public IEnumerable<T> Decode<T>(int player, MsgType type) where T : struct, Google.FlatBuffers.IFlatbufferObject =>
        ToClients.Where(f => f.Player == player && f.Type == type).Select(f => WorldKit.Decode<T>(type, f.Payload)).ToList();

    public IEnumerable<T> DecodeAuthority<T>(MsgType type) where T : struct, Google.FlatBuffers.IFlatbufferObject =>
        ToAuthority.Where(f => f.Type == type).Select(f => WorldKit.Decode<T>(type, f.Payload)).ToList();
}

/// <summary>A manager over a line galaxy (sectors 1..n joined by gates) with a fake clock and a recording transport.</summary>
public sealed class InterestRig
{
    public const int Alice = 1;
    public const int Bob = 2;

    private static readonly byte[] Sha = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();

    public InterestRig(int sectors = 6, InterestOptions? options = null, byte[]? galaxy = null)
    {
        Options = options ?? new InterestOptions();
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        Mirror = new WorldMirror(Time);
        Transport = new FakeTransport();
        Manager = new InterestManager(Mirror, () => Options, Time, Transport);
        Mirror.HandleForTest(new InboundFrame(AsFrame(MsgType.GalaxyMetadata, galaxy ?? LineGalaxy(Sha, sectors)), 0), authority: true);
    }

    public InterestOptions Options { get; }

    public FakeTimeProvider Time { get; }

    public WorldMirror Mirror { get; }

    public FakeTransport Transport { get; }

    public InterestManager Manager { get; }

    private uint _seq;

    /// <summary>Adds transient ships to a sector (ids from <paramref name="firstId"/>).</summary>
    public void Ships(ushort sector, uint firstId, int count, EntityKind kind = EntityKind.ShipS, int px = 0)
    {
        var records = Enumerable.Range(0, count).Select(i => Rec(firstId + (uint)i, kind, sector, px + (i * 64))).ToArray();
        Mirror.Spawn(records);
    }

    public void Mixed(ushort sector, uint firstId, int small, int large)
    {
        Ships(sector, firstId, small, EntityKind.ShipS);
        Ships(sector, firstId + (uint)small, large, EntityKind.ShipL);
    }

    /// <summary>The authority completes a sector for the given capture epoch (default: the latest sent).</summary>
    public void Complete(ushort sector, uint? epoch = null, uint count = 0) =>
        Manager.HandleSectorComplete(Decode<SectorComplete>(
            MsgType.SectorComplete,
            MessageEncoder.EncodePayload(b => SectorComplete.Pack(b, new SectorCompleteT { Sector = sector, Epoch = epoch ?? Manager.CaptureEpoch, EntityCount = count }), 32)));

    public void CompleteAllCaptured()
    {
        foreach (var s in Manager.LastCaptureSectors.ToArray())
        {
            Complete(s.Sector);
        }
    }

    /// <summary>A client's player ship appears in <paramref name="sector"/> (a <c>PlayerState</c> relayed through the mirror).</summary>
    public void Move(int player, ushort sector, int px = 0, int py = 0, int pz = 0) =>
        Mirror.ApplyPlayerState(player, Decode<PlayerState>(MsgType.PlayerState, PlayerStatePayload(++_seq, 0, sector, px, py, pz)));

    public void Activate(int player, ulong caps = 0, bool authority = false) => Manager.ClientActivated(player, caps, authority);

    public void Advance(TimeSpan by)
    {
        Time.Advance(by);
        Manager.Tick(Time.GetTimestamp());
    }

    public void Tick() => Manager.Tick(Time.GetTimestamp());

    /// <summary>Lets the capture-set debounce (500 ms) pass so a pending set is sent.</summary>
    public void Settle() => Advance(TimeSpan.FromMilliseconds(600));

    public void ClearSent()
    {
        Transport.ToClients.Clear();
        Transport.ToAuthority.Clear();
    }

    // ---- what a client was sent, in order

    public List<uint> Spawned(int player) =>
        [.. Transport.Decode<EntitySpawn>(player, MsgType.EntitySpawn).SelectMany(s => Enumerable.Range(0, s.EntitiesLength).Select(i => s.Entities(i)!.Value.NetId))];

    public List<uint> Despawned(int player, DespawnReason? reason = null) =>
        [.. Transport.Decode<EntityDespawn>(player, MsgType.EntityDespawn)
            .SelectMany(s => Enumerable.Range(0, s.EntriesLength).Select(i => s.Entries(i)!.Value))
            .Where(e => reason is null || e.Reason == reason)
            .Select(e => e.NetId)];

    public List<(uint Epoch, bool Full, Dictionary<ushort, InterestTier> Tiers)> Updates(int player) =>
        [.. Transport.Decode<InterestUpdate>(player, MsgType.InterestUpdate).Select(u => (u.Epoch, u.Full, Enumerable.Range(0, u.SectorsLength).ToDictionary(i => u.Sectors(i)!.Value.Sector, i => u.Sectors(i)!.Value.Tier)))];

    public List<(ushort Sector, uint Count)> Completes(int player) =>
        [.. Transport.Decode<SectorComplete>(player, MsgType.SectorComplete).Select(c => (c.Sector, c.EntityCount))];

    /// <summary>Order of message types a client received.</summary>
    public List<MsgType> Types(int player) => [.. Transport.ToClients.Where(f => f.Player == player).Select(f => f.Type)];

    public List<CaptureSet> CaptureSets() => [.. Transport.DecodeAuthority<CaptureSet>(MsgType.CaptureSet)];

    public static Dictionary<ushort, int> Rates(CaptureSet set) =>
        Enumerable.Range(0, set.SectorsLength).ToDictionary(i => set.Sectors(i)!.Value.Sector, i => (int)set.Sectors(i)!.Value.RateHz);
}
