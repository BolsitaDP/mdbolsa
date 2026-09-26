namespace MdBolsa.Core.Tags;

// Implemented by MdBolsa.Data (SqliteTagIndex). One row per (note, tag) pair -
// there's deliberately no separate tag table, so a tag can't outlive the last
// note that used it (see docs/decisions/0008-tags-and-metadata.md).
public interface ITagIndex
{
    // Every tag in the vault with how many notes carry it, for the tag panel.
    IReadOnlyList<TagCount> GetTagCounts();

    IReadOnlyList<string> GetTagsForNote(Guid noteId);

    IReadOnlyList<Guid> GetNoteIdsForTag(string tag);

    // Replaces a note's whole tag set (empty list removes them all).
    void ReplaceTagsForNote(Guid noteId, IReadOnlyList<string> tags);

    // Deletes tags belonging to notes that are no longer in the vault. Returns the
    // number of rows deleted.
    int DeleteTagsForNotesNotIn(IReadOnlyCollection<Guid> noteIdsStillPresent);
}

public readonly record struct TagCount(string Tag, int NoteCount);
