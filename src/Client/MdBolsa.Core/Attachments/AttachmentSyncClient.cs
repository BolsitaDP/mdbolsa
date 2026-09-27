using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MdBolsa.Contracts;
using MdBolsa.Core.Sync;

namespace MdBolsa.Core.Attachments;

// Pull first, then push, exactly as NoteSyncClient does - and for a different
// reason, which is worth stating. Notes can conflict, so the order matters: pulling
// first means a conflict is detected against the newest server state rather than
// fought over. Attachments cannot conflict, because a hash has exactly one possible
// content. So this is simpler than the note sync in every respect except one: what it
// has to get right is *idempotence*. Running it twice, or running it on two devices
// that both had the same screenshot, has to end with one copy of the file and
// nothing to reconcile.
//
// The cursor is the server's `seen_at` watermark, stored with the note cursor in the
// same SQLite file, so attachments and notes stay in step in one sync pass.
public sealed class AttachmentSyncClient(
    HttpClient http,
    Guid deviceId,
    string token,
    ISyncStateStore state,
    IAttachmentIndex index)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>How many attachments to move per request. Small, because each one is
    /// a round trip and a 32 MB worst case.</summary>
    private const int PageSize = 20;

    public async Task<AttachmentSyncResult> SyncAsync(
        string vaultRoot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return AttachmentSyncResult.FailedResult("No sync token configured.");

        var folder = Path.Combine(vaultRoot, AttachmentRules.FolderPath);
        Directory.CreateDirectory(folder);

        int pulled = 0, pushed = 0, skipped = 0;
        string? failure = null;

        // --- pull: everything the server has that this device does not ---------
        // The attachment watermark, not the note cursor. They are different streams
        // and sharing one watermark means whichever ran ahead silently starves the
        // other - a note sync at T+1h hides every attachment the server saw before T.
        var since = state.GetAttachmentSince() ?? DateTimeOffset.UnixEpoch;
        var newest = since;

        try
        {
            while (true)
            {
                var pageCursor = state.GetAttachmentCursor() ?? string.Empty;
                var pageSeenAt = state.GetAttachmentPageSeenAt()?.ToString("O") ?? string.Empty;

                var url = $"api/attachments/changes?since={Uri.EscapeDataString(since.ToString("O"))}" +
                          $"&limit={PageSize}" +
                          $"&cursor={Uri.EscapeDataString(pageCursor)}" +
                          $"&cursorSeenAt={Uri.EscapeDataString(pageSeenAt)}";

                using var request = CreateRequest(HttpMethod.Get, url);
                using var response = await http.SendAsync(request, cancellationToken);
                await EnsureSuccessAsync(response, cancellationToken);

                var page = await response.Content.ReadFromJsonAsync<AttachmentPage>(Json, cancellationToken)
                    ?? new AttachmentPage([], null, null);

                foreach (var attachment in page.Attachments)
                {
                    if (attachment.SeenAt > newest) newest = attachment.SeenAt;

                    if (index.Has(attachment.Hash))
                    {
                        // Already here. Marked as known to the server as well, because
                        // "I have it" and "the server has it" are different facts and
                        // the push side needs the second one.
                        state.MarkAttachmentPushed(attachment.Hash);
                        skipped++;
                        continue;
                    }

                    if (await DownloadAsync(attachment, folder, cancellationToken)) pulled++;
                }

                if (page.NextCursor is null || page.Attachments.Count == 0) break;

                state.SetAttachmentCursor(page.NextCursor);
                state.SetAttachmentPageSeenAt(page.NextSeenAt);
            }
        }
        catch (SyncException ex)
        {
            failure = ex.Message;
        }

        // --- push: everything local the server has not got -------------------
        if (failure is null)
        {
            foreach (var attachment in index.GetAll())
            {
                // The one that matters: without this the client re-uploads every file
                // in the vault on every sync, forever, because it has no way to ask
                // "do you already have this?" - it just assumed not.
                if (state.IsAttachmentPushed(attachment.Hash))
                {
                    skipped++;
                    continue;
                }

                var fullPath = Path.Combine(
                    vaultRoot, attachment.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(fullPath))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    if (await UploadAsync(fullPath, attachment, cancellationToken))
                    {
                        state.MarkAttachmentPushed(attachment.Hash);
                        pushed++;
                    }
                }
                catch (SyncException ex)
                {
                    failure = ex.Message;
                    break;
                }
            }
        }

        if (failure is not null) return AttachmentSyncResult.FailedResult(failure);

        state.SetAttachmentSince(newest);
        state.SetAttachmentCursor(null);
        state.SetAttachmentPageSeenAt(null);
        return new AttachmentSyncResult(pulled, pushed, skipped);
    }

    private async Task<bool> DownloadAsync(
        StoredAttachment attachment, string folder, CancellationToken cancellationToken)
    {
        // A .crdownload or .part next to the real file would be picked up by the
        // scanner and uploaded, so the temporary is written under a name the rules
        // reject and then moved into place.
        // The extension comes back from the *content type*, because content-addressed
        // data has no file name to carry one. Without it a download lands as a file
        // called "3f7868...", which Windows will not open and Obsidian will not render
        // - present, and useless.
        var target = Path.Combine(
            folder, attachment.Hash + AttachmentRules.ExtensionFor(attachment.ContentType));
        var temporary = target + ".part";

        using var request = CreateRequest(HttpMethod.Get, $"api/attachments/{attachment.Hash}");
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        await EnsureSuccessAsync(response, cancellationToken);

        await using (var stream = File.Create(temporary))
        {
            await response.Content.CopyToAsync(stream, cancellationToken);
        }

        // Verify what arrived. A truncated download that was never noticed becomes a
        // permanently corrupt file, because the next sync sees the hash it expects
        // and skips it.
        if (!string.Equals(AttachmentHash.OfFile(temporary), attachment.Hash, StringComparison.Ordinal))
        {
            File.Delete(temporary);
            throw new SyncException($"The attachment {attachment.Hash[..12]} did not arrive intact.");
        }

        File.Move(temporary, target, overwrite: true);

        index.Upsert(new AttachmentMetadata(
            attachment.Hash,
            $"{AttachmentRules.FolderPath}/{Path.GetFileName(target)}",
            new FileInfo(target).Length,
            attachment.ContentType,
            DateTimeOffset.UtcNow));

        state.MarkAttachmentPushed(attachment.Hash);
        return true;
    }

    private async Task<bool> UploadAsync(
        string fullPath, AttachmentMetadata attachment, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllBytesAsync(fullPath, cancellationToken);

        using var request = CreateRequest(HttpMethod.Put, $"api/attachments/{attachment.Hash}");
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(attachment.ContentType) ? "application/octet-stream" : attachment.ContentType);

        using var response = await http.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode) return true;

        // The server refusing a hash it does not agree with is a bug worth shouting
        // about, not something to retry: the bytes on disk are not what the index
        // says they are, and only the user can decide what to do about that.
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new SyncException($"The server rejected {Path.GetFileName(fullPath)}: {detail}");
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return false;
    }

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

public sealed record AttachmentSyncResult(int Pulled, int Pushed, int Skipped, bool Failed = false, string? Error = null)
{
    public static AttachmentSyncResult FailedResult(string error) => new(0, 0, 0, Failed: true, Error: error);
}

// The server's page of "what have I not got", as JSON. Not in Contracts on purpose:
// nothing outside the client and the server reads this shape, and a DTO that only
// two ends use does not need to be a shared contract that a third could come to
// depend on.
public sealed record StoredAttachment(
    string Hash, long Size, string ContentType, Guid DeviceId, DateTimeOffset SeenAt);

public sealed record AttachmentPage(IReadOnlyList<StoredAttachment> Attachments, string? NextCursor, DateTimeOffset? NextSeenAt);
