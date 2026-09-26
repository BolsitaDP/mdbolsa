using MdBolsa.Core.Tags;
using MdBolsa.Data.Tags;

namespace MdBolsa.Data.Tests.Tags;

public class SqliteTagIndexTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mdbolsa-tag-tests-{Guid.NewGuid()}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void ReplaceTagsForNote_ThenGetTagsForNote_RoundTrips()
    {
        var index = new SqliteTagIndex(_dbPath);
        var noteId = Guid.NewGuid();

        index.ReplaceTagsForNote(noteId, ["docker", "homelab"]);

        Assert.Equal(["docker", "homelab"], index.GetTagsForNote(noteId));
    }

    [Fact]
    public void ReplaceTagsForNote_ReplacesPreviousTags_ForThatNoteOnly()
    {
        var index = new SqliteTagIndex(_dbPath);
        var noteId = Guid.NewGuid();
        var otherNoteId = Guid.NewGuid();
        index.ReplaceTagsForNote(noteId, ["old"]);
        index.ReplaceTagsForNote(otherNoteId, ["untouched"]);

        index.ReplaceTagsForNote(noteId, ["new"]);

        Assert.Equal(["new"], index.GetTagsForNote(noteId));
        Assert.Equal(["untouched"], index.GetTagsForNote(otherNoteId));
    }

    [Fact]
    public void ReplaceTagsForNote_WithNoTags_RemovesThem()
    {
        var index = new SqliteTagIndex(_dbPath);
        var noteId = Guid.NewGuid();
        index.ReplaceTagsForNote(noteId, ["docker"]);

        index.ReplaceTagsForNote(noteId, []);

        Assert.Empty(index.GetTagsForNote(noteId));
        Assert.Empty(index.GetTagCounts());
    }

    [Fact]
    public void GetTagCounts_CountsNotesPerTag()
    {
        var index = new SqliteTagIndex(_dbPath);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        index.ReplaceTagsForNote(a, ["shared", "only-a"]);
        index.ReplaceTagsForNote(b, ["shared"]);

        var counts = index.GetTagCounts();

        Assert.Equal(2, counts.Count);
        Assert.Equal(new TagCount("only-a", 1), counts[0]);
        Assert.Equal(new TagCount("shared", 2), counts[1]);
    }

    [Fact]
    public void GetNoteIdsForTag_ReturnsEveryNoteWithThatTag()
    {
        var index = new SqliteTagIndex(_dbPath);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        index.ReplaceTagsForNote(a, ["shared"]);
        index.ReplaceTagsForNote(b, ["shared"]);
        index.ReplaceTagsForNote(unrelated, ["elsewhere"]);

        var noteIds = index.GetNoteIdsForTag("shared");

        Assert.Equal(2, noteIds.Count);
        Assert.Contains(a, noteIds);
        Assert.Contains(b, noteIds);
    }

    [Fact]
    public void GetNoteIdsForTag_NormalizesTheRequestedTag()
    {
        var index = new SqliteTagIndex(_dbPath);
        var noteId = Guid.NewGuid();
        index.ReplaceTagsForNote(noteId, ["docker"]);

        Assert.Equal(noteId, Assert.Single(index.GetNoteIdsForTag("#Docker")));
    }

    [Fact]
    public void DeleteTagsForNotesNotIn_RemovesTagsForMissingNotes()
    {
        var index = new SqliteTagIndex(_dbPath);
        var keepId = Guid.NewGuid();
        var removeId = Guid.NewGuid();
        index.ReplaceTagsForNote(keepId, ["shared"]);
        index.ReplaceTagsForNote(removeId, ["shared"]);

        var deleted = index.DeleteTagsForNotesNotIn([keepId]);

        Assert.Equal(1, deleted);
        Assert.Equal(keepId, Assert.Single(index.GetNoteIdsForTag("shared")));
    }

    [Fact]
    public void DeleteTagsForNotesNotIn_WithNoNotesRemaining_ClearsEverything()
    {
        var index = new SqliteTagIndex(_dbPath);
        index.ReplaceTagsForNote(Guid.NewGuid(), ["shared"]);

        Assert.Equal(1, index.DeleteTagsForNotesNotIn([]));
        Assert.Empty(index.GetTagCounts());
    }
}
