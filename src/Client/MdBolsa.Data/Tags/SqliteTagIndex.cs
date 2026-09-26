using MdBolsa.Core.Tags;
using Microsoft.Data.Sqlite;

namespace MdBolsa.Data.Tags;

public sealed class SqliteTagIndex : ITagIndex
{
    private readonly string _connectionString;

    public SqliteTagIndex(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        EnsureSchema();
    }

    public IReadOnlyList<TagCount> GetTagCounts()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tag, COUNT(DISTINCT note_id) FROM note_tags GROUP BY tag ORDER BY tag";

        using var reader = command.ExecuteReader();
        var results = new List<TagCount>();
        while (reader.Read()) results.Add(new TagCount(reader.GetString(0), reader.GetInt32(1)));
        return results;
    }

    public IReadOnlyList<string> GetTagsForNote(Guid noteId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tag FROM note_tags WHERE note_id = $noteId ORDER BY tag";
        command.Parameters.AddWithValue("$noteId", noteId.ToString());

        using var reader = command.ExecuteReader();
        var results = new List<string>();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    public IReadOnlyList<Guid> GetNoteIdsForTag(string tag)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT note_id FROM note_tags WHERE tag = $tag";
        command.Parameters.AddWithValue("$tag", TagParser.Normalize(tag));

        using var reader = command.ExecuteReader();
        var results = new List<Guid>();
        while (reader.Read()) results.Add(Guid.Parse(reader.GetString(0)));
        return results;
    }

    public void ReplaceTagsForNote(Guid noteId, IReadOnlyList<string> tags)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM note_tags WHERE note_id = $noteId";
            delete.Parameters.AddWithValue("$noteId", noteId.ToString());
            delete.ExecuteNonQuery();
        }

        foreach (var tag in tags)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO note_tags (note_id, tag) VALUES ($noteId, $tag)";
            insert.Parameters.AddWithValue("$noteId", noteId.ToString());
            insert.Parameters.AddWithValue("$tag", tag);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public int DeleteTagsForNotesNotIn(IReadOnlyCollection<Guid> noteIdsStillPresent)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        if (noteIdsStillPresent.Count == 0)
        {
            command.CommandText = "DELETE FROM note_tags";
            return command.ExecuteNonQuery();
        }

        var parameterNames = noteIdsStillPresent.Select((_, i) => $"$id{i}").ToArray();
        command.CommandText =
            $"DELETE FROM note_tags WHERE note_id NOT IN ({string.Join(",", parameterNames)})";
        var i = 0;
        foreach (var id in noteIdsStillPresent)
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
            CREATE TABLE IF NOT EXISTS note_tags (
                note_id TEXT NOT NULL,
                tag TEXT NOT NULL,
                PRIMARY KEY (note_id, tag)
            );
            CREATE INDEX IF NOT EXISTS ix_note_tags_tag ON note_tags(tag);
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
