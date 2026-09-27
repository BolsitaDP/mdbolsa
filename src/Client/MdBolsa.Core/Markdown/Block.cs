namespace MdBolsa.Core.Markdown;

// A Markdown document, parsed into the few shapes a preview can draw.
//
// Deliberately not a general Markdown implementation. This is the subset a preview
// pane needs - headings, paragraphs, lists, quotes, code, rules, links, images -
// and it is a closed set, because a closed set is a set you can test. Inline spans
// inside a paragraph keep their text and lose their markers, which is a
// degradation you can see rather than a wrong answer you cannot.
//
// See docs/decisions/0013-live-preview-and-source.md for why this is hand-written
// and why it lives in Core instead of arriving with a library.

public abstract record Block
{
    /// <summary>Heading level 1-6.</summary>
    public sealed record Heading(int Level, IReadOnlyList<Inline> Inlines) : Block;

    public sealed record Paragraph(IReadOnlyList<Inline> Inlines) : Block;

    /// <summary>A bullet list. <paramref name="Ordered"/> false means bullets.</summary>
    public sealed record List(bool Ordered, int Start, IReadOnlyList<IReadOnlyList<Inline>> Items) : Block;

    public sealed record Quote(IReadOnlyList<Block> Blocks) : Block;

    /// <summary>A fenced code block. The language is whatever followed the fence,
    /// for a highlighter to use later.</summary>
    public sealed record Code(string? Language, string Text) : Block;

    public sealed record Rule : Block;

    public sealed record Image(string Alt, string Source) : Block;
}

public abstract record Inline
{
    public sealed record Text(string Value) : Inline;

    /// <summary>A wiki link, `[[target]]` or `[[target|alias]]`.</summary>
    public sealed record WikiLink(string Target, string? Alias) : Inline;

    /// <summary>A Markdown link, with the target as written.</summary>
    public sealed record Link(string Target, IReadOnlyList<Inline> Label) : Inline;

    /// <summary>Code, ticks or double ticks. Never spans a blank line.</summary>
    public sealed record Code(string Value) : Inline;

    /// <summary>An image, either "![alt](path)" or the embed form "![[name]]". The
    /// alt doubles as the name for the embed form, which is all it carries.</summary>
    public sealed record Image(string Alt, string Source) : Inline;

    /// <summary>Emphasis and strong. The distinction is kept because a preview
    /// renders them differently, and because dropping it now means every consumer
    /// has to guess.</summary>
    public sealed record Emphasis(IReadOnlyList<Inline> Children, bool Strong) : Inline;
}
