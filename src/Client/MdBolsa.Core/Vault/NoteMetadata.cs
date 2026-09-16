namespace MdBolsa.Core.Vault;

public sealed record NoteMetadata(
    Guid Id,
    string RelativePath,
    string Title,
    string ContentHash,
    int Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
