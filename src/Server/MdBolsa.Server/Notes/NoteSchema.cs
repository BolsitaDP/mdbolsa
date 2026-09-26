using Npgsql;

namespace MdBolsa.Server.Notes;

// Phase 10 adds the history table. See 0011 for the shape of everything else.
//
// Two decisions worth stating:
//
//  * **Every accepted write is kept, nothing is pruned.** A personal vault's edit
//    history is small, and "keep the last 10" is a policy that quietly destroys
//    the thing someone came looking for. If that ever needs to change, it changes
//    here, once.
//  * **The previous row is copied before the current one is overwritten**, in the
//    same transaction as the write. Doing it the other way round would leave a
//    window where an accepted write had already destroyed the old content and the
//    history insert hadn't landed - i.e. exactly the data loss the history exists
//    to prevent.
//
// The revision number in the history row is the revision the content *had*, so
// the newest row for a note is the current one.
public static class NoteSchema
{
    public const string Version = "2";

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

                -- Phase 10: the edit history. One row per accepted revision.
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

                -- "Show me this note's history, newest first" is the only query that
                -- matters, and it is asked per note.
                CREATE INDEX IF NOT EXISTS ix_note_versions_note
                    ON note_versions(note_id, revision DESC);

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
