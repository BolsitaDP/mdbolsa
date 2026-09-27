using System.Net;
using System.Text;
using System.Text.Json;
using MdBolsa.Contracts;
using MdBolsa.Core.Sync;
using MdBolsa.Core.Vault;

namespace MdBolsa.Core.Tests.Sync;

// The two choices a person has about a conflicted note, and the promise that
// neither of them loses anything: "keep mine" leaves the server's copy in the
// history, "take theirs" leaves the local file the only casualty.
public class ConflictResolverTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _root = Directory.CreateTempSubdirectory("mdbolsa-resolve-").FullName;
    private readonly Guid _deviceId = Guid.NewGuid();
    private readonly FakeStore _state = new();
    private readonly FakeVaultIndex _vault = new();
    private readonly FakeHandler _http = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ConflictResolver Resolver() =>
        new(new HttpClient(_http) { BaseAddress = new Uri("http://localhost/") }, _deviceId, "token", _state, _vault);

    private Guid WriteNote(string content, out string fullPath, out NoteMetadata note)
    {
        var id = Guid.NewGuid();
        fullPath = Path.Combine(_root, "Contested.md");
        File.WriteAllText(fullPath, content);
        note = new NoteMetadata(id, "Contested.md", "Contested", "hash", 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        _vault.Notes.Add(note);
        _state.AddConflict(new SyncConflict(id, "Contested.md", 2, 7, Guid.NewGuid(), DateTimeOffset.UtcNow));
        return id;
    }

    [Fact]
    public async Task KeepLocal_PushesTheLocalCopy_WithAFreshTimestamp()
    {
        var id = WriteNote("# Mine", out _, out _);
        NoteUpsert? sent = null;
        _http.OnPut(request =>
        {
            sent = JsonSerializer.Deserialize<NoteUpsert>(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(), Json);
            return Results.JsonBody(new NoteStored(id, "Contested.md", "Contested", "h", 8, _deviceId, DateTimeOffset.UtcNow, false));
        });

        var result = await Resolver().KeepLocalAsync(_root, id);

        Assert.True(result.Resolved);
        Assert.Equal(ConflictResolution.KeepLocal, result.Choice);
        Assert.NotNull(sent);
        Assert.Equal("# Mine", sent!.Content);
        // Above both the local and the server's revision, so the history reads
        // sensibly, and newer than the stored copy so the server accepts it.
        Assert.Equal(8, sent.Revision);
        Assert.Equal(_deviceId, sent.DeviceId);
    }

    [Fact]
    public async Task KeepLocal_ClearsTheConflict()
    {
        var id = WriteNote("# Mine", out _, out _);
        _http.OnPut(_ => Results.JsonBody(new NoteStored(id, "Contested.md", "Contested", "h", 8, _deviceId, DateTimeOffset.UtcNow, false)));

        await Resolver().KeepLocalAsync(_root, id);

        Assert.Empty(_state.GetConflicts());
    }

    [Fact]
    public async Task KeepLocal_ReportsARefusal_AndKeepsTheConflict()
    {
        var id = WriteNote("# Mine", out _, out _);
        _http.OnPut(_ => new HttpResponseMessage(HttpStatusCode.Conflict));

        var result = await Resolver().KeepLocalAsync(_root, id);

        Assert.False(result.Resolved);
        Assert.NotNull(result.Error);
        // Still conflicted: a refused write must not look like a resolved one.
        Assert.Single(_state.GetConflicts());
    }

    [Fact]
    public async Task TakeRemote_OverwritesTheLocalFile_WithTheServerCopy()
    {
        var id = WriteNote("# Mine", out var fullPath, out _);
        _http.OnGet(_ => Results.JsonBody(new NoteRecord(
            id, "Contested.md", "Contested", "# Theirs", "hash-theirs", 7,
            Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)));

        var result = await Resolver().TakeRemoteAsync(_root, id);

        Assert.True(result.Resolved);
        Assert.Equal(ConflictResolution.TakeRemote, result.Choice);
        Assert.Equal("# Theirs", File.ReadAllText(fullPath));
    }

    [Fact]
    public async Task TakeRemote_RecordsTheServersHash_SoTheNextSyncDoesNotConflictAgain()
    {
        var id = WriteNote("# Mine", out _, out _);
        _http.OnGet(_ => Results.JsonBody(new NoteRecord(
            id, "Contested.md", "Contested", "# Theirs", "hash-theirs", 7,
            Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)));

        await Resolver().TakeRemoteAsync(_root, id);

        // This is the subtle part: without the hash, the next sync would see the
        // freshly written file as a new local edit and conflict all over again.
        Assert.Equal("hash-theirs", _state.GetPushedHash(id));
        Assert.Empty(_state.GetConflicts());
    }

    [Fact]
    public async Task TakeRemote_Refuses_IfTheServerCopyIsGone()
    {
        var id = WriteNote("# Mine", out var fullPath, out _);
        _http.OnGet(_ => Results.JsonBody(new NoteRecord(
            id, "Contested.md", "Contested", "", "h", 7, Guid.NewGuid(),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));

        var result = await Resolver().TakeRemoteAsync(_root, id);

        Assert.False(result.Resolved);
        Assert.Equal("# Mine", File.ReadAllText(fullPath));
    }

    [Fact]
    public async Task GetHistory_ReturnsTheServersRevisions()
    {
        var id = WriteNote("# Mine", out _, out _);
        _http.OnGet(_ => Results.JsonBody(new List<NoteVersion>
        {
            new(id, 7, "Contested.md", "# Theirs", "h2", Guid.NewGuid(), DateTimeOffset.UtcNow, true),
            new(id, 6, "Contested.md", "# Older", "h1", Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(-1), false),
        }));

        var history = await Resolver().GetHistoryAsync(id);

        Assert.Equal(2, history.Count);
        Assert.Equal(7, history[0].Revision);
        Assert.True(history[0].IsCurrent);
        Assert.False(history[1].IsCurrent);
    }

    [Fact]
    public async Task Restore_PutsAnEarlierRevisionBackIntoTheLocalFile()
    {
        var id = WriteNote("# Mine", out var fullPath, out _);
        _http.OnGet(_ => Results.JsonBody(new List<NoteVersion>
        {
            new(id, 7, "Contested.md", "# Theirs", "h2", Guid.NewGuid(), DateTimeOffset.UtcNow, true),
            new(id, 6, "Contested.md", "# Older", "h1", Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(-1), false),
        }));

        var result = await Resolver().RestoreAsync(_root, id, revision: 6);

        Assert.True(result.Resolved);
        Assert.Equal(ConflictResolution.Restore, result.Choice);
        Assert.Equal("# Older", File.ReadAllText(fullPath));
    }

    [Fact]
    public async Task Restore_OfAnOlderRevision_LeavesTheNoteStillChanged()
    {
        // Restoring writes old content as the note's *current* content, so the next
        // sync has to push it. Recording the server's hash here would make the note
        // look synced when it isn't, and the edit would be lost.
        var id = WriteNote("# Mine", out _, out _);
        _http.OnGet(_ => Results.JsonBody(new List<NoteVersion>
        {
            new(id, 7, "Contested.md", "# Theirs", "h2", Guid.NewGuid(), DateTimeOffset.UtcNow, true),
            new(id, 6, "Contested.md", "# Older", "h1", Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(-1), false),
        }));

        await Resolver().RestoreAsync(_root, id, revision: 6);

        Assert.Null(_state.GetPushedHash(id));
    }

    [Fact]
    public async Task Restore_OfTheCurrentRevision_LeavesTheNoteInSync()
    {
        // Restoring what the server already holds genuinely puts the two back in
        // agreement, so this is the one case that can honestly record the hash and
        // clear the conflict.
        var id = WriteNote("# Mine", out _, out _);
        _http.OnGet(_ => Results.JsonBody(new List<NoteVersion>
        {
            new(id, 7, "Contested.md", "# Theirs", "hash-theirs", Guid.NewGuid(), DateTimeOffset.UtcNow, true),
            new(id, 6, "Contested.md", "# Older", "h1", Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(-1), false),
        }));

        await Resolver().RestoreAsync(_root, id, revision: 7);

        Assert.Equal("hash-theirs", _state.GetPushedHash(id));
        Assert.Empty(_state.GetConflicts());
    }

    [Fact]
    public async Task Restore_OfARevisionThatIsGone_Fails_AndLeavesTheFileAlone()
    {
        var id = WriteNote("# Mine", out var fullPath, out _);
        _http.OnGet(_ => Results.JsonBody(new List<NoteVersion>
        {
            new(id, 7, "Contested.md", "# Theirs", "h2", Guid.NewGuid(), DateTimeOffset.UtcNow, true),
        }));

        var result = await Resolver().RestoreAsync(_root, id, revision: 3);

        Assert.False(result.Resolved);
        Assert.NotNull(result.Error);
        Assert.Equal("# Mine", File.ReadAllText(fullPath));
    }

    [Fact]
    public async Task Resolution_Fails_ForANoteThatIsNoLongerInTheVault()
    {
        var result = await Resolver().KeepLocalAsync(_root, Guid.NewGuid());

        Assert.False(result.Resolved);
        Assert.NotNull(result.Error);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private Func<HttpRequestMessage, HttpResponseMessage> _onGet =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        private Func<HttpRequestMessage, HttpResponseMessage> _onPut =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public void OnGet(Func<HttpRequestMessage, HttpResponseMessage> handler) => _onGet = handler;

        public void OnPut(Func<HttpRequestMessage, HttpResponseMessage> handler) => _onPut = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult((request.Method == HttpMethod.Put ? _onPut : _onGet)(request));
    }

    private sealed class FakeStore : ISyncStateStore
    {
        private readonly Dictionary<Guid, string> _hashes = new();
        private readonly List<SyncConflict> _conflicts = new();

        public NoteCursor? GetCursor() => null;

        public void SetCursor(NoteCursor cursor) { }

        public void ResetCursor() { }

        public string? GetAttachmentCursor() => null;

        public void SetAttachmentCursor(string? cursor) { }

        public DateTimeOffset? GetAttachmentPageSeenAt() => null;

        public void SetAttachmentPageSeenAt(DateTimeOffset? seenAt) { }

        public DateTimeOffset? GetAttachmentSince() => null;

        public void SetAttachmentSince(DateTimeOffset? since) { }

        public bool IsAttachmentPushed(string hash) => false;

        public void MarkAttachmentPushed(string hash) { }

        public void ClearAttachmentPushed() { }

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
        public static HttpResponseMessage JsonBody<T>(T value) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(value, Json),
                    Encoding.UTF8,
                    "application/json"),
            };
    }
}
