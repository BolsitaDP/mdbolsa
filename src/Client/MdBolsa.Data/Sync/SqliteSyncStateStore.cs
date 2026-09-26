using MdBolsa.Contracts;
using MdBolsa.Core.Sync;
using Microsoft.Data.Sqlite;

namespace MdBolsa.Data.Sync;

// Local sync state, in the same SQLite file as the rest of the local indexes: a
// cursor and a per-note hash are derived state, exactly like a search index, and
// keeping them in one file means one thing to delete when rebuilding from the
// vault (a full resync is the correct recovery for any of it).
//
// Everything here is rebuildable, and nothing here is authoritative: the vault's
// .md files are. A lost or stale sync state costs a re-sync, not data.
public sealed class SqliteSyncStateStore : ISyncStateStore
{
    private readonly string _connectionString;

    public SqliteSyncStateStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        EnsureSchema();
    }

    public NoteCursor? GetCursor()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        // A single-row table, so the cursor is "the row" rather than a key/value
        // pair that could end up duplicated.
        command.CommandText = "SELECT updated_at, note_id FROM sync_cursor LIMIT 1";

        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new NoteCursor(DateTimeOffset.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)))
            : null;
    }

    public void SetCursor(NoteCursor cursor)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_cursor (id, updated_at, note_id) VALUES (1, $updatedAt, $noteId)
            ON CONFLICT(id) DO UPDATE SET updated_at = excluded.updated_at, note_id = excluded.note_id;
            """;
        command.Parameters.AddWithValue("$updatedAt", cursor.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$noteId", cursor.NoteId.ToString());
        command.ExecuteNonQuery();
    }

    public void ResetCursor()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sync_cursor";
        command.ExecuteNonQuery();
    }

    public string? GetPushedHash(Guid noteId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content_hash FROM sync_note_state WHERE note_id = $noteId";
        command.Parameters.AddWithValue("$noteId", noteId.ToString());

        var result = command.ExecuteScalar();
        return result as string;
    }

    public void SetPushedHash(Guid noteId, string hash)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_note_state (note_id, content_hash) VALUES ($noteId, $hash)
            ON CONFLICT(note_id) DO UPDATE SET content_hash = excluded.content_hash;
            """;
        command.Parameters.AddWithValue("$noteId", noteId.ToString());
        command.Parameters.AddWithValue("$hash", hash);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<SyncConflict> GetConflicts()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT note_id, relative_path, local_revision, server_revision, server_device_id, detected_at
              FROM sync_conflicts ORDER BY detected_at;
            """;

        using var reader = command.ExecuteReader();
        var conflicts = new List<SyncConflict>();
        while (reader.Read())
        {
            conflicts.Add(new SyncConflict(
                Guid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                DateTimeOffset.Parse(reader.GetString(5))));
        }

        return conflicts;
    }

    public void AddConflict(SyncConflict conflict)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        // One row per note, re-detected rather than duplicated: a conflict you
        // haven't dealt with shouldn't grow a list every time you press Sync.
        command.CommandText = """
            INSERT INTO sync_conflicts (note_id, relative_path, local_revision, server_revision, server_device_id, detected_at)
            VALUES ($noteId, $path, $local, $server, $device, $detected)
            ON CONFLICT(note_id) DO UPDATE SET
                relative_path = excluded.relative_path,
                local_revision = excluded.local_revision,
                server_revision = excluded.server_revision,
                server_device_id = excluded.server_device_id,
                detected_at = excluded.detected_at;
            """;
        command.Parameters.AddWithValue("$noteId", conflict.NoteId.ToString());
        command.Parameters.AddWithValue("$path", (object?)conflict.RelativePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$local", (object?)conflict.LocalRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("$server", (object?)conflict.ServerRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("$device", (object?)conflict.ServerDeviceId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$detected", conflict.DetectedAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void RemoveConflict(Guid noteId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sync_conflicts WHERE note_id = $noteId";
        command.Parameters.AddWithValue("$noteId", noteId.ToString());
        command.ExecuteNonQuery();
    }

    public void ClearConflicts()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sync_conflicts";
        command.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sync_cursor (
                id         INTEGER PRIMARY KEY CHECK (id = 1),
                updated_at TEXT NOT NULL,
                note_id    TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sync_note_state (
                note_id     TEXT PRIMARY KEY,
                content_hash TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sync_conflicts (
                note_id           TEXT PRIMARY KEY,
                relative_path     TEXT NULL,
                local_revision    INTEGER NULL,
                server_revision   INTEGER NULL,
                server_device_id  TEXT NULL,
                detected_at       TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
