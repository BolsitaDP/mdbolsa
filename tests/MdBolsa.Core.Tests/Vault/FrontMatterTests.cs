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
}
