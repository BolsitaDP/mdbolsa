using MdBolsa.Server.Endpoints;
using MdBolsa.Contracts;

namespace MdBolsa.Server.Tests;

// The rules the API refuses to store, tested without a database. The data access
// itself (NoteStore) needs a real PostgreSQL, which is what the dev compose stack
// is for - these cover the decisions that are cheap to get wrong and expensive to
// discover in production.
public class NoteValidationTests
{
    private static NoteUpsert Valid() => new(
        Guid.NewGuid(),
        "Personal/Home.md",
        "Home",
        "# Home",
        "ABC123",
        1,
        Guid.NewGuid(),
        DateTimeOffset.UtcNow);

    [Fact]
    public void Validate_AcceptsAWellFormedNote() => Assert.Null(NoteValidation.Validate(Valid()));

    [Fact]
    public void Validate_RejectsAnEmptyId() =>
        Assert.Contains("id", NoteValidation.Validate(Valid() with { Id = Guid.Empty }));

    [Fact]
    public void Validate_RejectsAMissingDevice() =>
        Assert.Contains("device", NoteValidation.Validate(Valid() with { DeviceId = Guid.Empty }));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_RejectsRevisionsBelowOne(int revision) =>
        Assert.Contains("Revision", NoteValidation.Validate(Valid() with { Revision = revision }));

    [Fact]
    public void Validate_RejectsABlankPath() =>
        Assert.Contains("path", NoteValidation.Validate(Valid() with { RelativePath = "  " }));

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../outside.md")]
    [InlineData("a/../../b.md")]
    public void Validate_RejectsPathsThatEscapeTheVault(string path) =>
        Assert.Contains("relative", NoteValidation.Validate(Valid() with { RelativePath = path }));

    [Theory]
    [InlineData("Personal/Home.md")]
    [InlineData("note.md")]
    [InlineData("deeply/nested/folder/note.md")]
    public void Validate_AcceptsNestedRelativePaths(string path) =>
        Assert.Null(NoteValidation.Validate(Valid() with { RelativePath = path }));

    [Fact]
    public void Validate_RejectsAMissingContentHash() =>
        Assert.Contains("hash", NoteValidation.Validate(Valid() with { ContentHash = "" }));

    [Fact]
    public void Validate_AllowsContentWithoutMarkdown() =>
        Assert.Null(NoteValidation.Validate(Valid() with { Content = "just some text, no frontmatter" }));
}
