using MdBolsa.Core.Vault;

namespace MdBolsa.Core.Tests.Vault;

public class NoteRenameTests
{
    [Theory]
    [InlineData("Docker", "Docker.md")]
    [InlineData("Docker.md", "Docker.md")]
    [InlineData("  Docker  ", "Docker.md")]
    [InlineData("notas de docker", "notas de docker.md")]
    [InlineData("2026-plan", "2026-plan.md")]
    public void TryResolve_KeepsTheName_AndNormalisesTheExtension(
        string requested, string expected)
    {
        var ok = NoteRename.TryResolve("Old.md", requested, targetExists: false, out var newName, out _);

        Assert.True(ok);
        Assert.Equal(expected, newName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".md")]
    [InlineData(".")]
    [InlineData("..")]
    public void TryResolve_Rejects_NamesThatArentNotes(string requested)
    {
        Assert.False(NoteRename.TryResolve("Old.md", requested, false, out _, out var error));
        Assert.NotEqual(string.Empty, error);
    }

    [Theory]
    [InlineData("sub/Other.md")]
    [InlineData("sub\\Other.md")]
    [InlineData("a/b")]
    public void TryResolve_Rejects_Paths(string requested) =>
        Assert.False(NoteRename.TryResolve("Old.md", requested, false, out _, out _));

    [Fact]
    public void TryResolve_Rejects_TheSameName_EvenWithDifferentCasing() =>
        Assert.False(NoteRename.TryResolve("Docker.md", "docker", false, out _, out var error));

    [Fact]
    public void TryResolve_RejectsCharactersWindowsDoesNotAllowInFileNames()
    {
        // ':' is illegal in a Windows file name; the guard is the platform's own list.
        Assert.False(NoteRename.TryResolve("Old.md", "12:30 notes", false, out _, out _));
    }

    [Fact]
    public void TryResolve_Rejects_AnExistingTarget()
    {
        Assert.False(NoteRename.TryResolve("Old.md", "Docker", targetExists: true, out _, out var error));
        Assert.Contains("already a note", error);
    }

    [Fact]
    public void TryResolve_AcceptsAValidRename()
    {
        var ok = NoteRename.TryResolve("Old.md", "Docker", false, out var newName, out var error);

        Assert.True(ok);
        Assert.Equal("Docker.md", newName);
        Assert.Equal(string.Empty, error);
    }
}
