using System.Reflection;
using Microsoft.Extensions.FileProviders;
using X4MP.Server.Hosting;

namespace X4MP.Server.Tests;

/// <summary>The GUI is embedded via MSBuild Link metadata; a Windows-style separator there breaks Linux.</summary>
public class EmbeddedGuiTests
{
    private static readonly Assembly ServerAssembly = typeof(ServerHost).Assembly;

    [Fact]
    public void EmbeddedWwwrootContainsIndexHtml()
    {
        var provider = new ManifestEmbeddedFileProvider(ServerAssembly, "wwwroot");
        Assert.True(provider.GetFileInfo("index.html").Exists);
    }

    [Fact]
    public void NoEmbeddedResourceNameOrManifestEntryContainsBackslash()
    {
        Assert.DoesNotContain(ServerAssembly.GetManifestResourceNames(), n => n.Contains('\\', StringComparison.Ordinal));

        using var stream = ServerAssembly.GetManifestResourceStream("Microsoft.Extensions.FileProviders.Embedded.Manifest.xml");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var xml = reader.ReadToEnd();
        Assert.DoesNotContain('\\', xml);
        // The manifest task splits Link on the platform separator: a wrong separator yields a single File entry
        // named "wwwroot\index.html" / "wwwroot/index.html" instead of Directory wwwroot containing File index.html.
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        Assert.DoesNotContain(doc.Descendants("File"), f => ((string?)f.Attribute("Name"))!.Contains('/', StringComparison.Ordinal));
        Assert.Contains(doc.Descendants("Directory"), d => (string?)d.Attribute("Name") == "wwwroot");
    }
}
