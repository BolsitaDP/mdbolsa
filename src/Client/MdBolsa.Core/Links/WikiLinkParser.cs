using System.Text.RegularExpressions;

namespace MdBolsa.Core.Links;

// Extracts [[Target]], [[Target|Display text]] and [[Target#Heading]] references
// from note content. Deliberately shallow - it is a plain regex over the raw text,
// not a Markdown parse, because links are found in frontmatter values, list items
// and one-line paragraphs alike, and a block parser would need to understand every
// shape a person can write a link in before it found the first one.
public static partial class WikiLinkParser
{
    // The character in front of the brackets is captured rather than asserted, so the
    // loop can tell a link from an *embed*. "![[diagram.png]]" references an
    // attachment, not a note: letting the regex match it made every embedded image
    // come back as an unresolved link, and a status line that cries wolf on any note
    // with a picture in it is a status line nobody reads.
    // Both groups are *named* on purpose. .NET numbers unnamed groups first and named
    // groups after them, so in `(?<Bang>!?)\[\[([^\]|#]+)\]\]` the target is group 1
    // and Bang is group 2 - the opposite of the order they appear in, and the reason
    // a version of this that read the target positionally returned nothing at all
    // while looking perfectly correct.
    [GeneratedRegex(@"(?<Bang>!?)\[\[(?<Target>[^\]|#]+)(?:#[^\]|]*)?(?:\|[^\]]*)?\]\]")]
    private static partial Regex LinkRegex();

    public static IReadOnlyList<string> ExtractTargets(string content)
    {
        var targets = new List<string>();

        foreach (Match match in LinkRegex().Matches(content))
        {
            // Value.Length, not Success: an optional group that matched nothing still
            // reports Success, so every plain link would look like an embed and the
            // vault would report every link in it as missing.
            if (match.Groups["Bang"].Value.Length > 0) continue;

            var target = match.Groups["Target"].Value.Trim();
            if (target.Length > 0) targets.Add(target);
        }

        return targets;
    }
}
