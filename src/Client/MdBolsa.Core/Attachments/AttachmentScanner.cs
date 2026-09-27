using MdBolsa.Core.Attachments;

namespace MdBolsa.Core.Attachments;

// Implemented by MdBolsa.Data (SqliteAttachmentIndex).
public interface IAttachmentIndex
{
    void Upsert(AttachmentMetadata attachment);

    /// <summary>Every attachment known locally, for the push side of a sync.</summary>
    IReadOnlyList<AttachmentMetadata> GetAll();

    AttachmentMetadata? Get(string hash);

    bool Has(string hash);

    /// <summary>Forgets attachments whose files are gone from the vault.</summary>
    int DeleteMissing(IReadOnlyCollection<string> hashesStillPresent);
}

// Walks the attachments folder, hashes what it finds, and records it.
//
// Two things it deliberately does not do:
//
//   * **Rename anything.** A file called `diagram.png` whose hash is 3f78... is
//     *an* attachment, and the scanner reports it under its content. Renaming it to
//     `3f78....png` would fix the reference in a note on the next save, but it also
//     means a file somebody deliberately named gets renamed by a background process.
//     The reference rewriting is the note's job, not the scanner's.
//   * **Care about a file it has seen before.** It re-hashes everything every scan.
//     For a personal vault that is a few hundred small files and takes milliseconds;
//     for a vault of large videos it would not, and then the fix is to trust the
//     size and mtime, not to guess now.
public sealed class AttachmentScanner(string vaultRoot, IAttachmentIndex index)
{
    public AttachmentScanResult Scan()
    {
        int added = 0, unchanged = 0;
        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var relativePath in AttachmentRules.Enumerate(vaultRoot))
        {
            var fullPath = Path.Combine(vaultRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

            long size;
            string hash;
            try
            {
                var info = new FileInfo(fullPath);

                // Caught mid-write by whatever is writing it. Skipping is right: the
                // next scan gets it whole, and hashing a partial file would put a name
                // in the index that does not match the file.
                if (info.Length == 0) continue;

                size = info.Length;
                hash = AttachmentHash.OfFile(fullPath);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            present.Add(hash);

            var existing = index.Get(hash);
            if (existing is not null && existing.RelativePath == relativePath && existing.Size == size)
            {
                unchanged++;
                continue;
            }

            // A different path or a different size for the same hash is not a change
            // of content - it is the same content somewhere else, or a note about a
            // file that has since been edited. Either way it is counted as new so the
            // index and the folder agree.
            added++;
            index.Upsert(new AttachmentMetadata(
                hash,
                relativePath,
                size,
                AttachmentRules.ContentTypeFor(relativePath),
                DateTimeOffset.UtcNow));
        }

        return new AttachmentScanResult(added, unchanged, index.DeleteMissing(present));
    }}
