using MdBolsa.Contracts;
using MdBolsa.Server.Auth;
using MdBolsa.Server.Notes;

namespace MdBolsa.Server.Endpoints;

public static class NoteEndpoints
{
    // Phase 9 shape: a storage/sync API with one shared token in front of it.
    // See docs/decisions/0011-client-sync.md.
    public static IEndpointRouteBuilder MapNoteEndpoints(this IEndpointRouteBuilder app, string? token)
    {
        var notes = app.MapGroup("/api/notes").WithTags("notes");

        // Every /api/notes route needs the token. /health does not: it exposes
        // nothing but the environment name and database host, and a health probe
        // that 401s is worse than useless.
        notes.AddEndpointFilter(async (context, next) =>
        {
            var presented = context.HttpContext.Request.Headers[SyncHeaders.Token].ToString();
            if (!TokenValidator.IsValid(presented, token))
            {
                return Results.Unauthorized();
            }

            return await next(context);
        });

        notes.MapGet("/{id:guid}", async Task<IResult> (Guid id, NoteStore store, CancellationToken ct) =>
        {
            var note = await store.GetAsync(id, ct);
            return note is null ? Results.NotFound() : Results.Ok(note);
        });

        // Returns the state the server actually stored, not just 204. A client
        // that sent revision 4 and reads back revision 5 from another device
        // knows its write was refused - that's a conflict to report (Phase 10
        // resolves it), and it's invisible if the endpoint only says "ok".
        notes.MapPut("/{id:guid}", async Task<IResult> (
            Guid id,
            NoteUpsert note,
            NoteStore store,
            CancellationToken ct) =>
        {
            if (id != note.Id)
            {
                return Results.BadRequest(new { error = "The id in the route and the body must match." });
            }

            var validation = NoteValidation.Validate(note);
            if (validation is not null) return Results.BadRequest(new { error = validation });

            NoteStored? stored;
            try
            {
                stored = await store.UpsertAsync(note, ct);
            }
            catch (NotePathConflictException clash)
            {
                // Another note already lives at that path. That is a conflict the
                // client can report, not a server fault - 409, like the stale-write
                // case below, and with the same body shape.
                return Results.Conflict(new
                {
                    error = clash.Message,
                    stored = await store.GetAsync(id, ct),
                });
            }
            if (stored is null)
            {
                // The write lost to something newer already on the server.
                var current = await store.GetAsync(id, ct);
                return Results.Conflict(new
                {
                    error = "A newer revision of this note is already on the server.",
                    stored = current,
                });
            }

            return Results.Ok(stored);
        });

        // A tombstone, not a hard delete - see NoteStore.SoftDeleteAsync for why.
        notes.MapDelete("/{id:guid}", async Task<IResult> (
            Guid id,
            HttpRequest request,
            NoteStore store,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(request.Headers[SyncHeaders.Device], out var deviceId))
            {
                return Results.BadRequest(new { error = $"{SyncHeaders.Device} header must be the acting device's id." });
            }

            // The client stamps the deletion, like every other write, so a device
            // whose clock lags can't have its delete silently lose to the note's
            // own updated_at.
            var updatedAt = DateTimeOffset.TryParse(
                request.Headers["X-MdBolsa-Updated-At"], out var parsed) ? parsed : DateTimeOffset.UtcNow;

            var deleted = await store.SoftDeleteAsync(id, deviceId, updatedAt, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        // The incremental sync feed: "everything that changed since X", paged.
        notes.MapGet("/changes", async Task<IResult> (
            DateTimeOffset? since,
            DateTimeOffset? cursorAt,
            Guid? cursorId,
            int? limit,
            NoteStore store,
            CancellationToken ct) =>
        {
            var page = await store.GetChangedSinceAsync(
                since ?? DateTimeOffset.UnixEpoch,
                cursorAt is not null && cursorId is not null ? new NoteCursor(cursorAt.Value, cursorId.Value) : null,
                Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize),
                ct);

            return Results.Ok(page);
        });

        return app;
    }

    public const int DefaultPageSize = 200;
    public const int MaxPageSize = 1000;
}
