using MdBolsa.Core.Vault;

namespace MdBolsa.Core.Tests.Vault;

public class VaultScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mdbolsa-vault-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Scan_AssignsStableId_AndWritesItBackToFile()
    {
        var path = Path.Combine(_root, "Note.md");
        File.WriteAllText(path, "# Note\n");
        var index = new InMemoryVaultIndex();

        var result = new VaultScanner(_root, index).Scan();

        Assert.Equal(1, result.Added);
        var note = Assert.Single(index.GetAll());
        Assert.NotEqual(Guid.Empty, note.Id);
        Assert.Contains($"id: {note.Id}", File.ReadAllText(path));
    }

    [Fact]
    public void Scan_IsIdempotent_OnUnchangedFiles()
    {
        File.WriteAllText(Path.Combine(_root, "Note.md"), "# Note\n");
        var index = new InMemoryVaultIndex();
        var scanner = new VaultScanner(_root, index);
        scanner.Scan();

        var second = scanner.Scan();

        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
        Assert.Equal(1, second.Unchanged);
        Assert.Single(index.GetAll());
    }

    [Fact]
    public void Scan_BumpsRevision_WhenContentChanges()
    {
        var path = Path.Combine(_root, "Note.md");
        File.WriteAllText(path, "# Note\n");
        var index = new InMemoryVaultIndex();
        var scanner = new VaultScanner(_root, index);
        scanner.Scan();

        File.WriteAllText(path, File.ReadAllText(path) + "\nMore content.\n");
        var result = scanner.Scan();

        Assert.Equal(1, result.Updated);
        var note = Assert.Single(index.GetAll());
        Assert.Equal(2, note.Revision);
    }

    [Fact]
    public void Scan_TracksRename_AsMoved_NotDeleteAndCreate()
    {
        var oldPath = Path.Combine(_root, "Old.md");
        File.WriteAllText(oldPath, "# Note\n");
        var index = new InMemoryVaultIndex();
        var scanner = new VaultScanner(_root, index);
        scanner.Scan();
        var originalId = index.GetAll().Single().Id;

        File.Move(oldPath, Path.Combine(_root, "New.md"));
        var result = scanner.Scan();

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Moved);
        var note = Assert.Single(index.GetAll());
        Assert.Equal(originalId, note.Id);
        Assert.Equal("New.md", note.RelativePath);
    }

    [Fact]
    public void Scan_WithCarriageReturnLineEndings_DoesNotReassignId()
    {
        var id = Guid.NewGuid();
        var content = $"---\rid: {id}\r---\r\r# Note\r";
        var path = Path.Combine(_root, "Note.md");
        File.WriteAllText(path, content);
        var index = new InMemoryVaultIndex();

        new VaultScanner(_root, index).Scan();

        var note = Assert.Single(index.GetAll());
        Assert.Equal(id, note.Id);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void Scan_RepeatedSavesWithVaryingLineEndings_KeepExactlyOneId()
    {
        var path = Path.Combine(_root, "Note.md");
        File.WriteAllText(path, "# Note\n");
        var index = new InMemoryVaultIndex();
        var scanner = new VaultScanner(_root, index);
        scanner.Scan();
        var id = index.GetAll().Single().Id;

        File.WriteAllText(path, $"---\nid: {id}\n---\n\n# Note\nEdited once.\n");
        scanner.Scan();
        File.WriteAllText(path, $"---\r\nid: {id}\r\n---\r\n\r\n# Note\r\nEdited twice.\r\n");
        scanner.Scan();

        var note = Assert.Single(index.GetAll());
        Assert.Equal(id, note.Id);
        var idOccurrences = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(path), "id:").Count;
        Assert.Equal(1, idOccurrences);
    }

    [Fact]
    public void Scan_RemovesDeletedFiles_FromIndex()
    {
        var path = Path.Combine(_root, "Note.md");
        File.WriteAllText(path, "# Note\n");
        var index = new InMemoryVaultIndex();
        var scanner = new VaultScanner(_root, index);
        scanner.Scan();

        File.Delete(path);
        var result = scanner.Scan();

        Assert.Equal(1, result.Deleted);
        Assert.Empty(index.GetAll());
    }

    private sealed class InMemoryVaultIndex : IVaultIndex
    {
        private readonly Dictionary<Guid, NoteMetadata> _notes = new();

        public IReadOnlyList<NoteMetadata> GetAll() => _notes.Values.ToList();

        public void Upsert(NoteMetadata note) => _notes[note.Id] = note;

        public int DeleteMissing(IReadOnlyCollection<Guid> idsStillPresent)
        {
            var toRemove = _notes.Keys.Where(id => !idsStillPresent.Contains(id)).ToList();
            foreach (var id in toRemove) _notes.Remove(id);
            return toRemove.Count;
        }
    }
}
