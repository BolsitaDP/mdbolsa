using MdBolsa.Core.Vault;
using Microsoft.Data.Sqlite;

namespace MdBolsa.Data.Vault;

public sealed class SqliteVaultIndex : IVaultIndex
{
    private readonly string _connectionString;

    public SqliteVaultIndex(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        EnsureSchema();
    }

    public IReadOnlyList<NoteMetadata> GetAll()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, path, title, content_hash, revision, created_at, updated_at FROM notes";

        using var reader = command.ExecuteReader();
        var results = new List<NoteMetadata>();
        while (reader.Read())
        {
            results.Add(new NoteMetadata(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                DateTimeOffset.Parse(reader.GetString(5)),
                DateTimeOffset.Parse(reader.GetString(6))));
        }
        return results;
    }

    public void Upsert(NoteMetadata note)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        // A stale row can be left at this path under a different id (e.g. an older
        // scan indexed this path before the file's id was assigned/changed) - the
        // unique index on path would otherwise reject the insert/update below.
        using (var deleteStale = connection.CreateCommand())
        {
            deleteStale.Transaction = transaction;
            deleteStale.CommandText = "DELETE FROM notes WHERE path = $path AND id <> $id";
            deleteStale.Parameters.AddWithValue("$path", note.RelativePath);
            deleteStale.Parameters.AddWithValue("$id", note.Id.ToString());
            deleteStale.ExecuteNonQuery();
        }

        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO notes (id, path, title, content_hash, revision, created_at, updated_at)
                VALUES ($id, $path, $title, $hash, $revision, $created, $updated)
                ON CONFLICT(id) DO UPDATE SET
                    path = excluded.path,
                    title = excluded.title,
                    content_hash = excluded.content_hash,
                    revision = excluded.revision,
                    updated_at = excluded.updated_at
                """;
            upsert.Parameters.AddWithValue("$id", note.Id.ToString());
            upsert.Parameters.AddWithValue("$path", note.RelativePath);
            upsert.Parameters.AddWithValue("$title", note.Title);
            upsert.Parameters.AddWithValue("$hash", note.ContentHash);
            upsert.Parameters.AddWithValue("$revision", note.Revision);
            upsert.Parameters.AddWithValue("$created", note.CreatedAt.ToString("O"));
            upsert.Parameters.AddWithValue("$updated", note.UpdatedAt.ToString("O"));
            upsert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public int DeleteMissing(IReadOnlyCollection<Guid> idsStillPresent)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        if (idsStillPresent.Count == 0)
        {
            command.CommandText = "DELETE FROM notes";
            return command.ExecuteNonQuery();
        }

        var parameterNames = idsStillPresent.Select((_, i) => $"$id{i}").ToArray();
        command.CommandText = $"DELETE FROM notes WHERE id NOT IN ({string.Join(",", parameterNames)})";
        var i = 0;
        foreach (var id in idsStillPresent)
        {
            command.Parameters.AddWithValue(parameterNames[i], id.ToString());
            i++;
        }
        return command.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS notes (
                id TEXT PRIMARY KEY,
                path TEXT NOT NULL,
                title TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                revision INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_notes_path ON notes(path);
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
