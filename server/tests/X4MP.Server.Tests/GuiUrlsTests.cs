using X4MP.Core.Net;
using X4MP.Server.Hosting;

namespace X4MP.Server.Tests;

public sealed class GuiUrlsTests
{
    [Fact]
    public void TheNodeTcpListenerIsNotListedAsAGuiUrl()
    {
        var net = new NetOptions { NodeTcpEndpoint = "0.0.0.0:47780" };
        var urls = GuiUrls.Filter(["http://0.0.0.0:47780", "http://localhost:47790", "http://[::]:47790"], net);
        Assert.Equal(["http://localhost:47790", "http://[::]:47790"], urls);
    }

    [Fact]
    public void WithoutNodeNetworkingEveryAddressIsAGuiUrl()
    {
        Assert.Equal(["http://localhost:47790"], GuiUrls.Filter(["http://localhost:47790"], null));
        Assert.Equal(["http://localhost:47780"], GuiUrls.Filter(["http://localhost:47780"], new NetOptions { Enabled = false }));
    }
}
