using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MdBolsa.Contracts;
using MdBolsa.Core.Vault;

namespace MdBolsa.Core.Sync;

/// <summary>A sync request that reached the server and was refused.</summary>
public sealed class SyncException(string message) : Exception(message);

// The client half of sync: push what changed here, pull what changed there, and
// *notice* when both happened to the same note without pretending to resolve it
// (that's Phase 10).
//
// Two decisions shape everything here:
//
//  1. **Pull happens before push.** A client that pushes first cannot tell a
//     stale local copy from a fresh one, and would silently overwrite a newer
//     remote edit. Pulling first means the client knows what the server holds
//     before it offers anything.
//  2. **"Changed here since the server last saw it" is answered by a hash, not a
//     timestamp.** ISyncStateStore keeps the hash the server last confirmed for
//     each note. Comparing hashes means a moved clock, or a file touched by
//     another tool, doesn't make a note look edited - and a note that only
//     changed over there is unambiguously safe to overwrite locally.
//
// Nothing here blocks the editor: sync is called explicitly and returns a
// result. Writing a note never waits on it.
public sealed class NoteSyncClient(
    HttpClient http,
    Guid deviceId,
    string token,
    ISyncStateStore state,
    IVaultIndex vaultIndex)
{
    private const int PageSize = 200;

    // camelCase on both sides: the server's minimal API serialises camelCase and
    // these records are PascalCase. Setting the policy for read *and* write is
    // what stops a silent "everything deserialises to null".
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<SyncResult> SyncAsync(string vaultRoot, CancellationToken cancellationToken = default)
    {
        // Distinct *notes*, not distinct events: one note can be seen as a
        // conflict twice in a single run (once when the pull finds the remote
        // edit, again when the push is refused), and "2 conflicts" for one note
        // is a confusing thing to tell someone about their notes.
        var conflicted = new HashSet<Guid>();

        try
        {
            // 1. Take what's on the server, then 2. offer what's here. The order is
            //    the whole point: it's what stops a stale local note from
            //    overwriting a newer remote one.
            var (applied, deleted) = await PullAsync(vaultRoot, conflicted, cancellationToken);
            var pushed = await PushAsync(vaultRoot, conflicted, cancellationToken);

            return new SyncResult(pushed, applied, deleted, conflicted.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SyncException ex)
        {
            return SyncResult.Failure(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            // A failed sync is a line in the status bar, never an exception in the
            // user's face: local-first means the app keeps working.
            return SyncResult.Failure(ex.Message);
        }
    }

    // --- Pull ---------------------------------------------------------------

    private async Task<(int Applied, int Deleted)> PullAsync(
        string vaultRoot, HashSet<Guid> conflicted, CancellationToken cancellationToken)
    {
        var applied = 0;
        var deleted = 0;

        // Page until the server stops handing out cursors. The cursor is persisted
        // as it advances, so an interrupted sync resumes where it stopped rather
        // than re-downloading the vault.
        while (true)
        {
            var cursor = state.GetCursor();
            var since = cursor?.UpdatedAt ?? DateTimeOffset.UnixEpoch;
            var url = "api/notes/changes" +
                      $"?since={Uri.EscapeDataString(since.ToString("O"))}" +
                      (cursor is null
                          ? string.Empty
                          : $"&cursorAt={Uri.EscapeDataString(cursor.UpdatedAt.ToString("O"))}&cursorId={cursor.NoteId}") +
                      $"&limit={PageSize}";

            using var request = CreateRequest(HttpMethod.Get, url);
            using var response = await http.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);

            var page = await response.Content.ReadFromJsonAsync<NoteChangesPage>(Json, cancellationToken)
                       ?? throw new SyncException("The sync server returned an unreadable page.");

            foreach (var change in page.Changes)
            {
                switch (Apply(vaultRoot, change, conflicted))
                {
                    case ApplyOutcome.Applied: applied++; break;
                    case ApplyOutcome.Deleted: deleted++; break;
                    case ApplyOutcome.Conflict:
                    case ApplyOutcome.NoChange: break;
                }
            }

            if (page.NextCursor is null) break;

            // A server that keeps handing back the same cursor would loop here
            // forever. Stopping is the right failure: a short sync is
            // recoverable, a hang is not. (A repeat is only visible from the
            // second page on - the first has no cursor to compare against.)
            if (page.NextCursor == state.GetCursor()) break;

            state.SetCursor(page.NextCursor);
        }

        return (applied, deleted);
    }

    private enum ApplyOutcome
    {
        NoChange,

        // The remote content was written to the vault.
        Applied,

        // The note is gone; the file was removed.
        Deleted,

        // Both sides changed; nothing was touched and a conflict was recorded.
        Conflict,
    }

    private ApplyOutcome Apply(string vaultRoot, NoteChange change, HashSet<Guid> conflicted)
    {
        // A tombstone carries no path - the server nulls every field except the
        // id - so the local index is the only thing that can say which file it
        // was. Getting this wrong means a deletion that silently does nothing.
        var relativePath = change.RelativePath ?? FindLocalPath(change.Id);
        var localPath = relativePath is null ? null : Resolve(vaultRoot, relativePath);
        var localHash = localPath is not null && File.Exists(localPath)
            ? HashOf(File.ReadAllText(localPath))
            : null;

        // Changed here since the server last confirmed this note? A null pushed
        // hash means we've never had one, so any local content counts as ours.
        var changedHere = localHash is not null && localHash != state.GetPushedHash(change.Id);

        if (change.Deleted)
        {
            if (localPath is null || !File.Exists(localPath)) return ApplyOutcome.NoChange;

            if (changedHere)
            {
                RecordConflict(change, relativePath, serverRevision: null, conflicted);
                return ApplyOutcome.Conflict;
            }

            File.Delete(localPath);
            return ApplyOutcome.Deleted;
        }

        if (change.Content is null || relativePath is null) return ApplyOutcome.NoChange;

        // Identical content: nothing to write.
        if (localHash is not null && localHash == change.ContentHash) return ApplyOutcome.NoChange;

        if (changedHere)
        {
            RecordConflict(change, relativePath, change.Revision, conflicted);
            return ApplyOutcome.Conflict;
        }

        Write(vaultRoot, relativePath, change.Content);
        state.SetPushedHash(change.Id, change.ContentHash ?? HashOf(change.Content));
        return ApplyOutcome.Applied;
    }

    private void RecordConflict(
        NoteChange change, string? relativePath, int? serverRevision, HashSet<Guid> conflicted)
    {
        conflicted.Add(change.Id);
        state.AddConflict(new SyncConflict(
            change.Id,
            relativePath,
            LocalRevision: null,
            serverRevision,
            change.DeviceId,
            DateTimeOffset.UtcNow));
    }

    // --- Push ---------------------------------------------------------------

    private async Task<int> PushAsync(
        string vaultRoot, HashSet<Guid> conflicted, CancellationToken cancellationToken)
    {
        var pushed = 0;

        foreach (var note in vaultIndex.GetAll())
        {
            var fullPath = Resolve(vaultRoot, note.RelativePath);
            if (!File.Exists(fullPath)) continue;

            var content = File.ReadAllText(fullPath);
            var hash = HashOf(content);

            // Unchanged since the server last confirmed it: don't send it. This is
            // what keeps a sync cheap without per-note dirty flags.
            if (state.GetPushedHash(note.Id) == hash) continue;

            var upsert = new NoteUpsert(
                note.Id,
                note.RelativePath,
                note.Title,
                content,
                hash,
                note.Revision,
                deviceId,
                note.UpdatedAt);

            using var request = CreateRequest(HttpMethod.Put, $"api/notes/{note.Id}");
            request.Content = JsonContent.Create(upsert, options: Json);
            using var response = await http.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                // Either a newer revision of this note exists (the server refuses
                // the write), or another note already holds this path. Both are
                // conflicts to show - not to retry, and not to resolve.
                var body = await response.Content.ReadFromJsonAsync<ConflictBody>(Json, cancellationToken);

                conflicted.Add(note.Id);
                state.AddConflict(new SyncConflict(
                    note.Id,
                    note.RelativePath,
                    note.Revision,
                    body?.Stored?.Revision,
                    body?.Stored?.DeviceId,
                    DateTimeOffset.UtcNow));

                continue;
            }

            await EnsureSuccessAsync(response, cancellationToken);

            // Only now is the server's copy ours: recording the hash on a refused
            // write would make the next sync skip the note entirely.
            state.SetPushedHash(note.Id, hash);
            pushed++;
        }

        return pushed;
    }

    // --- Plumbing -----------------------------------------------------------

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

    // Writes the note, creating its folder. The content arrives byte-for-byte
    // from the server; the `id:` in its frontmatter is what keeps the note's
    // identity stable across the round trip.
    private static void Write(string vaultRoot, string relativePath, string content)
    {
        var fullPath = Resolve(vaultRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    // Which file, in this vault, holds the note with this id?
    private string? FindLocalPath(Guid noteId) =>
        vaultIndex.GetAll().FirstOrDefault(n => n.Id == noteId)?.RelativePath;

    private static string Resolve(string vaultRoot, string relativePath) =>
        Path.Combine(vaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    // The same hash VaultScanner computes, so "the server has this content" and
    // "the index has this content" mean exactly the same thing.
    public static string HashOf(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private sealed record ConflictBody(ConflictStored? Stored);

    private sealed record ConflictStored(int Revision, Guid DeviceId);
}
