using MdBolsa.Core.Vault;

namespace MdBolsa.Core.Tags;

// Runs after a VaultScanner.Scan() (needs the current note set). Re-reads each
// note's file and rewrites its tag rows wholesale - same full-reindex-not-
// incremental approach, and same reasoning, as LinkScanner/SearchScanner: a
// separate focused pass is easier to test and reason about than threading tag
// extraction through the identity/revision bookkeeping, and re-reading a few
// hundred small text files costs nothing at personal-vault scale.
public sealed class TagScanner(string vaultRoot, IVaultIndex vaultIndex, ITagIndex tagIndex)
{
    public TagScanResult Scan()
    {
        var notes = vaultIndex.GetAll();
        var allTags = new HashSet<string>(StringComparer.Ordinal);
        int taggedNotes = 0;

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

            var tags = TagParser.Extract(content);
            tagIndex.ReplaceTagsForNote(note.Id, tags);

            if (tags.Count > 0) taggedNotes++;
            foreach (var tag in tags) allTags.Add(tag);
        }

        tagIndex.DeleteTagsForNotesNotIn(notes.Select(n => n.Id).ToList());
        return new TagScanResult(taggedNotes, allTags.Count);
    }
}

public readonly record struct TagScanResult(int TaggedNotes, int DistinctTags);
