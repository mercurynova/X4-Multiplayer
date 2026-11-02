using System.Text.Json;
using X4MP.Launcher.Core;

namespace X4MP.Launcher.Core.Tests;

public class LaunchFileTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose()
    {
        _tree.Dispose();
        GC.SuppressFinalize(this);
    }

    private static DateTimeOffset Expiry => new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheFileCarriesTheServerTheNameThePasswordAndTheExpiry()
    {
        var path = LaunchFile.Write(_tree.Root, "http://127.0.0.1:47790", "Alice", "secret", Expiry);

        Assert.Equal(_tree.Resolve("launch.json"), path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal("127.0.0.1:47790", root.GetProperty("server").GetString());
        Assert.Equal("Alice", root.GetProperty("name").GetString());
        Assert.Equal("secret", root.GetProperty("password").GetString());
        Assert.Equal("2026-11-02T12:00:00Z", root.GetProperty("expires_utc").GetString());
    }

    [Fact]
    public void TheFileHasNoBomAndNoLineBreak()
    {
        var path = LaunchFile.Write(_tree.Root, "localhost:47790", "Alice", string.Empty, Expiry);

        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.DoesNotContain((byte)'\n', bytes);
        Assert.DoesNotContain((byte)'\r', bytes);
    }

    [Fact]
    public void ASessionWithoutAPasswordWritesNoPasswordField()
    {
        var path = LaunchFile.Write(_tree.Root, "localhost:47790", "Alice", string.Empty, Expiry);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.False(document.RootElement.TryGetProperty("password", out _));
    }

    [Fact]
    public void ASecondWriteReplacesTheFirstOne()
    {
        LaunchFile.Write(_tree.Root, "localhost:47790", "Alice", "first", Expiry);

        var path = LaunchFile.Write(_tree.Root, "localhost:47790", "Alice", string.Empty, Expiry);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.False(document.RootElement.TryGetProperty("password", out _));
        Assert.Equal("Alice", document.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void TheFolderIsCreatedWhenItDoesNotExistYet()
    {
        var folder = _tree.Resolve("nested/X4MP");

        var path = LaunchFile.Write(folder, "localhost:47790", "Alice", string.Empty, Expiry);

        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankNameIsRefused(string name)
    {
        var error = Assert.Throws<ArgumentException>(
            () => LaunchFile.Write(_tree.Root, "localhost:47790", name, string.Empty, Expiry));

        Assert.Contains("name the other players will see", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(_tree.Resolve("launch.json")));
    }

    [Fact]
    public void ABadServerAddressIsRefusedBeforeAnythingIsWritten()
    {
        Assert.Throws<ArgumentException>(
            () => LaunchFile.Write(_tree.Root, "not an address", "Alice", string.Empty, Expiry));

        Assert.False(File.Exists(_tree.Resolve("launch.json")));
    }

    [Fact]
    public void TheExtensionFolderIsUsedWhenTheModRunsPortable()
    {
        var game = _tree.Dir("game");
        var install = new GameInstall(game, []);
        _tree.Touch("game/extensions/x4mp/x4mp.portable");

        var folder = LaunchFile.DirectoryFor(install, _tree.Dir("local"));

        Assert.Equal(Path.Combine(game, "extensions", "x4mp"), folder);
    }

    [Fact]
    public void AnOrdinaryInstallUsesLocalAppData()
    {
        var game = _tree.Dir("game");
        var local = _tree.Dir("local");
        var install = new GameInstall(game, []);

        Assert.Equal(Path.Combine(local, "X4MP"), LaunchFile.DirectoryFor(install, local));
        Assert.Equal(Path.Combine(local, "X4MP"), LaunchFile.DirectoryFor(null, local));
    }

    [Fact]
    public void ANameOutsideAsciiIsWrittenAsUtf8()
    {
        var path = LaunchFile.Write(_tree.Root, "localhost:47790", "\u0410\u043B\u0438\u0441\u0430", string.Empty, Expiry);

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal("\u0410\u043B\u0438\u0441\u0430", document.RootElement.GetProperty("name").GetString());
    }
}
