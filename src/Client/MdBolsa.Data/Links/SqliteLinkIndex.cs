using MdBolsa.Core.Links;
using Microsoft.Data.Sqlite;

namespace MdBolsa.Data.Links;

public sealed class SqliteLinkIndex : ILinkIndex
{
    private readonly string _connectionString;

    public SqliteLinkIndex(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        EnsureSchema();
    }

    public IReadOnlyList<NoteLink> GetAll()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_note_id, target_text, target_note_id FROM links";

        using var reader = command.ExecuteReader();
        var results = new List<NoteLink>();
        while (reader.Read())
        {
            results.Add(new NoteLink(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2))));
        }
        return results;
    }

    public void ReplaceLinksForNote(Guid sourceNoteId, IReadOnlyList<NoteLink> links)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM links WHERE source_note_id = $sourceId";
            delete.Parameters.AddWithValue("$sourceId", sourceNoteId.ToString());
            delete.ExecuteNonQuery();
        }

        foreach (var link in links)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO links (source_note_id, target_text, target_note_id)
                VALUES ($sourceId, $targetText, $targetId)
                """;
            insert.Parameters.AddWithValue("$sourceId", sourceNoteId.ToString());
            insert.Parameters.AddWithValue("$targetText", link.TargetText);
            insert.Parameters.AddWithValue("$targetId", (object?)link.TargetNoteId?.ToString() ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<Guid> GetBacklinkSourceIds(Guid targetNoteId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT source_note_id FROM links WHERE target_note_id = $targetId";
        command.Parameters.AddWithValue("$targetId", targetNoteId.ToString());

        using var reader = command.ExecuteReader();
        var results = new List<Guid>();
        while (reader.Read()) results.Add(Guid.Parse(reader.GetString(0)));
        return results;
    }

    public int DeleteLinksForNotesNotIn(IReadOnlyCollection<Guid> noteIdsStillPresent)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        if (noteIdsStillPresent.Count == 0)
        {
            command.CommandText = "DELETE FROM links";
            return command.ExecuteNonQuery();
        }

        var parameterNames = noteIdsStillPresent.Select((_, i) => $"$id{i}").ToArray();
        command.CommandText =
            $"DELETE FROM links WHERE source_note_id NOT IN ({string.Join(",", parameterNames)})";
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
            CREATE TABLE IF NOT EXISTS links (
                source_note_id TEXT NOT NULL,
                target_text TEXT NOT NULL,
                target_note_id TEXT NULL,
                PRIMARY KEY (source_note_id, target_text)
            );
            CREATE INDEX IF NOT EXISTS ix_links_target ON links(target_note_id);
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
