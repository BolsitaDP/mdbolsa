using Npgsql;

namespace MdBolsa.Server.Notes;

// All data access for notes. Raw Npgsql, parameters only, no string concatenation
// of values - same discipline as SqliteVaultIndex on the client.
//
// Placeholder style is `@name`, which is Npgsql's named-parameter syntax. `$name`
// is PostgreSQL's own placeholder syntax and Npgsql does *not* rewrite it, so it
// reaches the server literally and fails with "syntax error at or near $"; `$1` is
// positional and works, but `@name` keeps the SQL readable next to the C#.
//
// Concurrency rule, stated once here: the server never merges. A write wins on
// `updated_at` (last writer by clock), and a write that is *older* than what is
// stored is not applied, so a stale device can't silently clobber a newer edit.
// Deciding who should actually win - and telling two devices their edits conflict
// - is Phase 10's job (vision.md §11); this phase only keeps the data needed to
// detect it, and stops a stale write from destroying a newer one.
public sealed class NoteStore(string connectionString)
{
    public async Task UpsertAsync(NoteUpsert note, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO notes (id, relative_path, title, content, content_hash, revision,
                               device_id, created_at, updated_at, deleted_at)
            VALUES (@id, @path, @title, @content, @hash, @revision, @device, now(), @updated, NULL)
            ON CONFLICT (id) DO UPDATE SET
                relative_path = EXCLUDED.relative_path,
                title         = EXCLUDED.title,
                content       = EXCLUDED.content,
                content_hash  = EXCLUDED.content_hash,
                revision      = EXCLUDED.revision,
                device_id     = EXCLUDED.device_id,
                updated_at    = EXCLUDED.updated_at,
                deleted_at    = NULL
            WHERE EXCLUDED.updated_at >= notes.updated_at;
            """;

        AddNoteParameters(command, note);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Tombstone rather than DELETE. A hard delete is invisible to a client that
    // was offline: it would never learn the note is gone.
    public async Task<bool> SoftDeleteAsync(
        Guid id, Guid deviceId, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE notes
               SET deleted_at = @deletedAt, updated_at = @updatedAt, device_id = @device
             WHERE id = @id AND updated_at <= @updatedAt AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("device", deviceId);
        command.Parameters.AddWithValue("updatedAt", updatedAt);
        command.Parameters.AddWithValue("deletedAt", updatedAt);

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<NoteRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, relative_path, title, content, content_hash, revision, device_id,
                   updated_at, created_at, deleted_at
              FROM notes WHERE id = @id;
            """;
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadNote(reader) : null;
    }

    // The incremental sync query: everything that changed at or after the cursor,
    // oldest first, one page. Deleted rows come back as tombstones so a client can
    // apply the deletion.
    public async Task<NoteChangesPage> GetChangedSinceAsync(
        DateTimeOffset since,
        NoteCursor? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, relative_path, title, content, content_hash, revision, device_id,
                   updated_at, created_at, deleted_at
              FROM notes
             WHERE updated_at >= @since
               AND (NOT @hasCursor OR (updated_at, id) > (@cursorAt, @cursorId))
             ORDER BY updated_at, id
             LIMIT @limit;
            """;
        command.Parameters.AddWithValue("since", since);
        // The cursor pair is always passed, typed, and switched on by @hasCursor:
        // a NULL parameter would make the row comparison unknown, so a client
        // paging through changes would silently get the first page forever.
        command.Parameters.AddWithValue("hasCursor", cursor is not null);
        command.Parameters.AddWithValue("cursorAt", cursor?.UpdatedAt ?? since);
        command.Parameters.AddWithValue("cursorId", cursor?.NoteId ?? Guid.Empty);
        command.Parameters.AddWithValue("limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var changes = new List<NoteChange>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var deleted = !reader.IsDBNull(9);
            changes.Add(new NoteChange(
                reader.GetGuid(0),
                deleted ? null : reader.GetString(1),
                deleted ? null : reader.GetString(2),
                deleted ? null : reader.GetString(3),
                deleted ? null : reader.GetString(4),
                deleted ? null : reader.GetInt32(5),
                deleted ? null : reader.GetGuid(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                deleted));
        }

        // A full page means there may be more; hand back the watermark so a client
        // that loses its place can resume exactly where it stopped.
        var next = changes.Count == limit
            ? new NoteCursor(changes[^1].UpdatedAt, changes[^1].Id)
            : null;

        return new NoteChangesPage(changes, next);
    }

    private static void AddNoteParameters(NpgsqlCommand command, NoteUpsert note)
    {
        command.Parameters.AddWithValue("id", note.Id);
        command.Parameters.AddWithValue("path", note.RelativePath);
        command.Parameters.AddWithValue("title", note.Title);
        command.Parameters.AddWithValue("content", note.Content);
        command.Parameters.AddWithValue("hash", note.ContentHash);
        command.Parameters.AddWithValue("revision", note.Revision);
        command.Parameters.AddWithValue("device", note.DeviceId);
        command.Parameters.AddWithValue("updated", note.UpdatedAt);
    }

    private static NoteRecord ReadNote(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetGuid(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9));
}
