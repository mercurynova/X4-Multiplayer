using X4MP.Launcher.Core;

namespace X4MP.Launcher.Core.Tests;

public class ExternalLinksTests
{
    [Fact]
    public void AWorkshopIdProducesBothTheSteamAndTheWebAddress()
    {
        Assert.Equal("steam://url/CommunityFilePage/1234567890", ExternalLinks.WorkshopSteam(1234567890));
        Assert.Equal("https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890", ExternalLinks.WorkshopPage(1234567890));
        Assert.Equal("steam://url/CommunityFilePage/1234567890", ExternalLinks.WorkshopOpen(1234567890));
    }

    [Fact]
    public void AModWithoutAWorkshopIdHasNoWorkshopAddress()
    {
        Assert.Null(ExternalLinks.WorkshopSteam(0));
        Assert.Null(ExternalLinks.WorkshopOpen(0));
    }

    [Fact]
    public void ANexusAddressIsCanonicalisedAndOnlyForX4()
    {
        Assert.Equal(
            "https://www.nexusmods.com/x4foundations/mods/42",
            ExternalLinks.NexusPage("https://www.nexusmods.com/x4foundations/mods/42"));
        Assert.Null(ExternalLinks.NexusPage("https://www.nexusmods.com/skyrimspecialedition/mods/42"));
        Assert.Null(ExternalLinks.NexusPage(""));
        Assert.Null(ExternalLinks.NexusPage(null));
    }

    [Theory]
    [InlineData("steam://url/CommunityFilePage/1234567890")]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890")]
    [InlineData("https://www.nexusmods.com/x4foundations/mods/42")]
    [InlineData("https://www.nexusmods.com/x4foundations/mods/42/")]
    public void ThePagesTheLauncherBuildsAreAllowed(string url)
    {
        Assert.True(ExternalLinks.IsAllowed(url), url);
    }

    [Theory]
    [InlineData("http://www.nexusmods.com/x4foundations/mods/42")]
    [InlineData("https://www.nexusmods.com/x4foundations/mods/42?tab=files")]
    [InlineData("https://www.nexusmods.com/x4foundations/mods/42/files")]
    [InlineData("https://www.nexusmods.com/skyrimspecialedition/mods/42")]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890&x=1")]
    [InlineData("steam://url/CommunityFilePage/1234567890/extra")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("https://example.com/")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsRefused(string? url)
    {
        Assert.False(ExternalLinks.IsAllowed(url), url ?? "(null)");
    }

    [Fact]
    public void TheGameItselfIsTheOneSteamPageTheLauncherMayOpen()
    {
        Assert.True(ExternalLinks.IsAllowed(ExternalLinks.SteamGame));
        Assert.Equal("steam://rungameid/392160", ExternalLinks.SteamGame);
    }

    [Theory]
    [InlineData("steam://rungameid/392161")]
    [InlineData("steam://rungameid/392160/extra")]
    [InlineData("steam://rungameid/")]
    [InlineData("steam://install/392160")]
    public void NoOtherSteamCommandIsAllowed(string url)
    {
        Assert.False(ExternalLinks.IsAllowed(url));
    }
}
