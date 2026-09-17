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
        var block = ExtractBlock(NormalizeLineEndings(content));
        if (block is null) return null;

        var match = IdLineRegex().Match(block);
        return match.Success && Guid.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    public static string EnsureId(string content, Guid id)
    {
        if (TryReadId(content) is not null) return content;

        var normalized = NormalizeLineEndings(content);
        if (ExtractBlock(normalized) is not null)
        {
            var insertAt = normalized.IndexOf('\n') + 1;
            return normalized.Insert(insertAt, $"id: {id}\n");
        }

        return $"---\nid: {id}\n---\n\n{normalized}";
    }

    private static string? ExtractBlock(string content)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal)) return null;

        var closingIndex = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        return closingIndex < 0 ? null : content[3..closingIndex];
    }

    // Files can arrive with LF, CRLF, or (rarely) bare CR line endings depending on
    // their origin (a Windows editor, a WinUI TextBox getter, a file copied from
    // elsewhere) - detection must not depend on which one a given file happens to use,
    // or a note could get a second `id:` injected purely because of its line-ending
    // style.
    private static string NormalizeLineEndings(string content) =>
        content.Replace("\r\n", "\n").Replace('\r', '\n');
}
