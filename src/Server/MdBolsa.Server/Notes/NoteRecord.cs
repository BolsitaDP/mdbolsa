using MdBolsa.Contracts;

namespace MdBolsa.Server.Notes;

// One synchronized note as the server stores it, including the content it keeps
// but never interprets. The server does not parse note content: it stores the
// text the client sent, byte for byte, because the .md file is the source of
// truth (see docs/decisions/0002-markdown-as-canonical-storage.md) and the
// client is the only thing that understands it.
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
