using System.Text.RegularExpressions;

namespace MdBolsa.Core.Vault;

// Deliberately minimal: only ever *reads* the frontmatter block, and only ever *writes*
// the `id:` field. Never parses or rewrites the rest of the YAML block, so it can't
// corrupt frontmatter fields it doesn't understand (tags, custom fields, etc.) - the
// only write path is a single added line. See
// docs/decisions/0005-stable-note-identity.md for why this beats a full YAML parser
// here, and 0008-tags-and-metadata.md for the Phase 6 read-only consumers
// (TagParser, ReadFields).
public static partial class FrontMatter
{
    [GeneratedRegex(@"^id:\s*([0-9a-fA-F-]{36})\s*$", RegexOptions.Multiline)]
    private static partial Regex IdLineRegex();

    public static Guid? TryReadId(string content)
    {
        var block = TryReadBlock(content);
        if (block is null) return null;

        var match = IdLineRegex().Match(block);
        return match.Success && Guid.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    public static string EnsureId(string content, Guid id)
    {
        if (TryReadId(content) is not null) return content;

        var normalized = NormalizeLineEndings(content);
        if (TryReadBlock(normalized) is not null)
        {
            var insertAt = normalized.IndexOf('\n') + 1;
            return normalized.Insert(insertAt, $"id: {id}\n");
        }

        return $"---\nid: {id}\n---\n\n{normalized}";
    }

    // Raw frontmatter block (the text between the `---` fences), or null when the note
    // has none. Exposed for the Phase 6 tag/metadata readers, which need the block's
    // own lines rather than a single field - still read-only, never a rewrite.
    public static string? TryReadBlock(string content)
    {
        var normalized = NormalizeLineEndings(content);
        if (!normalized.StartsWith("---", StringComparison.Ordinal)) return null;

        var closingIndex = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        return closingIndex < 0 ? null : normalized[3..closingIndex];
    }

    // Top-level `key: value` frontmatter fields, for display (Phase 6). Deliberately
    // shallow and non-validating: only unindented scalar lines are read, values are
    // taken as their first line verbatim, and block sequences/nested maps are skipped
    // (tags are read separately by TagParser). Nothing here is ever written back to the
    // file - this exists to *show* what a note declares, not to interpret it.
    public static IReadOnlyList<MetadataField> ReadFields(string content)
    {
        var block = TryReadBlock(content);
        if (block is null) return [];

        var fields = new List<MetadataField>();
        foreach (var line in block.Split('\n'))
        {
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '-' || line[0] == '#') continue;

            var separator = line.IndexOf(':');
            if (separator <= 0) continue;

            var key = line[..separator].Trim();
            if (key.Length == 0 || key.Contains(' ')) continue;

            fields.Add(new MetadataField(key, Unquote(line[(separator + 1)..].Trim())));
        }

        return fields;
    }

    // Returns the note's content with any leading frontmatter block removed, so
    // callers that care about the actual prose (search indexing, tag scanning,
    // previews) don't match on id/tags/etc. noise.
    public static string Body(string content)
    {
        var normalized = NormalizeLineEndings(content);
        if (!normalized.StartsWith("---", StringComparison.Ordinal)) return normalized;

        var closingIndex = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (closingIndex < 0) return normalized;

        return normalized[(closingIndex + "\n---".Length)..].TrimStart('\n');
    }

    private static string Unquote(string value) =>
        value.Length >= 2 &&
        ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    // Files can arrive with LF, CRLF, or (rarely) bare CR line endings depending on
    // their origin (a Windows editor, a WinUI TextBox getter, a file copied from
    // elsewhere) - detection must not depend on which one a given file happens to use,
    // or a note could get a second `id:` injected purely because of its line-ending
    // style.
    private static string NormalizeLineEndings(string content) =>
        content.Replace("\r\n", "\n").Replace('\r', '\n');
}
