namespace MdBolsa.Core.Vault;

// Renaming a note is a filesystem move, but the decisions around it - what counts
// as a valid note name, when to append the extension, when to refuse outright - are
// pure logic and belong here where they can be tested. The shell supplies the I/O.
public static class NoteRename
{
    public const string Extension = ".md";

    // The name a request resolves to before validation: trimmed, with the extension
    // appended if the user left it off. The shell needs this to check whether the
    // destination file already exists, so it lives next to the validation rules
    // rather than being reimplemented there.
    public static string Normalize(string requestedName)
    {
        var trimmed = requestedName.Trim();
        return trimmed.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + Extension;
    }

    public static bool TryResolve(
        string currentName,
        string requestedName,
        bool targetExists,
        out string newName,
        out string error)
    {
        newName = string.Empty;

        var trimmed = requestedName.Trim();
        if (trimmed.Length == 0)
        {
            error = "Enter a name for the note.";
            return false;
        }

        // Check the raw request before normalising: "." and ".." would otherwise
        // grow an extension ("...md") and slip past the checks below, and a path
        // would get one too.
        if (trimmed is "." or "..")
        {
            error = "That is not a valid note name.";
            return false;
        }

        if (trimmed.Contains('/') || trimmed.Contains('\\'))
        {
            error = $"\"{trimmed}\" is not a valid note name - it must be a name, not a path.";
            return false;
        }

        trimmed = Normalize(trimmed);

        // A bare extension ("nothing.md" is fine, ".md" is not) would otherwise move
        // the file to a dotfile that Windows won't show by default.
        if (string.Equals(trimmed, Extension, StringComparison.OrdinalIgnoreCase))
        {
            error = "Enter a name for the note.";
            return false;
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = $"\"{trimmed}\" contains characters a note name can't have.";
            return false;
        }

        if (string.Equals(trimmed, currentName, StringComparison.OrdinalIgnoreCase))
        {
            error = $"\"{currentName}\" is already the name of this note.";
            return false;
        }

        if (targetExists)
        {
            error = $"There is already a note called {trimmed}.";
            return false;
        }

        newName = trimmed;
        error = string.Empty;
        return true;
    }
}
