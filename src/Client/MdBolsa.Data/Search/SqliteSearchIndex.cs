using MdBolsa.Core.Search;
using Microsoft.Data.Sqlite;

namespace MdBolsa.Data.Search;

// Backed by SQLite's FTS5 extension (bundled with SQLitePCLRaw's default e_sqlite3
// build - no new NuGet dependency). A hand-rolled inverted index or a library like
// Lucene.NET would duplicate what the database engine we already ship already does
// well; see docs/decisions/0007-search-with-fts5.md.
public sealed class SqliteSearchIndex : ISearchIndex
{
    private readonly string _connectionString;

    public SqliteSearchIndex(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        EnsureSchema();
    }

    public void IndexNote(Guid noteId, string title, string body)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        // FTS5 tables have no notion of a unique key to upsert against - delete then
        // insert, same pattern as the relational tables elsewhere.
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM notes_fts WHERE note_id = $noteId";
            delete.Parameters.AddWithValue("$noteId", noteId.ToString());
            delete.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO notes_fts (note_id, title, body) VALUES ($noteId, $title, $body)";
            insert.Parameters.AddWithValue("$noteId", noteId.ToString());
            insert.Parameters.AddWithValue("$title", title);
            insert.Parameters.AddWithValue("$body", body);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public int DeleteNotesNotIn(IReadOnlyCollection<Guid> noteIdsStillPresent)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        if (noteIdsStillPresent.Count == 0)
        {
            command.CommandText = "DELETE FROM notes_fts";
            return command.ExecuteNonQuery();
        }

        var parameterNames = noteIdsStillPresent.Select((_, i) => $"$id{i}").ToArray();
        command.CommandText = $"DELETE FROM notes_fts WHERE note_id NOT IN ({string.Join(",", parameterNames)})";
        var i = 0;
        foreach (var id in noteIdsStillPresent)
        {
            command.Parameters.AddWithValue(parameterNames[i], id.ToString());
            i++;
        }
        return command.ExecuteNonQuery();
    }

    public IReadOnlyList<SearchResult> Search(string query, int limit = 50)
    {
        var ftsQuery = BuildFtsQuery(query);
        if (ftsQuery.Length == 0) return [];

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT note_id, snippet(notes_fts, 2, '', '', '...', 12)
            FROM notes_fts
            WHERE notes_fts MATCH $query
            ORDER BY bm25(notes_fts)
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$query", ftsQuery);
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        var results = new List<SearchResult>();
        while (reader.Read())
        {
            results.Add(new SearchResult(Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        }
        return results;
    }

    // Never exposes raw FTS5 query syntax to the user (AND/OR/NOT, column filters,
    // stray quotes/hyphens can otherwise throw a syntax error from a plain-looking
    // search box). Each whitespace-separated term becomes a quoted phrase; SQLite
    // ANDs space-separated phrases together implicitly.
    private static string BuildFtsQuery(string userQuery)
    {
        var terms = userQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\""));
    }

    private void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS notes_fts USING fts5(
                note_id UNINDEXED,
                title,
                body,
                tokenize = 'unicode61'
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
