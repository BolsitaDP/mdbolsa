using System.Net;
using System.Net.Http.Json;
using MdBolsa.Contracts;
using MdBolsa.Core.Sync;
using MdBolsa.Core.Vault;
using MdBolsa.Data.Sync;
using MdBolsa.Data.Vault;

namespace MdBolsa.Server.Tests;

// The edit history, end to end: write a note twice and the first version is
// still readable afterwards. That is the whole promise of Phase 10 - "keep mine"
// is only safe if the version it replaces survives.
public class VersionHistoryTests : IDisposable
{
    private const string BaseAddress = "http://localhost:5080/";
    private const string Token = "dev-token-not-a-secret";

    private static readonly bool ServerAvailable = ProbeServer();

    private readonly string _vaultRoot = Directory.CreateTempSubdirectory("mdbolsa-history-").FullName;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mdbolsa-history-{Guid.NewGuid()}.db");
    private readonly Guid _deviceId = Guid.NewGuid();
    private readonly Guid _otherDeviceId = Guid.NewGuid();
    private Guid? _noteId;

    public void Dispose()
    {
        Directory.Delete(_vaultRoot, recursive: true);
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (!ServerAvailable || _noteId is null) return;

        try
        {
            using var connection = new Npgsql.NpgsqlConnection(new Npgsql.NpgsqlConnectionStringBuilder
            {
                Host = "localhost",
                Port = 5432,
                Database = "mdbolsa_dev",
                Username = "mdbolsa",
                Password = "mdbolsa_dev",
            }.ConnectionString);
            connection.Open();

            // The note itself *and* its history: a note left behind would collide
            // with the next run on the unique path index.
            using (var byId = connection.CreateCommand())
            {
                byId.CommandText = "DELETE FROM note_versions WHERE note_id = @id; DELETE FROM notes WHERE id = @id;";
                byId.Parameters.AddWithValue("id", _noteId.Value);
                byId.ExecuteNonQuery();
            }

            // And by path, for the case where a sync test pulled this note, the
            // local scanner minted it a new id, and it was pushed back before this
            // test got to clean up.
            using (var byPath = connection.CreateCommand())
            {
                byPath.CommandText =
                    "DELETE FROM note_versions WHERE note_id IN (SELECT id FROM notes WHERE relative_path LIKE @path);" +
                    "DELETE FROM notes WHERE relative_path LIKE @path;";
                byPath.Parameters.AddWithValue("path", "%/History.md");
                byPath.ExecuteNonQuery();
            }
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or System.Net.Sockets.SocketException)
        {
            // Best-effort cleanup.
        }
    }

    private static bool ProbeServer()
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return probe.GetAsync($"{BaseAddress}health").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    // Unique per test run, because the server allows one note per path.
    private string Folder => $"{_deviceId.ToString()[..8]}/History.md";

    private HttpClient Client()
    {
        var http = new HttpClient { BaseAddress = new Uri(BaseAddress) };
        http.DefaultRequestHeaders.Add(SyncHeaders.Token, Token);
        http.DefaultRequestHeaders.Add(SyncHeaders.Device, _deviceId.ToString());
        return http;
    }

    private async Task<HttpStatusCode> PutAsync(Guid deviceId, int revision, string content, DateTimeOffset updatedAt)
    {
        using var http = Client();
        http.DefaultRequestHeaders.Remove(SyncHeaders.Device);
        http.DefaultRequestHeaders.Add(SyncHeaders.Device, deviceId.ToString());

        // A real client always sends content whose frontmatter carries the note id -
        // VaultScanner guarantees it on this side, and without it the next local
        // scan would mint a new id for the same path.
        var body = new NoteUpsert(
            _noteId!.Value, Folder, "History", content, $"hash-{revision}", revision, deviceId, updatedAt);
        var response = await http.PutAsJsonAsync($"api/notes/{_noteId.Value}", body);
        return response.StatusCode;
    }

    [Fact]
    public async Task TheFirstVersionOfANoteIsStillReadableAfterASecondWrite()
    {
        if (!ServerAvailable) return;

        _noteId = Guid.NewGuid();
        var first = UtcNow();

        await PutAsync(_deviceId, 1, "# First", first);
        await PutAsync(_otherDeviceId, 2, "# Second", first.AddMinutes(1));

        var resolver = new ConflictResolver(
            Client(), _deviceId, Token, new SqliteSyncStateStore(_dbPath), new SqliteVaultIndex(_dbPath));

        var history = await resolver.GetHistoryAsync(_noteId.Value);

        Assert.Equal(2, history.Count);
        Assert.Equal(2, history[0].Revision);
        Assert.True(history[0].IsCurrent);
        Assert.Equal("# Second", history[0].Content);
        Assert.Equal(1, history[1].Revision);
        Assert.False(history[1].IsCurrent);
        Assert.Equal("# First", history[1].Content);
    }

    [Fact]
    public async Task ADeletedNoteKeepsItsContentInTheHistory()
    {
        if (!ServerAvailable) return;

        _noteId = Guid.NewGuid();
        var when = UtcNow();
        await PutAsync(_deviceId, 1, "# Doomed but recoverable", when);

        using (var http = Client())
        {
            http.DefaultRequestHeaders.Remove(SyncHeaders.Device);
            http.DefaultRequestHeaders.Add(SyncHeaders.Device, _otherDeviceId.ToString());
            http.DefaultRequestHeaders.Add(SyncHeaders.UpdatedAt, when.AddMinutes(1).ToString("O"));
            var deleted = await http.DeleteAsync($"api/notes/{_noteId.Value}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        var resolver = new ConflictResolver(
            Client(), _deviceId, Token, new SqliteSyncStateStore(_dbPath), new SqliteVaultIndex(_dbPath));

        var history = await resolver.GetHistoryAsync(_noteId.Value);

        // An accidental deletion is still undoable, because the content survived.
        Assert.Contains(history, v => v.Content == "# Doomed but recoverable");
        Assert.DoesNotContain(history, v => v.IsCurrent);
    }

    [Fact]
    public async Task RefusedWrites_LeaveNoHistory()
    {
        if (!ServerAvailable) return;

        _noteId = Guid.NewGuid();
        var when = UtcNow();
        await PutAsync(_deviceId, 1, "# Current", when.AddMinutes(1));

        // Older than what's stored: refused.
        var refused = await PutAsync(_otherDeviceId, 2, "# Stale", when);

        Assert.Equal(HttpStatusCode.Conflict, refused);

        var resolver = new ConflictResolver(
            Client(), _deviceId, Token, new SqliteSyncStateStore(_dbPath), new SqliteVaultIndex(_dbPath));
        var history = await resolver.GetHistoryAsync(_noteId.Value);

        // A write that didn't happen must not appear as a version.
        Assert.DoesNotContain(history, v => v.Content == "# Stale");
    }

    [Fact]
    public async Task AnUnknownNotesHistoryIsEmpty()
    {
        if (!ServerAvailable) return;

        var resolver = new ConflictResolver(
            Client(), _deviceId, Token, new SqliteSyncStateStore(_dbPath), new SqliteVaultIndex(_dbPath));

        var response = await resolver.GetHistoryAsync(Guid.NewGuid());

        Assert.Empty(response);
    }

    private static DateTimeOffset UtcNow() => DateTimeOffset.UtcNow.AddSeconds(-5);
}
