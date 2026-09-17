using MdBolsa.Core.Vault;

namespace MdBolsa.Core.Links;

// Runs after a VaultScanner.Scan() (needs the current note set to resolve targets
// against). Re-reads each note's file rather than sharing content with VaultScanner:
// a separate, focused pass is simpler to test and reason about than threading link
// extraction through the identity/revision bookkeeping in VaultScanner, and re-reading
// a few hundred small text files is not a meaningful cost at personal-vault scale.
public sealed class LinkScanner(string vaultRoot, IVaultIndex vaultIndex, ILinkIndex linkIndex)
{
    public LinkScanResult Scan()
    {
        var notes = vaultIndex.GetAll();
        int resolved = 0, unresolved = 0;

        foreach (var note in notes)
        {
            var fullPath = Path.Combine(vaultRoot, note.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            string content;
            try
            {
                content = File.ReadAllText(fullPath);
            }
            catch (IOException)
            {
                continue; // vanished mid-scan - the next VaultScanner.Scan() drops it from the index
            }

            var links = WikiLinkParser.ExtractTargets(content)
                .Distinct(StringComparer.Ordinal)
                .Select(target =>
                {
                    var targetId = ResolveTarget(target, notes);
                    if (targetId is null) unresolved++; else resolved++;
                    return new NoteLink(note.Id, target, targetId);
                })
                .ToList();

            linkIndex.ReplaceLinksForNote(note.Id, links);
        }

        linkIndex.DeleteLinksForNotesNotIn(notes.Select(n => n.Id).ToList());
        return new LinkScanResult(resolved, unresolved);
    }

    // Obsidian-style resolution: an exact relative-path match wins; otherwise, a
    // filename-only match resolves only if it's unambiguous (exactly one note in the
    // vault has that filename). An ambiguous or nonexistent target is left unresolved
    // rather than guessed at.
    private static Guid? ResolveTarget(string targetText, IReadOnlyList<NoteMetadata> notes)
    {
        var normalized = targetText.Replace('\\', '/').Trim();

        var exact = notes.FirstOrDefault(n =>
            string.Equals(WithoutExtension(n.RelativePath), normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Id;

        var lastSlash = normalized.LastIndexOf('/');
        var fileNameOnly = lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;

        var matches = notes
            .Where(n => string.Equals(
                Path.GetFileNameWithoutExtension(n.RelativePath), fileNameOnly, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0].Id : null;
    }

    private static string WithoutExtension(string relativePath) =>
        relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? relativePath[..^3] : relativePath;
}

public readonly record struct LinkScanResult(int Resolved, int Unresolved);
