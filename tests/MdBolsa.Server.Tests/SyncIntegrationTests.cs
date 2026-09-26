using System.Net.Http.Json;
using System.Text.Json;
using MdBolsa.Contracts;
using MdBolsa.Core.Sync;
using MdBolsa.Core.Vault;
using MdBolsa.Data.Sync;
using MdBolsa.Data.Vault;

namespace MdBolsa.Server.Tests;

// End-to-end sync against a *running* server and a real database, exercising
// the whole Phase 9 path: index a vault, push it, take a change made by another
// device, and hit a genuine two-sided conflict.
//
// This needs the dev stack up (`docker compose up -d` and
// `dotnet run --project src/Server/MdBolsa.Server`). When it isn't, the tests
// return early rather than failing: a missing local database is not a bug in the
// sync code, and a test suite that demands Docker is a test suite nobody runs.
// The rest of the sync behaviour is covered without a server in
// MdBolsa.Core.Tests.Sync.NoteSyncClientTests.
[Collection(IntegrationCollection.Name)]
public class SyncIntegrationTests : IDisposable
{
    private const string BaseAddress = "http://localhost:5080/";
    private const string Token = "dev-token-not-a-secret"; // matches appsettings.Development.json

    private readonly string _vaultRoot = Directory.CreateTempSubdirectory("mdbolsa-sync-e2e-").FullName;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mdbolsa-sync-e2e-{Guid.NewGuid()}.db");
    private readonly Guid _deviceId = Guid.NewGuid();
    private readonly Guid _otherDeviceId = Guid.NewGuid();

    private readonly List<Guid> _createdNoteIds = new();

    public void Dispose()
    {
        Directory.Delete(_vaultRoot, recursive: true);
        if (File.Exists(_dbPath)) File.Delete(_dbPath);

        // These tests share the development database with each other and with
        // whatever else is in it. Leaving rows behind would make a later run pull
        // a previous run's notes into its vault, which is confusing rather than
        // wrong - so clean up after ourselves.
        if (!ServerIsUp() || _createdNoteIds.Count == 0) return;

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

            foreach (var id in _createdNoteIds)
            {
                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM note_versions WHERE note_id = @id; DELETE FROM notes WHERE id = @id;";
                delete.Parameters.AddWithValue("id", id);
                delete.ExecuteNonQuery();
            }

            // Notes this test *pulled* from the shared database and then re-pushed
            // got ids minted by the local scanner, so they aren't in the list above.
            // Deleting by our own path prefix catches those too.
            using (var byPrefix = connection.CreateCommand())
            {
                byPrefix.CommandText =
                    "DELETE FROM note_versions WHERE note_id IN (SELECT id FROM notes WHERE relative_path LIKE @prefix);" +
                    "DELETE FROM notes WHERE relative_path LIKE @prefix;";
                byPrefix.Parameters.AddWithValue("prefix", _deviceId.ToString()[..8] + "/%");
                byPrefix.ExecuteNonQuery();
            }
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or System.Net.Sockets.SocketException)
        {
            // Cleanup is best-effort: failing a test because the tidy-up couldn't
            // run would be the tail wagging the dog.
        }
    }

    // Probed once per test run: with a 2-second timeout, asking per test (and
    // again in Dispose) would add half a minute of waiting to a suite that is
    // supposed to be fast.
    private static readonly bool ServerAvailable = ProbeServer();

    private static bool ServerIsUp() => ServerAvailable;

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

    private NoteSyncClient Client(ISyncStateStore state, IVaultIndex vaultIndex) =>
        new(new HttpClient { BaseAddress = new Uri(BaseAddress) }, _deviceId, Token, state, vaultIndex);

    private SqliteVaultIndex OpenVaultIndex() => new(_dbPath);

    private void Scan() => new VaultScanner(_vaultRoot, OpenVaultIndex()).Scan();

    // A folder unique to this test, so tests running against the same dev server
    // never claim the same vault path (the server allows one note per path).
    // Forward slashes, because that is what VaultScanner puts in the index and
    // therefore what the client sends to the server.
    private string Folder(string fileName) => $"{_deviceId.ToString()[..8]}/{fileName}";

    // These tests share one development database, so a sync from the epoch also
    // pulls in whatever every other test (and every previous run) left there.
    // Once a test has settled its own note, this throws those foreign notes back
    // out and forgets them, so the scenario under test only ever sees its own
    // note. Without it, "this note conflicts" turns into "five notes conflict",
    // which is true and useless.
    private void IsolateToOurNotes()
    {
        var prefix = _deviceId.ToString()[..8];
        foreach (var file in Directory.GetFiles(_vaultRoot, "*", SearchOption.AllDirectories))
        {
            if (!file.Contains(prefix, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
        }

        var state = new SqliteSyncStateStore(_dbPath);
        state.ResetCursor();
        state.ClearConflicts();
        Scan();
    }

    private Guid WriteNote(string relativePath, string content)
    {
        var id = Guid.NewGuid();
        _createdNoteIds.Add(id);
        var fullPath = Path.Combine(_vaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, $"---{Environment.NewLine}id: {id}{Environment.NewLine}---{Environment.NewLine}{Environment.NewLine}{content}");
        return id;
    }

    [Fact]
    public async Task A_NewNote_IsPushedToTheServer()
    {
        if (!ServerIsUp()) return;

        var id = WriteNote(Folder("Pushed.md"), "# Pushed");
        Scan();
        var state = new SqliteSyncStateStore(_dbPath);

        var result = await Client(state, OpenVaultIndex()).SyncAsync(_vaultRoot);

        Assert.False(result.Failed, result.Error);
        Assert.Equal(1, result.Pushed);

        using var http = new HttpClient { BaseAddress = new Uri(BaseAddress) };
        http.DefaultRequestHeaders.Add(SyncHeaders.Token, Token);
        var stored = await http.GetFromJsonAsync<NoteStored>($"api/notes/{id}");
        Assert.Equal(Folder("Pushed.md"), stored!.RelativePath);
    }

    [Fact]
    public async Task AChangeFromAnotherDevice_IsPulledIntoTheVault()
    {
        if (!ServerIsUp()) return;

        var id = WriteNote(Folder("Shared.md"), "# Original");
        Scan();
        var state = new SqliteSyncStateStore(_dbPath);
        var client = Client(state, OpenVaultIndex());
        await client.SyncAsync(_vaultRoot);

        // Another device edits the same note, later.
        using var http = new HttpClient { BaseAddress = new Uri(BaseAddress) };
        http.DefaultRequestHeaders.Add(SyncHeaders.Token, Token);
        http.DefaultRequestHeaders.Add(SyncHeaders.Device, _otherDeviceId.ToString());
        var body = new NoteUpsert(
            id, Folder("Shared.md"), "Shared", "# Edited on the other device", "hash-other", 2,
            _otherDeviceId, DateTimeOffset.UtcNow.AddMinutes(1));
        var put = await http.PutAsJsonAsync($"api/notes/{id}", body);
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());

        // A fresh client (new cursor, nothing cached) pulls it in.
        var pulled = await Client(new SqliteSyncStateStore(_dbPath), OpenVaultIndex()).SyncAsync(_vaultRoot);

        Assert.Equal(1, pulled.Pulled);
        Assert.Contains("Edited on the other device", File.ReadAllText(Path.Combine(_vaultRoot, Folder("Shared.md"))));
    }

    [Fact]
    public async Task ATombstoneFromAnotherDevice_DeletesTheLocalFile()
    {
        if (!ServerIsUp()) return;

        var id = WriteNote(Folder("Doomed.md"), "# Doomed");
        Scan();
        await Client(new SqliteSyncStateStore(_dbPath), OpenVaultIndex()).SyncAsync(_vaultRoot);

        using var http = new HttpClient { BaseAddress = new Uri(BaseAddress) };
        http.DefaultRequestHeaders.Add(SyncHeaders.Token, Token);
        http.DefaultRequestHeaders.Add(SyncHeaders.Device, _otherDeviceId.ToString());
        var deleted = await http.DeleteAsync($"api/notes/{id}");
        Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());

        var result = await Client(new SqliteSyncStateStore(_dbPath), OpenVaultIndex()).SyncAsync(_vaultRoot);

        Assert.Equal(1, result.Deleted);
        Assert.False(File.Exists(Path.Combine(_vaultRoot, Folder("Doomed.md"))));
    }

    [Fact]
    public async Task AConflict_IsReportedAndNeitherVersionIsOverwritten()
    {
        if (!ServerIsUp()) return;

        var id = WriteNote(Folder("Contested.md"), "# Original");
        Scan();
        var state = new SqliteSyncStateStore(_dbPath);
        await Client(state, OpenVaultIndex()).SyncAsync(_vaultRoot);
        IsolateToOurNotes();

        // Changed here...
        var localPath = Path.Combine(_vaultRoot, Folder("Contested.md"));
        File.WriteAllText(localPath, "---\nid: " + id + "\n---\n\n# My local edit");
        Scan();

        // ...and changed there, later.
        using var http = new HttpClient { BaseAddress = new Uri(BaseAddress) };
        http.DefaultRequestHeaders.Add(SyncHeaders.Token, Token);
        http.DefaultRequestHeaders.Add(SyncHeaders.Device, _otherDeviceId.ToString());
        var body = new NoteUpsert(
            id, Folder("Contested.md"), "Contested", "# Their edit", "hash-theirs", 9,
            _otherDeviceId, DateTimeOffset.UtcNow.AddSeconds(30));
        await http.PutAsJsonAsync($"api/notes/{id}", body);

        // Reset the cursor so this pull starts from the epoch. The shared dev
        // database can hold writes with timestamps ahead of ours (other tests use
        // future-dated writes), and a cursor left over from the first sync could
        // otherwise sit past our own note and hide the change we're testing.
        var fresh = new SqliteSyncStateStore(_dbPath);
        fresh.ResetCursor();

        var result = await Client(fresh, OpenVaultIndex()).SyncAsync(_vaultRoot);

        Assert.Equal(1, result.Conflicts);
        // The local edit survives untouched, and the conflict is on record for
        // Phase 10 to resolve deliberately.
        Assert.Contains("My local edit", File.ReadAllText(localPath));
        Assert.Contains(new SqliteSyncStateStore(_dbPath).GetConflicts(), c => c.NoteId == id);
    }

    [Fact]
    public async Task AWrongToken_FailsWithoutTouchingTheVault()
    {
        if (!ServerIsUp()) return;

        WriteNote(Folder("Local.md"), "# Local");
        Scan();
        var localPath = Path.Combine(_vaultRoot, Folder("Local.md"));
        var before = File.ReadAllText(localPath);

        var client = new NoteSyncClient(
            new HttpClient { BaseAddress = new Uri(BaseAddress) },
            _deviceId,
            "not-the-token",
            new SqliteSyncStateStore(_dbPath),
            OpenVaultIndex());

        var result = await client.SyncAsync(_vaultRoot);

        Assert.True(result.Failed);
        Assert.Contains("token", result.Error);
        Assert.Equal(before, File.ReadAllText(localPath));
    }

    [Fact]
    public async Task SyncingTwice_SendsNothingTheSecondTime()
    {
        if (!ServerIsUp()) return;

        WriteNote(Folder("Stable.md"), "# Stable");
        Scan();
        var state = new SqliteSyncStateStore(_dbPath);

        var first = await Client(state, OpenVaultIndex()).SyncAsync(_vaultRoot);
        var second = await Client(state, OpenVaultIndex()).SyncAsync(_vaultRoot);

        Assert.Equal(1, first.Pushed);
        Assert.Equal(0, second.Pushed); // the hash matches what the server confirmed
    }
}
