using MdBolsa.Server.Notes;
using Npgsql;

namespace MdBolsa.Server.Attachments;

/// <summary>One stored attachment. The hash is the identity and the primary key.</summary>
public sealed record StoredAttachment(
    string Hash,
    long Size,
    string ContentType,
    Guid DeviceId,
    DateTimeOffset SeenAt);

public sealed record AttachmentPage(
    IReadOnlyList<StoredAttachment> Attachments,
    string? NextCursor,
    DateTimeOffset? NextSeenAt);

/// <summary>
/// The hash in the request did not match the bytes in the body.
///
/// A client bug, or a corrupted transfer. Either way the server must not store it:
/// believing the client's claim would let one bad upload propagate silently to
/// every device, and the damage would only show up as a corrupt image weeks later.
/// </summary>
public sealed class AttachmentHashMismatchException(string expected, string actual)
    : Exception($"The attachment does not match its name. The request said {expected}, the bytes hash to {actual}.")
{
    public string Expected { get; } = expected;

    public string Actual { get; } = actual;
}

public sealed class AttachmentTooLargeException(long size, long limit)
    : Exception($"That attachment is {size / (1024 * 1024)} MB and the limit is {limit / (1024 * 1024)} MB.");

/// <summary>
/// All data access for attachments. Raw Npgsql, parameters only, as in NoteStore.
///
/// The interesting difference from notes is that there is nothing to arbitrate.
/// Every method is idempotent: storing the same hash twice is the same single row,
/// and downloading is a lookup. A device that uploads something the server already
/// has gets the same answer as one that just invented it, which is what makes the
/// two sides converge without either of them deciding anything.
/// </summary>
public sealed class AttachmentStore(string connectionString)
{
    // A hash is a hash: 64 lowercase hex characters. Validated at the endpoint so a
    // caller can never turn a string into a query, whatever it contains.
    public static bool IsValidHash(string? hash) =>
        hash is { Length: 64 } && hash.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// Stores the bytes if the hash is not already known. Returns what is stored,
    /// which for a repeat upload is the row that was already there.
    /// </summary>
    public async Task<StoredAttachment> StoreAsync(
        string hash,
        byte[] content,
        string contentType,
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        if (content.LongLength > AttachmentSchema.MaxAttachmentBytes)
        {
            throw new AttachmentTooLargeException(content.LongLength, AttachmentSchema.MaxAttachmentBytes);
        }

        var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
        if (!string.Equals(actual, hash, StringComparison.Ordinal))
        {
            throw new AttachmentHashMismatchException(hash, actual);
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The bytes and the metadata land together or not at all: a row saying an
        // attachment exists when its content is missing would make every device
        // believe it had already downloaded it.
        await using (var blob = connection.CreateCommand())
        {
            blob.Transaction = transaction;
            blob.CommandText = """
                INSERT INTO attachment_blobs (hash, content)
                VALUES (@hash, @content)
                ON CONFLICT (hash) DO NOTHING;
                """;
            blob.Parameters.AddWithValue("hash", hash);
            blob.Parameters.AddWithValue("content", content);
            await blob.ExecuteNonQueryAsync(cancellationToken);
        }

        // ON CONFLICT DO NOTHING on purpose: an upload of something already stored is
        // not an error and not an update, it is a no-op that returns the existing
        // row. Keeping seen_at from moving is deliberate - the incremental pull
        // walks seen_at, and touching it would re-download bytes every device
        // already has, forever.
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO attachments (hash, size, content_type, device_id)
                VALUES (@hash, @size, @type, @device)
                ON CONFLICT (hash) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("hash", hash);
            insert.Parameters.AddWithValue("size", content.LongLength);
            insert.Parameters.AddWithValue("type", contentType);
            insert.Transaction = transaction;
            insert.Parameters.AddWithValue("device", deviceId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return await GetAsync(hash, cancellationToken)
            ?? throw new InvalidOperationException("The attachment vanished between being written and read.");
    }

    public async Task<StoredAttachment?> GetAsync(string hash, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT hash, size, content_type, device_id, seen_at
              FROM attachments WHERE hash = @hash;
            """;
        command.Parameters.AddWithValue("hash", hash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredAttachment(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.GetFieldValue<DateTimeOffset>(4))
            : null;
    }

    public async Task<byte[]?> ReadAsync(string hash, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        // bytea comes back as a byte[]; GetFieldValue<byte[]> is the documented way to
        // ask for it rather than the provider's default conversion.
        command.CommandText = "SELECT content FROM attachment_blobs WHERE hash = @hash;";
        command.Parameters.AddWithValue("hash", hash);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as byte[];
    }

    /// <summary>Everything first seen at or after a point, oldest first, one page.</summary>
    public async Task<AttachmentPage> GetSeenSinceAsync(
        DateTimeOffset since,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default,
        DateTimeOffset? cursorSeenAt = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT hash, size, content_type, device_id, seen_at
              FROM attachments
             WHERE seen_at >= @since
               AND (NOT @hasCursor OR (seen_at, hash) > (@cursorAt, @cursorHash))
             ORDER BY seen_at, hash
             LIMIT @limit;
            """;
        command.Parameters.AddWithValue("since", since);
        command.Parameters.AddWithValue("hasCursor", cursor is not null);
        // The cursor is (seen_at, hash) of the *last row returned*, not of the query.
        // Passing `since` here instead makes the comparison
        // "(seen_at, hash) > (epoch, lastHash)" true for nearly every row, so every
        // page repeats the previous one and a paged pull never finishes.
        command.Parameters.AddWithValue("cursorAt", cursorSeenAt ?? since);
        command.Parameters.AddWithValue("cursorHash", cursor ?? string.Empty);
        command.Parameters.AddWithValue("limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var attachments = new List<StoredAttachment>();
        while (await reader.ReadAsync(cancellationToken))
        {
            attachments.Add(new StoredAttachment(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }

        // A full page may be followed by more, so the last row is handed back as the
        // resume point - same shape as the notes cursor.
        // A full page may be followed by more, so the last row is handed back as the
        // resume point - and both halves of it, because the comparison is a pair.
        var next = attachments.Count == limit ? attachments[^1].Hash : null;
        DateTimeOffset? nextSeenAt = attachments.Count == limit ? attachments[^1].SeenAt : null;
        return new AttachmentPage(attachments, next, nextSeenAt);
    }

    /// <summary>Names and sizes, newest first. Bytes are never selected here.</summary>
    public async Task<IReadOnlyList<StoredAttachment>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT hash, size, content_type, device_id, seen_at
              FROM attachments ORDER BY seen_at DESC LIMIT 500;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var attachments = new List<StoredAttachment>();
        while (await reader.ReadAsync(cancellationToken))
        {
            attachments.Add(new StoredAttachment(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return attachments;
    }

    public async Task<bool> HasAsync(string hash, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM attachments WHERE hash = @hash;";
        command.Parameters.AddWithValue("hash", hash);

        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
