namespace MdBolsa.Core.Links;

public sealed record NoteLink(Guid SourceNoteId, string TargetText, Guid? TargetNoteId);
