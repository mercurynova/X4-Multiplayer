using System.IO.Compression;
using System.Text;
using Google.FlatBuffers;
using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode.Tests;

public sealed class FakeSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-fakesave-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [Fact]
    public void TheSaveIsDeterministicPerSeedAndCounterAndAboutTheRequestedSize()
    {
        var a = FakeSaveGenerator.CreateSave(_dir, 42, 1, 300_000);
        string firstHash = a.ShaHex;
        File.Delete(a.Path);
        var again = FakeSaveGenerator.CreateSave(_dir, 42, 1, 300_000);
        Assert.Equal(firstHash, again.ShaHex);

        var otherCounter = FakeSaveGenerator.CreateSave(_dir, 42, 2, 300_000);
        var otherSeed = FakeSaveGenerator.CreateSave(_dir, 43, 1, 300_000);
        Assert.NotEqual(firstHash, otherCounter.ShaHex);
        Assert.NotEqual(firstHash, otherSeed.ShaHex);
        Assert.InRange(again.Size, 300_000, 310_000);
        Assert.Equal(32, again.Sha256.Length);
    }

    [Fact]
    public void TheSaveIsAGzipOfAnX4StyleSavegameDocument()
    {
        var save = FakeSaveGenerator.CreateSave(_dir, 42, 1, 5000, moneyCredits: 424_200);
        using var file = File.OpenRead(save.Path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        var head = new byte[200];
        int read = gzip.Read(head, 0, head.Length);
        string text = Encoding.UTF8.GetString(head, 0, read);
        Assert.StartsWith("<?xml", text, StringComparison.Ordinal);
        Assert.Contains("<savegame><info>", text, StringComparison.Ordinal);

        var body = new byte[1 << 16];
        using var again = new GZipStream(File.OpenRead(save.Path), CompressionMode.Decompress);
        int total = again.Read(body, 0, body.Length);
        Assert.Contains("money=\"424200\"", Encoding.UTF8.GetString(body, 0, total), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FakeSaveFlavor.NotGzip)]
    [InlineData(FakeSaveFlavor.GzipNotASave)]
    public void BrokenFlavoursAreStillHashedSoTheServerCanRejectThemByContent(FakeSaveFlavor flavor)
    {
        var broken = FakeSaveGenerator.CreateSave(_dir, 42, 1, 4096, flavor: flavor);
        Assert.True(broken.Size > 0);
        Assert.Equal(32, broken.Sha256.Length);
        if (flavor == FakeSaveFlavor.NotGzip)
        {
            Assert.NotEqual(0x1F, File.ReadAllBytes(broken.Path)[0]);
        }
    }

    [Fact]
    public void TheManifestIsAnX4mfFlatBufferOfTheStations()
    {
        var galaxy = FakeGalaxy.Generate(42, new GalaxyOptions { SectorCount = 20, ShipCount = 200 });
        var authority = new FakeAuthority(new FakeWorld(galaxy));
        var bytes = FakeSaveGenerator.BuildManifest(authority, new Id128T { Lo = 5, Hi = 6 }, 61.5, authority.NetIds.NextNetId);

        var bb = new ByteBuffer(bytes);
        Assert.True(Manifest.ManifestBufferHasIdentifier(bb));
        var manifest = Manifest.GetRootAsManifest(bb);
        Assert.Equal(galaxy.StationCount, manifest.EntriesLength);
        Assert.Equal(5UL, manifest.CheckpointId!.Value.Lo);
        Assert.Equal(61.5, manifest.GameTime);
        Assert.Equal(galaxy.Sectors.Count, manifest.SectorsLength);
        Assert.Equal(authority.Strings.Entries.Count, manifest.StringsLength);
        Assert.Equal(EntityKind.Station, manifest.Entries(0)!.Value.Kind);

        var file = FakeSaveGenerator.WriteManifest(_dir, "m", bytes);
        Assert.Equal(bytes.Length, file.Size);
        Assert.EndsWith(".x4mf", file.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSaveSizeOptionParses()
    {
        Assert.Equal(4, CliParser.Parse(["authority"]).Options!.SaveMb);
        Assert.Equal(200, CliParser.Parse(["authority", "--save-mb", "200"]).Options!.SaveMb);
        Assert.False(CliParser.Parse(["authority", "--save-mb", "0"]).Ok);
        Assert.False(CliParser.Parse(["authority", "--save-mb", "5000"]).Ok);
    }

    [Fact]
    public async Task ThePhaseTrackerFollowsTheRosterAndWaitsForAPhase()
    {
        var tracker = new FakePhaseTracker(playerId: 3);
        Assert.Null(tracker.Phase);
        Assert.False(await tracker.WaitForAsync(NodePhase.Loading, TimeSpan.FromMilliseconds(30), CancellationToken.None)); // nothing reported: times out

        Frame Roster(int player, NodePhase phase) => new(
            MsgType.RosterUpdate, FrameOptions.None, Lane.Control,
            MessageEncoder.EncodePayload(b => RosterUpdate.Pack(b, new RosterUpdateT
            {
                Full = false,
                Players = [new PlayerInfoT { PlayerId = (ushort)player, Name = "n", Phase = phase }],
                Removed = [],
            })));

        Assert.False(tracker.Observe(Roster(9, NodePhase.InGame))); // somebody else
        Assert.True(tracker.Observe(Roster(3, NodePhase.SyncingSave)));
        Assert.Equal(NodePhase.SyncingSave, tracker.Phase);

        var waiting = tracker.WaitForAsync(NodePhase.Matching, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        tracker.Observe(Roster(3, NodePhase.Loading));
        Assert.False(waiting.IsCompleted);
        tracker.Observe(Roster(3, NodePhase.Matching));
        Assert.True(await waiting);
    }
}
