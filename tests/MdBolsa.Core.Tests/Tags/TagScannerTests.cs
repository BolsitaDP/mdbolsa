using MdBolsa.Core.Tags;
using MdBolsa.Core.Vault;

namespace MdBolsa.Core.Tests.Tags;

public class TagScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mdbolsa-tag-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Scan_IndexesFrontMatterAndInlineTags()
    {
        var dockerId = Guid.NewGuid();
        var homeId = Guid.NewGuid();
        WriteNote("Development/Docker.md", "---\ntags: [homelab]\n---\n\nUses #docker daily.");
        WriteNote("Personal/Home.md", "Nothing tagged here... except #homelab.");
        var vaultIndex = new FakeVaultIndex(Note(dockerId, "Development/Docker.md"), Note(homeId, "Personal/Home.md"));
        var tagIndex = new FakeTagIndex();

        var result = new TagScanner(_root, vaultIndex, tagIndex).Scan();

        Assert.Equal(2, result.TaggedNotes);
        Assert.Equal(2, result.DistinctTags);
        Assert.Equal(["docker", "homelab"], tagIndex.GetTagsForNote(dockerId));
        Assert.Equal(["homelab"], tagIndex.GetTagsForNote(homeId));
    }

    [Fact]
    public void Scan_ReplacesTags_WhenANoteChanges()
    {
        var noteId = Guid.NewGuid();
        WriteNote("Note.md", "#one #two");
        var vaultIndex = new FakeVaultIndex(Note(noteId, "Note.md"));
        var tagIndex = new FakeTagIndex();
        new TagScanner(_root, vaultIndex, tagIndex).Scan();

        WriteNote("Note.md", "#two #three");
        var result = new TagScanner(_root, vaultIndex, tagIndex).Scan();

        Assert.Equal(["three", "two"], tagIndex.GetTagsForNote(noteId));
        Assert.Equal(2, result.DistinctTags);
    }

    [Fact]
    public void Scan_ClearsTags_ForNoteThatNoLongerHasThem()
    {
        var noteId = Guid.NewGuid();
        WriteNote("Note.md", "#one");
        var vaultIndex = new FakeVaultIndex(Note(noteId, "Note.md"));
        var tagIndex = new FakeTagIndex();
        new TagScanner(_root, vaultIndex, tagIndex).Scan();

        WriteNote("Note.md", "No tags now.");
        new TagScanner(_root, vaultIndex, tagIndex).Scan();

        Assert.Empty(tagIndex.GetTagsForNote(noteId));
    }

    [Fact]
    public void Scan_RemovesTags_ForNotesNoLongerInVault()
    {
        var keepId = Guid.NewGuid();
        var removeId = Guid.NewGuid();
        WriteNote("Keep.md", "#shared");
        WriteNote("Remove.md", "#shared");
        var vaultIndex = new FakeVaultIndex(Note(keepId, "Keep.md"), Note(removeId, "Remove.md"));
        var tagIndex = new FakeTagIndex();
        new TagScanner(_root, vaultIndex, tagIndex).Scan();

        vaultIndex.Notes.RemoveAll(n => n.Id == removeId);
        File.Delete(Path.Combine(_root, "Remove.md"));
        new TagScanner(_root, vaultIndex, tagIndex).Scan();

        Assert.Equal(keepId, Assert.Single(tagIndex.GetNoteIdsForTag("shared")));
    }

    [Fact]
    public void Scan_CountsUntaggedNotesSeparately()
    {
        WriteNote("Tagged.md", "#one");
        WriteNote("Untagged.md", "Nothing.");
        var vaultIndex = new FakeVaultIndex(Note(Guid.NewGuid(), "Tagged.md"), Note(Guid.NewGuid(), "Untagged.md"));
        var tagIndex = new FakeTagIndex();

        var result = new TagScanner(_root, vaultIndex, tagIndex).Scan();

        Assert.Equal(1, result.TaggedNotes);
        Assert.Equal(1, result.DistinctTags);
    }

    private void WriteNote(string relativePath, string body)
    {
        var fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, body);
    }

    private static NoteMetadata Note(Guid id, string relativePath) =>
        new(id, relativePath, Path.GetFileNameWithoutExtension(relativePath), "hash", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class FakeVaultIndex : IVaultIndex
    {
        public List<NoteMetadata> Notes { get; }

        public FakeVaultIndex(params NoteMetadata[] notes) => Notes = notes.ToList();

        public IReadOnlyList<NoteMetadata> GetAll() => Notes;

        public void Upsert(NoteMetadata note) => throw new NotSupportedException();

        public int DeleteMissing(IReadOnlyCollection<Guid> idsStillPresent) => throw new NotSupportedException();
    }

    private sealed class FakeTagIndex : ITagIndex
    {
        private readonly Dictionary<Guid, List<string>> _tagsByNote = new();

        public IReadOnlyList<TagCount> GetTagCounts() =>
            _tagsByNote.Values
                .SelectMany(t => t)
                .GroupBy(t => t, StringComparer.Ordinal)
                .Select(g => new TagCount(g.Key, g.Count()))
                .ToList();

        public IReadOnlyList<string> GetTagsForNote(Guid noteId) =>
            _tagsByNote.TryGetValue(noteId, out var tags) ? tags.OrderBy(t => t, StringComparer.Ordinal).ToList() : [];

        public IReadOnlyList<Guid> GetNoteIdsForTag(string tag) =>
            _tagsByNote.Where(pair => pair.Value.Contains(tag, StringComparer.Ordinal))
                .Select(pair => pair.Key)
                .ToList();

        public void ReplaceTagsForNote(Guid noteId, IReadOnlyList<string> tags) =>
            _tagsByNote[noteId] = tags.ToList();

        public int DeleteTagsForNotesNotIn(IReadOnlyCollection<Guid> noteIdsStillPresent)
        {
            var toRemove = _tagsByNote.Keys.Where(id => !noteIdsStillPresent.Contains(id)).ToList();
            foreach (var id in toRemove) _tagsByNote.Remove(id);
            return toRemove.Count;
        }
    }
}
