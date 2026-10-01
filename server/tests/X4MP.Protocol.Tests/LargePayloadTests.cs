using Google.FlatBuffers;
using X4MP.Proto;

namespace X4MP.Protocol.Tests;

/// <summary>
/// Payloads above 32767 bytes bypass the (unreliable at that size) FlatBuffers Verifier and rely on the
/// full-read pass. These tests pin that behaviour.
/// </summary>
public class LargePayloadTests
{
    private static readonly MessageRegistry Registry = MessageRegistry.Default;

    private static byte[] Pack<TObj, TTable>(TObj obj, Func<FlatBufferBuilder, TObj, Offset<TTable>> pack) where TTable : struct
    {
        var fbb = new FlatBufferBuilder(1 << 16);
        fbb.Finish(pack(fbb, obj).Value);
        return fbb.DataBuffer.ToSizedArray();
    }

    private static byte[] LargeGalaxy()
    {
        var g = new GalaxyMetadataT { SaveSha256 = [1, 2, 3], Sectors = [], Links = [] };
        for (ushort i = 1; i <= 600; i++)
            g.Sectors.Add(new SectorInfoT { Index = i, Macro = $"cluster_{i:D2}_sector001_macro", ClusterMacro = $"cluster_{i:D2}_macro", Name = $"Sector {i}", OwnerRef = i, GalaxyPos = new Vec3fT { X = i, Y = 0, Z = -i } });
        for (ushort i = 1; i < 600; i++)
            g.Links.Add(new SectorLinkT { From = i, To = (ushort)(i + 1), Kind = LinkKind.Gate, FromPos = new Vec3fT { X = 1, Y = 2, Z = 3 }, ToPos = new Vec3fT { X = 4, Y = 5, Z = 6 } });
        return Pack<GalaxyMetadataT, GalaxyMetadata>(g, GalaxyMetadata.Pack);
    }

    private static byte[] LargeCatchUp()
    {
        var c = new WorldCatchUpT { CheckpointId = new Id128T { Lo = 1, Hi = 2 }, Final = true, Entries = [] };
        for (uint i = 1; i <= 1500; i++)
        {
            var rec = new EntityRecordT { NetId = i, Kind = EntityKind.ShipS, Name = "Ship " + i, Idcode = "AAA-" + i, State = new EntityStateT { NetId = i, Px = (int)i } };
            c.Entries.Add(new JournalEntryT { Seq = i, Body = WorldMutationUnion.FromEntityRecord(rec) });
        }
        return Pack<WorldCatchUpT, WorldCatchUp>(c, WorldCatchUp.Pack);
    }

    [Fact]
    public void LargePayloadsRoundTripWithoutConsoleNoise()
    {
        var original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            var galaxy = LargeGalaxy();
            var catchUp = LargeCatchUp();
            Assert.True(galaxy.Length > MessageDescriptor.VerifierReliableLimit);
            Assert.True(catchUp.Length > MessageDescriptor.VerifierReliableLimit);

            var g = Registry.Decode(MsgType.GalaxyMetadata, galaxy);
            Assert.Equal(600, ((GalaxyMetadata)g).SectorsLength);
            Assert.Equal(599, ((GalaxyMetadata)g).LinksLength);

            var c = (WorldCatchUp)Registry.Decode(MsgType.WorldCatchUp, catchUp);
            Assert.Equal(1500, c.EntriesLength);
            Assert.Equal(1500u, c.Entries(1499)!.Value.BodyAsEntityRecord().NetId);
        }
        finally
        {
            Console.SetOut(original);
        }
        Assert.Equal("", captured.ToString());
    }

    [Theory]
    [InlineData(MsgType.GalaxyMetadata)]
    [InlineData(MsgType.WorldCatchUp)]
    public void CorruptedLargePayloadsOnlyEverRaiseProtocolViolation(MsgType type)
    {
        var payload = type == MsgType.GalaxyMetadata ? LargeGalaxy() : LargeCatchUp();
        var rng = new Random(99);
        for (int i = 0; i < 150; i++)
        {
            var copy = (byte[])payload.Clone();
            // flip bytes at random places, biased to the structural tail where late tables live
            for (int k = 0; k < 6; k++)
                copy[rng.Next(copy.Length)] = (byte)rng.Next(256);
            Try(type, copy);
        }

        for (int i = 0; i < 100; i++)
            Try(type, payload.AsSpan(0, rng.Next(1, payload.Length)).ToArray());
    }

    private static void Try(MsgType type, byte[] payload)
    {
        try
        {
            Registry.Decode(type, payload);
        }
        catch (ProtocolViolation)
        {
            // expected for corrupt input
        }
    }
}
