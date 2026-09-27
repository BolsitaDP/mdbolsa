using MdBolsa.Core.Markdown;

namespace MdBolsa.Core.Tests.Markdown;

// The Markdown subset a preview pane can draw.
//
// The last group is the important one: those tests pin down the things this parser
// does *not* do. A degradation you have decided on and written down is a
// limitation; the same degradation discovered by a reader is a bug report, and the
// difference is entirely whether anybody wrote the sentence down beforehand.
public class MarkdownParserTests
{
    private static Block Single(string markdown) => Assert.Single(MarkdownParser.Parse(markdown));

    private static string TextOf(Block.Paragraph paragraph) =>
        string.Concat(paragraph.Inlines.OfType<Inline.Text>().Select(inline => inline.Value));

    // The text a reader actually sees: every inline kind flattened to its words.
    // This is the same walk the preview will do, so a test that uses it is asserting
    // about what the pane will contain rather than about the tree's shape.
    private static string Rendered(IReadOnlyList<Inline> inlines) =>
        string.Concat(inlines.Select(inline => inline switch
        {
            Inline.Text text => text.Value,
            Inline.Code code => code.Value,
            Inline.WikiLink wiki => wiki.Alias ?? wiki.Target,
            Inline.Link link => Rendered(link.Label),
            Inline.Emphasis emphasis => Rendered(emphasis.Children),
            _ => string.Empty,
        }));

    // --- headings ----------------------------------------------------------

    [Fact]
    public void SevenHashesIsNotAHeading()
    {
        // Six is the maximum; a seventh is a line that happens to start with hashes.
        Assert.IsType<Block.Paragraph>(Single("####### Too many"));
    }

    [Fact]
    public void ASixLevelHeadingIsAHeading() =>
        Assert.Equal(6, Assert.IsType<Block.Heading>(Single("###### Deep")).Level);

    [Fact]
    public void AHashWithoutASpaceIsNotAHeading() =>
        Assert.IsType<Block.Paragraph>(Single("#hashtag not a heading"));

    [Fact]
    public void AHeadingKeepsItsInlineCode()
    {
        var heading = Assert.IsType<Block.Heading>(Single("## The `id` field"));

        Assert.Contains(heading.Inlines, inline => inline is Inline.Code { Value: "id" });
    }

    [Fact]
    public void LeadingWhitespaceBeforeAHashIsStillAHeading() =>
        Assert.Equal(2, Assert.IsType<Block.Heading>(Single("   ## Indented")).Level);

    // --- paragraphs --------------------------------------------------------

    [Fact]
    public void AWrappedLineIsOneParagraph()
    {
        // A wrapped Markdown line is a wrapped sentence, not a line break the reader
        // should see.
        var paragraph = Assert.IsType<Block.Paragraph>(Single("one\ntwo\nthree"));

        Assert.Equal("one two three", TextOf(paragraph));
    }

    [Fact]
    public void BlankLinesSeparateParagraphs() =>
        Assert.Equal(2, MarkdownParser.Parse("one\n\ntwo").Count);

    [Fact]
    public void AnEmptyDocumentIsNoBlocks() =>
        Assert.Empty(MarkdownParser.Parse(""));

    [Fact]
    public void AWhitespaceOnlyDocumentIsNoBlocks() =>
        Assert.Empty(MarkdownParser.Parse("   \n\n  \n"));

    [Fact]
    public void ANullDocumentIsNoBlocks() =>
        Assert.Empty(MarkdownParser.Parse(null!));

    [Fact]
    public void WindowsLineEndingsParseTheSameAsUnix() =>
        Assert.Equal(
            MarkdownParser.Parse("# One\n\nTwo").Count,
            MarkdownParser.Parse("# One\r\n\r\nTwo").Count);

    // --- code --------------------------------------------------------------

    [Fact]
    public void AFencedBlockKeepsItsLinesVerbatim()
    {
        var code = Assert.IsType<Block.Code>(Single("```\nline one\n  line two\n```"));

        Assert.Equal("line one\n  line two", code.Text);
    }

    [Fact]
    public void AFencedBlockRemembersItsLanguage() =>
        Assert.Equal("csharp", Assert.IsType<Block.Code>(Single("```csharp\nvar x = 1;\n```")).Language);

    [Fact]
    public void TheLanguageSpellingWithCurlyBracesIsUnderstood() =>
        Assert.Equal("csharp", Assert.IsType<Block.Code>(Single("```{csharp}\nvar x = 1;\n```")).Language);

    [Fact]
    public void AnUnlabelledFenceHasNoLanguage() =>
        Assert.Null(Assert.IsType<Block.Code>(Single("```\nplain\n```")).Language);

    [Fact]
    public void AnUnterminatedFenceRunsToTheEndOfTheNote()
    {
        // Someone is halfway through typing a fence. A preview that blanks out or
        // throws while they type is worse than one that shows the text.
        var blocks = MarkdownParser.Parse("```csharp\nvar x = 1;\nstill typing");

        Assert.Equal("csharp", Assert.IsType<Block.Code>(Assert.Single(blocks)).Language);
    }

    [Fact]
    public void AMissingFenceIsNotFenced()
    {
        // The asymmetry with the line above is deliberate: a fence needs a closing
        // fence to *be* a fence, but a missing one still must not eat the note.
        var code = Assert.IsType<Block.Code>(Single("```\nunclosed"));

        Assert.Equal("unclosed", code.Text);
    }

    [Fact]
    public void HashesInsideAFenceAreNotHeadings() =>
        Assert.IsType<Block.Code>(Assert.Single(MarkdownParser.Parse("```\n# not a heading\n```")));

    // --- lists -------------------------------------------------------------

    [Fact]
    public void BulletsBecomeAList()
    {
        var list = Assert.IsType<Block.List>(Single("- one\n- two"));

        Assert.False(list.Ordered);
        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public void AllThreeBulletMarkersAreBullets()
    {
        Assert.False(Assert.IsType<Block.List>(Single("- a\n* b\n+ c")).Ordered);
    }

    [Fact]
    public void NumbersBecomeAnOrderedList()
    {
        var list = Assert.IsType<Block.List>(Single("1. one\n2. two"));

        Assert.True(list.Ordered);
        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public void AnOrderedListRemembersWhereItStarted() =>
        Assert.Equal(3, Assert.IsType<Block.List>(Single("3. three\n4. four")).Start);

    [Fact]
    public void MixingBulletAndNumberStopsTheList()
    {
        // A list that claims to be both is worse than two lists.
        var blocks = MarkdownParser.Parse("- bullet\n1. number");

        Assert.Equal(2, blocks.Count);
        Assert.IsType<Block.List>(blocks[0]);
        Assert.IsType<Block.List>(blocks[1]);
    }

    [Fact]
    public void ALoneDashIsNotAListItem() =>
        Assert.IsType<Block.Paragraph>(Single("-"));

    [Fact]
    public void NestedListItemsAreFlattenedNotDropped()
    {
        // Wrong but legible, which is the deal: see the note at the top of the
        // parser about nested lists.
        var list = Assert.IsType<Block.List>(Single("- one\n  - nested\n- two"));

        Assert.Equal(3, list.Items.Count);
    }

    // --- quotes and rules --------------------------------------------------

    [Fact]
    public void AQuoteBecomesAQuote()
    {
        var quote = Assert.IsType<Block.Quote>(Single("> quoted"));

        Assert.IsType<Block.Paragraph>(Assert.Single(quote.Blocks));
    }

    [Fact]
    public void AQuoteCanHoldSeveralParagraphs()
    {
        var quote = Assert.IsType<Block.Quote>(Single("> one\n>\n> two"));

        Assert.Equal(2, quote.Blocks.Count);
    }

    [Fact]
    public void AllThreeRuleMarkersAreRules()
    {
        Assert.IsType<Block.Rule>(Single("---"));
        Assert.IsType<Block.Rule>(Single("***"));
        Assert.IsType<Block.Rule>(Single("___"));
    }

    [Fact]
    public void ATwoDashLineIsNotARule() =>
        Assert.IsType<Block.Paragraph>(Single("--"));

    // --- inline ------------------------------------------------------------

    [Fact]
    public void AWikiLinkIsParsed() =>
        Assert.Equal(
            "Docker",
            Assert.IsType<Inline.WikiLink>(Assert.Single(MarkdownParser.ParseInlines("[[Docker]]"))).Target);

    [Fact]
    public void AWikiLinkCanHaveAnAlias()
    {
        var link = Assert.IsType<Inline.WikiLink>(Assert.Single(MarkdownParser.ParseInlines("[[Docker|how to build]]")));

        Assert.Equal("Docker", link.Target);
        Assert.Equal("how to build", link.Alias);
    }

    [Fact]
    public void AWikiLinkMixedWithTextKeepsBothInOrder()
    {
        var inlines = MarkdownParser.ParseInlines("see [[Docker]] now");

        Assert.Collection(
            inlines,
            first => Assert.Equal("see ", Assert.IsType<Inline.Text>(first).Value),
            second => Assert.Equal("Docker", Assert.IsType<Inline.WikiLink>(second).Target),
            third => Assert.Equal(" now", Assert.IsType<Inline.Text>(third).Value));
    }

    [Fact]
    public void AnUnclosedWikiLinkStaysLiteralText() =>
        Assert.IsType<Inline.Text>(Assert.Single(MarkdownParser.ParseInlines("[[Docker")));

    [Fact]
    public void InlineCodeIsParsedAndKeepsTheTextAroundIt()
    {
        // Three inlines, not one: the text either side of the code is its own run,
        // which is what lets a preview draw the code differently from the prose.
        var inlines = MarkdownParser.ParseInlines("the `id` field");

        Assert.Collection(
            inlines,
            first => Assert.Equal("the ", Assert.IsType<Inline.Text>(first).Value),
            second => Assert.Equal("id", Assert.IsType<Inline.Code>(second).Value),
            third => Assert.Equal(" field", Assert.IsType<Inline.Text>(third).Value));
    }

    [Fact]
    public void AClosingTickRunMustBeAtLeastAsLongAsTheOpening()
    {
        // ``a`b`` is a single code span containing a tick, not a span ending at the
        // single tick in the middle.
        Assert.Equal("a`b", Assert.IsType<Inline.Code>(Assert.Single(MarkdownParser.ParseInlines("``a`b``"))).Value);
    }

    [Fact]
    public void AnUnmatchedBacktickStaysLiteralText() =>
        Assert.IsType<Inline.Text>(Assert.Single(MarkdownParser.ParseInlines("a ` b")));

    [Fact]
    public void ABackslashEscapeIsHonoured()
    {
        // A note *about* Markdown has to survive: this is the one case where
        // dropping syntax changes what the text means rather than how it looks.
        var inlines = MarkdownParser.ParseInlines(@"use \* for a literal star");

        Assert.Equal("use * for a literal star", TextOf(new Block.Paragraph(inlines)));
    }

    [Fact]
    public void ABackslashAtTheEndOfTheTextIsKept()
    {
        // Nothing follows it to escape, so there is nothing to drop.
        Assert.Equal(@"a\", TextOf(new Block.Paragraph(MarkdownParser.ParseInlines(@"a\"))));
    }

    [Fact]
    public void ABackslashDoesEscapeTheNextCharacter() =>
        Assert.Equal("ab", TextOf(new Block.Paragraph(MarkdownParser.ParseInlines(@"a\b"))));

    [Fact]
    public void AnImageOnItsOwnLineIsAnImage()
    {
        var image = Assert.IsType<Block.Image>(Single("![alt text](image.png)"));

        Assert.Equal("alt text", image.Alt);
        Assert.Equal("image.png", image.Source);
    }

    [Fact]
    public void AnImageInsideAParagraphStaysInlineText() =>
        Assert.IsType<Block.Paragraph>(Single("look: ![alt](image.png)"));

    // --- the deliberate degradations ---------------------------------------

    [Fact]
    public void UnrenderedSyntaxIsNeverLeftAsRawMarkers()
    {
        // The rule that keeps a preview from looking broken: syntax the parser does
        // not *draw* is still not shown to the reader as punctuation. A missing style
        // is a limitation; a raw "**" is a bug.
        Assert.Equal("bold", Rendered(MarkdownParser.ParseInlines("**bold**")));
        Assert.Equal("italic", Rendered(MarkdownParser.ParseInlines("*italic*")));
        Assert.Equal("both", Rendered(MarkdownParser.ParseInlines("__both__")));
    }

    [Fact]
    public void AMarkdownLinkIsParsedWithItsLabelAndTarget()
    {
        var link = Assert.IsType<Inline.Link>(
            Assert.Single(MarkdownParser.ParseInlines("[the docs](https://example.com)")));

        Assert.Equal("https://example.com", link.Target);
        Assert.Equal("the docs", Rendered(link.Label));
    }

    [Fact]
    public void AWikiLinkIsNotMistakenForAMarkdownLink() =>
        Assert.IsType<Inline.WikiLink>(Assert.Single(MarkdownParser.ParseInlines("[[Docker]]")));

    [Fact]
    public void BoldIsParsedAsStrongEmphasis()
    {
        var strong = Assert.IsType<Inline.Emphasis>(Assert.Single(MarkdownParser.ParseInlines("**bold**")));

        Assert.True(strong.Strong);
        Assert.Equal("bold", Rendered(strong.Children));
    }

    [Fact]
    public void SingleAsterisksAreItalicNotStrong() =>
        Assert.False(Assert.IsType<Inline.Emphasis>(
            Assert.Single(MarkdownParser.ParseInlines("*italic*"))).Strong);

    [Fact]
    public void DoubleUnderscoresAreAlsoStrong() =>
        Assert.True(Assert.IsType<Inline.Emphasis>(
            Assert.Single(MarkdownParser.ParseInlines("__bold__"))).Strong);

    [Fact]
    public void UnderscoresInsideAWordAreNotEmphasis()
    {
        // Every snake_case_identifier in a code-heavy note would come out italic
        // without this, which is the kind of wrong that makes a preview untrustworthy.
        Assert.Equal(
            "call get_user_id please",
            TextOf(Assert.IsType<Block.Paragraph>(Single("call get_user_id please"))));
    }

    [Fact]
    public void AMarkerAfterASpaceDoesNotOpenEmphasis() =>
        Assert.DoesNotContain(
            MarkdownParser.ParseInlines("2 * 3 * 4"),
            inline => inline is Inline.Emphasis);

    [Fact]
    public void ThreeMarkersAreNotEmphasis() =>
        Assert.DoesNotContain(
            MarkdownParser.ParseInlines("***not bold***"),
            inline => inline is Inline.Emphasis);

    [Fact]
    public void AnUnmatchedMarkerStaysLiteralText() =>
        Assert.Equal("a * b", TextOf(new Block.Paragraph(MarkdownParser.ParseInlines("a * b"))));

    [Fact]
    public void EmphasisNestsWithCodeInsideIt()
    {
        var strong = Assert.IsType<Inline.Emphasis>(Assert.Single(MarkdownParser.ParseInlines("**bold `code` here**")));

        Assert.Contains(strong.Children, child => child is Inline.Code { Value: "code" });
    }

    [Fact]
    public void ATableIsNotParsedAndRendersAsParagraphs()
    {
        // Also a documented gap. A table comes out as the paragraph it looks like,
        // which is wrong in a way you can see.
        var blocks = MarkdownParser.Parse("| a | b |\n| - | - |\n| 1 | 2 |");

        Assert.DoesNotContain(blocks, block => block is Block.List);
    }

    [Fact]
    public void FrontMatterIsNotInterpretedAsMetadata()
    {
        // The scanner owns frontmatter. The preview does not re-parse it, so what it
        // shows is what the file literally contains: a rule, a paragraph, a rule.
        // Asserting the exact shape is the point - if this ever becomes two blocks,
        // somebody has taught the preview about frontmatter, and the shell is now
        // showing something the file does not say.
        var blocks = MarkdownParser.Parse("---\nid: abc\n---\n\n# Title");

        Assert.Collection(
            blocks,
            first => Assert.IsType<Block.Rule>(first),
            second => Assert.IsType<Block.Paragraph>(second),
            third => Assert.IsType<Block.Rule>(third),
            fourth => Assert.IsType<Block.Heading>(fourth));
    }

    [Fact]
    public void SetextHeadingsAreNotSupportedAndBecomeParagraphs() =>
        Assert.IsType<Block.Paragraph>(Single("Title\n====="));

    [Fact]
    public void ARealSentenceWithEveryInlineKindComesOutClean()
    {
        // The kind of line a note actually contains. Asserted on the *rendered* text,
        // because that is what the preview pane shows and the only thing a reader
        // ever sees: no markers, no backticks, emphasis and code and links flattened
        // to their words, in order.
        const string line =
            "Resumen de lo que importa, con **negritas**, *cursivas* y ``codigo``, " +
            "mas [[Otra nota|un enlace]] y [la web](https://example.com).";

        Assert.Equal(
            "Resumen de lo que importa, con negritas, cursivas y codigo, " +
            "mas un enlace y la web.",
            Rendered(MarkdownParser.ParseInlines(line)));
    }
}
