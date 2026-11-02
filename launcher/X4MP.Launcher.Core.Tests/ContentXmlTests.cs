namespace X4MP.Launcher.Core.Tests;

public class ContentXmlTests
{
    [Fact]
    public void ReadsTheContentElementAndItsDependencies()
    {
        var description = ContentXml.Parse("""
            <?xml version="1.0" encoding="utf-8"?>
            <!-- <content id="commented_out"/> -->
            <content id="my_mod" name="My &amp; Mod" version="105" enabled="0" save="1">
              <dependency id="x4native" optional="false"/>
              <dependency id="other"/>
            </content>
            """);

        Assert.NotNull(description);
        Assert.Equal("my_mod", description.Id);
        Assert.Equal("My & Mod", description.Name);
        Assert.Equal("105", description.Version);
        Assert.False(description.EnabledByDefault);
        Assert.True(description.SaveDependent);
        Assert.Equal(2, description.Dependencies.Count);
        Assert.Equal(new ContentDependency("x4native", false), description.Dependencies[0]);
        Assert.Equal(new ContentDependency("other", false), description.Dependencies[1]);
    }

    [Fact]
    public void MissingFlagsMeanEnabledAndNotSaveRelevant()
    {
        var description = ContentXml.Parse("""<content id="minimal"/>""");

        Assert.NotNull(description);
        Assert.True(description.EnabledByDefault);
        Assert.False(description.SaveDependent);
        Assert.Empty(description.Dependencies);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("YES", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("No", false)]
    [InlineData("maybe", true)]
    public void ReadsTheBooleanSpellingX4Uses(string value, bool expected)
    {
        var description = ContentXml.Parse($"<content id=\"mod\" enabled=\"{value}\"/>");

        Assert.NotNull(description);
        Assert.Equal(expected, description.EnabledByDefault);
    }

    [Fact]
    public void AcceptsSingleQuotedAttributes()
    {
        var description = ContentXml.Parse("<content id='mod' name='One' version='900'/>");

        Assert.NotNull(description);
        Assert.Equal("mod", description.Id);
        Assert.Equal("One", description.Name);
    }

    [Fact]
    public void AnAttributeWithoutQuotesEndsTheScan()
    {
        Assert.Null(ContentXml.Parse("""<content id=broken name="x"/>"""));
    }

    [Fact]
    public void AnEmptyIdIsNotAMod()
    {
        Assert.Null(ContentXml.Parse("""<content id="" name="x"/>"""));
        Assert.Null(ContentXml.Parse("""<content name="x"/>"""));
    }

    [Fact]
    public void TextWithoutAContentTagIsNotAMod()
    {
        Assert.Null(ContentXml.Parse(string.Empty));
        Assert.Null(ContentXml.Parse("""<content.xml><extension id="x"/></content.xml>"""));
    }

    [Theory]
    [InlineData("900", "9.00")]
    [InlineData("105", "1.05")]
    [InlineData("100", "1.00")]
    [InlineData("000900", "9.00")]
    [InlineData("12", "12")]
    [InlineData("1.30", "1.30")]
    [InlineData("", "")]
    [InlineData("  900  ", "9.00")]
    public void NormalisesVersionsTheWayTheGameWritesThem(string value, string expected)
    {
        Assert.Equal(expected, ContentXml.NormaliseVersion(value));
    }
}
