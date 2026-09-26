using System.Text.RegularExpressions;
using MdBolsa.Core.Vault;

namespace MdBolsa.Core.Tags;

// Reads a note's tags from the two places Obsidian-style vaults put them: the
// frontmatter `tags:` field, and inline `#hashtags` in the body. Both are
// normalized to the same shape (lowercase, no leading `#`, `/` kept for
// nesting) so `#Development` in the body and `development` in the frontmatter
// are one tag, not two.
//
// Regex-based, in the same spirit as WikiLinkParser: a tag is a `#` followed by
// tag characters, and the only Markdown structure that matters here is knowing
// what *isn't* a tag (headings, code, URL fragments). No Markdig - see
// docs/decisions/0008-tags-and-metadata.md.
public static partial class TagParser
{
    [GeneratedRegex(@"^tags\s*:\s*(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex TagsKeyRegex();

    [GeneratedRegex(@"^\s*-\s*(.*?)\s*$")]
    private static partial Regex BlockListItemRegex();

    // A tag body: letters (any script), digits, underscore, dash, and `/` for
    // nesting. The leading group requires a non-tag boundary before the `#` so
    // `C#`, `foo#bar` and `https://host/page#anchor` are not tags, and the
    // character class starts immediately after `#` so an ATX heading (`# Title`,
    // which has a space) never matches.
    [GeneratedRegex(@"(?:\A|(?<=[\s(\[/""']))#([\p{L}\p{N}_/-]+)")]
    private static partial Regex InlineTagRegex();

    // A frontmatter tag value has to look like a tag in its entirety - a bare
    // word or number, optionally nested with `/`. Anything else (`"a note about
    // docker"`, a nested map, a URL) is ignored rather than half-interpreted.
    [GeneratedRegex(@"^[\p{L}\p{N}_/-]+$")]
    private static partial Regex TagValueRegex();

    [GeneratedRegex(@"^\s*(?:```|~~~)")]
    private static partial Regex CodeFenceRegex();

    public static IReadOnlyList<string> Extract(string content) =>
        ExtractFrom(content, inlineTags: true);

    // Same as Extract, minus inline `#hashtags`. Not used by the app today; kept
    // as the seam for a future "frontmatter tags only" mode rather than a second
    // copy of the frontmatter half of the logic.
    public static IReadOnlyList<string> ExtractFrontMatterOnly(string content) =>
        ExtractFrom(content, inlineTags: false);

    private static IReadOnlyList<string> ExtractFrom(string content, bool inlineTags)
    {
        var tags = new List<string>();
        tags.AddRange(ReadFrontMatterTags(content));

        if (inlineTags)
        {
            tags.AddRange(ReadInlineTags(FrontMatter.Body(content)));
        }

        return tags
            .Select(Normalize)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToList();
    }

    // Single source of truth for a tag's stored form: lowercase, no `#`, no
    // trailing `/` (`project/` is the same tag as `project`). Returns empty for
    // anything that isn't a usable tag, which is how callers filter.
    public static string Normalize(string tag)
    {
        var normalized = tag.Trim().TrimStart('#').Trim().TrimEnd('/').ToLowerInvariant();
        return normalized.Length == 0 ? string.Empty : normalized;
    }

    private static IEnumerable<string> ReadFrontMatterTags(string content)
    {
        var block = FrontMatter.TryReadBlock(content);
        if (block is null) yield break;

        var lines = block.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var key = TagsKeyRegex().Match(lines[i]);
            if (!key.Success) continue;

            var inlineValue = key.Groups[1].Value.Trim();
            if (inlineValue.Length == 0)
            {
                // Block sequence: `- tag` lines belonging to this key, until the
                // first line that isn't one.
                for (var j = i + 1; j < lines.Length; j++)
                {
                    var item = BlockListItemRegex().Match(lines[j]);
                    if (!item.Success) break;
                    if (TryNormalizeFrontMatterValue(item.Groups[1].Value, out var blockTag)) yield return blockTag;
                }
            }
            else
            {
                // Flow sequence `[a, b]` or a bare/comma-separated scalar list.
                var flow = inlineValue;
                if (flow.StartsWith('[') && flow.EndsWith(']')) flow = flow[1..^1];

                foreach (var part in flow.Split(','))
                {
                    if (TryNormalizeFrontMatterValue(part, out var flowTag)) yield return flowTag;
                }
            }

            yield break; // only the first `tags:` key counts, same as YAML would
        }
    }

    private static bool TryNormalizeFrontMatterValue(string value, out string tag)
    {
        tag = string.Empty;

        var unquoted = Unquote(value.Trim());
        if (!TagValueRegex().IsMatch(unquoted)) return false;

        tag = Normalize(unquoted);
        return tag.Length > 0;
    }

    private static IEnumerable<string> ReadInlineTags(string body)
    {
        var inFence = false;
        foreach (var rawLine in body.Split('\n'))
        {
            // A fenced code block can contain anything, including things that look
            // exactly like tags; skip its lines wholesale rather than trying to
            // parse the fence's info string.
            if (CodeFenceRegex().IsMatch(rawLine))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence) continue;

            // Inline code spans: alternating backtick-delimited segments are code,
            // the rest is prose. Counting segments is enough here - a tag can't
            // contain a backtick, so no nesting concerns.
            var segments = rawLine.Split('`');
            for (var i = 0; i < segments.Length; i += 2)
            {
                foreach (Match match in InlineTagRegex().Matches(segments[i]))
                {
                    var tag = Normalize(match.Groups[1].Value);
                    if (tag.Length == 0) continue;

                    // `#1` is far more often a heading anchor, issue reference or
                    // list number than a tag - require at least one letter.
                    if (!tag.Any(char.IsLetter)) continue;

                    yield return tag;
                }
            }
        }
    }

    private static string Unquote(string value) =>
        value.Length >= 2 &&
        ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;
}
