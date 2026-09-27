using Npgsql;

namespace MdBolsa.Server.Notes;

public static class AttachmentSchema
{
    // Schema 3: same as 2, plus attachments. Kept as one table rather than a
    // separate migration chain because there is exactly one database and one
    // version, and a migration framework would be the larger of the two things.
    public const string Version = "3";

    // The cap is a design decision, not a technical limit: 32 MB covers a screenshot,
    // a scan and a PDF of a document, and is far below "this is a video". See
    // docs/decisions/0011-attachments.md.
    public const long MaxAttachmentBytes = 32L * 1024 * 1024;

    public static async Task InitialiseAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var ddl = connection.CreateCommand())
        {
            // As in NoteSchema: a parameterless batch, because a multi-statement
            // command cannot carry parameters.
            ddl.CommandText = """
                CREATE TABLE IF NOT EXISTS notes (
                    id            uuid        PRIMARY KEY,
                    relative_path text        NOT NULL,
                    title         text        NOT NULL,
                    content       text        NOT NULL,
                    content_hash  text        NOT NULL,
                    revision      integer     NOT NULL,
                    device_id     uuid        NOT NULL,
                    created_at    timestamptz NOT NULL,
                    updated_at    timestamptz NOT NULL,
                    deleted_at    timestamptz NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ix_notes_live_path
                    ON notes(relative_path) WHERE deleted_at IS NULL;

                CREATE INDEX IF NOT EXISTS ix_notes_updated
                    ON notes(updated_at, id);

                CREATE TABLE IF NOT EXISTS note_versions (
                    note_id       uuid        NOT NULL,
                    revision      integer     NOT NULL,
                    relative_path text        NOT NULL,
                    content       text        NOT NULL,
                    content_hash  text        NOT NULL,
                    device_id     uuid        NOT NULL,
                    updated_at    timestamptz NOT NULL,
                    recorded_at   timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY (note_id, revision)
                );

                CREATE INDEX IF NOT EXISTS ix_note_versions_note
                    ON note_versions(note_id, revision DESC);

                -- Phase 11: attachments, identified by the hash of their contents.
                --
                -- There is no path, no revision and no deleted_at, and that is the
                -- whole point rather than an omission. Content-addressed data has one
                -- version: a byte sequence is either here or it is not, so there is
                -- nothing to conflict about and no history to keep. The trade is in
                -- docs/decisions/0011-attachments.md.
                CREATE TABLE IF NOT EXISTS attachments (
                    hash         text        PRIMARY KEY,
                    size         bigint      NOT NULL,
                    content_type text        NOT NULL,
                    device_id    uuid        NOT NULL,
                    seen_at      timestamptz NOT NULL DEFAULT now()
                );

                -- The bytes live in their own table, not in a bytea column on the
                -- row above. "What have I not got yet?" selects five columns and runs
                -- every few minutes; making that drag 32 MB blobs through memory on
                -- every device would be a slow way to learn a file name. Two tables,
                -- one transaction.
                CREATE TABLE IF NOT EXISTS attachment_blobs (
                    hash    text        PRIMARY KEY,
                    content bytea       NOT NULL
                );

                -- "What have I not got yet?" reads on seen_at with the hash as the
                -- tiebreak, so several attachments uploaded in the same instant page
                -- correctly instead of one at a time.
                CREATE INDEX IF NOT EXISTS ix_attachments_seen
                    ON attachments(seen_at, hash);

                CREATE TABLE IF NOT EXISTS schema_version (
                    version    text        NOT NULL PRIMARY KEY,
                    applied_at timestamptz NOT NULL DEFAULT now()
                );
                """;
            await ddl.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var record = connection.CreateCommand();
        record.CommandText = $"""
            INSERT INTO schema_version (version)
            VALUES ('{Version}') ON CONFLICT (version) DO NOTHING;
            """;
        await record.ExecuteNonQueryAsync(cancellationToken);
    }
}
