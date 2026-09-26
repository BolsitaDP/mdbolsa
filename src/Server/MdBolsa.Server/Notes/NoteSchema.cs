using Npgsql;

namespace MdBolsa.Server.Notes;

// The one table that matters, created idempotently at startup. Same
// "CREATE TABLE IF NOT EXISTS on boot, no migration framework" approach the
// client's SQLite indexes use - a migration framework only earns its place once
// a phase needs to *alter* an existing table, and Phase 10 (conflicts) is the
// first thing that will.
//
// The column set is the sync contract from vision.md §10, and it is deliberately
// shaped so the Phase 9/10 questions stay answerable:
//   - `updated_at` + `id` is the cursor for "everything since X" (watermark
//     reads, so a note changed twice between polls is returned once).
//   - `revision` is per-note and monotonic, so two devices editing the same note
//     can be *detected* (Phase 10) even though resolution isn't designed yet.
//   - `content_hash` lets a client skip re-downloading content it already has.
//   - `deleted_at` is a tombstone rather than a hard delete: a delete that
//     vanished from the table would be indistinguishable from "never synced",
//     and a client that was offline would never learn about the deletion.
// `device_id` records who wrote last, which conflict resolution and the audit
// trail both need.
public static class NoteSchema
{
    public const string Version = "1";

    public static async Task InitialiseAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // The DDL goes as one parameterless batch; the version insert is a separate
        // command. They can't share one: a multi-statement command cannot carry
        // parameters (PostgreSQL rejects the $1 with "syntax error at or near $"),
        // and Npgsql has to pick one protocol for the whole command.
        await using (var ddl = connection.CreateCommand())
        {
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

                -- One note per path among *live* notes. Tombstones keep their path
                -- out of the unique index, so re-creating a note you deleted and
                -- re-created doesn't collide with the tombstone.
                CREATE UNIQUE INDEX IF NOT EXISTS ix_notes_live_path
                    ON notes(relative_path) WHERE deleted_at IS NULL;

                -- The "everything changed since X" index. Watermark reads on
                -- (updated_at, id) so the cursor is stable when several notes share
                -- a timestamp.
                CREATE INDEX IF NOT EXISTS ix_notes_updated
                    ON notes(updated_at, id);

                CREATE TABLE IF NOT EXISTS schema_version (
                    version    text        NOT NULL PRIMARY KEY,
                    applied_at timestamptz NOT NULL DEFAULT now()
                );
                """;
            await ddl.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var record = connection.CreateCommand())
        {
            // The version is inlined rather than parameterised: it's a compile-time
            // constant from this file, so there is nothing to inject here, and it
            // keeps the DDL and the insert each a single simple statement.
            record.CommandText = $"""
                INSERT INTO schema_version (version)
                VALUES ('{Version}') ON CONFLICT (version) DO NOTHING;
                """;
            await record.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
