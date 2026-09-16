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
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO notes (id, path, title, content_hash, revision, created_at, updated_at)
            VALUES ($id, $path, $title, $hash, $revision, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                path = excluded.path,
                title = excluded.title,
                content_hash = excluded.content_hash,
                revision = excluded.revision,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$id", note.Id.ToString());
        command.Parameters.AddWithValue("$path", note.RelativePath);
        command.Parameters.AddWithValue("$title", note.Title);
        command.Parameters.AddWithValue("$hash", note.ContentHash);
        command.Parameters.AddWithValue("$revision", note.Revision);
        command.Parameters.AddWithValue("$created", note.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", note.UpdatedAt.ToString("O"));
        command.ExecuteNonQuery();
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
