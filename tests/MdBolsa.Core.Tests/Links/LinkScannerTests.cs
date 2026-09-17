using MdBolsa.Core.Links;
using MdBolsa.Core.Vault;

namespace MdBolsa.Core.Tests.Links;

public class LinkScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mdbolsa-link-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Scan_ResolvesLinkByExactRelativePath()
    {
        var homeId = Guid.NewGuid();
        var welcomeId = Guid.NewGuid();
        WriteNote("Welcome.md", "See [[Personal/Home]].");
        WriteNote("Personal/Home.md", "# Home");
        var vaultIndex = new FakeVaultIndex(Note(welcomeId, "Welcome.md"), Note(homeId, "Personal/Home.md"));
        var linkIndex = new FakeLinkIndex();

        var result = new LinkScanner(_root, vaultIndex, linkIndex).Scan();

        Assert.Equal(1, result.Resolved);
        Assert.Equal(0, result.Unresolved);
        var link = Assert.Single(linkIndex.GetAll());
        Assert.Equal(welcomeId, link.SourceNoteId);
        Assert.Equal(homeId, link.TargetNoteId);
    }

    [Fact]
    public void Scan_ResolvesLinkByFileNameOnly_WhenUnambiguous()
    {
        var homeId = Guid.NewGuid();
        var welcomeId = Guid.NewGuid();
        WriteNote("Welcome.md", "See [[Home]].");
        WriteNote("Personal/Home.md", "# Home");
        var vaultIndex = new FakeVaultIndex(Note(welcomeId, "Welcome.md"), Note(homeId, "Personal/Home.md"));
        var linkIndex = new FakeLinkIndex();

        new LinkScanner(_root, vaultIndex, linkIndex).Scan();

        var link = Assert.Single(linkIndex.GetAll());
        Assert.Equal(homeId, link.TargetNoteId);
    }

    [Fact]
    public void Scan_LeavesLinkUnresolved_WhenFileNameIsAmbiguous()
    {
        var welcomeId = Guid.NewGuid();
        var homeAId = Guid.NewGuid();
        var homeBId = Guid.NewGuid();
        WriteNote("Welcome.md", "See [[Home]].");
        WriteNote("Personal/Home.md", "# Home A");
        WriteNote("Work/Home.md", "# Home B");
        var vaultIndex = new FakeVaultIndex(
            Note(welcomeId, "Welcome.md"), Note(homeAId, "Personal/Home.md"), Note(homeBId, "Work/Home.md"));
        var linkIndex = new FakeLinkIndex();

        var result = new LinkScanner(_root, vaultIndex, linkIndex).Scan();

        Assert.Equal(0, result.Resolved);
        Assert.Equal(1, result.Unresolved);
        var link = Assert.Single(linkIndex.GetAll());
        Assert.Null(link.TargetNoteId);
    }

    [Fact]
    public void Scan_LeavesLinkUnresolved_WhenTargetDoesNotExist()
    {
        var welcomeId = Guid.NewGuid();
        WriteNote("Welcome.md", "See [[Nowhere]].");
        var vaultIndex = new FakeVaultIndex(Note(welcomeId, "Welcome.md"));
        var linkIndex = new FakeLinkIndex();

        var result = new LinkScanner(_root, vaultIndex, linkIndex).Scan();

        Assert.Equal(0, result.Resolved);
        Assert.Equal(1, result.Unresolved);
    }

    [Fact]
    public void Scan_RemovesLinks_ForNotesNoLongerInVault()
    {
        var welcomeId = Guid.NewGuid();
        WriteNote("Welcome.md", "See [[Nowhere]].");
        var vaultIndex = new FakeVaultIndex(Note(welcomeId, "Welcome.md"));
        var linkIndex = new FakeLinkIndex();
        new LinkScanner(_root, vaultIndex, linkIndex).Scan();
        Assert.Single(linkIndex.GetAll());

        vaultIndex.Notes.Clear();
        new LinkScanner(_root, vaultIndex, linkIndex).Scan();

        Assert.Empty(linkIndex.GetAll());
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

    private sealed class FakeLinkIndex : ILinkIndex
    {
        private readonly Dictionary<Guid, List<NoteLink>> _linksBySource = new();

        public IReadOnlyList<NoteLink> GetAll() => _linksBySource.Values.SelectMany(l => l).ToList();

        public void ReplaceLinksForNote(Guid sourceNoteId, IReadOnlyList<NoteLink> links) =>
            _linksBySource[sourceNoteId] = links.ToList();

        public IReadOnlyList<Guid> GetBacklinkSourceIds(Guid targetNoteId) =>
            GetAll().Where(l => l.TargetNoteId == targetNoteId).Select(l => l.SourceNoteId).Distinct().ToList();

        public int DeleteLinksForNotesNotIn(IReadOnlyCollection<Guid> noteIdsStillPresent)
        {
            var toRemove = _linksBySource.Keys.Where(id => !noteIdsStillPresent.Contains(id)).ToList();
            foreach (var id in toRemove) _linksBySource.Remove(id);
            return toRemove.Count;
        }
    }
}
