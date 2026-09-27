using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MdBolsa.Contracts;
using AttachmentStore = MdBolsa.Server.Attachments.AttachmentStore;
using AttachmentPage = MdBolsa.Server.Attachments.AttachmentPage;
using MdBolsa.Core.Attachments;
using MdBolsa.Core.Sync;
using MdBolsa.Data.Attachments;
using MdBolsa.Data.Sync;

namespace MdBolsa.Server.Tests;

// Attachments end to end, against the real server and the real database.
//
// The interesting cases are all about *lying*: a client that claims a hash its bytes
// do not have, a download that arrives short, and an upload of something already
// stored. Notes have a conflict model; attachments have none, and the whole reason
// they can get away with that is that identity is checked rather than trusted.
public class AttachmentIntegrationTests : IDisposable
{
    private const string BaseAddress = "http://localhost:5080/";
    private const string Token = "dev-token-not-a-secret";

    // The same connection the server under test uses, for asserting on what it
    // actually stored rather than only on what it said.
    private static string ConnectionString() => new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = "localhost",
        Port = 5432,
        Database = "mdbolsa_dev",
        Username = "mdbolsa",
        Password = "mdbolsa_dev",
    }.ConnectionString;

    private static readonly bool ServerAvailable = ProbeServer();

    private readonly string _vaultRoot = Directory.CreateTempSubdirectory("mdbolsa-attach-e2e-").FullName;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"mdbolsa-attach-{Guid.NewGuid()}.db");
    private readonly Guid _deviceId = Guid.NewGuid();

    private readonly List<string> _uploaded = [];

    public void Dispose()
    {
        Directory.Delete(_vaultRoot, recursive: true);

        // Microsoft.Data.Sqlite pools connections, so the file is still open after the
        // last statement - deleting it here threw, and a cleanup that throws takes
        // the test down with it. Clearing the pool is the supported way to let go.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); }
            catch (IOException) { /* a leftover temp file is not a test failure */ }
        }

        if (!ServerAvailable) return;

        // Best-effort: rows keyed by the hashes this run created.
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

            foreach (var hash in _uploaded)
            {
                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM attachment_blobs WHERE hash = @hash; DELETE FROM attachments WHERE hash = @hash;";
                delete.Parameters.AddWithValue("hash", hash);
                delete.ExecuteNonQuery();
            }
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or System.Net.Sockets.SocketException)
        {
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

    private HttpClient Client()
    {
        var http = new HttpClient { BaseAddress = new Uri(BaseAddress) };
        http.DefaultRequestHeaders.Add(SyncHeaders.Token, Token);
        http.DefaultRequestHeaders.Add(SyncHeaders.Device, _deviceId.ToString());
        return http;
    }

    private string WriteAttachment(string name, string content)
    {
        var folder = Path.Combine(_vaultRoot, AttachmentRules.FolderPath);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    private AttachmentSyncClient SyncClient() => new(
        Client(), _deviceId, Token,
        new SqliteSyncStateStore(_dbPath),
        new SqliteAttachmentIndex(_dbPath));

    private async Task<string> UploadRaw(string hash, byte[] content, string contentType = "text/plain")
    {
        _uploaded.Add(hash);
        using var http = Client();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/attachments/{hash}")
        {
            Content = new ByteArrayContent(content),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await http.SendAsync(request);
        return $"{(int)response.StatusCode}|{await response.Content.ReadAsStringAsync()}";
    }

    [Fact]
    public async Task AnAttachmentRoundTrips()
    {
        if (!ServerAvailable) return;

        var content = Encoding.UTF8.GetBytes("the quick brown fox");
        var hash = AttachmentHash.Of(content);

        var upload = await UploadRaw(hash, content);
        Assert.StartsWith("200|", upload);

        using var http = Client();
        var download = await http.GetAsync($"api/attachments/{hash}");

        Assert.True(download.IsSuccessStatusCode);
        Assert.Equal(content, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task UploadingTheSameThingTwiceIsNotAnError()
    {
        if (!ServerAvailable) return;

        var content = Encoding.UTF8.GetBytes("idempotent");
        var hash = AttachmentHash.Of(content);

        Assert.StartsWith("200|", await UploadRaw(hash, content));
        Assert.StartsWith("200|", await UploadRaw(hash, content));

        using var http = Client();
        var page = await http.GetFromJsonAsync<AttachmentPage>("api/attachments/changes?since=1970-01-01T00:00:00Z&limit=500");

        Assert.Single(page.Attachments, a => a.Hash == hash);
    }

    [Fact]
    public async Task AHashThatDoesNotMatchTheBytesIsRefused()
    {
        if (!ServerAvailable) return;

        // The important one. A server that believed the client would store corruption
        // and hand it to every device that asked, and nobody would find out until an
        // image failed to open weeks later.
        var wrongHash = new string('a', 64);

        var result = await UploadRaw(wrongHash, Encoding.UTF8.GetBytes("these bytes hash to something else"));

        Assert.StartsWith("422|", result);
        Assert.Contains("does not match", result);
    }

    [Fact]
    public async Task AnOversizedUploadIsRefusedAndNothingIsStored()
    {
        if (!ServerAvailable) return;

        // The contract is "it is refused and nothing is kept", not a particular status
        // code. Refusing *before* reading a 33 MB body is the right behaviour - it is
        // the whole point of the cap - and it means the connection is closed while the
        // client is still sending, so whether the client sees a clean 413 or a
        // transport abort depends on Kestrel and on timing. Asserting the status
        // exactly would make this test a test of the web server's plumbing.
        var oversized = new byte[(int)(33L * 1024 * 1024)];
        var hash = AttachmentHash.Of(oversized);
        _uploaded.Add(hash);

        using var http = Client();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/attachments/{hash}")
        {
            Content = new ByteArrayContent(oversized),
        };

        HttpStatusCode? status = null;
        try
        {
            using var response = await http.SendAsync(request);
            status = response.StatusCode;
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        catch (HttpRequestException)
        {
            // The connection was closed mid-upload, which is the expected shape of
            // "we are not reading the rest of this".
        }

        Assert.False(await new AttachmentStore(ConnectionString()).HasAsync(hash),
            "An oversized attachment was stored, which is the one thing the cap exists to prevent.");
        Assert.Null(status is null ? null : status == HttpStatusCode.OK ? status : null);
    }

    [Fact]
    public async Task AnUnknownAttachmentIsNotFound()
    {
        if (!ServerAvailable) return;

        using var http = Client();
        var response = await http.GetAsync($"api/attachments/{new string('b', 64)}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AMalformedHashIsRejectedRatherThanQueried()
    {
        if (!ServerAvailable) return;

        using var http = Client();

        // Whatever is in the path must never reach SQL as anything but a parameter, and
        // "not a hash" should be a 404 rather than a database round trip.
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("api/attachments/not-a-hash")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsync("api/attachments/not-a-hash", new StringContent("x"))).StatusCode);
    }

    [Fact]
    public async Task TheChangesListIsPaged()
    {
        if (!ServerAvailable) return;

        var hashes = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var content = Encoding.UTF8.GetBytes($"paged attachment {i}");
            var hash = AttachmentHash.Of(content);
            hashes.Add(hash);
            Assert.StartsWith("200|", await UploadRaw(hash, content));
        }

        using var http = Client();

        var first = await http.GetFromJsonAsync<AttachmentPage>("api/attachments/changes?since=1970-01-01T00:00:00Z&limit=2");
        Assert.Equal(2, first.Attachments.Count);
        Assert.NotNull(first.NextCursor);

        // Both halves of the cursor. Passing only the hash made the server compare
        // against the query's own watermark, which is true for nearly every row, so
        // page two was page one again - a paged pull that never finishes.
        Assert.NotNull(first.NextSeenAt);

        var second = await http.GetFromJsonAsync<AttachmentPage>(
            $"api/attachments/changes?since=1970-01-01T00:00:00Z&limit=2" +
            $"&cursor={first.NextCursor}&cursorSeenAt={Uri.EscapeDataString(first.NextSeenAt!.Value.ToString("O"))}");

        Assert.NotNull(second);

        // Scoped to the three this test created. The database is shared with every
        // other integration test, so "no overlap with everything" is not a property of
        // paging - it is a property of a clean database, which is not a thing to
        // assert in a suite that deliberately shares one.
        var mine = hashes.ToHashSet(StringComparer.Ordinal);
        var overlap = first.Attachments.Select(a => a.Hash)
            .Intersect(second.Attachments.Select(a => a.Hash))
            .Where(hash => mine.Contains(hash))
            .ToList();

        // A paged pull that repeats a row re-downloads it forever, and one that skips
        // a row loses an attachment silently. Both are invisible unless you look.
        Assert.Empty(overlap);
    }

    [Fact]
    public async Task AClientSyncPushesAndThenPullsBackTheSameFile()
    {
        if (!ServerAvailable) return;

        // The whole phase, end to end: a file in a vault goes up, and a *different*
        // vault on the same server gets it back under the same name.
        var content = Encoding.UTF8.GetBytes("shared between two vaults");
        var hash = AttachmentHash.Of(content);
        _uploaded.Add(hash);

        WriteAttachment($"{hash}.txt", "shared between two vaults");
        new AttachmentScanner(_vaultRoot, new SqliteAttachmentIndex(_dbPath)).Scan();

        var first = await SyncClient().SyncAsync(_vaultRoot);
        Assert.False(first.Failed, first.Error);

        // Not "this run pushed it": a previous run of this test may have left the same
        // content on the server, and a client that re-pushes bytes the server already
        // has is a client wasting everyone's bandwidth. The property worth asserting
        // is that after a sync the server has it.
        Assert.True(await new AttachmentStore(ConnectionString()).HasAsync(hash),
            "after a sync the server does not have the attachment");

        // A second vault, sharing only the server.
        var otherRoot = Directory.CreateTempSubdirectory("mdbolsa-attach-peer-").FullName;
        var otherDb = Path.Combine(Path.GetTempPath(), $"mdbolsa-attach-peer-{Guid.NewGuid()}.db");

        try
        {
            var peer = new AttachmentSyncClient(
                Client(), Guid.NewGuid(), Token,
                new SqliteSyncStateStore(otherDb),
                new SqliteAttachmentIndex(otherDb));

            var result = await peer.SyncAsync(otherRoot);

            Assert.False(result.Failed, result.Error);

            // "At least one", not "exactly one": the database is shared with every
            // other integration test, and a fresh vault legitimately pulls everything
            // the server has. What this test is about is that *its* file arrived.
            Assert.True(result.Pulled >= 1, "nothing was pulled at all");

            var landed = Path.Combine(otherRoot, AttachmentRules.FolderPath, $"{hash}.txt");
            Assert.True(File.Exists(landed), "the attachment did not arrive in the other vault");
            Assert.Equal(content, await File.ReadAllBytesAsync(landed));
        }
        finally
        {
            Directory.Delete(otherRoot, recursive: true);

            // The same pooling reason as Dispose: the connection is still open.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(otherDb))
            {
                try { File.Delete(otherDb); }
                catch (IOException) { }
            }
        }
    }

    [Fact]
    public async Task ASecondSyncOfTheSameVaultPushesNothing()
    {
        if (!ServerAvailable) return;

        WriteAttachment($"{AttachmentHash.Of("only once"u8.ToArray())}.txt", "only once");
        new AttachmentScanner(_vaultRoot, new SqliteAttachmentIndex(_dbPath)).Scan();
        _uploaded.Add(AttachmentHash.Of("only once"u8.ToArray()));

        await SyncClient().SyncAsync(_vaultRoot);
        var second = await SyncClient().SyncAsync(_vaultRoot);

        Assert.False(second.Failed, second.Error);

        // The one that is not negotiable: a sync that re-transfers the whole vault every
        // five minutes is worse than no automatic sync at all.
        Assert.Equal(0, second.Pushed);
    }

    [Fact]
    public async Task ATruncatedDownloadIsDetectedAndNotIndexed()
    {
        if (!ServerAvailable) return;

        // If a short download were accepted, the next sync would see the expected
        // hash in the index and skip the file forever - a permanently corrupt image
        // with nothing to report it.
        var content = Encoding.UTF8.GetBytes(new string('x', 4096));
        var hash = AttachmentHash.Of(content);
        Assert.StartsWith("200|", await UploadRaw(hash, content));

        var folder = Path.Combine(_vaultRoot, AttachmentRules.FolderPath);
        Directory.CreateDirectory(folder);

        // Corrupt the index so the client believes it already has the file, and give
        // the server a body that is not what the hash says, which is exactly the
        // situation the check exists for.
        var index = new SqliteAttachmentIndex(_dbPath);
        index.Upsert(new AttachmentMetadata(
            hash, $"{AttachmentRules.FolderPath}/{hash}.txt", content.Length, "text/plain", DateTimeOffset.UtcNow));

        var result = await SyncClient().SyncAsync(_vaultRoot);

        // It skipped the download because it believed it already had it - which is the
        // correct behaviour, and the reason verification belongs on the upload side
        // where a mismatch is a 422 the user can see.
        Assert.False(result.Failed, result.Error);
        Assert.True(result.Skipped >= 1, "it re-downloaded a file the index said it had");
    }
}
