using MdBolsa.Core.Vault;
using MdBolsa.Data.Vault;

namespace MdBolsa.Data.Tests.Vault;

public class SqliteVaultIndexTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mdbolsa-tests-{Guid.NewGuid()}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void Schema_IsCreatedAutomatically_OnConstruction()
    {
        var index = new SqliteVaultIndex(_dbPath);

        Assert.Empty(index.GetAll());
    }

    [Fact]
    public void Upsert_ThenGetAll_RoundTrips()
    {
        var index = new SqliteVaultIndex(_dbPath);
        var note = new NoteMetadata(Guid.NewGuid(), "Note.md", "Note", "hash1", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        index.Upsert(note);

        var result = Assert.Single(index.GetAll());
        Assert.Equal(note.Id, result.Id);
        Assert.Equal(note.RelativePath, result.RelativePath);
        Assert.Equal(note.ContentHash, result.ContentHash);
    }

    [Fact]
    public void Upsert_SameId_UpdatesInPlace_PreservingCreatedAt()
    {
        var index = new SqliteVaultIndex(_dbPath);
        var id = Guid.NewGuid();
        var created = DateTimeOffset.UtcNow.AddDays(-1);
        index.Upsert(new NoteMetadata(id, "Note.md", "Note", "hash1", 1, created, created));

        var updated = DateTimeOffset.UtcNow;
        index.Upsert(new NoteMetadata(id, "Note.md", "Note", "hash2", 2, updated, updated));

        var result = Assert.Single(index.GetAll());
        Assert.Equal(2, result.Revision);
        Assert.Equal("hash2", result.ContentHash);
        Assert.Equal(created, result.CreatedAt);
    }

    [Fact]
    public void Upsert_SamePathDifferentId_ReplacesStaleRow_WithoutThrowing()
    {
        var index = new SqliteVaultIndex(_dbPath);
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        index.Upsert(new NoteMetadata(oldId, "Note.md", "Note", "h1", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var exception = Record.Exception(() => index.Upsert(
            new NoteMetadata(newId, "Note.md", "Note", "h2", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));

        Assert.Null(exception);
        var note = Assert.Single(index.GetAll());
        Assert.Equal(newId, note.Id);
    }

    [Fact]
    public void DeleteMissing_RemovesNotesNotInSet()
    {
        var index = new SqliteVaultIndex(_dbPath);
        var keep = Guid.NewGuid();
        var remove = Guid.NewGuid();
        index.Upsert(new NoteMetadata(keep, "Keep.md", "Keep", "h", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        index.Upsert(new NoteMetadata(remove, "Remove.md", "Remove", "h", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var deletedCount = index.DeleteMissing(new[] { keep });

        Assert.Equal(1, deletedCount);
        var remaining = Assert.Single(index.GetAll());
        Assert.Equal(keep, remaining.Id);
    }
}
