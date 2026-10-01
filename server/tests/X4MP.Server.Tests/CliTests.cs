using X4MP.Server.Hosting;

namespace X4MP.Server.Tests;

public class CliTests
{
    [Fact]
    public void NoArgsMeansRun()
    {
        var cli = CliArguments.Parse([]);
        Assert.Equal(CliVerb.Run, cli.Verb);
        Assert.Null(cli.Error);
    }

    [Theory]
    [InlineData("version", CliVerb.Version)]
    [InlineData("run", CliVerb.Run)]
    [InlineData("VERSION", CliVerb.Version)]
    public void ParsesVerbs(string verb, CliVerb expected) =>
        Assert.Equal(expected, CliArguments.Parse([verb]).Verb);

    [Fact]
    public void ParsesOptionsInBothForms()
    {
        var cli = CliArguments.Parse(["run", "--data-dir", "C:\\x", "--port=5000", "--service", "--urls", "http://a"]);
        Assert.Equal("C:\\x", cli.DataDir);
        Assert.Equal(5000, cli.Port);
        Assert.True(cli.ServiceFlag);
        Assert.Equal(["--urls", "http://a"], cli.Remaining);
        Assert.Null(cli.Error);
    }

    [Fact]
    public void OptionsWithoutVerbWork() =>
        Assert.Equal("d", CliArguments.Parse(["--data-dir=d"]).DataDir);

    [Theory]
    [InlineData("bogus")]
    [InlineData("--port", "abc")]
    [InlineData("--data-dir")]
    [InlineData("service")]
    [InlineData("service", "restart")]
    public void BadInputProducesError(params string[] args) =>
        Assert.NotNull(CliArguments.Parse(args).Error);

    [Fact]
    public void DataDirPrecedenceIsArgThenEnvThenDefault()
    {
        var exe = Path.Combine(Path.GetTempPath(), "exe");
        Assert.Equal(Path.GetFullPath("argdir"), DataDirResolver.Resolve("argdir", "envdir", false, exe));
        Assert.Equal(Path.GetFullPath("envdir"), DataDirResolver.Resolve(null, "envdir", false, exe));
        Assert.Equal(Path.Combine(exe, "data"), DataDirResolver.Resolve(null, null, false, exe));
        Assert.Equal(DataDirResolver.ServiceDataDir(), DataDirResolver.Resolve(" ", "", true, exe));
        Assert.EndsWith("X4MP", DataDirResolver.ServiceDataDir(), StringComparison.Ordinal);
    }

    [Fact]
    public void VersionTextHasVersionProtocolAndBuild()
    {
        var text = new ServerInfo().DescribeVersion();
        Assert.Contains("x4mp-server 0.1.0", text, StringComparison.Ordinal);
        Assert.Contains("protocol: 0.1", text, StringComparison.Ordinal);
        Assert.Contains("build: ", text, StringComparison.Ordinal);
    }
}
