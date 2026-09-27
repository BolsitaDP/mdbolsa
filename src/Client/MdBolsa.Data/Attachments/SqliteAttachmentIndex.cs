using MdBolsa.Core.Attachments;
using Microsoft.Data.Sqlite;

namespace MdBolsa.Data.Attachments;

/// <summary>
/// The local record of what attachments exist. Same SQLite file as every other index,
/// for the same reason: it is a rebuildable cache of the folder, and a cache that
/// lives in its own file is a cache that can be out of step with the vault.
/// </summary>
public sealed class SqliteAttachmentIndex(string databasePath) : IAttachmentIndex
{
    public void Upsert(AttachmentMetadata attachment)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        // The hash is the primary key, so two files with identical content are one
        // row and the second is a no-op - which is the deduplication the naming
        // scheme buys, and it happens here rather than needing to be asked for.
        command.CommandText = """
            INSERT INTO attachments (hash, relative_path, size, content_type, indexed_at)
            VALUES ($hash, $path, $size, $type, $indexedAt)
            ON CONFLICT (hash) DO UPDATE SET
                relative_path = excluded.relative_path,
                size          = excluded.size,
                content_type  = excluded.content_type,
                indexed_at    = excluded.indexed_at;
            """;

        command.Parameters.AddWithValue("$hash", attachment.Hash);
        command.Parameters.AddWithValue("$path", attachment.RelativePath);
        command.Parameters.AddWithValue("$size", attachment.Size);
        command.Parameters.AddWithValue("$type", attachment.ContentType);
        command.Parameters.AddWithValue("$indexedAt", attachment.IndexedAt.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<AttachmentMetadata> GetAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        // Path order, so a push sends attachments in the same sequence every time and
        // a truncated run can be compared against what was sent.
        command.CommandText = """
            SELECT hash, relative_path, size, content_type, indexed_at
              FROM attachments ORDER BY relative_path;
            """;

        using var reader = command.ExecuteReader();
        var results = new List<AttachmentMetadata>();
        while (reader.Read())
        {
            results.Add(new AttachmentMetadata(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }

        return results;
    }

    public AttachmentMetadata? Get(string hash)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT hash, relative_path, size, content_type, indexed_at
              FROM attachments WHERE hash = $hash;
            """;
        command.Parameters.AddWithValue("$hash", hash);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public bool Has(string hash) => Get(hash) is not null;

    public int DeleteMissing(IReadOnlyCollection<string> hashesStillPresent)
    {
        if (hashesStillPresent.Count == 0)
        {
            using var connection = Open();
            using var clear = connection.CreateCommand();
            clear.CommandText = "DELETE FROM attachments;";
            return clear.ExecuteNonQuery();
        }

        // Built as a parameter list rather than an "IN (...)" string: the hashes come
        // from the filesystem, and the one rule in this codebase is that a value never
        // becomes SQL text.
        var names = hashesStillPresent.Select((_, index) => $"$h{index}").ToArray();

        using var deleteConnection = Open();
        using var command = deleteConnection.CreateCommand();
        command.CommandText =
            $"DELETE FROM attachments WHERE hash NOT IN ({string.Join(", ", names)});";

        var index = 0;
        foreach (var hash in hashesStillPresent)
        {
            command.Parameters.AddWithValue(names[index++], hash);
        }

        return command.ExecuteNonQuery();
    }

    private static AttachmentMetadata Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt64(2),
        reader.GetString(3),
        DateTimeOffset.Parse(reader.GetString(4)));

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        connection.Open();
        EnsureSchema(connection);
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS attachments (
                hash         text PRIMARY KEY,
                relative_path text NOT NULL,
                size         integer NOT NULL,
                content_type text NOT NULL,
                indexed_at   text NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_attachments_path
                ON attachments(relative_path);
            """;
        command.ExecuteNonQuery();
    }
}
