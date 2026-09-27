using System.Security.Cryptography;
using Npgsql;

namespace MdBolsa.Server.Auth;

/// <summary>
/// One device that holds its own token. Never holds the token itself: the server
/// stores a hash and cannot show the secret again, which is the whole point.
/// </summary>
public sealed record DeviceToken(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? RevokedAt)
{
    public bool IsRevoked => RevokedAt is not null;
}

/// <summary>
/// Per-device tokens, replacing the "one shared secret for everybody" arrangement
/// that Phase 9 chose on purpose and that Phase 11 made untenable.
///
/// The argument for it, in the ADR's own words: with a shared token, compromising
/// any one device compromises every device, and revoking means rotating a secret
/// that has to reach every device by hand. Downloads were bad enough. Then
/// attachments arrived, and the token could now be used to *upload* - to spend
/// someone's disk, fill their database with data they will then have to store,
/// and put arbitrary bytes where a client will render them. That is the moment a
/// self-hosted vault stops being a home-network tool.
///
/// The design keeps the simple thing that already works. The shared token stays
/// valid, so no existing client is locked out and the "paste one string and it
/// works" setup is unchanged. What is new is that the shared token can *mint* a
/// per-device token, and from then on a device can be revoked on its own - a
/// lost laptop stops being a reason to re-key the house.
///
/// Two rules that matter more than the schema:
///
/// * **Only the shared token can mint or revoke.** A device token cannot issue
///   another one, so a stolen device cannot escalate into permanent access.
///   Every device that exists is one the owner deliberately created or revoked.
/// * **A revoked token stays revoked.** It is not deleted, because "this token
///   used to exist and no longer works" is a question an owner will ask, and a
///   row that vanished leaves nothing to answer with.
/// </summary>
public sealed class DeviceTokenStore(string connectionString)
{
    /// <summary>Schema version, reported by /health like the notes schema's.</summary>
    public const int Version = 1;

    // The prefix does two jobs. It makes a pasted token recognisable as one of ours
    // rather than as some other secret somebody copied in, and it lets the
    // authenticator skip the database entirely for anything that obviously is not
    // a device token - so a wrong token costs a comparison, not a round trip.
    private const string Prefix = "mdb_";

    // Long enough that guessing is hopeless, short enough to paste. 16 random
    // bytes: this is a credential for a server on someone's own network, and
    // making it longer buys nothing a rate limiter wouldn't.
    private const int SecretBytes = 16;

    public static bool LooksLikeDeviceToken(string? value) =>
        value is { Length: > 4 } && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string NewToken() =>
        Prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(SecretBytes)).ToLowerInvariant();

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();

    // Created on boot like every other table here (see NoteSchema): one database,
    // one version, and a server that starts without its tables would fail every
    // request later, far from the cause.
    public static async Task InitialiseAsync(string connectionString)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS device_tokens (
                id           uuid        PRIMARY KEY,
                name         text        NOT NULL,
                token_hash   text        NOT NULL UNIQUE,
                created_at   timestamptz NOT NULL,
                last_seen_at timestamptz,
                revoked_at   timestamptz
            );
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(ddl, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Creates a device token. The plaintext is returned here and never again:
    /// it is not in this row, and nothing can recover it.
    /// </summary>
    public async Task<(DeviceToken Device, string Token)> CreateAsync(
        string name, CancellationToken cancellationToken = default)
    {
        var token = NewToken();
        var now = DateTimeOffset.UtcNow;
        var device = new DeviceToken(Guid.NewGuid(), name, now, null, null);

        const string sql = """
            INSERT INTO device_tokens (id, name, token_hash, created_at)
            VALUES (@id, @name, @hash, @created);
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", device.Id);
        command.Parameters.AddWithValue("name", device.Name);
        command.Parameters.AddWithValue("hash", Hash(token));
        command.Parameters.AddWithValue("created", now);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return (device, token);
    }

    /// <summary>
    /// Resolves a presented token to a device, or null if it is unknown or revoked.
    ///
    /// Last-seen is throttled: a busy client syncs every few minutes but a token
    /// check happens on every single request, and writing on every request would
    /// turn "when was this laptop last here" into the most-written row in the
    /// database. Five minutes is close enough to answer the question the row is
    /// there to answer.
    /// </summary>
    public async Task<DeviceToken?> FindByTokenAsync(
        string token, CancellationToken cancellationToken = default)
    {
        var hash = Hash(token);
        const string sql = """
            UPDATE device_tokens
               SET last_seen_at = @now
             WHERE token_hash = @hash
               AND revoked_at IS NULL
               AND (last_seen_at IS NULL OR last_seen_at < @stale)
            RETURNING id, name, created_at, last_seen_at, revoked_at;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("hash", hash);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("stale", DateTimeOffset.UtcNow.AddMinutes(-5));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var found = Read(reader);
            await reader.DisposeAsync();
            return found;
        }

        // No rows updated: either the token is unknown, or it is known and was seen
        // within the last five minutes. The second case is the common one, so it
        // must be a cheap read rather than a failed write.
        await reader.DisposeAsync();
        return await ReadAsync(hash, cancellationToken);
    }

    private async Task<DeviceToken?> ReadAsync(string hash, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, name, created_at, last_seen_at, revoked_at
              FROM device_tokens
             WHERE token_hash = @hash;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("hash", hash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var device = Read(reader);
        return device.IsRevoked ? null : device;
    }

    /// <summary>Every device, revoked ones included, newest first.</summary>
    public async Task<IReadOnlyList<DeviceToken>> ListAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, name, created_at, last_seen_at, revoked_at
              FROM device_tokens
             ORDER BY created_at DESC;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var devices = new List<DeviceToken>();
        while (await reader.ReadAsync(cancellationToken)) devices.Add(Read(reader));

        return devices;
    }

    /// <summary>
    /// Revokes a device. Returns null if there is no such device; returns the row
    /// if it was revoked now or was already revoked before.
    /// </summary>
    public async Task<DeviceToken?> RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE device_tokens
               SET revoked_at = COALESCE(revoked_at, @now)
             WHERE id = @id
            RETURNING id, name, created_at, last_seen_at, revoked_at;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static DeviceToken Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetFieldValue<DateTimeOffset>(2),
        reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
        reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
}
