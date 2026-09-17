namespace MdBolsa.Core.Links;

// Implemented by MdBolsa.Data (SqliteLinkIndex).
public interface ILinkIndex
{
    IReadOnlyList<NoteLink> GetAll();

    // Full replace, not an incremental diff: a note's outgoing links are cheap to
    // recompute from its own content on every scan, so there's no need to track
    // individual additions/removals.
    void ReplaceLinksForNote(Guid sourceNoteId, IReadOnlyList<NoteLink> links);

    // Notes whose content links to targetNoteId.
    IReadOnlyList<Guid> GetBacklinkSourceIds(Guid targetNoteId);

    // Deletes any links whose source note is not in noteIdsStillPresent. Returns the
    // number of source notes' link sets removed.
    int DeleteLinksForNotesNotIn(IReadOnlyCollection<Guid> noteIdsStillPresent);
}
