namespace MdBolsa.Contracts;

// The wire contract, shared by the server and the client (see
// docs/decisions/0011-client-sync.md). DTOs only: no logic, no dependencies.

// Headers are part of the contract, not an implementation detail of either side:
// the client sets them, the server reads them, and a typo in one is a silent
// auth failure. They're constants so both sides compile against the same
// spelling.
public static class SyncHeaders
{
    // The shared secret for this server. Phase 9's deliberately simple
    // authentication: one token per deployment, presented by every device.
    // See docs/decisions/0011-client-sync.md for what it does and doesn't buy.
    public const string Token = "X-MdBolsa-Token";

    // Which device is acting. Required on writes so the server can record who
    // wrote last (needed for conflict detection in Phase 10), and on deletes so
    // a tombstone says whose deletion it was.
    public const string Device = "X-MdBolsa-Device";

    // When the acting device made the change. Deletes carry it so a device whose
    // clock lags can't have its delete silently lose to the note's own
    // updated_at.
    public const string UpdatedAt = "X-MdBolsa-Updated-At";
}

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

// A note exactly as the server stores it, including the content it keeps but
// never interprets. The server does not parse note content: it stores the text
// the client sent, byte for byte, because the .md file is the source of truth
// (see docs/decisions/0002-markdown-as-canonical-storage.md) and the client is
// the only thing that understands it.
public sealed record NoteRecord(
    Guid Id,
    string RelativePath,
    string Title,
    string Content,
    string ContentHash,
    int Revision,
    Guid DeviceId,
    DateTimeOffset UpdatedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DeletedAt);

// One past revision of a note. Phase 9 could tell you *that* two devices
// disagreed; it could not tell you what either side said, which makes "keep mine
// or take theirs" a guess. This is the missing half: every accepted write leaves
// a copy here, so both versions stay readable and a human can choose.
public sealed record NoteVersion(
    Guid NoteId,
    int Revision,
    string RelativePath,
    string Content,
    string ContentHash,
    Guid DeviceId,
    DateTimeOffset UpdatedAt,
    bool IsCurrent);
