using System.Text.RegularExpressions;

namespace MdBolsa.Core.Vault;

// Deliberately minimal: only ever reads or injects the `id:` field. Never parses or
// rewrites the rest of the YAML block, so it can't corrupt frontmatter fields it doesn't
// understand (tags, custom fields, etc.) - it only ever adds a line. See
// docs/decisions/0005-stable-note-identity.md for why this beats a full YAML parser here.
public static partial class FrontMatter
{
    [GeneratedRegex(@"^id:\s*([0-9a-fA-F-]{36})\s*$", RegexOptions.Multiline)]
    private static partial Regex IdLineRegex();

    public static Guid? TryReadId(string content)
    {
        var block = ExtractBlock(content);
        if (block is null) return null;

        var match = IdLineRegex().Match(block);
        return match.Success && Guid.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    public static string EnsureId(string content, Guid id)
    {
        if (TryReadId(content) is not null) return content;

        if (ExtractBlock(content) is not null)
        {
            var insertAt = content.IndexOf('\n') + 1;
            return content.Insert(insertAt, $"id: {id}\n");
        }

        return $"---\nid: {id}\n---\n\n{content}";
    }

    private static string? ExtractBlock(string content)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal)) return null;

        var closingIndex = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        return closingIndex < 0 ? null : content[3..closingIndex];
    }
}
