using MdBolsa.Data.Search;

namespace MdBolsa.Data.Tests.Search;

public class SqliteSearchIndexTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mdbolsa-search-tests-{Guid.NewGuid()}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void Search_FindsNote_ByBodyText()
    {
        var index = new SqliteSearchIndex(_dbPath);
        var noteId = Guid.NewGuid();
        index.IndexNote(noteId, "Docker", "Notes about running containers and Docker Compose.");

        var results = index.Search("containers");

        var result = Assert.Single(results);
        Assert.Equal(noteId, result.NoteId);
        Assert.Contains("containers", result.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Search_FindsNote_ByTitleText()
    {
        var index = new SqliteSearchIndex(_dbPath);
        var noteId = Guid.NewGuid();
        index.IndexNote(noteId, "Raspberry Pi", "Some unrelated body text.");

        var results = index.Search("Raspberry");

        Assert.Single(results);
    }

    [Fact]
    public void Search_IsCaseInsensitive()
    {
        var index = new SqliteSearchIndex(_dbPath);
        index.IndexNote(Guid.NewGuid(), "Note", "Contains the word Welcome in it.");

        Assert.Single(index.Search("welcome"));
    }

    [Fact]
    public void Search_RequiresAllTerms()
    {
        var index = new SqliteSearchIndex(_dbPath);
        index.IndexNote(Guid.NewGuid(), "Note", "Docker and Compose together.");

        Assert.Single(index.Search("Docker Compose"));
        Assert.Empty(index.Search("Docker Kubernetes"));
    }

    [Fact]
    public void Search_ReturnsEmpty_ForBlankQuery()
    {
        var index = new SqliteSearchIndex(_dbPath);
        index.IndexNote(Guid.NewGuid(), "Note", "Some content.");

        Assert.Empty(index.Search("   "));
    }

    [Fact]
    public void Search_DoesNotThrow_OnSpecialCharacters()
    {
        var index = new SqliteSearchIndex(_dbPath);
        index.IndexNote(Guid.NewGuid(), "Note", "Some content.");

        var exception = Record.Exception(() => index.Search("\"unterminated AND OR NOT -weird*"));

        Assert.Null(exception);
    }

    [Fact]
    public void IndexNote_ReplacesPreviousContent_ForSameNote()
    {
        var index = new SqliteSearchIndex(_dbPath);
        var noteId = Guid.NewGuid();
        index.IndexNote(noteId, "Note", "Old content about apples.");

        index.IndexNote(noteId, "Note", "New content about oranges.");

        Assert.Empty(index.Search("apples"));
        Assert.Single(index.Search("oranges"));
    }

    [Fact]
    public void DeleteNotesNotIn_RemovesMissingNotes()
    {
        var index = new SqliteSearchIndex(_dbPath);
        var keepId = Guid.NewGuid();
        var removeId = Guid.NewGuid();
        index.IndexNote(keepId, "Keep", "Keep this note.");
        index.IndexNote(removeId, "Remove", "Remove this note.");

        index.DeleteNotesNotIn(new[] { keepId });

        Assert.Single(index.Search("Keep"));
        Assert.Empty(index.Search("Remove"));
    }
}
