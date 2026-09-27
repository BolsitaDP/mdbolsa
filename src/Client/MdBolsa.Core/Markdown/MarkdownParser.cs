using System.Text;

namespace MdBolsa.Core.Markdown;

// Markdown -> blocks. Line-based, no dependencies, no regex soup.
//
// The parser is *block first, then inline*, which is the only structure that makes
// a subset behave predictably: a line is either a block start or it is text, and
// you never have to ask whether `---` is a setext underline or a thematic break
// because the previous line already decided.
//
// What it does NOT do, on purpose (docs/decisions/0013):
//   * inline emphasis, inline code and links inside a paragraph keep their text and
//     lose their markers. `**bold**` renders as "bold". That is a visible
//     degradation, and it is visible on purpose - the alternative is a preview that
//     quietly reinterprets text.
//   * tables. Not parsed at all, so a table renders as the paragraphs it looks like.
//   * nested lists. Flattened to one level, because the preview draws a flat list
//     and a half-nested list is harder to read than an honest flat one.
//   * setext headings (`Title\n=====`) and HTML blocks. Not supported; they render
//     as paragraphs, which is what a plain-text reader would do with them too.
//
// Everything it does support has a test, and the ones above are tested too - to pin
// down that they degrade rather than break.
public static class MarkdownParser
{
    public static IReadOnlyList<Block> Parse(string markdown)
    {
        var blocks = new List<Block>();
        var lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var index = 0;
        while (index < lines.Length)
        {
            var line = lines[index];

            if (line.Trim().Length == 0) { index++; continue; }

            // Fenced code first, before anything that could read a ` inside it.
            if (TryReadFence(lines, index, out var fence))
            {
                blocks.Add(new Block.Code(fence.Language, fence.Text));
                index = fence.NextIndex;
                continue;
            }

            if (IsRule(line))
            {
                blocks.Add(new Block.Rule());
                index++;
                continue;
            }

            if (TryReadHeading(line, out var heading))
            {
                blocks.Add(new Block.Heading(heading.Level, ParseInlines(heading.Text)));
                index++;
                continue;
            }

            if (TryReadQuote(lines, ref index, out var quote))
            {
                blocks.Add(quote);
                continue;
            }

            if (TryReadList(lines, ref index, out var list))
            {
                blocks.Add(list);
                continue;
            }

            if (IsImageOnly(line, out var image))
            {
                blocks.Add(image);
                index++;
                continue;
            }

            blocks.Add(new Block.Paragraph(ParseInlines(Collapse(lines, ref index))));
        }

        return blocks;
    }

    // --- fences ------------------------------------------------------------

    private readonly record struct Fence(string? Language, string Text, int NextIndex);

    private static bool TryReadFence(string[] lines, int index, out Fence fence)
    {
        fence = default;

        var trimmed = lines[index].TrimStart();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return false;

        // The language is the rest of the fence line, trimmed. "```{csharp}" and
        // "``` csharp" both mean csharp to a reader, and normalising here means the
        // preview does not have to know which spelling people use.
        var language = trimmed[3..].Trim();
        if (language.StartsWith('{') && language.EndsWith('}')) language = language[1..^1];
        if (language.Contains(' ')) language = language[..language.IndexOf(' ')];

        var body = new List<string>();
        var next = index + 1;
        while (next < lines.Length && !lines[next].TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            body.Add(lines[next]);
            next++;
        }

        fence = new Fence(
            string.IsNullOrWhiteSpace(language) ? null : language,
            string.Join('\n', body),
            // An unterminated fence runs to the end of the note rather than throwing:
            // someone is halfway through typing one, and a preview that blanks out
            // while they type is worse than one that shows the text.
            Math.Min(next + 1, lines.Length));

        return true;
    }

    // --- inline ------------------------------------------------------------

    // Two tiny "I found a thing and here is where it ends" records, rather than one
    // record with a flag per kind. Each reader answers exactly one question, and a
    // caller cannot forget to check a flag it does not care about.
    private readonly record struct CodeSpan(string Text, int NextIndex);

    private readonly record struct WikiSpan(string Target, string? Alias, int NextIndex);

    private readonly record struct EmphasisSpan(int RunLength, string Inner, int NextIndex);

    private readonly record struct LinkSpan(string Target, string Label, int NextIndex);

    public static IReadOnlyList<Inline> ParseInlines(string text)
    {
        var inlines = new List<Inline>();
        var plain = new StringBuilder();

        void FlushPlain()
        {
            if (plain.Length == 0) return;
            inlines.Add(new Inline.Text(plain.ToString()));
            plain.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            // A backslash escape is honoured even though nothing else inline is.
            // Without it, a note *about* Markdown ("use \* for a literal star")
            // loses the star - the one case where dropping syntax changes what the
            // text means rather than how it looks.
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                plain.Append(text[i + 1]);
                i++;
                continue;
            }

            if (text[i] == '`')
            {
                var code = ReadCode(text, i);
                if (code is not null)
                {
                    FlushPlain();
                    inlines.Add(new Inline.Code(code.Value.Text));
                    i = code.Value.NextIndex - 1;
                    continue;
                }
            }

            if (text[i] == '[' && i + 1 < text.Length && text[i + 1] == '[')
            {
                var wiki = ReadWikiLink(text, i);
                if (wiki is not null)
                {
                    FlushPlain();
                    inlines.Add(new Inline.WikiLink(wiki.Value.Target, wiki.Value.Alias));
                    i = wiki.Value.NextIndex - 1;
                    continue;
                }
            }

            if (text[i] == '[')
            {
                var link = ReadLink(text, i);
                if (link is not null)
                {
                    FlushPlain();
                    inlines.Add(new Inline.Link(link.Value.Target, ParseInlines(link.Value.Label)));
                    i = link.Value.NextIndex - 1;
                    continue;
                }
            }

            if (text[i] is '*' or '_')
            {
                var emphasis = ReadEmphasis(text, i);
                if (emphasis is not null)
                {
                    FlushPlain();
                    inlines.Add(new Inline.Emphasis(
                        ParseInlines(emphasis.Value.Inner),
                        Strong: emphasis.Value.RunLength == 2));
                    i = emphasis.Value.NextIndex - 1;
                    continue;
                }
            }

            plain.Append(text[i]);
        }

        FlushPlain();
        return inlines.Count > 0 ? inlines : [new Inline.Text(string.Empty)];
    }

    private static CodeSpan? ReadCode(string text, int start)
    {
        var ticks = 0;
        while (start + ticks < text.Length && text[start + ticks] == '`') ticks++;

        // Walk the closing runs rather than searching for a literal string of
        // backticks: a closing run has to be *at least* as long as the opening one,
        // so ``a`b`` closes at the double tick and not at the single one in the
        // middle. The scan position always moves forward by at least one, which is
        // the property that keeps this loop from spinning when it meets a character
        // that is not a backtick.
        var search = start + ticks;

        while (true)
        {
            var next = text.IndexOf('`', search);
            if (next < 0) return null;

            var run = 0;
            while (next + run < text.Length && text[next + run] == '`') run++;

            if (run >= ticks) return new CodeSpan(text[(start + ticks)..next], next + run);

            search = next + run;
        }
    }
    private static WikiSpan? ReadWikiLink(string text, int start)
    {
        var close = text.IndexOf("]]", start + 2, StringComparison.Ordinal);
        if (close < 0) return null;

        var body = text[(start + 2)..close];

        // `[[target|alias]]`, and also `[[target|label]]`, because both spellings
        // are in real notes and refusing one of them would show raw brackets.
        var pipe = body.IndexOf('|');
        var target = (pipe < 0 ? body : body[..pipe]).Trim();
        if (target.Length == 0) return null;

        var alias = pipe < 0 ? null : body[(pipe + 1)..].Trim();
        return new WikiSpan(target, alias is { Length: > 0 } ? alias : null, close + 2);
    }

    private static LinkSpan? ReadLink(string text, int start)
    {
        // "[label](target)". The wiki form is handled by the caller before this, so
        // "[[a]]" never arrives here as a link whose label is "[a".
        var labelEnd = text.IndexOf("](", start + 1, StringComparison.Ordinal);
        if (labelEnd < 0) return null;

        var targetEnd = text.IndexOf(')', labelEnd + 2);
        if (targetEnd < 0) return null;

        var label = text[(start + 1)..labelEnd];
        if (label.Contains('[')) return null;

        return new LinkSpan(text[(labelEnd + 2)..targetEnd], label, targetEnd + 1);
    }

    // Emphasis, read pragmatically: a run of one or two markers closed by a run of
    // the same length, with no space before the closing run. It is not CommonMark's
    // delimiter algorithm and does not need to be - the job is to draw **bold** as
    // bold, not to be a parser somebody can write a paper about.
    private static EmphasisSpan? ReadEmphasis(string text, int start)
    {
        var marker = text[start];

        var run = 0;
        while (start + run < text.Length && text[start + run] == marker) run++;

        // Three or more is not emphasis. In full Markdown that is a horizontal rule
        // or literal text; here it is text, and treating it as emphasis would swallow
        // the rest of the line.
        if (run is < 1 or > 2) return null;

        // Note what is *not* checked here: a marker after a space is a perfectly good
        // OPENER - "con **negritas**" is the common case, not the exception. The rule
        // about whitespace belongs to the closing marker instead, and lives below.

        // Underscores inside a word are underscores. Without this, every
        // snake_case_identifier in a code-heavy note comes out italic.
        if (marker == '_' && start > 0 && IsWordCharacter(text[start - 1])) return null;

        var contentStart = start + run;

        for (var i = contentStart; i < text.Length; i++)
        {
            if (text[i] != marker || i == contentStart) continue;

            var closing = 0;
            while (i + closing < text.Length && text[i + closing] == marker) closing++;

            if (closing != run) { i += closing - 1; continue; }
            if (char.IsWhiteSpace(text[i - 1])) continue;

            // ...and the same intraword rule for the closer.
            if (marker == '_' && i + closing < text.Length && IsWordCharacter(text[i + closing])) continue;

            return new EmphasisSpan(run, text[contentStart..i], i + closing);
        }

        return null;
    }

    private static bool IsWordCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == '_';
    // --- block helpers -----------------------------------------------------

    private static bool IsRule(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 3) return false;

        // Three or more of the same rule character, and nothing else. A line of "---"
        // is a rule; a line of "-----" under a paragraph is a setext heading in full
        // Markdown, which is not supported, and treating it as a rule is the
        // degradation that hurts least.
        foreach (var marker in new[] { '-', '*', '_' })
        {
            if (trimmed.All(character => character == marker)) return true;
        }

        return false;
    }

    private static bool TryReadHeading(string line, out (int Level, string Text) heading)
    {
        heading = default;
        var trimmed = line.TrimStart();

        var level = 0;
        while (level < trimmed.Length && trimmed[level] == '#') level++;

        // Six is the maximum in Markdown, and a seventh is not a heading - it is a
        // line that happens to start with hashes.
        if (level is < 1 or > 6) return false;
        if (level < trimmed.Length && trimmed[level] is not (' ' or '\t')) return false;

        heading = (level, trimmed[level..].Trim());
        return true;
    }

    private static bool TryReadQuote(string[] lines, ref int index, out Block.Quote quote)
    {
        quote = null!;
        if (!lines[index].TrimStart().StartsWith('>')) return false;

        var inner = new List<string>();
        while (index < lines.Length && lines[index].TrimStart().StartsWith('>'))
        {
            var stripped = lines[index].TrimStart()[1..];
            inner.Add(stripped.StartsWith(' ') ? stripped[1..] : stripped);
            index++;
        }

        // A blank `>` line separates paragraphs inside the quote, and the recursion
        // handles that for free.
        quote = new Block.Quote(Parse(string.Join('\n', inner)));
        return true;
    }

    private static bool TryReadList(string[] lines, ref int index, out Block.List list)
    {
        list = null!;

        var first = ReadListMarker(lines[index]);
        if (first is null) return false;

        var ordered = first.Value.Ordered;
        var start = first.Value.Start;
        var items = new List<IReadOnlyList<Inline>>();

        while (index < lines.Length)
        {
            var marker = ReadListMarker(lines[index]);
            if (marker is null) break;

            // Mixing bullet and numbered lists in one block would produce a list that
            // claims to be both. Stop instead, and let the next block start.
            if (marker.Value.Ordered != ordered) break;

            items.Add(ParseInlines(marker.Value.Text));
            index++;
        }

        list = new Block.List(ordered, start, items);
        return true;
    }

    private static (bool Ordered, int Start, string Text)? ReadListMarker(string line)
    {
        var trimmed = line.TrimStart();

        // Indentation is dropped rather than nested: see the note about nested lists
        // at the top of this file. A nested item still becomes a top-level one, which
        // is wrong but legible; half-rendering a hierarchy is not.
        var content = trimmed;
        var ordered = false;
        var start = 1;

        // A marker needs a following space, so a lone "-" is a dash and not an empty
        // list item.
        if (content.Length > 1 && (content[0] is '-' or '*' or '+') && content[1] is ' ' or '\t')
        {
            return (ordered: false, start: 1, Text: content[1..].Trim());
        }

        var digits = 0;
        while (digits < content.Length && char.IsAsciiDigit(content[digits])) digits++;

        if (digits is > 0 && digits < content.Length && (content[digits] is '.' or ')'))
        {
            var afterMarker = content[(digits + 1)..];
            if (afterMarker.Length == 0 || afterMarker[0] is ' ' or '\t')
            {
                return (ordered: true, start: int.Parse(content[..digits]), Text: afterMarker.Trim());
            }
        }

        return null;
    }

    private static bool IsImageOnly(string line, out Block.Image image)
    {
        image = null!;

        var trimmed = line.Trim();
        if (!trimmed.StartsWith("![", StringComparison.Ordinal)) return false;
        if (!trimmed.EndsWith(')')) return false;

        var close = trimmed.IndexOf("](", StringComparison.Ordinal);
        if (close < 0) return false;

        image = new Block.Image(trimmed[2..close], trimmed[(close + 2)..^1]);
        return true;
    }

    // A paragraph is every line until a blank one or a line that starts something
    // else. Joined with a space, because a wrapped Markdown line is a wrapped
    // sentence, not a line break the reader should see.
    private static string Collapse(string[] lines, ref int index)
    {
        var parts = new List<string>();
        while (index < lines.Length && lines[index].Trim().Length > 0 &&
               !IsBlockStart(lines[index]))
        {
            parts.Add(lines[index].Trim());
            index++;
        }

        // Every line was a block start, which should not happen - the caller only
        // reaches here for the first line, and it was not a block start. Consume one
        // so the loop cannot spin.
        if (parts.Count == 0 && index < lines.Length) parts.Add(lines[index++].Trim());

        return string.Join(' ', parts);
    }

    private static bool IsBlockStart(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0) return true;
        if (trimmed.StartsWith("```", StringComparison.Ordinal)) return true;
        if (trimmed.StartsWith('>')) return true;
        if (IsRule(trimmed)) return true;
        if (TryReadHeading(trimmed, out _)) return true;
        return ReadListMarker(trimmed) is not null;
    }
}
