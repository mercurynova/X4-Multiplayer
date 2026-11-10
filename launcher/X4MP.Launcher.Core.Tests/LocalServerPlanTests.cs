using X4MP.Launcher.Core;

namespace X4MP.Launcher.Core.Tests;

public class LocalServerPlanTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose()
    {
        _tree.Dispose();
        GC.SuppressFinalize(this);
    }

    private GameInstall Game()
    {
        var game = _tree.Dir("game");
        _tree.Touch("game/X4.exe");
        return new GameInstall(game, []);
    }

    [Fact]
    public void TheBundleBesideTheLauncherIsUsedFirst()
    {
        var launcher = _tree.Dir("launcher");
        _tree.Touch("launcher/server/x4mp-server.exe");
        var local = _tree.Dir("local");

        var plan = LocalServerPlan.Create(Game(), local, launcher);

        Assert.Equal(Path.Combine(launcher, "server", "x4mp-server.exe"), plan.ExePath);
        Assert.Equal(Path.Combine(local, "X4MP"), plan.StateDirectory);
        Assert.Equal(Path.Combine(local, "X4MP", "server-data"), plan.DataDirectory);
        Assert.Equal(Path.Combine(local, "X4MP", "logs"), plan.LogDirectory);
        Assert.Equal(LocalServerPlan.DefaultNodePort, plan.NodePort);
        Assert.Equal(LocalServerPlan.DefaultUdpPort, plan.UdpPort);
        Assert.Equal(LocalServerPlan.DefaultHttpPort, plan.HttpPort);
        Assert.Equal("127.0.0.1:47780", LocalServerPlan.HostAddress());
        Assert.Equal("http://127.0.0.1:47790/healthz", plan.HealthUri.ToString());
        Assert.Equal("http://127.0.0.1:47790/", plan.AdminAddress);
        Assert.Equal(
            ["--data-dir", plan.DataDirectory, "--port", "47790"],
            plan.Arguments);
        Assert.Equal("0.0.0.0:47780", plan.Environment["X4MP__Net__NodeTcpEndpoint"]);
        Assert.Equal("47781", plan.Environment["X4MP__Net__UdpPort"]);
        Assert.Equal(plan.AdminPassword, plan.Environment["X4MP__Net__AdminPassword"]);
    }

    [Fact]
    public void TheExtensionBundleIsTheFallback()
    {
        var game = Game();
        _tree.Touch("game/extensions/x4mp/server/x4mp-server.exe");
        var launcher = _tree.Dir("launcher");

        var plan = LocalServerPlan.Create(game, _tree.Dir("local"), launcher);

        Assert.Equal(
            Path.Combine(game.ExtensionsDir, "x4mp", "server", "x4mp-server.exe"),
            plan.ExePath);
    }

    [Fact]
    public void AMissingBundleNamesEveryPlaceThatWasTried()
    {
        var game = Game();
        var launcher = _tree.Dir("launcher");

        var error = Assert.Throws<FileNotFoundException>(
            () => LocalServerPlan.Create(game, _tree.Dir("local"), launcher));

        Assert.Contains("x4mp-server.exe", error.Message, StringComparison.Ordinal);
        Assert.Contains(launcher, error.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(game.ExtensionsDir, "x4mp"), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExistingAdminPasswordIsReused()
    {
        var launcher = _tree.Dir("launcher");
        _tree.Touch("launcher/server/x4mp-server.exe");
        _tree.Write("local/X4MP/server-data/admin-password.txt", "abcdefgh\r\n");

        var plan = LocalServerPlan.Create(Game(), _tree.Dir("local"), launcher);

        Assert.Equal("abcdefgh", plan.AdminPassword);
        Assert.Equal("abcdefgh", File.ReadAllText(plan.AdminPasswordFile).Trim());
    }

    [Fact]
    public void AShortAdminPasswordIsReplacedAndWritten()
    {
        var launcher = _tree.Dir("launcher");
        _tree.Touch("launcher/server/x4mp-server.exe");
        _tree.Write("local/X4MP/server-data/admin-password.txt", "short\n");

        var plan = LocalServerPlan.Create(Game(), _tree.Dir("local"), launcher);

        Assert.Equal(20, plan.AdminPassword.Length);
        Assert.Equal(plan.AdminPassword, File.ReadAllText(plan.AdminPasswordFile).Trim());
        Assert.All(plan.AdminPassword, character =>
            Assert.Contains(character, "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789"));
    }

    [Fact]
    public void TheGeneratedAdminPasswordIsStableAcrossStarts()
    {
        var launcher = _tree.Dir("launcher");
        _tree.Touch("launcher/server/x4mp-server.exe");
        var game = Game();
        var local = _tree.Dir("local");

        var first = LocalServerPlan.Create(game, local, launcher);
        var second = LocalServerPlan.Create(game, local, launcher);

        Assert.Equal(first.AdminPassword, second.AdminPassword);
    }

    [Fact]
    public void APortableExtensionKeepsTheServerDataInTheExtensionFolder()
    {
        var game = Game();
        _tree.Touch("game/extensions/x4mp/x4mp.portable");
        _tree.Touch("game/extensions/x4mp/server/x4mp-server.exe");
        var launcher = _tree.Dir("launcher");

        var plan = LocalServerPlan.Create(game, _tree.Dir("local"), launcher);

        Assert.Equal(Path.Combine(game.ExtensionsDir, "x4mp"), plan.StateDirectory);
        Assert.Equal(Path.Combine(game.ExtensionsDir, "x4mp", "server-data"), plan.DataDirectory);
    }

    [Fact]
    public void ThePortsFollowTheNodePort()
    {
        var launcher = _tree.Dir("launcher");
        _tree.Touch("launcher/server/x4mp-server.exe");

        var plan = LocalServerPlan.Create(Game(), _tree.Dir("local"), launcher, nodePort: 50000);

        Assert.Equal(50000, plan.NodePort);
        Assert.Equal(50001, plan.UdpPort);
        Assert.Equal(50010, plan.HttpPort);
        Assert.Equal("127.0.0.1:50000", LocalServerPlan.HostAddress(50000));
        Assert.Equal("0.0.0.0:50000", plan.Environment["X4MP__Net__NodeTcpEndpoint"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void APortOutsideTheRangeIsRefused(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LocalServerPlan.Create(Game(), _tree.Dir("local"), _tree.Dir("launcher"), port));
    }
}
