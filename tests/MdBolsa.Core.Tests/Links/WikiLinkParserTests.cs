using MdBolsa.Core.Links;

namespace MdBolsa.Core.Tests.Links;

public class WikiLinkParserTests
{
    [Fact]
    public void ExtractTargets_ReturnsEmpty_WhenNoLinks()
    {
        Assert.Empty(WikiLinkParser.ExtractTargets("# Note\n\nNo links here.\n"));
    }

    [Fact]
    public void ExtractTargets_FindsSimpleLink()
    {
        var targets = WikiLinkParser.ExtractTargets("See [[Welcome]] for more.");

        Assert.Equal(new[] { "Welcome" }, targets);
    }

    [Fact]
    public void ExtractTargets_FindsMultipleLinks()
    {
        var targets = WikiLinkParser.ExtractTargets("[[Personal/Home]] and [[Development/Docker]].");

        Assert.Equal(new[] { "Personal/Home", "Development/Docker" }, targets);
    }

    [Fact]
    public void ExtractTargets_StripsDisplayText()
    {
        var targets = WikiLinkParser.ExtractTargets("[[Welcome|the welcome note]]");

        Assert.Equal(new[] { "Welcome" }, targets);
    }

    [Fact]
    public void ExtractTargets_StripsHeadingReference()
    {
        var targets = WikiLinkParser.ExtractTargets("[[Welcome#Introduction]]");

        Assert.Equal(new[] { "Welcome" }, targets);
    }

    [Fact]
    public void ExtractTargets_IgnoresEmptyBrackets()
    {
        Assert.Empty(WikiLinkParser.ExtractTargets("[[]]"));
    }
}
