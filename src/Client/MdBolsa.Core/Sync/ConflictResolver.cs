using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MdBolsa.Contracts;
using MdBolsa.Core.Vault;

namespace MdBolsa.Core.Sync;

// Resolving a conflict: the two choices a person actually has, and nothing
// clever in between.
//
// "Keep mine" needs no special endpoint. The server's rule is that a newer write
// wins, so pushing the local content with a fresh timestamp *is* taking over -
// and the version it replaces is already archived in `note_versions`, so nothing
// is lost by doing it. "Take theirs" doesn't need a write to the server at all:
// fetch the remote content once and write it over the local file.
//
// Neither of them merges. A merge would need a common ancestor and
// Markdown-aware diffing, and a bad automatic merge is worse than no merge,
// because it produces a third version neither person wrote. See
// docs/decisions/0012-conflict-resolution.md.
public sealed class ConflictResolver(
    HttpClient http,
    Guid deviceId,
    string token,
    ISyncStateStore state,
    IVaultIndex vaultIndex)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The note's edit history, newest first.</summary>
    public async Task<IReadOnlyList<NoteVersion>> GetHistoryAsync(
        Guid noteId, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"api/notes/{noteId}/versions");
        using var response = await http.SendAsync(request, cancellationToken);

        // A note the server has never heard of has no history, and that is a
        // perfectly good answer to "what are its versions?" - not an error worth
        // putting in front of someone.
        if (response.StatusCode == HttpStatusCode.NotFound) return [];

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<NoteVersion>>(Json, cancellationToken) ?? [];
    }

    /// <summary>Pushes the local copy over the server's, with a fresh timestamp so
    /// the server accepts it. The server's copy becomes history, not rubble.</summary>
    public async Task<ConflictResolutionResult> KeepLocalAsync(
        string vaultRoot, Guid noteId, CancellationToken cancellationToken = default)
    {
        var relativePath = RelativePathOf(noteId);
        if (relativePath is null)
            return ConflictResolutionResult.Failed("This note is no longer in the vault.");

        var fullPath = Resolve(vaultRoot, relativePath);
        if (!File.Exists(fullPath))
            return ConflictResolutionResult.Failed("The file for this note is gone.");

        var content = File.ReadAllText(fullPath);
        var hash = NoteSyncClient.HashOf(content);
        var revision = NextRevision(noteId);

        var upsert = new NoteUpsert(
            noteId,
            relativePath,
            Path.GetFileNameWithoutExtension(relativePath),
            content,
            hash,
            revision,
            deviceId,
            DateTimeOffset.UtcNow);

        using var request = CreateRequest(HttpMethod.Put, $"api/notes/{noteId}");
        request.Content = JsonContent.Create(upsert, options: Json);
        using var response = await http.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            return ConflictResolutionResult.Failed(
                $"The server refused the write ({(int)response.StatusCode}). {detail}".Trim());
        }

        state.SetPushedHash(noteId, hash);
        state.RemoveConflict(noteId);
        return new ConflictResolutionResult(true, ConflictResolution.KeepLocal);
    }

    /// <summary>Takes the server's version over the local file. Nothing is written
    /// to the server: it already has this content, and after this the two agree.</summary>
    public async Task<ConflictResolutionResult> TakeRemoteAsync(
        string vaultRoot, Guid noteId, CancellationToken cancellationToken = default)
    {
        var relativePath = RelativePathOf(noteId);
        if (relativePath is null)
            return ConflictResolutionResult.Failed("This note is not in the vault, so there is nothing to overwrite.");

        using var request = CreateRequest(HttpMethod.Get, $"api/notes/{noteId}");
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var remote = await response.Content.ReadFromJsonAsync<NoteRecord>(Json, cancellationToken);
        if (remote is null || remote.DeletedAt is not null)
            return ConflictResolutionResult.Failed("The server's copy of this note no longer exists.");

        var fullPath = Resolve(vaultRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, remote.Content);

        // The whole point of recording the hash: without it the next sync would
        // see this as a fresh local edit and conflict all over again.
        state.SetPushedHash(noteId, remote.ContentHash);
        state.RemoveConflict(noteId);

        return new ConflictResolutionResult(true, ConflictResolution.TakeRemote);
    }

    /// <summary>
    /// Puts an earlier revision back into the local file.
    ///
    /// Restoring is not undoing: it writes old content as the note's *current*
    /// content, so the next sync pushes it as a new revision and the history grows
    /// rather than rewrites. That is the only honest version of "go back" in a tool
    /// where more than one device may be writing - the alternative, rewinding the
    /// server's history, throws away the edits made since.
    ///
    /// The one exception: restoring the revision the server currently holds puts
    /// the two back in agreement, so that case records the hash and clears any
    /// conflict. Otherwise the conflict stands, because the note still differs from
    /// the server and pretending otherwise would just resurface as a conflict on the
    /// next sync.
    /// </summary>
    public async Task<ConflictResolutionResult> RestoreAsync(
        string vaultRoot, Guid noteId, int revision, CancellationToken cancellationToken = default)
    {
        var relativePath = RelativePathOf(noteId);
        if (relativePath is null)
            return ConflictResolutionResult.Failed("This note is not in the vault, so there is nothing to restore into.");

        // The history endpoint already carries the content of every revision, so
        // there is no second request to make and no second shape to keep in step.
        var version = (await GetHistoryAsync(noteId, cancellationToken))
            .FirstOrDefault(candidate => candidate.Revision == revision);

        if (version is null)
            return ConflictResolutionResult.Failed($"Revision {revision} is no longer on the server.");

        var fullPath = Resolve(vaultRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, version.Content);

        if (version.IsCurrent)
        {
            state.SetPushedHash(noteId, version.ContentHash);
            state.RemoveConflict(noteId);
        }

        return new ConflictResolutionResult(true, ConflictResolution.Restore);
    }

    private string? RelativePathOf(Guid noteId) =>
        vaultIndex.GetAll().FirstOrDefault(n => n.Id == noteId)?.RelativePath;

    // One above whatever this device has seen, so the history reads sensibly
    // (1, 2, 3...) even after devices have been writing concurrently. The server
    // doesn't require monotonic revisions across devices - it gates on
    // updated_at - so this is for the human reading the list.
    private int NextRevision(Guid noteId)
    {
        var local = vaultIndex.GetAll().FirstOrDefault(n => n.Id == noteId)?.Revision ?? 0;
        var server = state.GetConflicts().FirstOrDefault(c => c.NoteId == noteId)?.ServerRevision ?? 0;
        return Math.Max(local, server) + 1;
    }

    private static string Resolve(string vaultRoot, string relativePath) =>
        Path.Combine(vaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativeUrl)
    {
        var request = new HttpRequestMessage(method, relativeUrl);
        request.Headers.Add(SyncHeaders.Token, token);
        request.Headers.Add(SyncHeaders.Device, deviceId.ToString());
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "The sync server rejected the token.",
            _ => $"The sync server returned {(int)response.StatusCode} {response.StatusCode}.",
        };

        throw new SyncException(detail.Length > 0 ? $"{message} {detail}" : message);
    }
}
