using MdBolsa.Core.Vault;

namespace MdBolsa.Core.Tests.Vault;

public class FrontMatterTests
{
    [Fact]
    public void TryReadId_ReturnsNull_WhenNoFrontMatter()
    {
        Assert.Null(FrontMatter.TryReadId("# Just a note\n\nNo frontmatter here.\n"));
    }

    [Fact]
    public void TryReadId_ReturnsNull_WhenFrontMatterHasNoId()
    {
        var content = "---\ncreated: 2026-09-15\ntags: [personal]\n---\n\n# Home\n";
        Assert.Null(FrontMatter.TryReadId(content));
    }

    [Fact]
    public void TryReadId_ReturnsId_WhenPresent()
    {
        var id = Guid.NewGuid();
        var content = $"---\nid: {id}\ncreated: 2026-09-15\n---\n\n# Home\n";
        Assert.Equal(id, FrontMatter.TryReadId(content));
    }

    [Fact]
    public void EnsureId_PrependsNewFrontMatterBlock_WhenNoneExists()
    {
        var id = Guid.NewGuid();
        var result = FrontMatter.EnsureId("# Home\n", id);

        Assert.Equal(id, FrontMatter.TryReadId(result));
        Assert.Contains("# Home", result);
    }

    [Fact]
    public void EnsureId_InsertsIdIntoExistingBlock_PreservingOtherFields()
    {
        var id = Guid.NewGuid();
        var content = "---\ncreated: 2026-09-15\ntags: [personal]\n---\n\n# Home\n";
        var result = FrontMatter.EnsureId(content, id);

        Assert.Equal(id, FrontMatter.TryReadId(result));
        Assert.Contains("created: 2026-09-15", result);
        Assert.Contains("tags: [personal]", result);
    }

    [Fact]
    public void EnsureId_IsNoOp_WhenIdAlreadyPresent()
    {
        var id = Guid.NewGuid();
        var content = $"---\nid: {id}\n---\n\nBody\n";

        var result = FrontMatter.EnsureId(content, Guid.NewGuid());

        Assert.Equal(content, result);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void TryReadId_FindsId_RegardlessOfLineEndingStyle(string newline)
    {
        var id = Guid.NewGuid();
        var content = string.Join(newline, "---", $"id: {id}", "created: 2026-09-15", "---", "", "# Note", "");

        Assert.Equal(id, FrontMatter.TryReadId(content));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void EnsureId_IsNoOp_WhenIdAlreadyPresent_RegardlessOfLineEndingStyle(string newline)
    {
        var id = Guid.NewGuid();
        var content = string.Join(newline, "---", $"id: {id}", "---", "", "Body", "");

        var result = FrontMatter.EnsureId(content, Guid.NewGuid());

        Assert.Equal(id, FrontMatter.TryReadId(result));
    }

    [Fact]
    public void Body_ReturnsContentAfterFrontMatter()
    {
        var content = "---\nid: abc\n---\n\n# Note\nBody text.\n";

        Assert.Equal("# Note\nBody text.\n", FrontMatter.Body(content));
    }

    [Fact]
    public void Body_ReturnsWholeContent_WhenNoFrontMatter()
    {
        var content = "# Note\nBody text.\n";

        Assert.Equal(content, FrontMatter.Body(content));
    }

    [Fact]
    public void TryReadBlock_ReturnsRawBlock_OrNull()
    {
        var content = "---\nid: abc\ntags: [x]\n---\n\n# Note\n";

        Assert.Equal("\nid: abc\ntags: [x]", FrontMatter.TryReadBlock(content));
        Assert.Null(FrontMatter.TryReadBlock("# Note\n"));
    }

    [Fact]
    public void ReadFields_ReturnsTopLevelScalarFields()
    {
        var content = "---\nid: abc\ncreated: 2026-09-15\nstatus: \"in progress\"\n---\n\n# Note\n";

        var fields = FrontMatter.ReadFields(content);

        Assert.Equal(3, fields.Count);
        Assert.Equal(new MetadataField("id", "abc"), fields[0]);
        Assert.Equal(new MetadataField("created", "2026-09-15"), fields[1]);
        Assert.Equal(new MetadataField("status", "in progress"), fields[2]);
    }

    [Fact]
    public void ReadFields_SkipsNestedLinesAndBlockSequences()
    {
        var content = "---\ntags:\n  - one\n  - two\nnested:\n  key: value\n---\n\n# Note\n";

        // Only the two top-level keys survive; their values live on the indented
        // lines below them, which this deliberately shallow reader doesn't follow,
        // so they come back empty rather than guessed at.
        var fields = FrontMatter.ReadFields(content);

        Assert.Equal(2, fields.Count);
        Assert.Equal(new MetadataField("tags", string.Empty), fields[0]);
        Assert.Equal(new MetadataField("nested", string.Empty), fields[1]);
    }

    [Fact]
    public void ReadFields_ReturnsNothing_WhenNoFrontMatter()
    {
        Assert.Empty(FrontMatter.ReadFields("# Note\nkey: value\n"));
    }
}
