using MdBolsa.Contracts;
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
// Concurrency rule, unchanged from Phase 8: the server never merges. A write wins
// on `updated_at` (last writer by clock), and an older write is not applied, so a
// stale device can't silently clobber a newer edit. What Phase 10 adds is that
// the loser isn't destroyed: the superseded revision is copied into
// `note_versions` in the same transaction, so both versions remain readable.
public sealed class NoteStore(string connectionString)
{
    public async Task<NoteStored?> UpsertAsync(NoteUpsert note, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        NoteStored? stored;
        try
        {
            // Archive whatever is being replaced *before* replacing it, in the same
            // transaction. Doing it the other way round would leave a window where
            // the old content is already gone and the history row isn't written
            // yet - the exact data loss the history exists to prevent.
            await using (var archive = connection.CreateCommand())
            {
                archive.Transaction = transaction;
                archive.CommandText = """
                    INSERT INTO note_versions
                        (note_id, revision, relative_path, content, content_hash, device_id, updated_at)
                    SELECT id, revision, relative_path, content, content_hash, device_id, updated_at
                      FROM notes
                     WHERE id = @id
                       AND NOT EXISTS (SELECT 1 FROM note_versions v
                                      WHERE v.note_id = notes.id AND v.revision = notes.revision)
                    ON CONFLICT (note_id, revision) DO NOTHING;
                    """;
                archive.Parameters.AddWithValue("id", note.Id);
                await archive.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var write = connection.CreateCommand())
            {
                write.Transaction = transaction;
                write.CommandText = """
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
                    WHERE EXCLUDED.updated_at >= notes.updated_at
                    RETURNING id, relative_path, title, content_hash, revision, device_id, updated_at, deleted_at;
                    """;

                AddNoteParameters(write, note);

                await using var reader = await write.ExecuteReaderAsync(cancellationToken);
                // No row returned means the WHERE clause rejected the write:
                // something newer is already stored. The caller turns that into a
                // conflict report rather than pretending the write landed.
                stored = await reader.ReadAsync(cancellationToken) ? ReadStored(reader) : null;
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Another note already holds this path. That is a conflict to report,
            // not a server fault.
            await transaction.RollbackAsync(cancellationToken);
            throw new NotePathConflictException(note.RelativePath);
        }

        if (stored is null)
        {
            // The write was refused, so the archive insert above is pointless -
            // roll it back rather than recording a version of a note that didn't
            // change.
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        // And archive what we just wrote, so the history includes the current
        // revision too (a client asking for "version 1" of a note that has only
        // ever been at revision 1 should get an answer).
        await using (var record = connection.CreateCommand())
        {
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO note_versions
                    (note_id, revision, relative_path, content, content_hash, device_id, updated_at)
                VALUES (@id, @revision, @path, @content, @hash, @device, @updated)
                ON CONFLICT (note_id, revision) DO NOTHING;
                """;
            record.Parameters.AddWithValue("id", note.Id);
            record.Parameters.AddWithValue("revision", note.Revision);
            record.Parameters.AddWithValue("path", note.RelativePath);
            record.Parameters.AddWithValue("content", note.Content);
            record.Parameters.AddWithValue("hash", note.ContentHash);
            record.Parameters.AddWithValue("device", note.DeviceId);
            record.Parameters.AddWithValue("updated", note.UpdatedAt);
            await record.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return stored;
    }

    // Tombstone rather than DELETE. A hard delete is invisible to a client that
    // was offline: it would never learn the note is gone. The content is archived
    // first, so an accidental deletion is still recoverable.
    public async Task<bool> SoftDeleteAsync(
        Guid id, Guid deviceId, DateTimeOffset updatedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var archive = connection.CreateCommand())
        {
            archive.Transaction = transaction;
            archive.CommandText = """
                INSERT INTO note_versions
                    (note_id, revision, relative_path, content, content_hash, device_id, updated_at)
                SELECT id, revision, relative_path, content, content_hash, device_id, updated_at
                  FROM notes
                 WHERE id = @id
                   AND NOT EXISTS (SELECT 1 FROM note_versions v
                                  WHERE v.note_id = notes.id AND v.revision = notes.revision)
                ON CONFLICT (note_id, revision) DO NOTHING;
                """;
            archive.Parameters.AddWithValue("id", id);
            await archive.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            UPDATE notes
               SET deleted_at = @deletedAt, updated_at = @updatedAt, device_id = @device
             WHERE id = @id AND updated_at <= @updatedAt AND deleted_at IS NULL;
            """;
        delete.Parameters.AddWithValue("id", id);
        delete.Parameters.AddWithValue("device", deviceId);
        delete.Parameters.AddWithValue("updatedAt", updatedAt);
        delete.Parameters.AddWithValue("deletedAt", updatedAt);

        var deleted = await delete.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (deleted) await transaction.CommitAsync(cancellationToken);
        else await transaction.RollbackAsync(cancellationToken);

        return deleted;
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

    // A note's edit history, newest first. The current revision is included and
    // flagged, so a client can render "version 3 (current)" alongside the ones it
    // lost.
    public async Task<IReadOnlyList<NoteVersion>> GetVersionsAsync(
        Guid noteId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.note_id, v.revision, v.relative_path, v.content, v.content_hash,
                   v.device_id, v.updated_at, (n.revision = v.revision AND n.deleted_at IS NULL)
              FROM note_versions v
              JOIN notes n ON n.id = v.note_id
             WHERE v.note_id = @noteId
             ORDER BY v.revision DESC;
            """;
        command.Parameters.AddWithValue("noteId", noteId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var versions = new List<NoteVersion>();
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(new NoteVersion(
                reader.GetGuid(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetGuid(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetBoolean(7)));
        }

        return versions;
    }

    public async Task<NoteVersion?> GetVersionAsync(
        Guid noteId, int revision, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.note_id, v.revision, v.relative_path, v.content, v.content_hash,
                   v.device_id, v.updated_at, (n.revision = v.revision AND n.deleted_at IS NULL)
              FROM note_versions v
              JOIN notes n ON n.id = v.note_id
             WHERE v.note_id = @noteId AND v.revision = @revision;
            """;
        command.Parameters.AddWithValue("noteId", noteId);
        command.Parameters.AddWithValue("revision", revision);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new NoteVersion(
            reader.GetGuid(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetGuid(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetBoolean(7));
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

    private static NoteStored ReadStored(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetGuid(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            !reader.IsDBNull(7));

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
