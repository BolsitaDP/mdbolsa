using MdBolsa.Core.Attachments;

namespace MdBolsa.Core.Tests.Attachments;

// Which files count as attachments, and what they are called. The naming rules are
// the heart of the phase, so they are tested harder than they look.
public class AttachmentRulesTests
{
    [Fact]
    public void TheFolderIsAttachmentsAtTheVaultRoot() =>
        Assert.Equal("Attachments", AttachmentRules.FolderName);

    [Theory]
    [InlineData("Attachments/diagram.png", true)]
    [InlineData("Attachments/nested/deep/file.pdf", true)]
    [InlineData("attachments/diagram.png", true)]   // case-insensitive: macOS vs Windows
    [InlineData("Notes/diagram.png", false)]        // not in the folder
    [InlineData("diagram.png", false)]
    [InlineData("Adjuntos/diagram.png", false)]     // not *our* folder name
    public void OnlyFilesInsideTheAttachmentsFolderCount(string path, bool expected) =>
        Assert.Equal(expected, AttachmentRules.IsAttachmentPath(path));

    [Theory]
    [InlineData("Attachments/.hidden.png", false)]      // a dotfile is a tool mid-something
    [InlineData("Attachments/scan.tmp", false)]        // a partial write
    [InlineData("Attachments/thing.crdownload", false)]
    [InlineData("Attachments/backup~", false)]
    [InlineData("Attachments/noextension", false)]     // dot > 0 rejects a trailing dot
    [InlineData("Attachments/.png", false)]            // and a leading one
    [InlineData("Attachments/image.png", true)]
    public void ObviousNonAttachmentsAreRejected(string path, bool expected) =>
        Assert.Equal(expected, AttachmentRules.IsAttachmentPath(path));

    [Fact]
    public void ContentTypesAreKnownForTheFormatsWorthNaming()
    {
        Assert.Equal("image/png", AttachmentRules.ContentTypeFor("Attachments/a.png"));
        Assert.Equal("image/jpeg", AttachmentRules.ContentTypeFor("Attachments/a.JPG"));
        Assert.Equal("application/pdf", AttachmentRules.ContentTypeFor("Attachments/a.pdf"));
        Assert.Equal("image/svg+xml", AttachmentRules.ContentTypeFor("Attachments/a.svg"));
    }

    [Fact]
    public void AnUnknownFormatIsOctetStream()
    {
        // A wrong guess in a Content-Type is worse than no guess: a viewer sniffs the
        // bytes anyway, and "text/plain" on a PNG gets it saved as text somewhere.
        Assert.Equal("application/octet-stream", AttachmentRules.ContentTypeFor("Attachments/a.xyz"));
    }

    [Fact]
    public void AnAttachmentIsNamedAfterItsHashAndKeepsItsExtension()
    {
        var hash = new string('a', 64);

        Assert.Equal(hash + ".png", AttachmentRules.NameFor(hash, "Attachments/whatever.png"));
    }

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("application/pdf", ".pdf")]
    [InlineData("text/plain", ".txt")]
    [InlineData("application/octet-stream", ".bin")]
    [InlineData(null, ".bin")]
    public void AnExtensionComesBackFromTheContentType(string? contentType, string expected) =>
        // A content-addressed download has no name to carry an extension, so without
        // this it arrives as "3f7868..." and nothing on the machine will open it.
        Assert.Equal(expected, AttachmentRules.ExtensionFor(contentType));
}

public class AttachmentHashTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mdbolsa-hash-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void TheHashIsLowercaseHexOfTheContent()
    {
        var content = "hello"u8.ToArray();

        var hash = AttachmentHash.Of(content);

        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f'));
        // The published SHA-256 of "hello", so this fails if the algorithm changes and
        // not only if the plumbing does.
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", hash);
    }

    [Fact]
    public void TheSameBytesAlwaysHashTheSame()
    {
        // The property the whole phase rests on: identical content means an identical
        // name, on every device, forever.
        Assert.Equal(AttachmentHash.Of("same"u8.ToArray()), AttachmentHash.Of("same"u8.ToArray()));
    }

    [Fact]
    public void DifferentBytesHashDifferently() =>
        Assert.NotEqual(AttachmentHash.Of("a"u8.ToArray()), AttachmentHash.Of("b"u8.ToArray()));

    [Fact]
    public void HashingAFileMatchesHashingItsBytes()
    {
        var path = Path.Combine(_root, "a.png");
        File.WriteAllBytes(path, [1, 2, 3, 4]);

        Assert.Equal(AttachmentHash.Of([1, 2, 3, 4]), AttachmentHash.OfFile(path));
    }

    [Fact]
    public void AnEmptyFileHashesToTheEmptyHash() =>
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            AttachmentHash.Of([]));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ", false)]
    [InlineData("3f786850e387550fdab836ed7e6dc881de23001b", false)]
    public void OnlyAWellFormedHashPasses(string? hash, bool expected) =>
        Assert.Equal(expected, AttachmentHash.IsValid(hash));
}
