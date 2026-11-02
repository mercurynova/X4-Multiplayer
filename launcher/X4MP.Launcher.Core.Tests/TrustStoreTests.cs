using X4MP.Launcher.Core;

namespace X4MP.Launcher.Core.Tests;

public class TrustStoreTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose()
    {
        _tree.Dispose();
        GC.SuppressFinalize(this);
    }

    private static readonly string[] Localhost = ["http://localhost:47790/"];

    private string Path_ => _tree.Resolve("trusted-servers.json");

    [Fact]
    public void AStoreWithoutAFileTrustsNothing()
    {
        var store = TrustStore.Load(Path_);

        Assert.Empty(store.Servers);
        Assert.False(store.IsTrusted("http://localhost:47790/"));
        Assert.False(File.Exists(Path_));
    }

    [Fact]
    public void TrustingAServerSurvivesAReload()
    {
        var store = TrustStore.Load(Path_);

        Assert.True(store.Trust("http://localhost:47790/"));
        Assert.True(store.IsTrusted("http://localhost:47790/"));

        var reloaded = TrustStore.Load(Path_);
        Assert.True(reloaded.IsTrusted("http://localhost:47790/"));
        Assert.Single(reloaded.Servers);
    }

    [Fact]
    public void TrustingTheSameServerTwiceChangesNothing()
    {
        var store = TrustStore.Load(Path_);

        Assert.True(store.Trust("http://localhost:47790/"));
        Assert.False(store.Trust("http://localhost:47790/"));
        Assert.Single(store.Servers);
    }

    [Fact]
    public void AnUnknownOrEmptyAddressIsNotTrusted()
    {
        var store = TrustStore.Load(Path_);

        Assert.False(store.IsTrusted("http://192.168.1.20:47790/"));
        Assert.False(store.IsTrusted(""));
        Assert.False(store.IsTrusted("not an address"));
        Assert.False(store.IsTrusted(null));
    }

    [Fact]
    public void AUsedKeyIsNormalisedBeforeItIsStored()
    {
        var store = TrustStore.Load(Path_);

        Assert.True(store.Trust("LOCALHOST:47790/some/path"));

        var reloaded = TrustStore.Load(Path_);
        Assert.True(reloaded.IsTrusted("http://localhost:47790/"));
        Assert.Equal(Localhost, reloaded.Servers);
    }

    [Fact]
    public void ADamagedFileIsTreatedAsNoTrust()
    {
        _tree.Write("trusted-servers.json", "{ this is not json");

        var store = TrustStore.Load(Path_);

        Assert.Empty(store.Servers);
        Assert.False(store.IsTrusted("http://localhost:47790/"));
    }

    [Fact]
    public void DuplicatesInTheFileAreDroppedOnLoad()
    {
        _tree.Write(
            "trusted-servers.json",
            "{\"version\":1,\"servers\":[\"http://localhost:47790/\",\"http://localhost:47790/\",\"http://192.168.1.20:47790/\"]}");

        var store = TrustStore.Load(Path_);

        Assert.Equal(2, store.Servers.Count);
    }
}
