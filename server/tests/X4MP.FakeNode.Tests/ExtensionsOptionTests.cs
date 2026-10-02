using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode.Tests;

public sealed class ExtensionsOptionTests
{
    private const string Json = """
        [
          // comments and trailing commas are fine
          { "id": "ego_dlc_split", "name": "Split Vendetta", "version": "900", "source": "Dlc", "classHint": "Dlc" },
          { "id": "ws_1234567890", "name": "Warehouse Fleets", "version": "1.4", "source": "Workshop", "workshopId": 1234567890, "classHint": "Sim", "contentHash": "0a0b" },
          { "id": "ui_mod", "enabled": false, "classHint": "ClientOnly" },
        ]
        """;

    [Fact]
    public void ParsesTheJsonFormat()
    {
        var list = ExtensionListFile.Parse(Json);

        Assert.Equal(3, list.Count);
        Assert.Equal(("ego_dlc_split", "Split Vendetta", "900", ExtensionSource.Dlc, true), (list[0].Id, list[0].Name, list[0].Version, list[0].Source, list[0].Enabled));
        Assert.Equal(1234567890ul, list[1].WorkshopId);
        Assert.Equal([0x0a, 0x0b], list[1].ContentHash);
        Assert.Equal(ExtensionClass.Sim, list[1].ClassHint);
        Assert.False(list[2].Enabled);
        Assert.Equal("ui_mod", list[2].Name); // name defaults to the id
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[{\"version\":\"1\"}]")]
    [InlineData("[{\"id\":\"x\",\"source\":\"Nowhere\"}]")]
    [InlineData("[{\"id\":\"x\",\"contentHash\":\"zz\"}]")]
    public void RejectsBadFiles(string json) => Assert.ThrowsAny<Exception>(() => ExtensionListFile.Parse(json));

    [Fact]
    public void TheCommandLineLoadsTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "x4mp-ext-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, Json);
        try
        {
            var ok = CliParser.Parse(["client", "--name", "Bob", "--extensions", path]);
            Assert.True(ok.Ok, ok.Error);
            Assert.Equal(["ego_dlc_split", "ws_1234567890", "ui_mod"], ok.Options!.Extensions!.Select(e => e.Id));

            var none = CliParser.Parse(["client"]);
            Assert.Null(none.Options!.Extensions);

            var missing = CliParser.Parse(["client", "--extensions", path + ".missing"]);
            Assert.False(missing.Ok);
            Assert.Contains("--extensions", missing.Error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheHelloHashIsTheDocumentedOneForTheReportedList()
    {
        var list = ExtensionListFile.Parse(Json);
        var expected = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("ego_dlc_split@900\nws_1234567890@1.4\n"));
        Assert.Equal(expected, ExtensionReports.ComputeHash(list)); // the disabled client-only mod is not part of it
    }
}
