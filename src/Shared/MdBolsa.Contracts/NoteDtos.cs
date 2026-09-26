namespace MdBolsa.Contracts;

// A note as the client sends it. Everything the server needs to store a change
// travels here; the server fills in CreatedAt and the DeletedAt tombstone.
public sealed record NoteUpsert(
    Guid Id,
    string RelativePath,
    string Title,
    string Content,
    string ContentHash,
    int Revision,
    Guid DeviceId,
    DateTimeOffset UpdatedAt);

// The server's answer to a write: the state it actually stored. This is how a
// client learns that its write was *dropped* - the server doesn't merge, and a
// write older than what's stored is refused (see 0010). A client that sent
// revision 4 and reads back revision 5 from another device knows it lost the
// race, which is a conflict to report, not to resolve.
public sealed record NoteStored(
    Guid Id,
    string RelativePath,
    string Title,
    string ContentHash,
    int Revision,
    Guid DeviceId,
    DateTimeOffset UpdatedAt,
    bool Deleted);

// What "changed since" returns. `Deleted` distinguishes a tombstone (the note
// went away on another device) from a normal row, so a client can apply the
// deletion without guessing from a missing row.
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

public sealed record NoteDeleteRequest(Guid DeviceId, DateTimeOffset UpdatedAt);
