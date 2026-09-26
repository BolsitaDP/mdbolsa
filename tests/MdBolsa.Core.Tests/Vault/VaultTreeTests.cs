using MdBolsa.Core.Vault;

namespace MdBolsa.Core.Tests.Vault;

public class VaultTreeTests
{
    private static NoteMetadata Note(string relativePath) =>
        new(Guid.NewGuid(), relativePath, Path.GetFileNameWithoutExtension(relativePath),
            "hash", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public void Build_PutsANoteAtTheTopLevel_WhenItHasNoFolder()
    {
        var tree = VaultTree.Build([Note("Welcome.md")]);

        var node = Assert.Single(tree);
        Assert.False(node.IsFolder);
        Assert.Equal("Welcome", node.Name);
        Assert.Equal("Welcome.md", node.RelativePath);
    }

    [Fact]
    public void Build_StripsTheMarkdownExtensionFromNoteNames() =>
        Assert.Equal("Docker", Assert.Single(VaultTree.Build([Note("Docker.md")])).Name);

    [Fact]
    public void Build_GroupsNotesIntoTheirFolder()
    {
        var tree = VaultTree.Build([Note("Development/Docker.md"), Note("Personal/Home.md")]);

        Assert.Equal(2, tree.Count);
        var development = tree.Single(node => node.Name == "Development");
        Assert.True(development.IsFolder);
        Assert.Equal("Docker", Assert.Single(development.Children).Name);
    }

    [Fact]
    public void Build_NestsFoldersAsDeeplyAsThePathsDo()
    {
        var tree = VaultTree.Build([Note("a/b/c/Deep.md")]);

        var a = Assert.Single(tree);
        var b = Assert.Single(a.Children);
        var c = Assert.Single(b.Children);
        var deep = Assert.Single(c.Children);
        Assert.Equal("Deep", deep.Name);
        Assert.Equal("a/b/c/Deep.md", deep.RelativePath);
    }

    [Fact]
    public void Build_CreatesAFolderOnlyOnce_ForNotesSharingIt()
    {
        var tree = VaultTree.Build([Note("Notes/One.md"), Note("Notes/Two.md")]);

        var folder = Assert.Single(tree);
        Assert.True(folder.IsFolder);
        Assert.Equal(2, folder.Children.Count);
    }

    [Fact]
    public void Build_MergesAFolderCreatedByBothADeepAndAShallowNote()
    {
        var tree = VaultTree.Build([Note("a/b/Deep.md"), Note("a/Shallow.md")]);

        var a = Assert.Single(tree);
        Assert.Equal(2, a.Children.Count);
        Assert.Contains(a.Children, node => node.Name == "b" && node.IsFolder);
        Assert.Contains(a.Children, node => node.Name == "Shallow" && !node.IsFolder);
    }

    [Fact]
    public void Build_SortsFoldersBeforeNotes_ThenByName()
    {
        var tree = VaultTree.Build(
        [
            Note("zeta.md"),
            Note("Alpha.md"),
            Note("Beta/one.md"),
        ]);

        Assert.Equal(["Beta", "Alpha", "zeta"], tree.Select(node => node.Name));
    }

    [Fact]
    public void Build_SortsCaseInsensitively()
    {
        var tree = VaultTree.Build([Note("apple.md"), Note("Banana.md"), Note("cherry.md")]);

        Assert.Equal(["apple", "Banana", "cherry"], tree.Select(node => node.Name));
    }

    [Fact]
    public void Build_IsCaseInsensitiveAboutFolderIdentity()
    {
        // One folder, not two, even if the paths disagree about the casing.
        var tree = VaultTree.Build([Note("Notes/One.md"), Note("notes/Two.md")]);

        var folder = Assert.Single(tree);
        Assert.Equal(2, folder.Children.Count);
    }

    [Fact]
    public void Build_HandlesAnEmptyVault() => Assert.Empty(VaultTree.Build([]));

    [Fact]
    public void Build_IgnoresAnEmptyRelativePath() =>
        Assert.Empty(VaultTree.Build([Note("")]));

    [Fact]
    public void FullPath_IsTheNotePath_AndTheFolderPathForFolders()
    {
        var tree = VaultTree.Build([Note("a/b/Deep.md")]);
        var folder = Assert.Single(tree);
        var note = Assert.Single(Assert.Single(folder.Children).Children);

        Assert.Equal("a", folder.FullPath);
        Assert.Equal("a/b/Deep.md", note.FullPath);
    }

    [Fact]
    public void NotesIn_ListsEveryNote_InTreeOrder()
    {
        var tree = VaultTree.Build([Note("b/Two.md"), Note("a/One.md"), Note("root.md")]);

        Assert.Equal(
            ["a/One.md", "b/Two.md", "root.md"],
            VaultTree.NotesIn(tree).Select(node => node.RelativePath));
    }

    [Fact]
    public void FolderPaths_ListsEveryFolder_WithItsFullPath()
    {
        var tree = VaultTree.Build([Note("a/b/c/Deep.md"), Note("root.md")]);

        Assert.Equal(["a", "a/b", "a/b/c"], VaultTree.FolderPaths(tree));
    }
}
