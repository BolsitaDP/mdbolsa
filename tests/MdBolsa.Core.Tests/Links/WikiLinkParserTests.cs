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

    [Fact]
    public void AnEmbeddedImageIsNotALink()
    {
        // "![[diagram.png]]" is a reference to an attachment, not to a note. Reading
        // it as a link made every note with a picture in it report an unresolved link,
        // which is how a status line stops being read.
        Assert.Empty(WikiLinkParser.ExtractTargets("![[diagram.png]]"));
        Assert.Empty(WikiLinkParser.ExtractTargets("![[diagram.png|The diagram]]"));
    }

    [Fact]
    public void AnEmbedAndALinkInTheSameLineBothBehave()
    {
        var targets = WikiLinkParser.ExtractTargets("![[a.png]] and [[Docker]]");

        Assert.Equal(["Docker"], targets);
    }
}
