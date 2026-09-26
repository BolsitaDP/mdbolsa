using MdBolsa.Server.Notes;

namespace MdBolsa.Server.Endpoints;

// What the server refuses to store, and why. Kept separate from the endpoints so
// the rules are unit-testable without a database (see MdBolsa.Server.Tests).
public static class NoteValidation
{
    public static string? Validate(NoteUpsert note)
    {
        if (note.Id == Guid.Empty) return "A note needs a real id.";
        if (note.DeviceId == Guid.Empty) return "A write needs to say which device made it.";
        if (note.Revision < 1) return "Revision starts at 1.";
        if (string.IsNullOrWhiteSpace(note.RelativePath)) return "A note needs a path in the vault.";
        if (note.RelativePath.StartsWith('/') || note.RelativePath.Contains(".."))
        {
            // The vault is a flat namespace of relative paths; a path that escapes
            // it is either a bug or an attempt, and neither belongs in storage.
            return "Path must be relative to the vault root.";
        }
        if (note.ContentHash.Length == 0) return "A note needs a content hash.";

        return null;
    }
}
