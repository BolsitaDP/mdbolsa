namespace MdBolsa.Server.Notes;

// One synchronized note as the server stores it. These fields are the sync
// metadata vision.md §10 asks for: `Id` and `ContentHash` identify the content,
// `DeviceId` says which device last wrote it, `Revision`/`UpdatedAt` answer
// "give me everything changed since X" without shipping the whole vault.
//
// The server deliberately does not parse note content. It stores the text the
// client sent, byte for byte, because the .md file is the source of truth
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
