namespace MdBolsa.Core.Graph;

public enum GraphNodeKind
{
    Note,
    Tag,
}

public enum GraphEdgeKind
{
    // A resolved [[wiki link]] between two notes, directed source -> target.
    Link,

    // A note's tag, directed note -> tag.
    Tag,
}

// Keys are prefixed by kind ("note:{guid}" / "tag:{name}") so a tag whose name
// happens to look like a note id can never collide with a note, and so the UI can
// recover the note id from a key without a lookup table.
public sealed record GraphNode(string Key, GraphNodeKind Kind, string Label)
{
    public static GraphNode ForNote(Guid noteId, string relativePath) =>
        new(NotePrefix + noteId, GraphNodeKind.Note, relativePath);

    public static GraphNode ForTag(string tag) =>
        new(TagPrefix + tag, GraphNodeKind.Tag, tag);

    public static bool TryGetNoteId(string key, out Guid noteId)
    {
        noteId = Guid.Empty;
        return key.StartsWith(NotePrefix, StringComparison.Ordinal) &&
               Guid.TryParse(key.AsSpan(NotePrefix.Length), out noteId);
    }

    private const string NotePrefix = "note:";
    private const string TagPrefix = "tag:";
}
