namespace MdBolsa.Core.Search;

// Implemented by MdBolsa.Data (SqliteSearchIndex).
public interface ISearchIndex
{
    void IndexNote(Guid noteId, string title, string body);

    int DeleteNotesNotIn(IReadOnlyCollection<Guid> noteIdsStillPresent);

    IReadOnlyList<SearchResult> Search(string query, int limit = 50);
}
