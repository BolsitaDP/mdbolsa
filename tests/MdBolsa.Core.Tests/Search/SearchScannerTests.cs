using MdBolsa.Core.Search;
using MdBolsa.Core.Vault;

namespace MdBolsa.Core.Tests.Search;

public class SearchScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mdbolsa-search-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Scan_IndexesEachNote_ByTitleAndBody()
    {
        var noteId = Guid.NewGuid();
        WriteNote("Docker.md", "---\nid: irrelevant\n---\n\nNotes about containers.");
        var vaultIndex = new FakeVaultIndex(Note(noteId, "Docker.md", "Docker"));
        var searchIndex = new FakeSearchIndex();

        var indexed = new SearchScanner(_root, vaultIndex, searchIndex).Scan();

        Assert.Equal(1, indexed);
        var (title, body) = Assert.Single(searchIndex.NotesById.Values);
        Assert.Equal("Docker", title);
        Assert.DoesNotContain("id: irrelevant", body);
        Assert.Contains("containers", body);
    }

    [Fact]
    public void Scan_RemovesNotes_NoLongerInVault()
    {
        var noteId = Guid.NewGuid();
        WriteNote("Note.md", "Body text.");
        var vaultIndex = new FakeVaultIndex(Note(noteId, "Note.md", "Note"));
        var searchIndex = new FakeSearchIndex();
        new SearchScanner(_root, vaultIndex, searchIndex).Scan();
        Assert.Single(searchIndex.NotesById);

        vaultIndex.Notes.Clear();
        new SearchScanner(_root, vaultIndex, searchIndex).Scan();

        Assert.Empty(searchIndex.NotesById);
    }

    private void WriteNote(string relativePath, string content) =>
        File.WriteAllText(Path.Combine(_root, relativePath), content);

    private static NoteMetadata Note(Guid id, string relativePath, string title) =>
        new(id, relativePath, title, "hash", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class FakeVaultIndex : IVaultIndex
    {
        public List<NoteMetadata> Notes { get; }

        public FakeVaultIndex(params NoteMetadata[] notes) => Notes = notes.ToList();

        public IReadOnlyList<NoteMetadata> GetAll() => Notes;

        public void Upsert(NoteMetadata note) => throw new NotSupportedException();

        public int DeleteMissing(IReadOnlyCollection<Guid> idsStillPresent) => throw new NotSupportedException();
    }

    private sealed class FakeSearchIndex : ISearchIndex
    {
        public Dictionary<Guid, (string Title, string Body)> NotesById { get; } = new();

        public void IndexNote(Guid noteId, string title, string body) => NotesById[noteId] = (title, body);

        public int DeleteNotesNotIn(IReadOnlyCollection<Guid> noteIdsStillPresent)
        {
            var toRemove = NotesById.Keys.Where(id => !noteIdsStillPresent.Contains(id)).ToList();
            foreach (var id in toRemove) NotesById.Remove(id);
            return toRemove.Count;
        }

        public IReadOnlyList<SearchResult> Search(string query, int limit = 50) => throw new NotSupportedException();
    }
}
