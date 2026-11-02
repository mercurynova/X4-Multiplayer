using X4MP.Launcher.Core;

namespace X4MP.Launcher.Core.Tests;

public class ServerAddressTests
{
    [Theory]
    [InlineData("192.168.1.20:47790", "http://192.168.1.20:47790/")]
    [InlineData("localhost:47790", "http://localhost:47790/")]
    [InlineData("http://localhost:47790", "http://localhost:47790/")]
    [InlineData("HTTP://Example.COM:47790", "http://example.com:47790/")]
    [InlineData("https://example.com:47790", "https://example.com:47790/")]
    public void ABareAddressBecomesAUrlWithATrailingSlash(string text, string expected)
    {
        Assert.Equal(expected, ServerAddress.Normalise(text).ToString());
    }

    [Fact]
    public void APathQueryAndFragmentAreDropped()
    {
        var uri = ServerAddress.Normalise("http://example.com:47790/api/v1/join/mod-manifest?x=1#frag");

        Assert.Equal("http://example.com:47790/", uri.ToString());
        Assert.Equal("/", uri.AbsolutePath);
        Assert.Equal("", uri.Query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankAddressIsRejectedWithAnExample(string? text)
    {
        var error = Assert.Throws<ArgumentException>(() => ServerAddress.Normalise(text));

        Assert.Contains("192.168.1.20:47790", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not a host")]
    [InlineData("ftp://example.com")]
    [InlineData("http://")]
    public void SomethingThatIsNotAnHttpAddressIsRejected(string text)
    {
        var error = Assert.Throws<ArgumentException>(() => ServerAddress.Normalise(text));

        Assert.Contains("not a server address", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheKeyIsTheNormalisedAddressSoTwoSpellingsMatch()
    {
        Assert.Equal(
            ServerAddress.Key("192.168.1.20:47790"),
            ServerAddress.Key("HTTP://192.168.1.20:47790/somewhere"));
    }

    [Theory]
    [InlineData("192.168.1.20:47790", "192.168.1.20:47790")]
    [InlineData("http://localhost:47790/api/v1/join/mod-manifest", "localhost:47790")]
    [InlineData("https://Example.COM", "example.com:443")]
    [InlineData("http://example.com", "example.com:80")]
    [InlineData("http://[::1]:47790", "[::1]:47790")]
    public void TheModGetsAHostAndAPortWithoutAScheme(string address, string expected)
    {
        Assert.Equal(expected, ServerAddress.HostAndPort(address));
    }
}
