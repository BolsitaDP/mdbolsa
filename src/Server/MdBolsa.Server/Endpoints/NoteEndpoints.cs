using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MdBolsa.Server.Notes;

namespace MdBolsa.Server.Endpoints;

public static class NoteEndpoints
{
    // Phase 8 scope: a storage/sync API, nothing more. No authentication - see
    // docs/decisions/0010-server-foundation.md for why that's an explicit,
    // temporary decision and what has to happen before this is reachable from
    // anything but localhost.
    public static IEndpointRouteBuilder MapNoteEndpoints(this IEndpointRouteBuilder app)
    {
        var notes = app.MapGroup("/api/notes").WithTags("notes");

        notes.MapGet("/{id:guid}", async Task<IResult> (Guid id, NoteStore store, CancellationToken ct) =>
        {
            var note = await store.GetAsync(id, ct);
            return note is null ? Results.NotFound() : Results.Ok(note);
        });

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

            await store.UpsertAsync(note, ct);
            return Results.NoContent();
        });

        // A tombstone, not a hard delete - see NoteStore.SoftDeleteAsync for why.
        notes.MapDelete("/{id:guid}", async Task<IResult> (
            Guid id,
            HttpRequest request,
            NoteStore store,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(request.Headers["X-MdBolsa-Device"], out var deviceId))
            {
                return Results.BadRequest(new { error = "X-MdBolsa-Device header must be the acting device's id." });
            }

            var deleted = await store.SoftDeleteAsync(id, deviceId, DateTimeOffset.UtcNow, ct);
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
