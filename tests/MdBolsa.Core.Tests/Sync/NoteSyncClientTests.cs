using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MdBolsa.Contracts;
using MdBolsa.Core.Sync;
using MdBolsa.Core.Vault;

namespace MdBolsa.Core.Tests.Sync;

// The sync client against a fake server: no network, no database, just the
// request/response contract and the decisions (what to push, what to write, what
// to refuse to touch). This is where the interesting behaviour of Phase 9 lives,
// so it is tested here rather than with curl.
public class NoteSyncClientTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _root = Directory.CreateTempSubdirectory("mdbolsa-sync-tests-").FullName;
    private readonly Guid _deviceId = Guid.NewGuid();
    private readonly FakeSyncStateStore _state = new();
    private readonly FakeVaultIndex _vault = new();
    private readonly FakeHandler _http = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private NoteSyncClient Client() =>
        new(new HttpClient(_http) { BaseAddress = new Uri("http://localhost/") }, _deviceId, "token", _state, _vault);

    [Fact]
    public async Task Sync_UploadsANewNote_AndRecordsItsHash()
    {
        var note = WriteNote("Note.md", "# Hello", id: out var id);
        _vault.Notes.Add(note);
        _http.OnPut(_ => Results.Json(new NoteStored(id, "Note.md", "Note", "hash", 1, _deviceId, DateTimeOffset.UtcNow, false)));

        var result = await Client().SyncAsync(_root);

        Assert.False(result.Failed);
        Assert.Equal(1, result.Pushed);
        Assert.NotNull(_state.GetPushedHash(id));
    }

    [Fact]
    public async Task Sync_SendsTheDeviceAndTokenHeaders()
    {
        var note = WriteNote("Note.md", "# Hello", id: out _);
        _vault.Notes.Add(note);
        _http.OnPut(_ => Results.Json(new NoteStored(Guid.NewGuid(), "n", "t", "h", 1, _deviceId, DateTimeOffset.UtcNow, false)));

        await Client().SyncAsync(_root);

        var request = Assert.Single(_http.Requests.Where(r => r.Method == HttpMethod.Put));
        Assert.Equal("token", request.Headers[SyncHeaders.Token]);
        Assert.Equal(_deviceId.ToString(), request.Headers[SyncHeaders.Device]);
    }

    [Fact]
    public async Task Sync_SkipsANoteTheServerAlreadyHas()
    {
        var note = WriteNote("Note.md", "# Hello", id: out var id);
        _vault.Notes.Add(note);
        _state.SetPushedHash(id, NoteSyncClient.HashOf("# Hello"));

        var result = await Client().SyncAsync(_root);

        Assert.Equal(0, result.Pushed);
        Assert.DoesNotContain(_http.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task Sync_WritesARemoteNoteIntoTheVault()
    {
        _http.OnGet(_ => Results.Json(new NoteChangesPage(
        [
            new NoteChange(Guid.NewGuid(), "FromPhone.md", "FromPhone", "# From phone", "hash-1", 1,
                Guid.NewGuid(), DateTimeOffset.UtcNow, false),
        ], null)));

        var result = await Client().SyncAsync(_root);

        Assert.Equal(1, result.Pulled);
        Assert.Equal("# From phone", File.ReadAllText(Path.Combine(_root, "FromPhone.md")));
    }

    [Fact]
    public async Task Sync_DeletesARemoteTombstone()
    {
        var note = WriteNote("Gone.md", "# Gone", id: out var id);
        _vault.Notes.Add(note);
        _state.SetPushedHash(id, NoteSyncClient.HashOf("# Gone"));
        _http.OnGet(_ => Results.Json(new NoteChangesPage(
        [
            new NoteChange(id, null, null, null, null, null, null, DateTimeOffset.UtcNow, true),
        ], null)));

        var result = await Client().SyncAsync(_root);

        Assert.Equal(1, result.Deleted);
        Assert.False(File.Exists(Path.Combine(_root, "Gone.md")));
    }

    [Fact]
    public async Task Sync_ReportsAConflict_AndLeavesBothVersionsAlone()
    {
        // Changed here *and* over there: the client must write neither.
        var localContent = "# My local edit";
        var note = WriteNote("Contested.md", localContent, id: out var id);
        _vault.Notes.Add(note);
        _state.SetPushedHash(id, NoteSyncClient.HashOf("# original"));
        _http.OnGet(_ => Results.Json(new NoteChangesPage(
        [
            new NoteChange(id, "Contested.md", "Contested", "# Their edit", "hash-theirs", 7,
                Guid.NewGuid(), DateTimeOffset.UtcNow, false),
        ], null)));

        var result = await Client().SyncAsync(_root);

        Assert.Equal(1, result.Conflicts);
        Assert.Equal(localContent, File.ReadAllText(Path.Combine(_root, "Contested.md")));
        Assert.Contains(_state.GetConflicts(), c => c.NoteId == id);
    }

    [Fact]
    public async Task Sync_RecordsAConflict_WhenTheServerRefusesTheWrite()
    {
        var note = WriteNote("Contested.md", "# Mine", id: out var id);
        _vault.Notes.Add(note);
        _http.OnGet(_ => Results.Json(new NoteChangesPage([], null)));
        _http.OnPut(_ => Results.Json(
            new { error = "newer revision", stored = new { revision = 9, deviceId = Guid.NewGuid() } },
            statusCode: HttpStatusCode.Conflict));

        var result = await Client().SyncAsync(_root);

        Assert.Equal(1, result.Conflicts);
        var conflict = Assert.Single(_state.GetConflicts());
        Assert.Equal(id, conflict.NoteId);
        Assert.Equal(9, conflict.ServerRevision);
        // A refused write must NOT be recorded as pushed, or the next sync would
        // skip the note and the edit would be lost quietly.
        Assert.Null(_state.GetPushedHash(id));
    }

    [Fact]
    public async Task Sync_ReportsAnUnauthorizedServer_AsAFailure()
    {
        _http.OnGet(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await Client().SyncAsync(_root);

        Assert.True(result.Failed);
        Assert.Contains("token", result.Error);
    }

    [Fact]
    public async Task Sync_Fails_WithoutThrowing_WhenTheServerIsUnreachable()
    {
        _http.OnGet(_ => throw new HttpRequestException("connection refused"));

        var result = await Client().SyncAsync(_root);

        Assert.True(result.Failed);
        Assert.Contains("connection refused", result.Error);
    }

    [Fact]
    public async Task Sync_PullsBeforeItPushes()
    {
        // The order is the safety property: a client that pushes first can
        // overwrite a newer remote edit without ever seeing it.
        var note = WriteNote("Note.md", "# Hello", id: out _);
        _vault.Notes.Add(note);
        _http.OnGet(_ => Results.Json(new NoteChangesPage([], null)));
        _http.OnPut(_ => Results.Json(new NoteStored(Guid.NewGuid(), "n", "t", "h", 1, _deviceId, DateTimeOffset.UtcNow, false)));

        await Client().SyncAsync(_root);

        var order = _http.Requests.Select(r => r.Method).ToList();
        Assert.Equal(HttpMethod.Get, order[0]);
        Assert.Equal(HttpMethod.Put, order[^1]);
    }

    [Fact]
    public async Task Sync_AdvancesTheCursorAcrossPages()
    {
        var firstCursor = new NoteCursor(DateTimeOffset.UnixEpoch.AddHours(1), Guid.NewGuid());
        var pages = 0;

        // One page that hands out a cursor, then a page that ends it.
        _http.OnGet(_ => Results.Json(new NoteChangesPage([], ++pages == 1 ? firstCursor : null)));

        await Client().SyncAsync(_root);

        Assert.Equal(2, _http.Requests.Count);
        Assert.Equal(firstCursor, _state.GetCursor());
    }

    [Fact]
    public async Task Sync_Stops_WhenTheServerRepeatsTheSameCursor()
    {
        // A server that keeps handing back one cursor would otherwise loop here
        // forever, and a hang is a worse failure than a short sync.
        var stuck = new NoteCursor(DateTimeOffset.UnixEpoch.AddHours(1), Guid.NewGuid());
        _http.OnGet(_ => Results.Json(new NoteChangesPage([], stuck)));

        var result = await Client().SyncAsync(_root);

        Assert.False(result.Failed);
        Assert.Equal(2, _http.Requests.Count); // page 1 sets the cursor, page 2 notices the repeat
    }
    // --- helpers ------------------------------------------------------------

    private NoteMetadata WriteNote(string relativePath, string content, out Guid id)
    {
        var fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);

        id = Guid.NewGuid();
        return new NoteMetadata(id, relativePath, Path.GetFileNameWithoutExtension(relativePath),
            NoteSyncClient.HashOf(content), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        // Sensible defaults so a test only has to describe the part it cares
        // about: pull returns nothing new, and a push is accepted and echoed back
        // the way the real server echoes the stored state.
        private Func<HttpRequestMessage, HttpResponseMessage> _onGet =
            _ => Results.Json(new NoteChangesPage([], null));

        private Func<HttpRequestMessage, HttpResponseMessage> _onPut = EchoStored;

        public List<RecordedRequest> Requests { get; } = [];

        public void OnGet(Func<HttpRequestMessage, HttpResponseMessage> handler) => _onGet = handler;

        public void OnPut(Func<HttpRequestMessage, HttpResponseMessage> handler) => _onPut = handler;

        private static HttpResponseMessage EchoStored(HttpRequestMessage request)
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var sent = JsonSerializer.Deserialize<NoteUpsert>(body, Json)!;
            return Results.Json(new NoteStored(
                sent.Id, sent.RelativePath, sent.Title, sent.ContentHash, sent.Revision,
                sent.DeviceId, sent.UpdatedAt, false));
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(
                h => h.Key,
                h => string.Join(",", h.Value),
                StringComparer.OrdinalIgnoreCase);

            Requests.Add(new RecordedRequest(request.Method, headers, request.RequestUri!));
            var responder = request.Method == HttpMethod.Put ? _onPut : _onGet;
            return Task.FromResult(responder(request));
        }

        public sealed record RecordedRequest(
            HttpMethod Method,
            IReadOnlyDictionary<string, string> Headers,
            Uri RequestUri);
    }

    private sealed class FakeSyncStateStore : ISyncStateStore
    {
        private readonly Dictionary<Guid, string> _hashes = new();
        private readonly List<SyncConflict> _conflicts = new();

        public NoteCursor? Cursor { get; private set; }

        public NoteCursor? GetCursor() => Cursor;

        public void SetCursor(NoteCursor cursor) => Cursor = cursor;

        public void ResetCursor() => Cursor = null;

        public string? GetPushedHash(Guid noteId) => _hashes.GetValueOrDefault(noteId);

        public void SetPushedHash(Guid noteId, string hash) => _hashes[noteId] = hash;

        public IReadOnlyList<SyncConflict> GetConflicts() => _conflicts;

        public void AddConflict(SyncConflict conflict) => _conflicts.Add(conflict);

        public void RemoveConflict(Guid noteId) => _conflicts.RemoveAll(c => c.NoteId == noteId);

        public void ClearConflicts() => _conflicts.Clear();
    }

    private sealed class FakeVaultIndex : IVaultIndex
    {
        public List<NoteMetadata> Notes { get; } = [];

        public IReadOnlyList<NoteMetadata> GetAll() => Notes;

        public void Upsert(NoteMetadata note) => Notes.Add(note);

        public int DeleteMissing(IReadOnlyCollection<Guid> idsStillPresent) => 0;
    }

    private static class Results
    {
        public static HttpResponseMessage Json<T>(T value, HttpStatusCode statusCode = HttpStatusCode.OK) =>
            new(statusCode)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                    Encoding.UTF8,
                    "application/json"),
            };
    }
}
