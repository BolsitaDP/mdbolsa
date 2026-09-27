using MdBolsa.Contracts;
using MdBolsa.Server.Attachments;
using MdBolsa.Server.Notes;
using MdBolsa.Server.Auth;

namespace MdBolsa.Server.Endpoints;

// The attachment surface, Phase 11.
//
// Four operations, and the shape mirrors the notes API deliberately so the client
// has one thing to learn:
//
//   PUT    /api/attachments/{hash}   upload the bytes
//   GET    /api/attachments/{hash}   download them
//   GET    /api/attachments/changes  the incremental pull
//   GET    /api/attachments          what is stored (names and sizes, not bytes)
//
// The one thing that is *not* like notes is that there is nothing to arbitrate: the
// hash is the identity, so the same upload twice is the same single row, and no
// response here is ever a conflict. See docs/decisions/0011-attachments.md.
public static class AttachmentEndpoints
{
    public static IEndpointRouteBuilder MapAttachmentEndpoints(this IEndpointRouteBuilder app, TokenAuthenticator auth)
    {
        var attachments = app.MapGroup("/api/attachments").WithTags("attachments");

        // Same check as the note routes. A device token can upload, which is the
        // whole reason this phase exists: with a shared token, whoever holds it
        // spends this server's disk.
        attachments.AddEndpointFilter(TokenAuthFilter.ForGroup(auth));

        attachments.MapGet("/", async Task<IResult> (AttachmentStore store, CancellationToken ct) =>
            Results.Ok(await store.ListAsync(ct)));

        attachments.MapGet("/changes", async Task<IResult> (
            HttpRequest request,
            AttachmentStore store,
            CancellationToken ct) =>
        {
            if (!TryReadSince(request, out var since, out var error)) return error!;

            var limit = Math.Clamp(
                int.TryParse(request.Query["limit"], out var parsed) ? parsed : 100,
                1,
                500);

            // `cursorSeenAt` is its own parameter rather than something parsed back out
            // of the cursor string: a composite key that has to be split correctly is a
            // thing that will eventually be split wrongly.
            var cursorSeenAt = DateTimeOffset.TryParse(request.Query["cursorSeenAt"], out var parsedSeenAt)
                ? parsedSeenAt
                : (DateTimeOffset?)null;

            return Results.Ok(await store.GetSeenSinceAsync(
                since, request.Query["cursor"], limit, ct, cursorSeenAt));
        });

        attachments.MapGet("/{hash}", async Task<IResult> (
            string hash,
            HttpRequest request,
            AttachmentStore store,
            CancellationToken ct) =>
        {
            if (!AttachmentStore.IsValidHash(hash)) return Results.NotFound();

            var bytes = await store.ReadAsync(hash, ct);
            if (bytes is null) return Results.NotFound();

            // The stored content type is echoed back rather than sniffed from the
            // request: the uploader declared it once, and a download that guesses
            // differently each time is how "image.png" ends up downloading as text.
            var stored = await store.GetAsync(hash, ct);
            return Results.File(bytes, stored?.ContentType ?? "application/octet-stream");
        });

        attachments.MapPut("/{hash}", async Task<IResult> (
            string hash,
            HttpRequest request,
            AttachmentStore store,
            CancellationToken ct) =>
        {
            if (!AttachmentStore.IsValidHash(hash)) return Results.BadRequest(new { error = "Not a valid hash." });

            // Checked before reading the body: a 100 MB upload should be refused
            // without first being copied into memory.
            if (request.ContentLength is { } declared && declared > AttachmentSchema.MaxAttachmentBytes)
            {
                return Results.Json(
                    new { error = $"Too large. The limit is {AttachmentSchema.MaxAttachmentBytes / (1024 * 1024)} MB." },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            byte[] content;
            try
            {
                content = await ReadBoundedAsync(request, ct);
            }
            catch (AttachmentTooLargeException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            if (!Guid.TryParse(request.Headers[SyncHeaders.Device].ToString(), out var deviceId))
            {
                return Results.BadRequest(new { error = "Missing or malformed device header." });
            }

            try
            {
                var stored = await store.StoreAsync(
                    hash, content, ContentTypes.For(request.ContentType), deviceId, ct);

                return Results.Ok(new { stored.Hash, stored.Size, stored.ContentType, AlreadyPresent = false });
            }
            catch (AttachmentHashMismatchException ex)
            {
                // 422, not 400 and not 500: the request was well-formed, it just
                // disagreed with itself. Storing it anyway would spread the
                // corruption to every device.
                return Results.UnprocessableEntity(new { error = ex.Message, expected = ex.Expected, actual = ex.Actual });
            }
            catch (AttachmentTooLargeException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status413PayloadTooLarge);
            }
        });

        return app;
    }

    // Reads at most one byte past the cap, so an oversized body is detected without
    // buffering all of it. Content-Length is a hint and can lie or be absent, so the
    // limit is enforced while reading too, not only before.
    private static async Task<byte[]> ReadBoundedAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var limit = AttachmentSchema.MaxAttachmentBytes;
        using var buffer = new MemoryStream();

        var readBuffer = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(readBuffer, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new AttachmentTooLargeException(buffer.Length + read, limit);
            }

            await buffer.WriteAsync(readBuffer.AsMemory(0, read), cancellationToken);
        }

        return buffer.ToArray();
    }

    private static bool TryReadSince(HttpRequest request, out DateTimeOffset since, out IResult? error)
    {
        error = null;
        var raw = request.Query["since"].ToString();

        if (DateTimeOffset.TryParse(raw, out since)) return true;

        // No `since` means "everything", not "now": a client that has never synced
        // asks for the whole vault, and the epoch is the honest way to say that.
        if (string.IsNullOrWhiteSpace(raw))
        {
            since = DateTimeOffset.UnixEpoch;
            return true;
        }

        since = default;
        error = Results.BadRequest(new { error = $"'{raw}' is not a date." });
        return false;
    }
}

file static class ContentTypes
{
    // Only the handful worth naming. Everything else is served as
    // application/octet-stream, which is honest: a browser or an image viewer
    // sniffs the bytes anyway, and a wrong guess in the header is worse than no
    // guess.
    public static string For(string? declared) => declared switch
    {
        null or "" => "application/octet-stream",
        var value => value.Split(';')[0].Trim(),
    };
}
