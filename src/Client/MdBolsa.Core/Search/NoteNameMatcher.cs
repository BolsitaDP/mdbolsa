using System.Globalization;
using System.Text;
using MdBolsa.Core.Vault;

namespace MdBolsa.Core.Search;

// Matching a query against note *names*, which is what a quick switcher does and
// what FTS cannot do.
//
// FTS5 answers "which notes talk about this", not "which note is called this". The
// difference is the whole feel of a switcher: someone opening a note they already
// know the name of is looking for a filename, and a tool that makes them remember a
// word from inside the note to find it has misunderstood the request.
//
// The ranking is deliberately shallow - exact, then prefix, then word-start, then
// anywhere - and stops there. A fuzzy subsequence matcher scores almost anything
// three letters long as a near-match, which means the top result is a coin toss and
// someone pressing Enter is gambling. Better to show six confident results than
// sixty speculative ones, and better still to show nothing than the wrong note
// opened without anybody noticing.
public enum NameMatchKind
{
    /// <summary>The name is exactly the query.</summary>
    Exact,

    /// <summary>The name starts with the query. The common case: "nota" finds "Nota1".</summary>
    Prefix,

    /// <summary>A word inside the name starts with the query.</summary>
    WordStart,

    /// <summary>The query appears somewhere in the name.</summary>
    Anywhere,
}

public sealed record NameMatch(NoteMetadata Note, NameMatchKind Kind, string MatchedWord)
{
    // Lower is better. Kind decides, and within a kind the shorter name wins,
    // because "Notas" is a better guess for "nota" than "Notas de la reunion de
    // marzo" - both start with the query, and one of them is obviously it.
    public int Rank => Kind switch
    {
        NameMatchKind.Exact => 0,
        NameMatchKind.Prefix => 1,
        NameMatchKind.WordStart => 2,
        _ => 3,
    };
}

public static class NoteNameMatcher
{
    // Accents and case are folded for *matching* only, never for storage: this
    // decides what a query finds, not what a file is called. Without it,
    // "informacion" cannot find "Información" - and filenames in this app are
    // written without accents most of the time, so that is the spelling people
    // actually type.
    //
    // What gets folded away. Built from explicit ranges rather than written as a
    // literal like "🌀-ͯ", because `[.. "🌀-ͯ"]` does NOT mean "U+0300 to U+036F"
    // - it spreads the *string* into three characters: the first bound, a hyphen,
    // and the last bound. That put a literal '-' in the ignore list, so Fold
    // silently deleted every hyphen in every filename and "blog-postgres" stopped
    // having a word boundary in it. A test for one dash caught it; a literal range
    // would have shipped it.
    //
    // The ranges are combining diacritical marks - what NFD leaves behind after
    // pulling "ó" apart into "o" + U+0301. The singles are the few letters with no
    // decomposition and no accent to strip, but with a single-letter ASCII
    // equivalent, so dropping them is right: "pøstgresql" folds to "postgresql".
    //
    // ß and þ are deliberately **not** in the list. They are two letters of sound
    // ("ss", "th"), so deleting them would corrupt the text rather than fold it,
    // and a switcher that mangles a word is worse than one that asks for the accent.
    private static readonly char[] FoldIgnorable = BuildFoldIgnorable();

    private static char[] BuildFoldIgnorable()
    {
        var ranges = new (char First, char Last)[]
        {
            ('\u0300', '\u036F'),   // combining diacritical marks
            ('\u1AB0', '\u1AFF'),
            ('\u1DC0', '\u1DFF'),
            ('\u20D0', '\u20FF'),
            ('\uFE20', '\uFE2F'),
        };

        var singles = new[]
        {
            '\u00F8',   // ø
            '\u0142',   // ł
            '\u0111',   // đ
            '\u0127',   // ħ
            '\u00F0',   // ð
            '\u0131',   // ı
            '\u0138',   // ĸ
        };

        var characters = new List<char>(256);
        foreach (var (first, last) in ranges)
        {
            // Counted as ints: a char loop past U+FFFF would wrap to U+0000 and
            // never end.
            for (var code = (int)first; code <= (int)last; code++)
            {
                characters.Add((char)code);
            }
        }

        characters.AddRange(singles);
        return [.. characters];
    }
    private static string Fold(string text) =>
        new string(text.ToLowerInvariant().Normalize(NormalizationForm.FormD)
            .Where(character => !FoldIgnorable.Contains(character))
            .ToArray());

    public static NameMatchKind? Classify(string fileNameWithoutExtension, string query)
    {
        if (query.Length == 0) return null;

        var name = Fold(fileNameWithoutExtension);
        var needle = Fold(query);

        if (name == needle) return NameMatchKind.Exact;
        if (name.StartsWith(needle, StringComparison.Ordinal)) return NameMatchKind.Prefix;

        // Word starts, including after a space, a dash, an underscore or a dot, so
        // "post" matches " blog-postgres" and "v1.2 release".
        var wordStarts = new[] { ' ', '-', '_', '.' };
        var index = 0;
        while ((index = name.IndexOf(needle, index, StringComparison.Ordinal)) > 0)
        {
            if (wordStarts.Contains(name[index - 1])) return NameMatchKind.WordStart;
            index++;
        }

        return name.Contains(needle, StringComparison.Ordinal) ? NameMatchKind.Anywhere : null;
    }

    /// <summary>
    /// Every note whose name matches, best first. A note that matches several ways
    /// is reported at its best kind, and a multi-word query only counts as a match
    /// when every word matches *something*, which is what makes "not mar" find
    /// "Notas de marzo" without also finding every note that merely says "nota".
    /// </summary>
    public static IReadOnlyList<NameMatch> Match(
        IEnumerable<NoteMetadata> notes, string query, int limit = 20)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return [];

        var matches = new List<NameMatch>();

        foreach (var note in notes)
        {
            var fileName = Path.GetFileNameWithoutExtension(note.RelativePath);
            var best = NameMatchKind.Anywhere;
            var bestWord = string.Empty;
            var matchedEverything = true;

            foreach (var word in words)
            {
                var kind = Classify(fileName, word);
                if (kind is null)
                {
                    matchedEverything = false;
                    break;
                }

                if (kind < best)
                {
                    best = kind.Value;
                    bestWord = fileName
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(part => Fold(part).StartsWith(Fold(word), StringComparison.Ordinal))
                        ?? fileName;
                }
            }

            if (matchedEverything)
            {
                matches.Add(new NameMatch(note, best, bestWord));
            }
        }

        return matches
            .OrderBy(match => match.Rank)
            .ThenBy(match => Path.GetFileNameWithoutExtension(match.Note.RelativePath).Length)
            .ThenBy(match => match.Note.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }
}
