using MdBolsa.Core.Links;
using MdBolsa.Data.Links;

namespace MdBolsa.Data.Tests.Links;

public class SqliteLinkIndexTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mdbolsa-link-tests-{Guid.NewGuid()}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void ReplaceLinksForNote_ThenGetAll_RoundTrips()
    {
        var index = new SqliteLinkIndex(_dbPath);
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        index.ReplaceLinksForNote(sourceId, new[] { new NoteLink(sourceId, "Home", targetId) });

        var link = Assert.Single(index.GetAll());
        Assert.Equal(sourceId, link.SourceNoteId);
        Assert.Equal(targetId, link.TargetNoteId);
        Assert.Equal("Home", link.TargetText);
    }

    [Fact]
    public void ReplaceLinksForNote_ReplacesPreviousLinks_ForThatSourceOnly()
    {
        var index = new SqliteLinkIndex(_dbPath);
        var sourceId = Guid.NewGuid();
        var otherSourceId = Guid.NewGuid();
        index.ReplaceLinksForNote(sourceId, new[] { new NoteLink(sourceId, "Old", null) });
        index.ReplaceLinksForNote(otherSourceId, new[] { new NoteLink(otherSourceId, "Untouched", null) });

        index.ReplaceLinksForNote(sourceId, new[] { new NoteLink(sourceId, "New", null) });

        var links = index.GetAll();
        Assert.Equal(2, links.Count);
        Assert.Contains(links, l => l.SourceNoteId == sourceId && l.TargetText == "New");
        Assert.Contains(links, l => l.SourceNoteId == otherSourceId && l.TargetText == "Untouched");
    }

    [Fact]
    public void GetBacklinkSourceIds_ReturnsNotesLinkingToTarget()
    {
        var index = new SqliteLinkIndex(_dbPath);
        var targetId = Guid.NewGuid();
        var sourceA = Guid.NewGuid();
        var sourceB = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        index.ReplaceLinksForNote(sourceA, new[] { new NoteLink(sourceA, "Target", targetId) });
        index.ReplaceLinksForNote(sourceB, new[] { new NoteLink(sourceB, "Target", targetId) });
        index.ReplaceLinksForNote(unrelated, new[] { new NoteLink(unrelated, "Elsewhere", null) });

        var backlinks = index.GetBacklinkSourceIds(targetId);

        Assert.Equal(2, backlinks.Count);
        Assert.Contains(sourceA, backlinks);
        Assert.Contains(sourceB, backlinks);
    }

    [Fact]
    public void DeleteLinksForNotesNotIn_RemovesLinksForMissingSources()
    {
        var index = new SqliteLinkIndex(_dbPath);
        var keepId = Guid.NewGuid();
        var removeId = Guid.NewGuid();
        index.ReplaceLinksForNote(keepId, new[] { new NoteLink(keepId, "A", null) });
        index.ReplaceLinksForNote(removeId, new[] { new NoteLink(removeId, "B", null) });

        var deleted = index.DeleteLinksForNotesNotIn(new[] { keepId });

        Assert.Equal(1, deleted);
        var link = Assert.Single(index.GetAll());
        Assert.Equal(keepId, link.SourceNoteId);
    }
}
