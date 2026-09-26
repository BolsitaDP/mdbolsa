using MdBolsa.Contracts;

namespace MdBolsa.Server.Notes;

// Two notes claiming the same path in the vault. Only one can exist there, so
// the write is refused - but as a *conflict* the client can report, not as a
// database error: this is the "someone else already has a note at that path"
// case, which is a normal thing to hit when two devices create notes with the
// same name.
//
// Postgres reports it as SQLSTATE 23505 (unique_violation); NoteStore translates
// it so the endpoint can answer 409 instead of leaking a 500.
public sealed class NotePathConflictException(string path)
    : Exception($"Another note already exists at {path}.")
{
    public string RelativePath { get; } = path;
}
