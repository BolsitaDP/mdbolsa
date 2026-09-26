using MdBolsa.Contracts;
using MdBolsa.Core.Sync;
using MdBolsa.Data.Sync;

namespace MdBolsa.Data.Tests.Sync;

public class SqliteSyncStateStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mdbolsa-sync-state-{Guid.NewGuid()}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void Cursor_IsNullBeforeTheFirstSync() =>
        Assert.Null(new SqliteSyncStateStore(_dbPath).GetCursor());

    [Fact]
    public void Cursor_RoundTrips()
    {
        var store = new SqliteSyncStateStore(_dbPath);
        var cursor = new NoteCursor(DateTimeOffset.UtcNow, Guid.NewGuid());

        store.SetCursor(cursor);

        Assert.Equal(cursor, store.GetCursor());
    }

    [Fact]
    public void Cursor_IsReplaced_NotDuplicated()
    {
        var store = new SqliteSyncStateStore(_dbPath);
        store.SetCursor(new NoteCursor(DateTimeOffset.UtcNow, Guid.NewGuid()));
        var second = new NoteCursor(DateTimeOffset.UtcNow.AddHours(1), Guid.NewGuid());

        store.SetCursor(second);

        Assert.Equal(second, store.GetCursor());
    }

    [Fact]
    public void PushedHash_IsNullForAnUnknownNote() =>
        Assert.Null(new SqliteSyncStateStore(_dbPath).GetPushedHash(Guid.NewGuid()));

    [Fact]
    public void PushedHash_RoundTrips()
    {
        var store = new SqliteSyncStateStore(_dbPath);
        var noteId = Guid.NewGuid();

        store.SetPushedHash(noteId, "HASH1");
        Assert.Equal("HASH1", store.GetPushedHash(noteId));

        store.SetPushedHash(noteId, "HASH2");
        Assert.Equal("HASH2", store.GetPushedHash(noteId));
    }

    [Fact]
    public void Conflicts_RoundTrip()
    {
        var store = new SqliteSyncStateStore(_dbPath);
        var noteId = Guid.NewGuid();
        var serverDevice = Guid.NewGuid();

        store.AddConflict(new SyncConflict(noteId, "Notes/A.md", 3, 5, serverDevice, DateTimeOffset.UtcNow));

        var conflict = Assert.Single(store.GetConflicts());
        Assert.Equal(noteId, conflict.NoteId);
        Assert.Equal("Notes/A.md", conflict.RelativePath);
        Assert.Equal(3, conflict.LocalRevision);
        Assert.Equal(5, conflict.ServerRevision);
        Assert.Equal(serverDevice, conflict.ServerDeviceId);
    }

    [Fact]
    public void Conflicts_ToleratesUnknownRevisionAndDevice()
    {
        var store = new SqliteSyncStateStore(_dbPath);

        store.AddConflict(new SyncConflict(Guid.NewGuid(), null, null, null, null, DateTimeOffset.UtcNow));

        var conflict = Assert.Single(store.GetConflicts());
        Assert.Null(conflict.LocalRevision);
        Assert.Null(conflict.ServerRevision);
        Assert.Null(conflict.ServerDeviceId);
    }

    [Fact]
    public void Conflicts_AreOneRowPerNote_SoRepeatedSyncsDontGrowTheList()
    {
        var store = new SqliteSyncStateStore(_dbPath);
        var noteId = Guid.NewGuid();

        store.AddConflict(new SyncConflict(noteId, "A.md", 1, 2, null, DateTimeOffset.UtcNow));
        store.AddConflict(new SyncConflict(noteId, "A.md", 2, 3, null, DateTimeOffset.UtcNow.AddMinutes(1)));

        var conflict = Assert.Single(store.GetConflicts());
        Assert.Equal(2, conflict.LocalRevision);
    }

    [Fact]
    public void ClearConflicts_EmptiesTheList()
    {
        var store = new SqliteSyncStateStore(_dbPath);
        store.AddConflict(new SyncConflict(Guid.NewGuid(), "A.md", 1, 2, null, DateTimeOffset.UtcNow));

        store.ClearConflicts();

        Assert.Empty(store.GetConflicts());
    }
}
