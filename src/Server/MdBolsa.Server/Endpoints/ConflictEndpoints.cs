using MdBolsa.Contracts;
using MdBolsa.Server.Auth;
using MdBolsa.Server.Notes;

namespace MdBolsa.Server.Endpoints;

// The conflict-resolution surface, Phase 10.
//
// Two operations, and deliberately no third:
//
//   GET  /api/notes/{id}/versions        - the edit history, newest first
//   GET  /api/notes/{id}/versions/{rev}   - one revision's content
//
// There is no "merge" endpoint and no automatic merging, on the client or here.
// A merge needs a common ancestor and Markdown-aware diffing; guessing at it
// would produce a third version that neither person wrote. So the server's job
// is to make sure *both* versions are still readable, and a human decides. See
// docs/decisions/0012-conflict-resolution.md.
public static class ConflictEndpoints
{
    public static IEndpointRouteBuilder MapConflictEndpoints(this IEndpointRouteBuilder app, string? token)
    {
        var notes = app.MapGroup("/api/notes").WithTags("conflicts");

        notes.AddEndpointFilter(async (context, next) =>
        {
            var presented = context.HttpContext.Request.Headers[SyncHeaders.Token].ToString();
            return TokenValidator.IsValid(presented, token) ? await next(context) : Results.Unauthorized();
        });

        notes.MapGet("/{id:guid}/versions", async Task<IResult> (
            Guid id,
            NoteStore store,
            CancellationToken ct) =>
        {
            var note = await store.GetAsync(id, ct);
            if (note is null) return Results.NotFound();

            return Results.Ok(await store.GetVersionsAsync(id, ct));
        });

        notes.MapGet("/{id:guid}/versions/{revision:int}", async Task<IResult> (
            Guid id,
            int revision,
            NoteStore store,
            CancellationToken ct) =>
        {
            var version = await store.GetVersionAsync(id, revision, ct);
            return version is null ? Results.NotFound() : Results.Ok(version);
        });

        return app;
    }
}
