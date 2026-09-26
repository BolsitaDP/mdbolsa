using MdBolsa.Core.Tags;

namespace MdBolsa.Core.Tests.Tags;

public class TagParserTests
{
    [Fact]
    public void Extract_ReadsFlowSequenceTags()
    {
        var tags = TagParser.Extract("""
            ---
            id: 11111111-1111-1111-1111-111111111111
            tags: [development, homelab]
            ---

            # Docker
            """);

        Assert.Equal(["development", "homelab"], tags);
    }

    [Fact]
    public void Extract_ReadsBlockSequenceTags()
    {
        var tags = TagParser.Extract("""
            ---
            tags:
              - development
              - homelab
            ---
            """);

        Assert.Equal(["development", "homelab"], tags);
    }

    [Fact]
    public void Extract_ReadsSingleScalarAndCommaSeparatedTags()
    {
        Assert.Equal(["homelab"], TagParser.Extract("---\ntags: homelab\n---"));
        Assert.Equal(["a", "b"], TagParser.Extract("---\ntags: a, b\n---"));
    }

    [Fact]
    public void Extract_StripsQuotes_AndIgnoresValuesThatArentTags()
    {
        Assert.Equal(["quoted"], TagParser.Extract("---\ntags: [\"quoted\"]\n---"));
        Assert.Empty(TagParser.Extract("---\ntags: [\"a note about docker\"]\n---"));
        Assert.Empty(TagParser.Extract("---\ntags: []\n---"));
    }

    [Fact]
    public void Extract_IgnoresIndentedTagsKey_AndUsesTheFirstTopLevelOne()
    {
        Assert.Empty(TagParser.Extract("---\nnested:\n  tags: [notatag]\n---"));
        Assert.Equal(["first"], TagParser.Extract("---\ntags: [first]\ntags: [second]\n---"));
    }

    [Fact]
    public void Extract_ReadsInlineHashtags()
    {
        var tags = TagParser.Extract("Shipping #Docker notes, tagged #homelab/server too.");

        Assert.Equal(["docker", "homelab/server"], tags);
    }

    [Fact]
    public void Extract_MergesFrontMatterAndInlineTags_DeduplicatedAndLowercased()
    {
        var tags = TagParser.Extract("""
            ---
            tags: [Docker, homelab]
            ---

            More about #docker and #DOCKER.
            """);

        Assert.Equal(["docker", "homelab"], tags);
    }

    [Fact]
    public void Extract_IgnoresMarkdownHeadings()
    {
        var tags = TagParser.Extract("# Title\n\n## Section\n\n### Deep\n");

        Assert.Empty(tags);
    }

    [Fact]
    public void Extract_IgnoresHashInsideWords_AndNumericOnlyTags()
    {
        var tags = TagParser.Extract("Learning C# and F# is fun. See issue #42 and a#b.");

        Assert.Empty(tags);
    }

    [Fact]
    public void Extract_IgnoresTagsInsideCode()
    {
        var tags = TagParser.Extract("""
            Real #tag here.

            ```
            #notatag in a fence
            ```

            Inline `#alsonotatag` span.

            ~~~
            #alsonotatag either
            ~~~
            """);

        Assert.Equal(["tag"], tags);
    }

    [Fact]
    public void Extract_IgnoresFrontmatterTagsAsInlineTags()
    {
        // The frontmatter block is stripped before inline scanning, so a `tags:`
        // line can never contribute through the inline path as well. (The inline
        // tag here is mid-sentence on purpose - `# beta` on its own line would be a
        // heading, not a tag.)
        var tags = TagParser.Extract("---\ntags: [alpha]\n---\n\nBody text #beta\n");

        Assert.Equal(["alpha", "beta"], tags);
    }

    [Fact]
    public void Extract_HandlesCrlfLineEndings()
    {
        var tags = TagParser.Extract("---\r\ntags: [alpha]\r\n---\r\n\r\nBody text #beta\r\n");

        Assert.Equal(["alpha", "beta"], tags);
    }

    [Fact]
    public void Extract_ReturnsNothing_ForNoteWithoutTags()
    {
        Assert.Empty(TagParser.Extract("Just a note."));
        Assert.Empty(TagParser.Extract(""));
    }

    [Fact]
    public void ExtractFrontMatterOnly_SkipsInlineTags()
    {
        var tags = TagParser.ExtractFrontMatterOnly("---\ntags: [alpha]\n---\n\n# beta\n");

        Assert.Equal(["alpha"], tags);
    }

    [Theory]
    [InlineData("Docker", "docker")]
    [InlineData("#docker", "docker")]
    [InlineData("  #Docker  ", "docker")]
    [InlineData("project/", "project")]
    [InlineData("#", "")]
    [InlineData("", "")]
    public void Normalize_ProducesTheStoredTagForm(string input, string expected) =>
        Assert.Equal(expected, TagParser.Normalize(input));
}
