using System.Text.RegularExpressions;

namespace MdBolsa.Core.Links;

// Extracts [[Target]], [[Target|Display text]], and [[Target#Heading]] references.
// Headings aren't resolved to anything yet (no block/heading-level linking in this
// phase) - only the note-level target before the # is kept. A plain regex rather than
// a full Markdown parser (Markdig etc.): nothing else about the note's Markdown
// structure matters for this, just this one bracket syntax, and pulling in a full
// parser wouldn't buy anything until Phase 3's editor actually needs to render
// Markdown (headings, code fences, etc.).
public static partial class WikiLinkParser
{
    [GeneratedRegex(@"\[\[([^\]|#]+)(?:#[^\]|]*)?(?:\|[^\]]*)?\]\]")]
    private static partial Regex LinkRegex();

    public static IReadOnlyList<string> ExtractTargets(string content)
    {
        var targets = new List<string>();
        foreach (Match match in LinkRegex().Matches(content))
        {
            var target = match.Groups[1].Value.Trim();
            if (target.Length > 0) targets.Add(target);
        }
        return targets;
    }
}
