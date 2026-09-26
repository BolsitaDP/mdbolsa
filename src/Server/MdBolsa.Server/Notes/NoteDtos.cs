namespace MdBolsa.Server.Notes;

// The wire shape clients PUT. Everything the server needs to store a change
// arrives here; the server fills in CreatedAt and the DeletedAt tombstone.
public sealed record NoteUpsert(
    Guid Id,
    string RelativePath,
    string Title,
    string Content,
    string ContentHash,
    int Revision,
    Guid DeviceId,
    DateTimeOffset UpdatedAt);

// What "changed since" returns. `Deleted` distinguishes a tombstone (the note
// went away on another device) from a normal row, so a client can apply the
// deletion without guessing from a missing row - important because "absent" and
// "not yet synced" look identical otherwise.
public sealed record NoteChange(
    Guid Id,
    string? RelativePath,
    string? Title,
    string? Content,
    string? ContentHash,
    int? Revision,
    Guid? DeviceId,
    DateTimeOffset UpdatedAt,
    bool Deleted);

// Watermark for paging "changed since": the (updated_at, id) of the last row
// returned. Both halves are needed because several notes can share a timestamp,
// and comparing on the timestamp alone would skip rows on the boundary.
public sealed record NoteCursor(DateTimeOffset UpdatedAt, Guid NoteId);

public sealed record NoteChangesPage(IReadOnlyList<NoteChange> Changes, NoteCursor? NextCursor);
