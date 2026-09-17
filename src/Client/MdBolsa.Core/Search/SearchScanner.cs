using MdBolsa.Core.Vault;

namespace MdBolsa.Core.Search;

// Runs after a VaultScanner.Scan() (needs the current note set). Re-reads each note's
// file rather than sharing content with VaultScanner/LinkScanner - same reasoning as
// LinkScanner: a separate, focused pass is simpler to test and reason about, and
// re-reading a few hundred small text files is not a meaningful cost at
// personal-vault scale. Full reindex every scan, not incremental - same as LinkScanner.
public sealed class SearchScanner(string vaultRoot, IVaultIndex vaultIndex, ISearchIndex searchIndex)
{
    public int Scan()
    {
        var notes = vaultIndex.GetAll();
        var indexed = 0;

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

            searchIndex.IndexNote(note.Id, note.Title, FrontMatter.Body(content));
            indexed++;
        }

        searchIndex.DeleteNotesNotIn(notes.Select(n => n.Id).ToList());
        return indexed;
    }
}
