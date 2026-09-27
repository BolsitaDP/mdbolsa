using MdBolsa.Core.Attachments;

namespace MdBolsa.Core.Tests.Attachments;

// Walking the attachments folder: what gets indexed, what gets forgotten, and the
// one rule that keeps a half-written file out of the index.
public class AttachmentScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mdbolsa-attach-").FullName;
    private readonly FakeIndex _index = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AttachmentScanner Scanner() => new(_root, _index);

    private string Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public void AFirstScanFindsNothingInAnEmptyVault()
    {
        var result = Scanner().Scan();

        Assert.Equal(0, result.Added);
        Assert.Empty(_index.All);
    }

    [Fact]
    public void AnAttachmentIsIndexedUnderItsContentHash()
    {
        Write("Attachments/diagram.png", "not really a png");

        Scanner().Scan();

        var attachment = Assert.Single(_index.All);
        Assert.Equal(AttachmentHash.Of("not really a png"u8.ToArray()), attachment.Hash);
        Assert.Equal("Attachments/diagram.png", attachment.RelativePath);
        Assert.Equal("image/png", attachment.ContentType);
    }

    [Fact]
    public void TwoFilesWithTheSameContentAreOneAttachment()
    {
        // Deduplication is the reason for identifying attachments by content: the
        // same screenshot pasted twice is one file, not two.
        Write("Attachments/a.png", "identical");
        Write("Attachments/b.png", "identical");

        Scanner().Scan();

        Assert.Single(_index.All);
    }

    [Fact]
    public void ASecondScanOfAnUnchangedFolderAddsNothing()
    {
        Write("Attachments/a.png", "content");

        Assert.Equal(1, Scanner().Scan().Added);
        var second = Scanner().Scan();

        Assert.Equal(0, second.Added);
        Assert.Equal(1, second.Unchanged);
    }

    [Fact]
    public void AChangedFileIsIndexedAsANewHash()
    {
        Write("Attachments/a.png", "before");
        Scanner().Scan();

        Write("Attachments/a.png", "after");
        var result = Scanner().Scan();

        // Added, and the old hash is *forgotten*: the index mirrors the folder, so
        // content that is no longer on disk is not something to keep offering to the
        // server. Asserting the count was 2 would have pinned the opposite - an index
        // that accumulates history nobody asked for.
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Missing);

        var attachment = Assert.Single(_index.All);
        Assert.Equal(AttachmentHash.Of("after"u8.ToArray()), attachment.Hash);
    }

    [Fact]
    public void ADeletedFileIsForgotten()
    {
        var path = Write("Attachments/a.png", "content");
        Scanner().Scan();

        File.Delete(path);
        var result = Scanner().Scan();

        Assert.Equal(1, result.Missing);
        Assert.Empty(_index.All);
    }

    [Fact]
    public void FilesOutsideTheFolderAreIgnored()
    {
        Write("Notes/diagram.png", "not an attachment");
        Write("diagram.png", "nor this");

        Scanner().Scan();

        Assert.Empty(_index.All);
    }

    [Fact]
    public void DotfilesAndPartialsAreIgnored()
    {
        Write("Attachments/.DS_Store", "junk");
        Write("Attachments/download.tmp", "half");
        Write("Attachments/real.png", "content");

        Scanner().Scan();

        Assert.Equal("Attachments/real.png", Assert.Single(_index.All).RelativePath);
    }

    [Fact]
    public void AnEmptyFileIsSkipped()
    {
        // Hashing a zero-byte file that is still being written would put a name in the
        // index that will not match the file a second later.
        Write("Attachments/empty.png", string.Empty);
        Write("Attachments/real.png", "content");

        Scanner().Scan();

        Assert.Equal("Attachments/real.png", Assert.Single(_index.All).RelativePath);
    }

    [Fact]
    public void NestedFoldersInsideAttachmentsAreWalked()
    {
        Write("Attachments/2026/diagram.png", "content");

        Scanner().Scan();

        Assert.Equal("Attachments/2026/diagram.png", Assert.Single(_index.All).RelativePath);
    }

    [Fact]
    public void TheScanIsDeterministic()
    {
        Write("Attachments/b.png", "b");
        Write("Attachments/a.png", "a");
        Write("Attachments/c.png", "c");

        Scanner().Scan();

        // Path order, not filesystem order: the same vault scans the same way twice.
        Assert.Equal(
            ["Attachments/a.png", "Attachments/b.png", "Attachments/c.png"],
            _index.ScanOrder);
    }

    private sealed class FakeIndex : IAttachmentIndex
    {
        private readonly Dictionary<string, AttachmentMetadata> _byHash = new(StringComparer.Ordinal);
        public List<AttachmentMetadata> All => _byHash.Values.ToList();
        public List<string> ScanOrder { get; } = [];

        public void Upsert(AttachmentMetadata attachment)
        {
            if (!_byHash.ContainsKey(attachment.Hash)) ScanOrder.Add(attachment.RelativePath);
            _byHash[attachment.Hash] = attachment;
        }

        public IReadOnlyList<AttachmentMetadata> GetAll() => _byHash.Values.ToList();

        public AttachmentMetadata? Get(string hash) =>
            _byHash.GetValueOrDefault(hash);

        public bool Has(string hash) => _byHash.ContainsKey(hash);

        public int DeleteMissing(IReadOnlyCollection<string> hashesStillPresent)
        {
            var gone = _byHash.Keys.Where(hash => !hashesStillPresent.Contains(hash)).ToList();
            foreach (var hash in gone) _byHash.Remove(hash);
            return gone.Count;
        }
    }
}
