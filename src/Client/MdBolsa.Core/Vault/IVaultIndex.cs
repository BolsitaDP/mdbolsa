namespace MdBolsa.Core.Vault;

// Implemented by MdBolsa.Data (SqliteVaultIndex). Keyed by NoteMetadata.Id, not by path,
// so a renamed/moved file is reconciled in place rather than treated as delete+create.
public interface IVaultIndex
{
    IReadOnlyList<NoteMetadata> GetAll();

    void Upsert(NoteMetadata note);

    // Deletes any indexed note whose Id is not in idsStillPresent. Returns the number deleted.
    int DeleteMissing(IReadOnlyCollection<Guid> idsStillPresent);
}
